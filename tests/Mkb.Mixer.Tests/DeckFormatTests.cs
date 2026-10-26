using Mkb.Mixer.Audio;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace Mkb.Mixer.Tests;

public class DeckFormatTests
{
    [Fact]
    public void DecksDecodeAtTheRateTheDeviceOpenedAt()
    {
        // The 16-bit 48 kHz fallback: the mixer still wants float, at 48 kHz.
        var device = new AudioFormat
        {
            SampleRate = 48000, Channels = 2, Format = SampleFormat.S16,
            Layout = AudioFormat.GetLayoutFromChannels(2)
        };

        AudioFormat deck = SoundFlowAudioEngine.DeckFormat(device);

        Assert.Equal(48000, deck.SampleRate);
        Assert.Equal(2, deck.Channels);
        Assert.Equal(SampleFormat.F32, deck.Format);
    }

    [Fact]
    public void ThePreferredFormatIsUnchanged() =>
        Assert.Equal(SoundFlowAudioEngine.OutputFormat, SoundFlowAudioEngine.DeckFormat(SoundFlowAudioEngine.OutputFormat));
}
