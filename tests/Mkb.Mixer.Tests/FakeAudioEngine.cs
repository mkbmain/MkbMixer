using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

/// <summary>An <see cref="IAudioEngine"/> with no sound card behind it.</summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    public FakeDeck A { get; } = new(DeckId.A);
    public FakeDeck B { get; } = new(DeckId.B);

    public IDeck DeckA => A;
    public IDeck DeckB => B;
    public bool IsOutputAvailable => true;
    public string? OutputError => null;

    public float LastCrossfader { get; private set; } = 0.5f;

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
