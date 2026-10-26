namespace Mkb.Mixer.Audio;

/// <summary>
/// Builds a <see cref="TrackAnalysis"/> from decoded audio fed in chunks, so a
/// track never has to be held whole at the output rate.
/// </summary>
/// <remarks>
/// A full decode at 44.1 kHz stereo float is about 21 MB a minute, and background
/// library analysis does one after another; on a phone that is enough to get the
/// app killed mid-set. Everything the detectors and the waveform need fits in the
/// mono, decimated signal plus a bounded set of peaks, about a sixteenth of that.
/// </remarks>
public sealed class TrackAnalyser
{
    /// <summary>
    /// Most peaks held. Past this, neighbouring peaks are merged and each then
    /// covers twice as many blocks: a six-minute track ends up at 16 blocks
    /// (1.5 ms) a peak, a megabyte instead of sixteen. A waveform bucket is still
    /// over a hundred peaks wide, so only its edges can move, by a millisecond or so.
    /// </summary>
    internal const int PeakCapacity = 1 << 18;

    private readonly int _block;
    private readonly int _rate;
    private float[] _mono;
    private int _monoCount;
    private float[] _peaks;
    private int _peakCount;
    private int _blocksPerPeak = 1;
    private int _blocksInPeak;
    private float _runPeak;        // max over the blocks of the peak being built
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
        long capacity = Math.Clamp(expectedSamples / _block + 1, 1, (long)_rate * 4 * 3600);
        _mono = new float[capacity];
        _peaks = new float[Math.Min(capacity, PeakCapacity)];
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

            // Grown by hand rather than a List, so Finish can lend the filled part
            // to the detectors instead of copying it.
            if (_monoCount == _mono.Length) Array.Resize(ref _mono, _mono.Length * 2);
            _mono[_monoCount++] = _sum / _block;
            if (_peak > _runPeak) _runPeak = _peak;
            if (++_blocksInPeak == _blocksPerPeak) PushPeak();
            _sum = 0f;
            _peak = 0f;
            _filled = 0;
        }
    }

    /// <param name="final">Push the run even if it is short; only at the end of the track.</param>
    private void PushPeak(bool final = false)
    {
        if (_peakCount == _peaks.Length)
        {
            if (_peaks.Length < PeakCapacity)
                Array.Resize(ref _peaks, Math.Min(_peaks.Length * 2, PeakCapacity));
            else
            {
                // Full: halve the resolution in place. Each slot stays the max of an
                // aligned run of blocks, so the result does not depend on chunking.
                for (int i = 0; i < _peakCount / 2; i++)
                    _peaks[i] = Math.Max(_peaks[2 * i], _peaks[2 * i + 1]);
                _peakCount /= 2;
                _blocksPerPeak *= 2;
                // The run in hand is now half a slot; it carries on to fill it.
                if (!final) return;
            }
        }
        _peaks[_peakCount++] = _runPeak;
        _runPeak = 0f;
        _blocksInPeak = 0;
    }

    /// <summary>Runs the detectors over everything fed so far. A trailing part-block is dropped.</summary>
    public TrackAnalysis Finish()
    {
        // The last few whole blocks still show, even short of a full run.
        if (_blocksInPeak > 0) PushPeak(final: true);

        // One peak per run of blocks is a mono signal of magnitudes, so the
        // bucketing is the same as for raw samples.
        Waveform wave = Waveform.FromSamples(_peaks.AsSpan(0, _peakCount), channels: 1, Waveform.DefaultBuckets);
        var signal = new AnalysisSignal(_mono.AsMemory(0, _monoCount), _rate);
        var (bpm, offset) = TempoDetector.Detect(signal);
        var (first, last) = SilenceDetector.Detect(signal);
        return new TrackAnalysis(wave, bpm, offset, first, last);
    }
}
