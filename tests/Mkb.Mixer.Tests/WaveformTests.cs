using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class WaveformTests
{
    [Fact]
    public void DownsamplesToTheRequestedBucketCount()
    {
        var samples = new float[10_000];
        var wave = Waveform.FromSamples(samples, channels: 2, buckets: 100);
        Assert.Equal(100, wave.Peaks.Length);
    }

    [Fact]
    public void CapturesThePeakWithinEachBucket()
    {
        // Mono ramp 0..1 over 1000 samples, split into 10 buckets: each bucket's
        // peak is its last (largest) value.
        var samples = new float[1000];
        for (int i = 0; i < samples.Length; i++) samples[i] = i / 1000f;

        var wave = Waveform.FromSamples(samples, channels: 1, buckets: 10);

        Assert.Equal(0.099f, wave.Peaks[0], 2);
        Assert.Equal(0.999f, wave.Peaks[9], 2);
    }

    [Fact]
    public void UsesAbsoluteValueSoNegativeSwingsCount()
    {
        var samples = new[] { -0.9f, 0.1f, -0.2f, 0.3f };
        var wave = Waveform.FromSamples(samples, channels: 1, buckets: 1);
        Assert.Equal(0.9f, wave.Peaks[0], 3);
    }

    [Fact]
    public void EmptyInputProducesAFlatWaveformRatherThanThrowing()
    {
        var wave = Waveform.FromSamples([], channels: 2, buckets: 50);
        Assert.Equal(50, wave.Peaks.Length);
        Assert.All(wave.Peaks, p => Assert.Equal(0f, p));
    }

    [Fact]
    public void FewerSamplesThanBucketsStillFillsEveryBucket()
    {
        var wave = Waveform.FromSamples([1f, 1f, 1f], channels: 1, buckets: 20);
        Assert.Equal(20, wave.Peaks.Length);
    }

    [Fact]
    public void PeaksAreNormalisedIntoZeroToOne()
    {
        var samples = new[] { 0.5f, -0.25f, 0.75f, 0.1f };
        var wave = Waveform.FromSamples(samples, channels: 1, buckets: 4);
        Assert.All(wave.Peaks, p => Assert.InRange(p, 0f, 1f));
    }

    [Theory]
    [InlineData(0)] [InlineData(-5)]
    public void RejectsNonPositiveBucketCounts(int buckets)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => Waveform.FromSamples([1f], channels: 1, buckets: buckets));

    [Fact]
    public void PositionToFractionMapsClicksOntoTheTimeline()
    {
        var wave = new Waveform(new float[100]);
        Assert.Equal(0.0, wave.FractionAt(0, width: 200), 3);
        Assert.Equal(0.5, wave.FractionAt(100, width: 200), 3);
        Assert.Equal(1.0, wave.FractionAt(200, width: 200), 3);
        // Clicks outside the control clamp rather than seeking past the ends.
        Assert.Equal(0.0, wave.FractionAt(-30, width: 200), 3);
        Assert.Equal(1.0, wave.FractionAt(999, width: 200), 3);
    }
}
