using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using System;
using Android.OS;
using Avalonia.Android;
using Avalonia.Threading;

namespace Mkb.Mixer.App.Android;

/// <summary>
/// A tablet gets two decks side by side over a library browser, which only fits
/// held sideways, so it is locked to landscape. A phone gets a tabbed layout
/// instead, locked upright.
/// </summary>
[Activity(
    Label = "MKB Mixer",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int StorageRequest = 1;

    private static string StoragePermission =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ? Manifest.Permission.ReadMediaAudio
            : Manifest.Permission.ReadExternalStorage;

    /// <summary>Android's own phone/tablet boundary: 600dp on the shortest side.</summary>
    private const int TabletSmallestWidthDp = 600;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Decided before base.OnCreate, which is where Avalonia builds the view.
        bool phone = Resources?.Configuration?.SmallestScreenWidthDp < TabletSmallestWidthDp;
        App.UsePhoneLayout = phone;
        App.PlatformRoots = () => StorageVolumes.List(ApplicationContext!);
        RequestedOrientation = phone ? ScreenOrientation.SensorPortrait : ScreenOrientation.SensorLandscape;

        base.OnCreate(savedInstanceState);

        // An SD card or USB drive going in or out changes the browser's roots.
        var volumes = new IntentFilter();
        volumes.AddAction(Intent.ActionMediaMounted);
        volumes.AddAction(Intent.ActionMediaUnmounted);
        volumes.AddAction(Intent.ActionMediaRemoved);
        volumes.AddAction(Intent.ActionMediaEject);
        volumes.AddDataScheme("file");
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            RegisterReceiver(_volumeChanged, volumes, ReceiverFlags.NotExported);
        else
            RegisterReceiver(_volumeChanged, volumes);

        RequestMissingPermissions();
    }

    private readonly VolumeChangedReceiver _volumeChanged = new();

    protected override void OnDestroy()
    {
        UnregisterReceiver(_volumeChanged);
        base.OnDestroy();
    }

    private void RequestMissingPermissions()
    {
        // Storage: without it the library browser lists folders but no audio files.
        // Notifications (13+): without it the playback service still keeps the mix
        // going with the screen off, but its notification is hidden.
        string[] wanted = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? [StoragePermission, Manifest.Permission.PostNotifications]
            : [StoragePermission];
        string[] missing = Array.FindAll(wanted, p => CheckSelfPermission(p) != Permission.Granted);
        if (missing.Length > 0)
            RequestPermissions(missing, StorageRequest);
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != StorageRequest) return;

        // The tree was built before access was granted, so it is missing files.
        int storage = Array.IndexOf(permissions, StoragePermission);
        if (storage >= 0 && grantResults[storage] == Permission.Granted)
            Dispatcher.UIThread.Post(() => CurrentViewModel()?.LoadRoots());
    }

    protected override void OnResume()
    {
        base.OnResume();
        // By now Avalonia has built the view, and so the view model, for certain.
        if (CurrentViewModel() is { } vm)
            PlaybackService.Watch(vm);
    }

    protected override void OnPause()
    {
        // There is no reliable "closing" moment on Android: a paused app may be
        // killed without warning, so this is the last safe point to save.
        CurrentViewModel()?.SaveState();
        base.OnPause();
    }

    private static ViewModels.MainViewModel? CurrentViewModel() =>
        (Avalonia.Application.Current as App)?.ViewModel;

    private sealed class VolumeChangedReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) =>
            Dispatcher.UIThread.Post(() => CurrentViewModel()?.LoadRoots());
    }
}
