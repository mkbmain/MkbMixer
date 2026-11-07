namespace Mkb.Mixer.Audio;

/// <summary>
/// Builds a <see cref="TrackAnalysis"/> from decoded audio fed in chunks, so a
/// track never has to be held whole at the output rate.
/// </summary>
/// <remarks>
/// A full decode at 44.1 kHz stereo float is about 21 MB a minute, and background
/// library analysis does one after another; on a phone that is enough to get the
/// app killed mid-set. All the detectors and the waveform need is the energy and peak
/// of each hop and the energy of each window, of the mono, decimated signal, so only
/// those are kept.
/// </remarks>
public sealed class TrackAnalyser
{
    private readonly int _block;
    private readonly int _rate;
    private readonly int _hop;
    private readonly int _window;

    // What the detectors and waveform need, one entry per hop or window, instead of
    // the whole decimated signal: about 2 MB for eight minutes against 60 MB.
    private readonly List<double> _hopEnergy = [];
    private readonly List<double> _windowEnergy = [];
    private readonly List<float> _hopPeaks = [];

    private float _sum;
    private int _filled;

    private double _hopSum, _windowSum;
    private float _hopPeak;
    private int _inHop, _inWindow;

    /// <param name="expectedSamples">
    /// The decoder's estimate of the interleaved sample count, if it has one. Sizing
    /// the lists once avoids the copies and slack of growing them as the track
    /// decodes; a wrong estimate costs only that.
    /// </param>
    public TrackAnalyser(int channels, int sampleRate, long expectedSamples = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        int factor = AnalysisSignal.DecimationFactor(sampleRate);
        _block = factor * channels;
        _rate = sampleRate / factor;
        _hop = TempoDetector.HopSamples(_rate);
        _window = SilenceDetector.WindowSamples(_rate);

        // Capped at four hours, so a corrupt header cannot ask for gigabytes.
        long mono = Math.Clamp(expectedSamples / _block + 1, 0, (long)_rate * 4 * 3600);
        _hopEnergy.Capacity = (int)(mono / _hop + 1);
        _hopPeaks.Capacity = _hopEnergy.Capacity;
        _windowEnergy.Capacity = (int)(mono / _window + 1);
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
            if (magnitude > _hopPeak) _hopPeak = magnitude;
            if (++_filled < _block) continue;

            float mono = _sum / _block;
            _sum = 0f;
            _filled = 0;

            double square = (double)mono * mono;
            _hopSum += square;
            _windowSum += square;

            if (++_inHop == _hop)
            {
                _hopEnergy.Add(_hopSum);
                _hopPeaks.Add(_hopPeak);
                _hopSum = 0;
                _hopPeak = 0f;
                _inHop = 0;
            }
            if (++_inWindow == _window)
            {
                _windowEnergy.Add(_windowSum);
                _windowSum = 0;
                _inWindow = 0;
            }
        }
    }

    /// <summary>Runs the detectors over everything fed so far. A trailing part-block is dropped.</summary>
    public TrackAnalysis Finish()
    {
        // One peak per hop is a mono signal of magnitudes, so the bucketing is the
        // same as for raw samples.
        Waveform wave = Waveform.FromSamples(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hopPeaks), channels: 1, Waveform.DefaultBuckets);
        var (bpm, offset) = TempoDetector.Detect(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_hopEnergy), _rate);
        var (first, last) = SilenceDetector.Detect(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_windowEnergy), _rate);
        return new TrackAnalysis(wave, bpm, offset, first, last);
    }
}
