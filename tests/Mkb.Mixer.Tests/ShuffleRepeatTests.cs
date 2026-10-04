using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class ShuffleRepeatTests
{
    /// <summary>Always picks the highest index it is offered, so shuffle is deterministic.</summary>
    private sealed class LastRandom : Random
    {
        public override int Next(int maxValue) => maxValue - 1;
    }

    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(3));

    private static DeckViewModel Deck(params Track[] queue)
    {
        var deck = new DeckViewModel(new FakeDeck(DeckId.A), new FakeAudioEngine(), new LastRandom());
        foreach (Track t in queue) deck.Playlist.Add(t);
        return deck;
    }

    [Fact]
    public void WithBothOffTheTopTrackIsTakenAndRemoved()
    {
        Track a = T("a"), b = T("b");
        var deck = Deck(a, b);

        Assert.Same(a, deck.DequeueNext());
        Assert.Equal(new[] { b }, deck.Playlist);
    }

    [Fact]
    public void AnEmptyQueueYieldsNothing() => Assert.Null(Deck().DequeueNext());

    [Fact]
    public void RepeatPutsThePlayedTrackBackOnTheEnd()
    {
        Track a = T("a"), b = T("b");
        var deck = Deck(a, b);
        deck.Repeat = true;

        Assert.Same(a, deck.DequeueNext());
        Assert.Equal(new[] { b, a }, deck.Playlist);
    }

    [Fact]
    public void ShuffleTakesFromAnywhereInTheQueue()
    {
        Track a = T("a"), b = T("b"), c = T("c");
        var deck = Deck(a, b, c);
        deck.Shuffle = true;

        Assert.Same(c, deck.DequeueNext());
        Assert.Equal(new[] { a, b }, deck.Playlist);
    }

    [Fact]
    public void ShuffleWithRepeatNeverPlaysTheSameTrackTwiceRunning()
    {
        Track a = T("a"), b = T("b"), c = T("c");
        var deck = Deck(a, b, c);
        deck.Shuffle = deck.Repeat = true;

        Track first = deck.DequeueNext()!;   // c, which repeat puts back on the end
        Track second = deck.DequeueNext()!;  // LastRandom would pick c again if allowed

        Assert.Same(c, first);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void ShuffleWithRepeatAndOneTrackKeepsPlayingIt()
    {
        Track a = T("a");
        var deck = Deck(a);
        deck.Shuffle = deck.Repeat = true;

        Assert.Same(a, deck.DequeueNext());
        Assert.Same(a, deck.DequeueNext());
    }

    [Fact]
    public void ShuffleCopesWithTheSameTrackQueuedTwice()
    {
        // Adding the selected library track twice queues the same object twice.
        Track a = T("a");
        var deck = Deck(a, a);
        deck.Shuffle = deck.Repeat = true;

        Assert.Same(a, deck.DequeueNext());
        Assert.Same(a, deck.DequeueNext());
    }

    [Fact]
    public void ShuffleAndRepeatAreRememberedPerDeck()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json");
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(path));
        vm.DeckA.Shuffle = true;
        vm.DeckB.Repeat = true;
        vm.SaveState();

        var reloaded = new MainViewModel(new FakeAudioEngine(), new SettingsStore(path));

        Assert.True(reloaded.DeckA.Shuffle);
        Assert.False(reloaded.DeckA.Repeat);
        Assert.False(reloaded.DeckB.Shuffle);
        Assert.True(reloaded.DeckB.Repeat);
    }
}
