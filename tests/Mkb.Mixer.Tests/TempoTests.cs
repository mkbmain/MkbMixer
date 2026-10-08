using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class TempoTests
{
    [Fact]
    public void ResetPutsTheDeckBackToNormalSpeed()
    {
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine()) { Tempo = 1.37 };
        Assert.Equal("1.37×", deck.TempoLabel);

        deck.ResetTempoCommand.Execute(null);

        Assert.Equal(1.0, deck.Tempo);
        Assert.Equal(1f, fake.Tempo);
        Assert.Equal("1.00×", deck.TempoLabel);
    }
}
