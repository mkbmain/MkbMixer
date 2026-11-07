using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class TrackAnalyserMemoryTests
{
    [Fact]
    public void AnalysingALongTrackStaysUnderEightMegabytes()
    {
        const int rate = 44100, channels = 2, minutes = 8, chunk = 16384;

        // Twenty beats of 126 BPM clicks, repeated: generated, never stored whole. Whole
        // beats, or the loop's seam would put a second tempo in the signal.
        float[] loop = Synth.Stereo(Synth.Clicks(126, 20 * 60.0 / 126, noise: 0.05, rate: rate));
        var buffer = new float[chunk];
        long total = (long)rate * channels * 60 * minutes;

        // Warm up the JIT so its allocations are not counted.
        new TrackAnalyser(channels, rate).Add(buffer);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var analyser = new TrackAnalyser(channels, rate, expectedSamples: total);
        long fed = 0;
        int pos = 0;
        while (fed < total)
        {
            int n = (int)Math.Min(chunk, total - fed);
            for (int i = 0; i < n; i++)
            {
                buffer[i] = loop[pos];
                if (++pos == loop.Length) pos = 0;
            }
            analyser.Add(buffer.AsSpan(0, n));
            fed += n;
        }
        TrackAnalysis result = analyser.Finish();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(result.Bpm);
        Assert.InRange(result.Bpm.Value, 125, 127);
        Assert.True(allocated < 8_000_000, $"allocated {allocated:N0} bytes");
    }
}
