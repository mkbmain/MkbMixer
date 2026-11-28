namespace Mkb.Mixer.Audio;

/// <summary>Estimates a track's tempo and where its first beat falls.</summary>
/// <remarks>
/// Builds an onset envelope (frame-to-frame rises in log energy, which picks out
/// drum hits), autocorrelates it, then scores each candidate BPM on its first
/// <see cref="Harmonics"/> beat multiples. Scoring the multiples sharpens the
/// estimate to well under one 10 ms frame. The result is folded into one octave
/// because autocorrelation cannot tell 75 from 150; the deck's x1/2 and x2 fix
/// the rare track that lands in the wrong one.
/// </remarks>
public static class TempoDetector
{
    public const double MinBpm = 70, MaxBpm = 180;

    /// <summary>Results are folded into [FoldLow, FoldHigh), which keeps 174 BPM drum and bass and 90 BPM hip-hop as they are.</summary>
    public const double FoldLow = 87.5, FoldHigh = 175;

    /// <summary>
    /// Below this the strongest periodicity is too weak to trust. A wrong BPM is
    /// worse than none, because SYNC and the auto-cue act on it.
    /// </summary>
    public const double MinConfidence = 0.1;

    private const int Harmonics = 8;
    private const int FramesPerSecond = 100;

    public static (double? Bpm, TimeSpan? BeatOffset) Detect(AnalysisSignal signal)
    {
        int hop = HopSamples(signal.SampleRate);
        int hops = signal.Samples.Length / hop;
        var energy = new double[hops];
        for (int h = 0; h < hops; h++)
            energy[h] = SumOfSquares(signal.Samples.AsSpan(h * hop, hop));
        return Detect(energy, signal.SampleRate);
    }

    /// <summary>Samples per 10 ms frame at <paramref name="sampleRate"/>.</summary>
    public static int HopSamples(int sampleRate) => Math.Max(1, sampleRate / FramesPerSecond);

    /// <summary>Sum of squares in the order the window sums have always used.</summary>
    public static double SumOfSquares(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float s in samples) sum += (double)s * s;
        return sum;
    }

    /// <summary>
    /// Detects from the sum of squares of each whole <see cref="HopSamples"/> hop, so
    /// a streaming caller need not keep the signal. Each analysis window spans two
    /// hops, so its energy is the sum of two neighbouring entries.
    /// </summary>
    public static (double? Bpm, TimeSpan? BeatOffset) Detect(ReadOnlySpan<double> hopEnergy, int sampleRate)
    {
        int hop = HopSamples(sampleRate);
        double frameRate = (double)sampleRate / hop;
        float[] onset = OnsetEnvelope(hopEnergy, hop);

        int maxLag = (int)Math.Ceiling(60 * frameRate / MinBpm * Harmonics) + 2;
        if (onset.Length < maxLag * 2) return (null, null);

        double[] ac = Autocorrelate(onset, maxLag);
        if (ac[0] <= 1e-12) return (null, null);

        double coarse = MinBpm, coarseScore = double.MinValue;
        for (double bpm = MinBpm; bpm <= MaxBpm; bpm += 0.5)
        {
            double s = Score(ac, frameRate, bpm);
            if (s > coarseScore) { coarseScore = s; coarse = bpm; }
        }

        double folded = Fold(coarse);
        double best = folded, bestScore = double.MinValue;
        for (double bpm = folded - 0.6; bpm <= folded + 0.6; bpm += 0.01)
        {
            double s = Score(ac, frameRate, bpm);
            if (s > bestScore) { bestScore = s; best = bpm; }
        }

        if (bestScore / (Harmonics * ac[0]) < MinConfidence) return (null, null);

        return (Math.Round(best, 2), Phase(onset, frameRate, best));
    }

    private static double Fold(double bpm)
    {
        while (bpm < FoldLow) bpm *= 2;
        while (bpm >= FoldHigh) bpm /= 2;
        return bpm;
    }

    /// <summary>Positive rises in log energy per 10 ms frame, with the mean removed.</summary>
    private static float[] OnsetEnvelope(ReadOnlySpan<double> hopEnergy, int hop)
    {
        int window = hop * 2;
        int frames = hopEnergy.Length - 2;
        if (frames < 2) return [];

        var energy = new double[frames];
        for (int f = 0; f < frames; f++)
            energy[f] = Math.Log10(1e-6 + (hopEnergy[f] + hopEnergy[f + 1]) / window);

        var onset = new float[frames];
        double mean = 0;
        for (int f = 1; f < frames; f++)
        {
            onset[f] = (float)Math.Max(0, energy[f] - energy[f - 1]);
            mean += onset[f];
        }
        mean /= frames;
        for (int f = 0; f < frames; f++) onset[f] -= (float)mean;
        return onset;
    }

    /// <summary>Autocorrelation normalised by overlap length, so long lags are not penalised.</summary>
    private static double[] Autocorrelate(float[] x, int maxLag)
    {
        var ac = new double[maxLag + 1];
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = 0; i + lag < x.Length; i++) sum += (double)x[i] * x[i + lag];
            ac[lag] = sum / (x.Length - lag);
        }
        return ac;
    }

    private static double Score(double[] ac, double frameRate, double bpm)
    {
        double period = 60 * frameRate / bpm;
        double sum = 0;
        for (int k = 1; k <= Harmonics; k++) sum += At(ac, period * k);
        return sum;
    }

    /// <summary>Linear interpolation between lags.</summary>
    private static double At(double[] ac, double lag)
    {
        int i = (int)lag;
        if (i + 1 >= ac.Length) return 0;
        double f = lag - i;
        return ac[i] * (1 - f) + ac[i + 1] * f;
    }

    /// <summary>The offset within one beat that lines up best with the onsets.</summary>
    private static TimeSpan Phase(float[] onset, double frameRate, double bpm)
    {
        double period = 60 * frameRate / bpm;
        int best = 0;
        double bestSum = double.MinValue;
        for (int phase = 0; phase < (int)period; phase++)
        {
            double sum = 0;
            for (double t = phase; ; t += period)
            {
                int i = (int)Math.Round(t);
                if (i >= onset.Length) break;
                sum += onset[i];
            }
            if (sum > bestSum) { bestSum = sum; best = phase; }
        }
        return TimeSpan.FromSeconds(best / frameRate);
    }
}
