namespace Mkb.Mixer.Audio;

/// <summary>Everything one decode of a track tells us.</summary>
/// <param name="BeatOffset">Where the first beat falls. Stored for phase sync; nothing uses it yet.</param>
public sealed record TrackAnalysis(
    Waveform Waveform,
    double? Bpm,
    TimeSpan? BeatOffset,
    TimeSpan? FirstSound,
    TimeSpan? LastSound)
{
    /// <summary>The file could not be decoded. Compared by reference, so never cached.</summary>
    public static TrackAnalysis Empty { get; } = new(Waveform.Empty, null, null, null, null);

    public static TrackAnalysis FromSamples(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        Waveform wave = Waveform.FromSamples(interleaved, channels, Waveform.DefaultBuckets);
        AnalysisSignal signal = AnalysisSignal.From(interleaved, channels, sampleRate);
        var (bpm, offset) = TempoDetector.Detect(signal);
        var (first, last) = SilenceDetector.Detect(signal);
        return new TrackAnalysis(wave, bpm, offset, first, last);
    }
}
