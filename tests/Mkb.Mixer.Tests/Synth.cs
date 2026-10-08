using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

/// <summary>Generates test audio with known tempo and levels, so detectors can be checked exactly.</summary>
internal static class Synth
{
    public const int Rate = AnalysisSignal.TargetRate;

    public static float[] Silence(double seconds, int rate = Rate) => new float[(int)(seconds * rate)];

    /// <summary>A 440 Hz sine whose RMS level is <paramref name="db"/> dBFS.</summary>
    public static float[] Tone(double seconds, double db, int rate = Rate)
    {
        var s = new float[(int)(seconds * rate)];
        double amp = Math.Pow(10, db / 20) * Math.Sqrt(2);
        for (int i = 0; i < s.Length; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * 440 * i / rate));
        return s;
    }

    /// <summary>A 440 Hz sine fading linearly in dB.</summary>
    public static float[] Fade(double seconds, double fromDb, double toDb, int rate = Rate)
    {
        var s = new float[(int)(seconds * rate)];
        for (int i = 0; i < s.Length; i++)
        {
            double db = fromDb + (toDb - fromDb) * i / s.Length;
            s[i] = (float)(Math.Pow(10, db / 20) * Math.Sqrt(2) * Math.Sin(2 * Math.PI * 440 * i / rate));
        }
        return s;
    }

    public static float[] Noise(double seconds, double amplitude, int seed = 1, int rate = Rate)
    {
        var random = new Random(seed);
        var s = new float[(int)(seconds * rate)];
        for (int i = 0; i < s.Length; i++) s[i] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        return s;
    }

    /// <summary>A short decaying 1 kHz click on every beat, optionally over white noise.</summary>
    public static float[] Clicks(double bpm, double seconds, double offset = 0, double noise = 0, int seed = 1, int rate = Rate)
    {
        float[] s = noise > 0 ? Noise(seconds, noise, seed, rate) : Silence(seconds, rate);
        int clickLength = (int)(0.02 * rate);
        for (int k = 0; ; k++)
        {
            int start = (int)Math.Round((offset + k * 60.0 / bpm) * rate);
            if (start >= s.Length) break;
            for (int j = 0; j < clickLength && start + j < s.Length; j++)
                s[start + j] += (float)(0.8 * Math.Exp(-j / (0.005 * rate)) * Math.Sin(2 * Math.PI * 1000 * j / rate));
        }
        return s;
    }

    public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Duplicates a mono buffer into interleaved stereo.</summary>
    public static float[] Stereo(float[] mono)
    {
        var s = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) s[2 * i] = s[2 * i + 1] = mono[i];
        return s;
    }

    public static AnalysisSignal Signal(float[] mono) => new(mono, Rate);
}
