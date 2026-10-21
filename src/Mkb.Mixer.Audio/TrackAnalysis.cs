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

    /// <summary>Analyses samples already in memory; a decode should feed a <see cref="TrackAnalyser"/> instead.</summary>
    public static TrackAnalysis FromSamples(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        var analyser = new TrackAnalyser(channels, sampleRate);
        analyser.Add(interleaved);
        return analyser.Finish();
    }
}
