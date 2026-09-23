using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Mkb.Mixer.Audio;

/// <summary>
/// <see cref="IAudioEngine"/> on top of SoundFlow's miniaudio backend, which ships
/// natives for Windows, macOS, Linux, FreeBSD, Android and iOS.
/// </summary>
public sealed class SoundFlowAudioEngine : IAudioEngine
{
    private readonly MiniAudioEngine? _engine;
    private readonly AudioPlaybackDevice? _device;
    private readonly SoundFlowDeck _a;
    private readonly SoundFlowDeck _b;
    private readonly List<string> _diagnostics = [];
    private bool _disposed;

    /// <summary>Public so the <c>--audio-info</c> diagnostic can probe with the same format.</summary>
    public static AudioFormat OutputFormat => Format;

    internal static readonly AudioFormat Format = new()
    {
        SampleRate = 44100,
        Channels = 2,
        Format = SampleFormat.F32
    };

    /// <summary>Backends to try, in order, for the host platform.</summary>
    private static MiniAudioBackend[] PreferredBackends()
    {
        if (OperatingSystem.IsWindows())
            return [MiniAudioBackend.Wasapi, MiniAudioBackend.DirectSound, MiniAudioBackend.WinMm];
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            return [MiniAudioBackend.CoreAudio];
        return [MiniAudioBackend.PulseAudio, MiniAudioBackend.Jack, MiniAudioBackend.Alsa];
    }

    public SoundFlowAudioEngine() : this(ConfiguredBackends()) { }

    /// <summary>
    /// Honours MKB_BACKEND (set by <c>--backend=</c>) so a user whose system picks
    /// the wrong backend can force the right one without a rebuild.
    /// </summary>
    private static MiniAudioBackend[] ConfiguredBackends()
    {
        string? forced = Environment.GetEnvironmentVariable("MKB_BACKEND");
        if (!string.IsNullOrWhiteSpace(forced) &&
            Enum.TryParse(forced, ignoreCase: true, out MiniAudioBackend backend))
            return [backend];
        return PreferredBackends();
    }

    /// <param name="backends">Backends to try in order. Exposed so a user can force one.</param>
    public SoundFlowAudioEngine(IReadOnlyList<MiniAudioBackend> backends)
    {
        // Each backend is tried on its own, because a backend whose *context*
        // initialises can still fail to open a *device*. That is exactly what
        // happens on a PulseAudio or PipeWire system: ALSA's context opens
        // because libasound is installed, then the device open fails with
        // "unable to open slave" because the sound server already holds the card.
        // Passing a priority list to miniaudio only covers the context step, so
        // the fallback has to happen out here.
        foreach (MiniAudioBackend backend in backends)
        {
            MiniAudioEngine? engine = null;
            try
            {
                engine = new MiniAudioEngine([backend]);
                AudioPlaybackDevice device = engine.InitializePlaybackDevice(null, Format);
                device.Start();

                _engine = engine;
                _device = device;
                IsOutputAvailable = true;

                string name = DefaultDeviceName(engine);
                OutputDescription = $"{backend} — {name}";
                _diagnostics.Add($"{backend}: OK ({name})");
                break;
            }
            catch (Exception e)
            {
                _diagnostics.Add($"{backend}: {e.Message}");
                OutputError = e.Message;
                try { engine?.Dispose(); } catch { /* nothing useful to do */ }
            }
        }

        if (!IsOutputAvailable)
        {
            // Still construct the decks so the app starts and the user can browse
            // and build playlists; it just cannot make any noise.
            _engine ??= TryBareEngine();
            OutputError ??= "no usable audio backend";
        }

        _a = new SoundFlowDeck(DeckId.A, _engine, _device?.MasterMixer);
        _b = new SoundFlowDeck(DeckId.B, _engine, _device?.MasterMixer);
    }

    /// <summary>A context with no device, so waveform analysis still works without output.</summary>
    private MiniAudioEngine? TryBareEngine()
    {
        foreach (MiniAudioBackend backend in PreferredBackends())
        {
            try { return new MiniAudioEngine([backend]); }
            catch { /* try the next one */ }
        }
        return null;
    }

    private static string DefaultDeviceName(MiniAudioEngine engine)
    {
        try
        {
            engine.UpdateAudioDevicesInfo();
            DeviceInfo[] devices = engine.PlaybackDevices;
            foreach (DeviceInfo d in devices)
                if (d.IsDefault) return d.Name;
            return devices.Length > 0 ? devices[0].Name : "default device";
        }
        catch
        {
            return "default device";
        }
    }

    public IDeck DeckA => _a;
    public IDeck DeckB => _b;
    public bool IsOutputAvailable { get; }
    public string? OutputError { get; }
    public string? OutputDescription { get; }
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public void ApplyCrossfader(float position)
    {
        var (gainA, gainB) = Crossfader.Gains(position);
        _a.Volume = gainA;
        _b.Volume = gainB;
    }

    public Task<Waveform> AnalyseAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (_engine is null) return Waveform.Empty;
            try
            {
                using var stream = File.OpenRead(path);
                var provider = new StreamDataProvider(_engine, Format, stream);

                var samples = new List<float>(capacity: 1 << 20);
                var buffer = new float[16384];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = provider.ReadBytes(buffer);
                    if (read <= 0) break;
                    samples.AddRange(buffer.AsSpan(0, read));
                }

                return Waveform.FromSamples(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples),
                    Format.Channels,
                    Waveform.DefaultBuckets);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return Waveform.Empty;
            }
        }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _a.Dispose();
        _b.Dispose();
        _device?.Dispose();
        _engine?.Dispose();
    }
}
