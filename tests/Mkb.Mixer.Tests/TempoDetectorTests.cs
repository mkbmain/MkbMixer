using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class TempoDetectorTests
{
    [Fact]
    public void OneDecodeGivesWaveformTempoAndSilence()
    {
        float[] mono = Synth.Concat(Synth.Silence(1, 44100), Synth.Clicks(120, 30, rate: 44100), Synth.Silence(2, 44100));

        TrackAnalysis a = TrackAnalysis.FromSamples(Synth.Stereo(mono), channels: 2, sampleRate: 44100);

        Assert.Equal(Waveform.DefaultBuckets, a.Waveform.Peaks.Length);
        Assert.Equal(120, a.Bpm!.Value, 0.5);
        Assert.Equal(1.0, a.FirstSound!.Value.TotalSeconds, 0.05);
        Assert.InRange(a.LastSound!.Value.TotalSeconds, 30.5, 31.05);
    }

    [Theory]
    [InlineData(90)] [InlineData(120)] [InlineData(128)] [InlineData(174)]
    public void FindsTheTempoOfAClickTrack(double bpm)
    {
        var (detected, _) = TempoDetector.Detect(Synth.Signal(Synth.Clicks(bpm, seconds: 30)));

        Assert.NotNull(detected);
        Assert.Equal(bpm, detected!.Value, 0.5);
    }

    [Fact]
    public void FindsTheTempoUnderNoise()
    {
        var (detected, _) = TempoDetector.Detect(Synth.Signal(Synth.Clicks(128, seconds: 30, noise: 0.05)));

        Assert.Equal(128, detected!.Value, 0.5);
    }

    [Fact]
    public void FoldsSlowTempoIntoTheDisplayedOctave()
    {
        // Autocorrelation cannot tell 75 from 150; the user corrects it with x1/2.
        var (detected, _) = TempoDetector.Detect(Synth.Signal(Synth.Clicks(75, seconds: 30)));

        Assert.Equal(150, detected!.Value, 0.5);
    }

    [Fact]
    public void FindsWhereTheFirstBeatFalls()
    {
        var (_, offset) = TempoDetector.Detect(Synth.Signal(Synth.Clicks(120, seconds: 30, offset: 0.25)));

        Assert.Equal(0.25, offset!.Value.TotalSeconds, 0.03);
    }

    [Fact]
    public void NoiseHasNoTempo()
    {
        var (detected, offset) = TempoDetector.Detect(Synth.Signal(Synth.Noise(30, 0.3)));

        Assert.Null(detected);
        Assert.Null(offset);
    }

    [Fact]
    public void SilenceHasNoTempo() =>
        Assert.Null(TempoDetector.Detect(Synth.Signal(Synth.Silence(30))).Bpm);

    [Fact]
    public void ATooShortClipHasNoTempo() =>
        Assert.Null(TempoDetector.Detect(Synth.Signal(Synth.Clicks(120, seconds: 3))).Bpm);
}
