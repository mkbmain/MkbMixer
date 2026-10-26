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

    /// <summary>Long enough that the peaks are merged twice, with a level that changes every few ms.</summary>
    private static float[] LongTrack()
    {
        var rnd = new Random(7);
        var s = new float[44100 * 60 * 2];
        float level = 0;
        for (int f = 0; f < s.Length / 2; f++)
        {
            if (f % 300 == 0) level = (float)rnd.NextDouble();
            s[2 * f] = level * (float)Math.Sin(f * 0.05);
            s[2 * f + 1] = -s[2 * f] * 0.5f;
        }
        return s;
    }

    [Fact]
    public void ALongTracksWaveformMovesOnlyAtBucketEdges()
    {
        float[] samples = LongTrack();
        int frames = samples.Length / 2;
        int buckets = Waveform.DefaultBuckets;

        Waveform streamed = FeedInChunks(samples, 16384).Waveform;

        // Each bucket must lie between the peak of its reference range shrunk and
        // grown by a few merged peaks' worth of frames (4 blocks of 4 frames each).
        const int slack = 2 * 4 * 4 + 4;
        for (int b = 0; b < buckets; b++)
        {
            int start = (int)((long)b * frames / buckets), end = (int)((long)(b + 1) * frames / buckets);
            float inner = Peak(start + slack, end - slack), outer = Peak(start - slack, end + slack);
            Assert.InRange(streamed.Peaks[b], inner, outer);
        }

        float Peak(int from, int to)
        {
            float p = 0;
            for (int i = Math.Max(0, from) * 2; i < Math.Min(frames, to) * 2; i++) p = Math.Max(p, Math.Abs(samples[i]));
            return p;
        }
    }

    [Fact]
    public void ALongTrackDoesNotDependOnChunkingOrTheEstimate()
    {
        float[] samples = LongTrack();
        TrackAnalysis reference = FeedInChunks(samples, 16384);

        var analyser = new TrackAnalyser(channels: 2, sampleRate: 44100, expectedSamples: samples.Length);
        for (int i = 0; i < samples.Length; i += 999)
            analyser.Add(samples.AsSpan(i, Math.Min(999, samples.Length - i)));
        TrackAnalysis a = analyser.Finish();

        Assert.Equal(reference.Waveform.Peaks, a.Waveform.Peaks);
        Assert.Equal(reference.LastSound, a.LastSound);
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
