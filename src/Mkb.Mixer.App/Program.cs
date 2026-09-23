using Avalonia;
using System;
using System.Linq;
using Mkb.Mixer.Audio;
using System.Collections.Generic;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Structs;
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

                Console.WriteLine();
                foreach (var d in devices)
                    Console.WriteLine($"               {(d.IsDefault ? "*" : " ")} {d.Name}");

                // Probe the default device and each named one, against each format,
                // because "Default Device" can fail to resolve even when real
                // devices are listed.
                var targets = new List<DeviceInfo?> { null };
                targets.AddRange(devices.Select(d => (DeviceInfo?)d));

                foreach (var target in targets)
                foreach (var fmt in Formats())
                {
                    string label = $"               {target?.Name ?? "<default>"} "
                                 + $"@ {fmt.SampleRate}Hz {fmt.Format} {fmt.Layout}";
                    try
                    {
                        using var device = engine.InitializePlaybackDevice(target, fmt);
                        device.Start();
                        device.Stop();
                        Console.WriteLine($"{label}  OPENS ✓");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{label}  failed: {ex.Message}");
                    }
                }
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
        Console.WriteLine("The app picks the first backend/device/format combination that opens.");
        Console.WriteLine("Force one with:  dotnet run --project src/Mkb.Mixer.App -- --backend=PulseAudio");
        return 0;
    }

    private static AudioFormat[] Formats() =>
    [
        SoundFlowAudioEngine.OutputFormat,
        AudioFormat.DvdHq,
        AudioFormat.Cd
    ];

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
