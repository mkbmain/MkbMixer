using System;
using System.ComponentModel;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Avalonia.Threading;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Android;

/// <summary>
/// Keeps the process alive while a deck is playing, and connects the mix to the
/// system's media controls. Without the service Android freezes or kills the app
/// shortly after it leaves the screen, which silences the mix and stops the
/// auto-cue clock that drives the next transition.
/// </summary>
/// <remarks>
/// The decks and the engine live in <see cref="MainViewModel"/>, not here: this
/// service tells the system that audio is playing, carries the notification a
/// foreground service must show, and owns the media session, audio focus and the
/// "becoming noisy" receiver, all of which pause or resume the whole mix through
/// <see cref="MainViewModel.PauseAll"/> and <see cref="MainViewModel.ResumePaused"/>.
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackService : Service
{
    private const string ChannelId = "playback";
    private const int NotificationId = 1;
    private const string ExtraText = "text";
    private const string ActionPlay = "com.mkbmain.mixer.PLAY";
    private const string ActionPause = "com.mkbmain.mixer.PAUSE";

    /// <summary>
    /// How long both decks must be silent before the service stops. Android 12+
    /// refuses to start a foreground service from the background, so stopping in
    /// the gap between one track ending and the next starting would leave the next
    /// one unprotected.
    /// </summary>
    private static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// After a pause from the lock screen, a headset button, a phone call or the
    /// output disconnecting, the service stays up this long so the play button keeps
    /// working: once it stops, Android 12+ will not let the app start it again from
    /// the background.
    /// </summary>
    private static readonly TimeSpan HeldStopDelay = TimeSpan.FromMinutes(10);

    private static MainViewModel? _watched;
    private static IDisposable? _pendingStop;
    private static bool _running;
    private static bool _held;
    private static bool _pausedForCall;
    private static string _text = string.Empty;
    private static PlaybackService? _instance;

    private PowerManager.WakeLock? _wakeLock;
    private MediaSession? _session;
    private AudioFocusRequestClass? _focusRequest;
    private readonly NoisyReceiver _noisy = new();

    /// <summary>Starts and stops the service as the view model's playback state changes.</summary>
    public static void Watch(MainViewModel viewModel)
    {
        // The activity can be recreated around the same view model; subscribe once.
        if (ReferenceEquals(_watched, viewModel)) return;
        _watched = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private static void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel vm) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsAnyDeckPlaying) when vm.IsAnyDeckPlaying:
                _pendingStop?.Dispose();
                _pendingStop = null;
                _held = false;
                _pausedForCall = false;   // starting playback is the user's own resume decision
                _text = vm.PlayingSummary;
                // A running service is updated in place; asking to start it again
                // from the background could be refused on Android 12+.
                if (_running && _instance is not null) _instance.OnResumed();
                else Start(vm);
                break;
            case nameof(MainViewModel.IsAnyDeckPlaying):
                _pendingStop ??= DispatcherTimer.RunOnce(Stop, _held ? HeldStopDelay : StopDelay);
                _instance?.Refresh();
                break;
            case nameof(MainViewModel.PlayingSummary) when _running && vm.IsAnyDeckPlaying:
                _text = vm.PlayingSummary;
                _instance?.Refresh();
                break;
        }
    }

    private static void Start(MainViewModel vm)
    {
        Context context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(PlaybackService)).PutExtra(ExtraText, vm.PlayingSummary);
        try
        {
            context.StartForegroundService(intent);
            _running = true;
        }
        catch (Java.Lang.IllegalStateException)   // ForegroundServiceStartNotAllowedException on 12+
        {
            // Playback began while the app was already in the background, which
            // only happens if the service stopped mid-mix. Nothing better to do
            // than say so; the audio itself carries on until the system steps in.
            vm.StatusMessage = "Background playback is not protected: open the app again to resume it";
        }
    }

    private static void Stop()
    {
        _pendingStop = null;
        _held = false;
        _pausedForCall = false;   // the call's focus Gain may never reach a stopped service
        if (!_running) return;
        _running = false;
        Context context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(PlaybackService)));
    }

    /// <summary>A pause the user did not make in the app: hold the service so play still works.</summary>
    private static void PauseFromSystem()
    {
        if (_watched?.PauseAll() == true) _held = true;
    }

    /// <summary>
    /// A notification action recreates a stopped service, and one that changes
    /// nothing never flips <c>IsAnyDeckPlaying</c>, so no stop would be scheduled
    /// and the service would stay foreground holding the wake lock.
    /// </summary>
    private static void HoldIfSilent()
    {
        if (IsPlaying) return;
        _held = true;
        _pendingStop ??= DispatcherTimer.RunOnce(Stop, HeldStopDelay);
    }

    private static void ResumeFromSystem()
    {
        _pausedForCall = false;
        _watched?.ResumePaused();
    }

    private static bool IsPlaying => _watched?.IsAnyDeckPlaying == true;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _instance = this;

        _session = new MediaSession(this, "MkbMixer");
        _session.SetCallback(new SessionCallback());
        _session.Active = true;

        // Sent only when the output carrying our audio goes away (wired headphones,
        // a splitter, a Bluetooth speaker or headphones). A Bluetooth watch or car
        // kit disconnecting does not send it, which is exactly the behaviour wanted;
        // do not swap this for a general Bluetooth-disconnect listener.
        var noisy = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterReceiver(_noisy, noisy, ReceiverFlags.NotExported);
        else
            RegisterReceiver(_noisy, noisy);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.GetStringExtra(ExtraText) is { } text) _text = text;

        // Must reach StartForeground within a few seconds of StartForegroundService,
        // every time, or the system kills the app. That includes the notification's
        // own play/pause buttons, which start the service to deliver their action.
        Notification notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(NotificationId, notification);
        _running = true;

        if (_wakeLock is null && GetSystemService(PowerService) is PowerManager power)
        {
            // The auto-cue runs on the UI thread's clock, not the audio callback, so
            // the CPU must stay up between audio buffers for transitions to happen.
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "MkbMixer:playback");
            _wakeLock?.Acquire();
        }

        switch (intent?.Action)
        {
            case ActionPause:
                Dispatcher.UIThread.Post(PauseFromSystem);
                Dispatcher.UIThread.Post(HoldIfSilent);
                break;
            case ActionPlay:
                Dispatcher.UIThread.Post(ResumeFromSystem);
                Dispatcher.UIThread.Post(HoldIfSilent);
                break;
            default:
                RequestFocus();
                break;
        }
        UpdateSession();

        // If the system kills the process the decks are gone with it; restarting
        // an empty service would only show a stale notification.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        _running = false;
        _pausedForCall = false;
        _instance = null;
        UnregisterReceiver(_noisy);
        if (_focusRequest is not null && GetSystemService(AudioService) is AudioManager audio)
            audio.AbandonAudioFocusRequest(_focusRequest);
        _session?.Release();
        _session = null;
        if (_wakeLock is { IsHeld: true }) _wakeLock.Release();
        _wakeLock = null;
        base.OnDestroy();
    }

    /// <summary>Playback restarted while the service was already up.</summary>
    private void OnResumed()
    {
        RequestFocus();
        Refresh();
    }

    /// <summary>Brings the notification and the media session up to date.</summary>
    private void Refresh()
    {
        UpdateSession();
        if (GetSystemService(NotificationService) is NotificationManager manager)
            manager.Notify(NotificationId, BuildNotification());
    }

    /// <summary>
    /// Asks for focus so another music app starting will pause us, and so a call
    /// will. <c>SetWillPauseWhenDucked(true)</c> stops Android lowering the mix for
    /// a notification sound on our behalf; <see cref="OnFocusChange"/> then decides.
    /// </summary>
    private void RequestFocus()
    {
        if (GetSystemService(AudioService) is not AudioManager audio) return;
        _focusRequest ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!)!
            .SetWillPauseWhenDucked(true)!
            .SetOnAudioFocusChangeListener(new FocusListener())!
            .Build();
        // The result is ignored on purpose: a refused request (mid-call, say) must
        // not stop the mix, because the DJ pressed play.
        audio.RequestAudioFocus(_focusRequest!);
    }

    private static void OnFocusChange(AudioFocus change)
    {
        switch (change)
        {
            case AudioFocus.Loss:
                // Another app started playing music. Stay paused until asked.
                _pausedForCall = false;
                PauseFromSystem();
                break;
            case AudioFocus.LossTransient when InCall():
                if (_watched?.PauseAll() == true)
                {
                    _held = true;
                    _pausedForCall = true;
                }
                break;
            case AudioFocus.Gain when _pausedForCall:
                ResumeFromSystem();
                break;
            // May-duck, and transient losses that are not a call (a voice note, a
            // video, a navigation prompt, a text alert): keep playing at full volume.
        }
    }

    private static bool InCall() =>
        global::Android.App.Application.Context.GetSystemService(AudioService) is AudioManager audio
        && audio.Mode is Mode.Ringtone or Mode.InCall or Mode.InCommunication;

    private void UpdateSession()
    {
        if (_session is null) return;
        bool playing = IsPlaying;
        _session.SetPlaybackState(new PlaybackState.Builder()
            .SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause)!
            .SetState(playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
                      PlaybackState.PlaybackPositionUnknown, 1f)!
            .Build());
        _session.SetMetadata(new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, string.IsNullOrEmpty(_text) ? "MKB Mixer" : _text)!
            .PutString(MediaMetadata.MetadataKeyArtist, "MKB Mixer")!
            .Build());
    }

    private Notification BuildNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) is null)
            manager.CreateNotificationChannel(
                new NotificationChannel(ChannelId, "Playback", NotificationImportance.Low)
                {
                    Description = "Shown while a deck is playing"
                });

        // The launcher intent brings the existing task forward rather than stacking
        // a second activity on top of it.
        Intent? open = PackageManager?.GetLaunchIntentForPackage(PackageName!);
        PendingIntent? tap = open is null
            ? null
            : PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        bool playing = IsPlaying;
        var toggle = new Notification.Action.Builder(
                Icon.CreateWithResource(this, playing
                    ? global::Android.Resource.Drawable.IcMediaPause
                    : global::Android.Resource.Drawable.IcMediaPlay),
                playing ? "Pause" : "Play",
                PendingIntent.GetForegroundService(this, playing ? 1 : 2,
                    new Intent(this, typeof(PlaybackService)).SetAction(playing ? ActionPause : ActionPlay),
                    PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent))
            .Build();

        string text = string.IsNullOrEmpty(_text) ? "Playing" : _text;
        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("MKB Mixer")!
            .SetContentText(playing ? text : $"Paused · {text}")!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetContentIntent(tap)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetCategory(Notification.CategoryTransport)!
            .AddAction(toggle)!
            .SetStyle(new Notification.MediaStyle()
                .SetMediaSession(_session?.SessionToken)!
                .SetShowActionsInCompactView(0))!
            .Build()!;
    }

    /// <summary>Lock screen, headset and Bluetooth buttons, and the system media panel.</summary>
    private sealed class SessionCallback : MediaSession.Callback
    {
        public override void OnPlay() => Dispatcher.UIThread.Post(ResumeFromSystem);
        public override void OnPause() => Dispatcher.UIThread.Post(PauseFromSystem);
    }

    private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) =>
            Dispatcher.UIThread.Post(() => OnFocusChange(focusChange));
    }

    private sealed class NoisyReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy)
                Dispatcher.UIThread.Post(PauseFromSystem);
        }
    }
}
