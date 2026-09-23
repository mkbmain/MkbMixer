namespace Mkb.Mixer.Audio;

/// <summary>
/// A track reduced to a fixed number of amplitude peaks for drawing, plus the
/// mapping from a click's X coordinate back to a position in the track.
/// </summary>
/// <remarks>
/// The original had no waveform and no seeking at all — play, pause and stop were
/// the only transport controls. Decoding a whole track to peaks measures at roughly
/// 370ms for a six-minute MP3, so this runs once on load, off the UI thread.
/// </remarks>
public sealed class Waveform(float[] peaks)
{
    /// <summary>Normalised 0..1 amplitude peaks, oldest first.</summary>
    public float[] Peaks { get; } = peaks;

    /// <summary>Default bucket count: finer than any realistic pixel width, cheap to hold.</summary>
    public const int DefaultBuckets = 2000;

    public static Waveform Empty { get; } = new(new float[DefaultBuckets]);

    /// <summary>Reduces interleaved samples to <paramref name="buckets"/> absolute peaks.</summary>
    public static Waveform FromSamples(ReadOnlySpan<float> samples, int channels, int buckets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(buckets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);

        var peaks = new float[buckets];
        if (samples.Length == 0)
            return new Waveform(peaks);

        int frames = samples.Length / channels;
        if (frames == 0)
            return new Waveform(peaks);

        for (int b = 0; b < buckets; b++)
        {
            int start = (int)((long)b * frames / buckets);
            int end = (int)((long)(b + 1) * frames / buckets);
            if (end <= start) end = Math.Min(start + 1, frames);

            float peak = 0f;
            for (int f = start; f < end; f++)
            {
                int offset = f * channels;
                for (int c = 0; c < channels; c++)
                {
                    float v = Math.Abs(samples[offset + c]);
                    if (v > peak) peak = v;
                }
            }
            peaks[b] = Math.Clamp(peak, 0f, 1f);
        }

        return new Waveform(peaks);
    }

    /// <summary>Maps a click at <paramref name="x"/> on a control of <paramref name="width"/> to 0..1 of the track.</summary>
    public double FractionAt(double x, double width) =>
        width <= 0 ? 0 : Math.Clamp(x / width, 0.0, 1.0);
}
