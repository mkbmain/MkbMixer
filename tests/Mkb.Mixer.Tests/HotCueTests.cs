using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class HotCueTests
{
    private static readonly Track Song = new("/m/song.mp3", "song", Duration: TimeSpan.FromMinutes(4));

    private static DeckViewModel Deck(FakeAudioEngine engine, TrackStore store) =>
        new(engine.A, engine, analysis: new AnalysisQueue(engine, store, post: a => a(), manual: true));

    private static async Task<(DeckViewModel Deck, FakeDeck Fake, TrackStore Store)> Loaded()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        DeckViewModel deck = Deck(engine, store);
        await deck.LoadAsync(Song);
        return (deck, engine.A, store);
    }

    [Fact]
    public async Task TappingAnEmptySlotSetsItAtThePlayhead()
    {
        var (deck, fake, store) = await Loaded();
        fake.Position = TimeSpan.FromSeconds(42);

        deck.HotCueCommand.Execute(deck.HotCues[1]);

        Assert.Equal(42, deck.HotCues[1].Seconds);
        Assert.Equal(42, store.Get(Song.Path)!.HotCueSeconds[1]);
        Assert.Equal(42.0 / 240, deck.CueFractions[1]!.Value, 6);
    }

    [Fact]
    public async Task TappingASetSlotJumpsThereAndKeepsPlaying()
    {
        var (deck, fake, _) = await Loaded();
        fake.Position = TimeSpan.FromSeconds(10);
        deck.HotCueCommand.Execute(deck.HotCues[0]);
        await deck.PlayCommand.ExecuteAsync(null);
        fake.Position = TimeSpan.FromSeconds(100);

        deck.HotCueCommand.Execute(deck.HotCues[0]);

        Assert.Equal(TimeSpan.FromSeconds(10), fake.Position);
        Assert.Equal(PlaybackState.Playing, fake.State);
    }

    [Fact]
    public async Task WhileStoppedItWaitsAtTheCueAndPlaysFromThere()
    {
        var (deck, fake, _) = await Loaded();
        fake.RewindsOnPlayFromStop = true;
        fake.Position = TimeSpan.FromSeconds(10);
        deck.HotCueCommand.Execute(deck.HotCues[0]);
        fake.Position = TimeSpan.Zero;

        deck.HotCueCommand.Execute(deck.HotCues[0]);
        Assert.Equal(PlaybackState.Stopped, fake.State);
        await deck.PlayCommand.ExecuteAsync(null);

        Assert.Equal(TimeSpan.FromSeconds(10), fake.Position);
    }

    [Fact]
    public async Task ClearingEmptiesTheSlotForGood()
    {
        var (deck, fake, store) = await Loaded();
        fake.Position = TimeSpan.FromSeconds(10);
        deck.HotCueCommand.Execute(deck.HotCues[2]);

        deck.ClearHotCueCommand.Execute(deck.HotCues[2]);

        Assert.False(deck.HotCues[2].IsSet);
        Assert.Null(store.Get(Song.Path)!.HotCueSeconds[2]);
        Assert.Null(deck.CueFractions[2]);
    }

    [Fact]
    public async Task ACueAtTheVeryStartCountsAsSet()
    {
        var (deck, fake, _) = await Loaded();
        deck.HotCueCommand.Execute(deck.HotCues[0]);   // playhead at 0:00
        Assert.True(deck.HotCues[0].IsSet);

        fake.Position = TimeSpan.FromSeconds(50);
        deck.HotCueCommand.Execute(deck.HotCues[0]);

        Assert.Equal(TimeSpan.Zero, fake.Position);
        Assert.Equal(0, deck.HotCues[0].Seconds);
    }

    [Fact]
    public async Task CuesComeBackWithTheTrack()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        store.Update(Song.Path, i => i.WithHotCue(3, 77));

        DeckViewModel deck = Deck(engine, store);
        await deck.LoadAsync(Song);

        Assert.Equal(77, deck.HotCues[3].Seconds);
    }

    [Fact]
    public void WithNothingLoadedTappingDoesNothing()
    {
        var engine = new FakeAudioEngine();
        DeckViewModel deck = Deck(engine, TrackStore.InMemory());

        deck.HotCueCommand.Execute(deck.HotCues[0]);

        Assert.False(deck.HotCues[0].IsSet);
    }
}
