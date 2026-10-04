using System;
using System.ComponentModel;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Threading;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Android;

/// <summary>
/// Keeps the process alive while a deck is playing. Without it Android freezes or
/// kills the app shortly after it leaves the screen, which silences the mix and
/// stops the auto-cue clock that drives the next transition.
/// </summary>
/// <remarks>
/// The decks and the engine live in <see cref="MainViewModel"/>, not here: this
/// service only exists to tell the system that audio is playing, and to show the
/// notification a foreground service must carry.
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackService : Service
{
    private const string ChannelId = "playback";
    private const int NotificationId = 1;
    private const string ExtraText = "text";

    /// <summary>
    /// How long both decks must be silent before the service stops. Android 12+
    /// refuses to start a foreground service from the background, so stopping in
    /// the gap between one track ending and the next starting would leave the next
    /// one unprotected.
    /// </summary>
    private static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(10);

    private static MainViewModel? _watched;
    private static IDisposable? _pendingStop;
    private static bool _running;

    private PowerManager.WakeLock? _wakeLock;

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
                Start(vm);
                break;
            case nameof(MainViewModel.IsAnyDeckPlaying):
                _pendingStop ??= DispatcherTimer.RunOnce(Stop, StopDelay);
                break;
            case nameof(MainViewModel.PlayingSummary) when _running && vm.IsAnyDeckPlaying:
                Start(vm);   // a started service just refreshes its notification
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
        if (!_running) return;
        _running = false;
        Context context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(PlaybackService)));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Must reach StartForeground within a few seconds of StartForegroundService,
        // every time, or the system kills the app.
        Notification notification = BuildNotification(intent?.GetStringExtra(ExtraText));
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(NotificationId, notification);

        if (_wakeLock is null && GetSystemService(PowerService) is PowerManager power)
        {
            // The auto-cue runs on the UI thread's clock, not the audio callback, so
            // the CPU must stay up between audio buffers for transitions to happen.
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "MkbMixer:playback");
            _wakeLock?.Acquire();
        }

        // If the system kills the process the decks are gone with it; restarting
        // an empty service would only show a stale notification.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        _running = false;
        if (_wakeLock is { IsHeld: true }) _wakeLock.Release();
        _wakeLock = null;
        base.OnDestroy();
    }

    private Notification BuildNotification(string? text)
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

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("MKB Mixer")!
            .SetContentText(string.IsNullOrEmpty(text) ? "Playing" : text)!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetContentIntent(tap)!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetCategory(Notification.CategoryTransport)!
            .Build()!;
    }
}
