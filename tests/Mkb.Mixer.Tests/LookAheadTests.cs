using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class LookAheadTests
{
    /// <summary>Returns the given picks in turn, so tests can tell a re-roll from a held pick.</summary>
    private sealed class ScriptedRandom(params int[] picks) : Random
    {
        private int _next;
        public override int Next(int maxValue) => Math.Min(picks[_next++ % picks.Length], maxValue - 1);
    }

    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(3));

    private static (DeckViewModel Deck, AnalysisQueue Queue, FakeAudioEngine Engine) Deck(Random? random = null)
    {
        var engine = new FakeAudioEngine();
        var queue = new AnalysisQueue(engine, TrackStore.InMemory(), post: a => a(), manual: true);
        return (new DeckViewModel(new FakeDeck(DeckId.A), engine, random, queue), queue, engine);
    }

    [Fact]
    public void TheShufflePickIsMadeOnceAndKept()
    {
        var (deck, _, _) = Deck(new ScriptedRandom(1, 0, 0, 0));
        foreach (string n in new[] { "a", "b", "c" }) deck.Playlist.Add(T(n));
        deck.Shuffle = true;

        Track peeked = deck.PeekNext()!;
        Assert.Same(peeked, deck.PeekNext());

        Assert.Same(peeked, deck.DequeueNext());
        Assert.Equal("b", peeked.Title);
    }

    [Fact]
    public void RemovingTheHeldTrackPicksAgain()
    {
        var (deck, _, _) = Deck();
        Track a = T("a"), b = T("b");
        deck.Playlist.Add(a);
        deck.Playlist.Add(b);
        Assert.Same(a, deck.PeekNext());

        deck.Playlist.Remove(a);

        Assert.Same(b, deck.DequeueNext());
    }

    [Fact]
    public void ReorderingAnUnshuffledQueuePicksTheNewTop()
    {
        var (deck, _, _) = Deck();
        Track a = T("a"), b = T("b");
        deck.Playlist.Add(a);
        deck.Playlist.Add(b);
        Assert.Same(a, deck.PeekNext());

        deck.Playlist.Move(0, 1);

        Assert.Same(b, deck.PeekNext());
    }

    [Fact]
    public async Task TheNextTrackIsAnalysedAheadOfTime()
    {
        var (deck, queue, engine) = Deck();
        deck.Playlist.Add(T("a"));
        deck.Playlist.Add(T("b"));

        await queue.DrainAsync();

        Assert.Equal(["/m/a.mp3"], engine.AnalysedPaths);
    }
}
