namespace Mkb.Mixer.Audio;

/// <summary>
/// Builds a <see cref="TrackAnalysis"/> from decoded audio fed in chunks, so a
/// track never has to be held whole at the output rate.
/// </summary>
/// <remarks>
/// A full decode at 44.1 kHz stereo float is about 21 MB a minute, and background
/// library analysis does one after another; on a phone that is enough to get the
/// app killed mid-set. Everything the detectors and the waveform need fits in the
/// mono, decimated signal plus one peak per decimation block, about an eighth of
/// that.
/// </remarks>
public sealed class TrackAnalyser
{
    private readonly int _block;
    private readonly int _rate;
    private readonly List<float> _mono;
    private readonly List<float> _peaks;
    private float _sum;
    private float _peak;
    private int _filled;

    /// <param name="expectedSamples">
    /// The decoder's estimate of the interleaved sample count, if it has one. Sizing
    /// the buffers once avoids the copies and slack of growing them as the track
    /// decodes; a wrong estimate costs only that.
    /// </param>
    public TrackAnalyser(int channels, int sampleRate, long expectedSamples = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        int factor = AnalysisSignal.DecimationFactor(sampleRate);
        _block = factor * channels;
        _rate = sampleRate / factor;

        // Capped at four hours, so a corrupt header cannot ask for gigabytes.
        long capacity = Math.Clamp(expectedSamples / _block + 1, 0, (long)_rate * 4 * 3600);
        _mono = new List<float>((int)capacity);
        _peaks = new List<float>((int)capacity);
    }

    /// <summary>Takes the next run of interleaved samples. Chunks may split a frame.</summary>
    public void Add(ReadOnlySpan<float> interleaved)
    {
        foreach (float s in interleaved)
        {
            // Summed in the same order as AnalysisSignal.From, so the detectors see
            // exactly what they would after a full decode.
            _sum += s;
            float magnitude = Math.Abs(s);
            if (magnitude > _peak) _peak = magnitude;
            if (++_filled < _block) continue;

            _mono.Add(_sum / _block);
            _peaks.Add(_peak);
            _sum = 0f;
            _peak = 0f;
            _filled = 0;
        }
    }

    /// <summary>Runs the detectors over everything fed so far. A trailing part-block is dropped.</summary>
    public TrackAnalysis Finish()
    {
        // One peak per block is a mono signal of magnitudes, so the bucketing is the
        // same as for raw samples.
        Waveform wave = Waveform.FromSamples(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_peaks), channels: 1, Waveform.DefaultBuckets);
        var signal = new AnalysisSignal(_mono.ToArray(), _rate);
        var (bpm, offset) = TempoDetector.Detect(signal);
        var (first, last) = SilenceDetector.Detect(signal);
        return new TrackAnalysis(wave, bpm, offset, first, last);
    }
}
