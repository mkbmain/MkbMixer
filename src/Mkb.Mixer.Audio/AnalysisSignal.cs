namespace Mkb.Mixer.Audio;

/// <summary>
/// A mono, decimated copy of a decoded track for the detectors. Tempo and silence
/// need nothing above a few kHz, and working at a quarter of the rate keeps
/// detection to a few tens of milliseconds on top of the decode.
/// </summary>
public readonly record struct AnalysisSignal(float[] Samples, int SampleRate)
{
    public const int TargetRate = 11025;

    /// <summary>Averages the channels, then averages blocks of samples down to about <see cref="TargetRate"/>.</summary>
    public static AnalysisSignal From(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        int factor = Math.Max(1, (int)Math.Round((double)sampleRate / TargetRate));
        int block = factor * channels;
        var mono = new float[interleaved.Length / block];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0f;
            ReadOnlySpan<float> slice = interleaved.Slice(i * block, block);
            foreach (float s in slice) sum += s;
            mono[i] = sum / block;
        }
        return new AnalysisSignal(mono, sampleRate / factor);
    }
}
