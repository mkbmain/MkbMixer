using System.Linq;
using SoundFlow.Abstracts;
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
    private readonly CueBus _cueBus = new();
    private AudioFormat _outputFormat = Format;
    private SoundModifier? _cueModifier;
    private AudioPlaybackDevice? _cueDevice;
    private readonly CueWatchdog _cueWatchdog = new();
    private string? _masterDeviceName;      // null with _masterIsDefault: opened "the default"
    private bool _masterIsDefault;
    private volatile float _cueMix;

    /// <summary>Public so the <c>--audio-info</c> diagnostic can probe with the same format.</summary>
    public static AudioFormat OutputFormat => Format;

    internal static readonly AudioFormat Format = new()
    {
        SampleRate = 44100,
        Channels = 2,
        Format = SampleFormat.F32,
        // Layout must be set explicitly. It defaults to Unknown, which decoding
        // tolerates but device initialisation rejects with FailedToOpenBackendDevice
        // — a silent-playback bug that only shows up on a machine with a real
        // sound card, since a machine without one fails for its own reasons.
        Layout = AudioFormat.GetLayoutFromChannels(2)
    };

    /// <summary>Formats to try, best first, if the preferred one is refused.</summary>
    private static AudioFormat[] CandidateFormats() =>
    [
        Format,
        AudioFormat.DvdHq,
        AudioFormat.Cd,
        new AudioFormat
        {
            SampleRate = 48000, Channels = 2, Format = SampleFormat.S16,
            Layout = AudioFormat.GetLayoutFromChannels(2)
        }
    ];

    /// <summary>Backends to try, in order, for the host platform.</summary>
    private static MiniAudioBackend[] PreferredBackends()
    {
        if (OperatingSystem.IsWindows())
            return [MiniAudioBackend.Wasapi, MiniAudioBackend.DirectSound, MiniAudioBackend.WinMm];
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            return [MiniAudioBackend.CoreAudio];
        if (OperatingSystem.IsAndroid())
            return [MiniAudioBackend.AAudio, MiniAudioBackend.OpenSl];
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
            }
            catch (Exception e)
            {
                _diagnostics.Add($"{backend}: context failed — {e.Message}");
                if (!OnlyDummyDevices) OutputError = e.Message;
                continue;
            }

            if (TryOpenDevice(engine, backend, out AudioPlaybackDevice? device, out string route))
            {
                _engine = engine;
                _device = device;
                IsOutputAvailable = true;
                OutputDescription = route;
                _diagnostics.Add($"{backend}: OK ({route})");
                break;
            }

            try { engine.Dispose(); } catch { /* nothing useful to do */ }
        }

        if (!IsOutputAvailable)
        {
            // Still construct the decks so the app starts and the user can browse
            // and build playlists; it just cannot make any noise.
            _engine ??= TryBareEngine();
            OutputError ??= "no usable audio backend";
        }

        _a = new SoundFlowDeck(DeckId.A, _engine, _device?.MasterMixer, _cueBus);
        _b = new SoundFlowDeck(DeckId.B, _engine, _device?.MasterMixer, _cueBus);
    }

    /// <summary>
    /// Devices that exist only to swallow audio. Opening one "succeeds" and then
    /// plays nothing, racing the playhead to the end of the track, so they are
    /// never an acceptable output.
    /// </summary>
    private static readonly string[] DummyDeviceMarkers =
    [
        "discard all samples",   // the ALSA/Pulse null device's own description
        "null",
        "dummy",
        "auto_null"
    ];

    public static bool IsDummyDevice(string? name) =>
        name is not null &&
        DummyDeviceMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Tries the default device, then each enumerated one, against each candidate
    /// format. "Default Device" can fail to resolve even when a real device is
    /// listed, so naming one explicitly is a necessary fallback. Dummy sinks are
    /// excluded entirely: they open happily and discard everything.
    /// </summary>
    private bool TryOpenDevice(
        MiniAudioEngine engine, MiniAudioBackend backend,
        out AudioPlaybackDevice? opened, out string route)
    {
        opened = null;
        route = string.Empty;

        DeviceInfo[] devices;
        try
        {
            engine.UpdateAudioDevicesInfo();
            devices = engine.PlaybackDevices;
        }
        catch (Exception e)
        {
            _diagnostics.Add($"{backend}: could not list devices — {e.Message}");
            devices = [];
        }

        foreach (DeviceInfo d in devices)
            _diagnostics.Add($"{backend}: sees device \"{d.Name}\"" +
                             (d.IsDefault ? " (default)" : "") +
                             (IsDummyDevice(d.Name) ? " [dummy — will not be used]" : ""));

        // MKB_DEVICE (set by --device=) forces a specific sink by name substring,
        // for when enumeration is unhelpful but a working device is known to exist.
        string? forcedName = Environment.GetEnvironmentVariable("MKB_DEVICE");
        if (!string.IsNullOrWhiteSpace(forcedName))
        {
            DeviceInfo[] matched = devices
                .Where(d => d.Name?.Contains(forcedName, StringComparison.OrdinalIgnoreCase) == true)
                .ToArray();
            if (matched.Length > 0)
            {
                _diagnostics.Add($"{backend}: forcing device matching \"{forcedName}\"");
                return TryTargets(engine, backend, matched.Select(d => (DeviceInfo?)d), out opened, out route);
            }
            _diagnostics.Add($"{backend}: no device matches \"{forcedName}\"");
        }

        DeviceInfo[] real = devices.Where(d => !IsDummyDevice(d.Name)).ToArray();
        if (real.Length == 0 && devices.Length > 0)
        {
            const string why = "only a dummy/null sink is available, so there is no real "
                             + "audio output. Check that PulseAudio or PipeWire is running "
                             + "and exposes a sink.";
            _diagnostics.Add($"{backend}: {why}");
            OutputError = why;
            OnlyDummyDevices = true;
            return false;
        }

        // null means "the default device"; the named ones are the fallbacks.
        var targets = new List<DeviceInfo?> { null };
        targets.AddRange(real.Where(d => d.IsDefault).Select(d => (DeviceInfo?)d));
        targets.AddRange(real.Where(d => !d.IsDefault).Select(d => (DeviceInfo?)d));

        return TryTargets(engine, backend, targets, out opened, out route);
    }

    /// <summary>Tries each candidate device against each candidate format.</summary>
    private bool TryTargets(
        MiniAudioEngine engine, MiniAudioBackend backend, IEnumerable<DeviceInfo?> targets,
        out AudioPlaybackDevice? opened, out string route)
    {
        opened = null;
        route = string.Empty;

        foreach (DeviceInfo? target in targets)
        {
            foreach (AudioFormat format in CandidateFormats())
            {
                try
                {
                    AudioPlaybackDevice device = engine.InitializePlaybackDevice(target, format);
                    device.Start();
                    opened = device;
                    _outputFormat = format;
                    _masterDeviceName = target?.Name;
                    _masterIsDefault = target is null;
                    route = $"{backend} — {target?.Name ?? "default"} "
                          + $"({format.SampleRate}Hz {format.Format})";
                    return true;
                }
                catch (Exception e)
                {
                    _diagnostics.Add(
                        $"{backend}/{target?.Name ?? "default"}/{format.SampleRate}Hz "
                        + $"{format.Format}: {e.Message}");
                    if (!OnlyDummyDevices) OutputError = e.Message;
                }
            }
        }

        return false;
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
    public bool IsOutputAvailable { get; private set; }
    public string? OutputError { get; private set; }
    public string? OutputDescription { get; private set; }
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    /// <summary>True when a backend worked but offered nothing except a null sink.</summary>
    public bool OnlyDummyDevices { get; private set; }

    public CueMode CueMode { get; private set; }
    public string? CueDevice { get; private set; }

    public float CueMix
    {
        get => _cueMix;
        set => _cueMix = Math.Clamp(value, 0f, 1f);
    }

    public IReadOnlyList<string> CueDeviceNames()
    {
        if (_engine is null) return [];
        try
        {
            _engine.UpdateAudioDevicesInfo();
            return _engine.PlaybackDevices
                .Where(d => !IsDummyDevice(d.Name))
                .Where(d => !CueDeviceRules.IsMasterOutput(d.Name, d.IsDefault, _masterDeviceName, _masterIsDefault))
                .Select(d => d.Name)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public bool TrySetCue(CueMode mode, string? deviceName, out string? error)
    {
        error = null;
        TearDownCue();
        if (mode == CueMode.Off) return true;

        if (!IsOutputAvailable || _engine is null || _device is null)
        {
            error = "there is no audio output";
            return false;
        }

        if (mode == CueMode.Split)
        {
            _cueModifier = new CueSplitModifier(_cueBus, () => _cueMix);
            _device.MasterMixer.AddModifier(_cueModifier);
            _cueBus.Active = true;
            CueMode = CueMode.Split;
            return true;
        }

        DeviceInfo[] matches;
        try
        {
            _engine.UpdateAudioDevicesInfo();
            matches = _engine.PlaybackDevices.Where(d => d.Name == deviceName).ToArray();
            if (matches.Any(d => CueDeviceRules.IsMasterOutput(d.Name, d.IsDefault, _masterDeviceName, _masterIsDefault)))
            {
                // The headphone feed on the room's own output would be heard by the room.
                error = $"\"{deviceName}\" is the room output, not a headphone output";
                return false;
            }
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
        if (matches.Length == 0)
        {
            error = $"cue device \"{deviceName}\" not found";
            return false;
        }

        try
        {
            // The cue device runs at the master's format, so the ring carries
            // samples at the rate the cue device consumes them.
            var ring = new CueRingBuffer(
                capacity: _outputFormat.SampleRate / 5 * _outputFormat.Channels,    // 200 ms
                highWater: _outputFormat.SampleRate / 20 * _outputFormat.Channels,  // 50 ms
                channels: _outputFormat.Channels);
            AudioPlaybackDevice cue = _engine.InitializePlaybackDevice(matches[0], _outputFormat);
            _cueDevice = cue;
            cue.MasterMixer.AddComponent(new CueSource(_engine, _outputFormat, ring, _cueWatchdog));
            _cueWatchdog.Arm(Environment.TickCount64);
            cue.Start();

            _cueModifier = new CueFeedModifier(_cueBus, ring, () => _cueMix);
            _device.MasterMixer.AddModifier(_cueModifier);
            _cueBus.Active = true;
            CueMode = CueMode.Device;
            CueDevice = matches[0].Name;
            return true;
        }
        catch (Exception e)
        {
            TearDownCue();
            error = e.Message;
            return false;
        }
    }

    public string? CueFault =>
        CueMode == CueMode.Device && _cueWatchdog.IsStalled(Environment.TickCount64)
            ? $"cue device \"{CueDevice}\" stopped responding"
            : null;

    private void TearDownCue()
    {
        _cueWatchdog.Disarm();
        _cueBus.Active = false;
        if (_cueModifier is not null)
        {
            _device?.MasterMixer.RemoveModifier(_cueModifier);
            _cueModifier = null;
        }
        if (_cueDevice is not null)
        {
            try { _cueDevice.Dispose(); } catch { /* already gone, e.g. unplugged */ }
            _cueDevice = null;
        }
        CueMode = CueMode.Off;
        CueDevice = null;
    }

    public void ApplyCrossfader(float position)
    {
        var (gainA, gainB) = Crossfader.Gains(position);
        _a.Volume = gainA;
        _b.Volume = gainB;
    }

    public Task<TrackAnalysis> AnalyseAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (_engine is null) return TrackAnalysis.Empty;
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

                return TrackAnalysis.FromSamples(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples),
                    Format.Channels,
                    Format.SampleRate);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return TrackAnalysis.Empty;
            }
        }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TearDownCue();
        _a.Dispose();
        _b.Dispose();
        _device?.Dispose();
        _engine?.Dispose();
    }
}
