using Mkb.Mixer.Audio;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace Mkb.Mixer.Tests;

/// <summary>
/// The output format must be fully specified. An AudioFormat built with an object
/// initialiser silently leaves Layout as Unknown, which decoding tolerates but
/// device initialisation rejects with FailedToOpenBackendDevice — playback is then
/// silent on every machine that has a real sound card.
/// </summary>
public class AudioFormatTests
{
    [Fact]
    public void OutputFormatHasAnExplicitChannelLayout()
    {
        Assert.NotEqual(ChannelLayout.Unknown, SoundFlowAudioEngine.OutputFormat.Layout);
    }

    [Fact]
    public void OutputFormatLayoutMatchesItsChannelCount()
    {
        AudioFormat f = SoundFlowAudioEngine.OutputFormat;
        Assert.Equal(AudioFormat.GetLayoutFromChannels(f.Channels), f.Layout);
    }

    [Fact]
    public void OutputFormatIsStereoCdRate()
    {
        AudioFormat f = SoundFlowAudioEngine.OutputFormat;
        Assert.Equal(2, f.Channels);
        Assert.Equal(44100, f.SampleRate);
        Assert.Equal(SampleFormat.F32, f.Format);
    }

    [Fact]
    public void AnObjectInitialiserWithoutLayoutIsTheTrapThisGuardsAgainst()
    {
        // Documents the defaulting behaviour that caused the bug.
        var careless = new AudioFormat { SampleRate = 44100, Channels = 2, Format = SampleFormat.F32 };
        Assert.Equal(ChannelLayout.Unknown, careless.Layout);
    }
}
