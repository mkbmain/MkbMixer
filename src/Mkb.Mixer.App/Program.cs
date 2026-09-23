using Avalonia;
using System;
using System.Linq;
using Mkb.Mixer.Audio;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;

namespace Mkb.Mixer.App;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--audio-info"))
            return AudioInfo();

        if (args.FirstOrDefault(a => a.StartsWith("--backend=", StringComparison.Ordinal))
            is { } forced)
        {
            Environment.SetEnvironmentVariable("MKB_BACKEND", forced["--backend=".Length..]);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Prints which audio backends this machine actually offers and which one can
    /// open a device. Run with <c>--audio-info</c> when playback is silent.
    /// </summary>
    private static int AudioInfo()
    {
        Console.WriteLine($"Platform: {Environment.OSVersion}");
        Console.WriteLine($"miniaudio compiled-in backends: " +
                          string.Join(", ", MiniAudioEngine.AvailableBackends));
        Console.WriteLine();

        foreach (MiniAudioBackend backend in MiniAudioEngine.AvailableBackends)
        {
            Console.Write($"{backend,-12} ");
            MiniAudioEngine? engine = null;
            try
            {
                engine = new MiniAudioEngine([backend]);
                Console.Write("context OK, ");
                engine.UpdateAudioDevicesInfo();
                var devices = engine.PlaybackDevices;
                Console.Write($"{devices.Length} playback device(s); ");

                using var device = engine.InitializePlaybackDevice(null, SoundFlowAudioEngine.OutputFormat);
                device.Start();
                device.Stop();
                Console.WriteLine("DEVICE OPENS ✓");
                foreach (var d in devices)
                    Console.WriteLine($"                 {(d.IsDefault ? "*" : " ")} {d.Name}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"FAILED: {e.Message}");
            }
            finally
            {
                try { engine?.Dispose(); } catch { }
            }
        }

        Console.WriteLine();
        Console.WriteLine("The app picks the first backend whose device opens.");
        Console.WriteLine("Force one with:  dotnet run --project src/Mkb.Mixer.App -- --backend=PulseAudio");
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
