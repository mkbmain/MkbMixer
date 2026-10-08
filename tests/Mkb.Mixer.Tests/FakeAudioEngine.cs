using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

/// <summary>An <see cref="IAudioEngine"/> with no sound card behind it.</summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    public FakeDeck A { get; } = new(DeckId.A);
    public FakeDeck B { get; } = new(DeckId.B);

    public IDeck DeckA => A;
    public IDeck DeckB => B;
    public bool IsOutputAvailable { get; set; } = true;
    public string? OutputError { get; set; }
    public string? OutputDescription => "Fake — test device";
    public IReadOnlyList<string> Diagnostics => ["Fake: OK"];

    public float LastCrossfader { get; private set; } = 0.5f;

    public CueMode CueMode { get; private set; }
    public string? CueDevice { get; private set; }
    public float CueMix { get; set; }
    public string? CueFault { get; set; }
    public List<string> CueDeviceList { get; } = ["Fake headphones", "Fake USB"];

    /// <summary>When set, every mode except Off fails with this message.</summary>
    public string? CueFailure { get; set; }

    public IReadOnlyList<string> CueDeviceNames() => CueDeviceList;

    public bool TrySetCue(CueMode mode, string? deviceName, out string? error)
    {
        error = mode == CueMode.Off ? null
            : CueFailure
              ?? (mode == CueMode.Device && !CueDeviceList.Contains(deviceName ?? "")
                  ? $"cue device \"{deviceName}\" not found"
                  : null);
        bool ok = error is null;
        CueMode = ok ? mode : CueMode.Off;
        CueDevice = ok && mode == CueMode.Device ? deviceName : null;
        return ok;
    }

    public void ApplyCrossfader(float position)
    {
        LastCrossfader = position;
        var (a, b) = Crossfader.Gains(position);
        A.Volume = a;
        B.Volume = b;
    }

    /// <summary>Per-path results. Anything not listed gets <see cref="Analysis"/> with no BPM.</summary>
    public Dictionary<string, TrackAnalysis> Analyses { get; } = [];

    /// <summary>Paths whose analysis throws, as a broken decoder might.</summary>
    public HashSet<string> Throwing { get; } = [];

    /// <summary>Every path analysed, in order.</summary>
    public List<string> AnalysedPaths { get; } = [];

    /// <summary>A recognisable synthetic waveform, so renders are deterministic, plus the given numbers.</summary>
    public static TrackAnalysis Analysis(double? bpm = null, double? first = null, double? last = null)
    {
        var peaks = new float[Waveform.DefaultBuckets];
        for (int i = 0; i < peaks.Length; i++)
            peaks[i] = 0.35f + 0.55f * MathF.Abs(MathF.Sin(i * 0.012f)) * (0.6f + 0.4f * MathF.Sin(i * 0.0013f));
        return new TrackAnalysis(new Waveform(peaks), bpm, bpm is null ? null : TimeSpan.Zero,
            first is null ? null : TimeSpan.FromSeconds(first.Value),
            last is null ? null : TimeSpan.FromSeconds(last.Value));
    }

    public Task<TrackAnalysis> AnalyseAsync(string path, CancellationToken ct = default)
    {
        lock (AnalysedPaths) AnalysedPaths.Add(path);
        if (Throwing.Contains(path)) throw new InvalidOperationException($"cannot decode {path}");
        return Task.FromResult(Analyses.TryGetValue(path, out TrackAnalysis? a) ? a : Analysis());
    }

    public void Dispose() { }
}
