using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class SilenceDetectorTests
{
    [Fact]
    public void DownmixesAndDecimatesToTheAnalysisRate()
    {
        var stereo = Enumerable.Repeat(0.5f, 44100 * 2).ToArray();

        AnalysisSignal signal = AnalysisSignal.From(stereo, channels: 2, sampleRate: 44100);

        Assert.Equal(11025, signal.SampleRate);
        Assert.Equal(11025, signal.Samples.Length);
        Assert.All(signal.Samples, s => Assert.Equal(0.5f, s, 4));
    }

    [Fact]
    public void FindsLeadingAndTrailingSilence()
    {
        float[] audio = Synth.Concat(Synth.Silence(2), Synth.Tone(10, -10), Synth.Silence(3));

        var (first, last) = SilenceDetector.Detect(Synth.Signal(audio));

        Assert.Equal(2.0, first!.Value.TotalSeconds, 0.05);
        Assert.Equal(12.0, last!.Value.TotalSeconds, 0.05);
    }

    [Fact]
    public void AQuietFadeOutCountsAsTheEnd()
    {
        // -10 dB for 10 s, then down to -60 dB over 8 s: it crosses -30 dB at 13.2 s
        // and -45 dB at 15.6 s. The near-silent tail should not hold up a mix.
        float[] audio = Synth.Concat(Synth.Tone(10, -10), Synth.Fade(8, -10, -60), Synth.Silence(2));

        var (_, last) = SilenceDetector.Detect(Synth.Signal(audio));

        Assert.Equal(13.2, last!.Value.TotalSeconds, 0.1);
    }

    [Fact]
    public void ALongQuietOutroIsKept()
    {
        // 20 s at -38 dB is quiet music, not a fade, so it is not cut.
        float[] audio = Synth.Concat(Synth.Tone(10, -10), Synth.Tone(20, -38), Synth.Silence(2));

        var (_, last) = SilenceDetector.Detect(Synth.Signal(audio));

        Assert.Equal(30.0, last!.Value.TotalSeconds, 0.05);
    }

    [Fact]
    public void DigitalSilenceHasNoSound()
    {
        var (first, last) = SilenceDetector.Detect(Synth.Signal(Synth.Silence(5)));

        Assert.Null(first);
        Assert.Null(last);
    }
}
