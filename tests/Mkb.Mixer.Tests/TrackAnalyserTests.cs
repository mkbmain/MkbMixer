using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class TrackAnalyserTests
{
    private static float[] Track() =>
        Synth.Stereo(Synth.Concat(
            Synth.Silence(1, 44100),
            Synth.Clicks(126, 20, noise: 0.05, rate: 44100),
            Synth.Silence(2, 44100)));

    private static TrackAnalysis FeedInChunks(float[] samples, int chunk)
    {
        var analyser = new TrackAnalyser(channels: 2, sampleRate: 44100);
        for (int i = 0; i < samples.Length; i += chunk)
            analyser.Add(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)));
        return analyser.Finish();
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(1001)] [InlineData(16384)]
    public void ChunkSizeDoesNotChangeTheResult(int chunk)
    {
        float[] samples = Track();
        TrackAnalysis whole = FeedInChunks(samples, samples.Length);

        TrackAnalysis chunked = FeedInChunks(samples, chunk);

        Assert.Equal(whole.Bpm, chunked.Bpm);
        Assert.Equal(whole.BeatOffset, chunked.BeatOffset);
        Assert.Equal(whole.FirstSound, chunked.FirstSound);
        Assert.Equal(whole.LastSound, chunked.LastSound);
        Assert.Equal(whole.Waveform.Peaks, chunked.Waveform.Peaks);
    }

    [Fact]
    public void DetectorsSeeTheSameSignalAsAFullDecode()
    {
        float[] samples = Track();
        AnalysisSignal signal = AnalysisSignal.From(samples, channels: 2, sampleRate: 44100);
        var (bpm, offset) = TempoDetector.Detect(signal);
        var (first, last) = SilenceDetector.Detect(signal);

        TrackAnalysis streamed = FeedInChunks(samples, 4096);

        Assert.Equal(bpm, streamed.Bpm);
        Assert.Equal(offset, streamed.BeatOffset);
        Assert.Equal(first, streamed.FirstSound);
        Assert.Equal(last, streamed.LastSound);
    }

    [Fact]
    public void WaveformMatchesOneDrawnFromEverySample()
    {
        float[] samples = Track();
        Waveform full = Waveform.FromSamples(samples, channels: 2, Waveform.DefaultBuckets);

        Waveform streamed = FeedInChunks(samples, 4096).Waveform;

        Assert.Equal(Waveform.DefaultBuckets, streamed.Peaks.Length);
        // Peaks are kept per decimation block, so a bucket edge can shift by a
        // fraction of a millisecond; the shape is otherwise the same.
        for (int i = 0; i < full.Peaks.Length; i++)
            Assert.True(Math.Abs(full.Peaks[i] - streamed.Peaks[i]) < 0.15f,
                $"bucket {i}: {full.Peaks[i]} vs {streamed.Peaks[i]}");
        Assert.Equal(full.Peaks.Max(), streamed.Peaks.Max(), 3);
    }

    [Theory]
    [InlineData(-1L)] [InlineData(10L)] [InlineData(long.MaxValue)]
    public void AWrongLengthEstimateDoesNotChangeTheResult(long expected)
    {
        float[] samples = Track();
        TrackAnalysis reference = FeedInChunks(samples, 4096);

        var analyser = new TrackAnalyser(channels: 2, sampleRate: 44100, expectedSamples: expected);
        analyser.Add(samples);
        TrackAnalysis a = analyser.Finish();

        Assert.Equal(reference.Bpm, a.Bpm);
        Assert.Equal(reference.LastSound, a.LastSound);
        Assert.Equal(reference.Waveform.Peaks, a.Waveform.Peaks);
    }

    [Fact]
    public void NothingFedGivesAnEmptyResult()
    {
        TrackAnalysis a = new TrackAnalyser(channels: 2, sampleRate: 44100).Finish();

        Assert.Equal(Waveform.DefaultBuckets, a.Waveform.Peaks.Length);
        Assert.All(a.Waveform.Peaks, p => Assert.Equal(0f, p));
        Assert.Null(a.Bpm);
    }

    [Fact]
    public void RejectsNonsenseFormats()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackAnalyser(channels: 0, sampleRate: 44100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackAnalyser(channels: 2, sampleRate: 0));
    }
}
