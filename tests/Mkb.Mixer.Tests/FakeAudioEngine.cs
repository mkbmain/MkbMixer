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

    /// <summary>Returns a recognisable synthetic waveform so renders are deterministic.</summary>
    public Task<Waveform> AnalyseAsync(string path, CancellationToken ct = default)
    {
        var peaks = new float[Waveform.DefaultBuckets];
        for (int i = 0; i < peaks.Length; i++)
            peaks[i] = 0.35f + 0.55f * MathF.Abs(MathF.Sin(i * 0.012f)) * (0.6f + 0.4f * MathF.Sin(i * 0.0013f));
        return Task.FromResult(new Waveform(peaks));
    }

    public void Dispose() { }
}
