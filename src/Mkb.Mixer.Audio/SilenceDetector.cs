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
        int window = Math.Max(1, (int)(signal.SampleRate * Window.TotalSeconds));
        int count = signal.Samples.Length / window;
        if (count == 0) return (null, null);

        var db = new double[count];
        for (int w = 0; w < count; w++)
        {
            double sum = 0;
            for (int i = w * window; i < (w + 1) * window; i++)
                sum += (double)signal.Samples[i] * signal.Samples[i];
            double rms = Math.Sqrt(sum / window);
            db[w] = rms <= 0 ? double.NegativeInfinity : 20 * Math.Log10(rms);
        }

        int first = Array.FindIndex(db, d => d > SilenceDb);
        if (first < 0) return (null, null);
        int last = Array.FindLastIndex(db, d => d > SilenceDb);
        int loud = Array.FindLastIndex(db, d => d > FadeEndDb);
        int searchWindows = (int)(FadeSearch / Window);
        int end = loud >= 0 && last - loud <= searchWindows ? loud : last;

        return (At(first), At(end + 1));

        TimeSpan At(int w) => TimeSpan.FromSeconds((double)w * window / signal.SampleRate);
    }
}
