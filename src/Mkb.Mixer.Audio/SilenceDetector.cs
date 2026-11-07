namespace Mkb.Mixer.Audio;

/// <summary>Finds where a track's real sound starts and ends.</summary>
/// <remarks>
/// Levels are measured as RMS over 50 ms windows, so a single click or a hiss tail
/// does not count as sound. The end is pulled back to where a fade-out drops below
/// <see cref="FadeEndDb"/> if that happens within the final <see cref="FadeSearch"/>,
/// so a long near-silent tail does not leave dead air in a mix; a long quiet outro
/// further back than that is music and is kept.
/// </remarks>
public static class SilenceDetector
{
    public const double SilenceDb = -45;
    public const double FadeEndDb = -30;
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan FadeSearch = TimeSpan.FromSeconds(10);

    public static (TimeSpan? FirstSound, TimeSpan? LastSound) Detect(AnalysisSignal signal)
    {
        int window = WindowSamples(signal.SampleRate);
        int count = signal.Samples.Length / window;
        var energy = new double[count];
        for (int w = 0; w < count; w++)
            energy[w] = TempoDetector.SumOfSquares(signal.Samples.AsSpan(w * window, window));
        return Detect(energy, signal.SampleRate);
    }

    /// <summary>Samples per <see cref="Window"/> at <paramref name="sampleRate"/>.</summary>
    public static int WindowSamples(int sampleRate) =>
        Math.Max(1, (int)(sampleRate * Window.TotalSeconds));

    /// <summary>Detects from the sum of squares of each whole window, so a streaming caller need not keep the signal.</summary>
    public static (TimeSpan? FirstSound, TimeSpan? LastSound) Detect(ReadOnlySpan<double> windowEnergy, int sampleRate)
    {
        int window = WindowSamples(sampleRate);
        int count = windowEnergy.Length;
        if (count == 0) return (null, null);

        var db = new double[count];
        for (int w = 0; w < count; w++)
        {
            double rms = Math.Sqrt(windowEnergy[w] / window);
            db[w] = rms <= 0 ? double.NegativeInfinity : 20 * Math.Log10(rms);
        }

        int first = Array.FindIndex(db, d => d > SilenceDb);
        if (first < 0) return (null, null);
        int last = Array.FindLastIndex(db, d => d > SilenceDb);
        int loud = Array.FindLastIndex(db, d => d > FadeEndDb);
        int searchWindows = (int)(FadeSearch / Window);
        int end = loud >= 0 && last - loud <= searchWindows ? loud : last;

        return (At(first), At(end + 1));

        TimeSpan At(int w) => TimeSpan.FromSeconds((double)w * window / sampleRate);
    }
}
