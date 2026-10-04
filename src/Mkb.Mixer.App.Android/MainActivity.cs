using Android;
using Android.App;
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
        RequestedOrientation = phone ? ScreenOrientation.SensorPortrait : ScreenOrientation.SensorLandscape;

        base.OnCreate(savedInstanceState);

        // Without this the library browser can list folders but no audio files.
        if (CheckSelfPermission(StoragePermission) != Permission.Granted)
            RequestPermissions([StoragePermission], StorageRequest);
    }

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != StorageRequest) return;

        // The tree was built before access was granted, so it is missing files.
        if (grantResults is [Permission.Granted, ..])
            Dispatcher.UIThread.Post(() => CurrentViewModel()?.LoadRoots());
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
}
