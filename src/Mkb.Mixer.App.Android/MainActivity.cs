using Android;
using Android.App;
using Android.Content.PM;
using System;
using Android.OS;
using Avalonia.Android;
using Avalonia.Threading;

namespace Mkb.Mixer.App.Android;

/// <summary>
/// The layout is two decks side by side over a library browser, which only fits a
/// tablet held sideways, so the activity is locked to landscape.
/// </summary>
[Activity(
    Label = "MKB Mixer",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int StorageRequest = 1;

    private static string StoragePermission =>
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ? Manifest.Permission.ReadMediaAudio
            : Manifest.Permission.ReadExternalStorage;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
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
