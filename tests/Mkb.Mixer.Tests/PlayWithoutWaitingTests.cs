using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

/// <summary>
/// Audio must not wait for the whole-track analysis. FakeAudioEngine used to
/// return finished analyses, which hid that play began only once analysis did.
/// </summary>
public class PlayWithoutWaitingTests
{
    private static readonly Track Slow = new("/m/slow.mp3", "Slow", "Artist", Duration: TimeSpan.FromMinutes(3));

    private static (FakeAudioEngine Engine, DeckViewModel Deck) NewDeck()
    {
        var engine = new FakeAudioEngine();
        engine.Held.Add(Slow.Path);
        engine.Analyses[Slow.Path] = FakeAudioEngine.Analysis(bpm: 128);
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")));
        return (engine, vm.DeckA);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task PlayStartsAudioWhileAnalysisIsStillRunning()
    {
        var (engine, deck) = NewDeck();
        deck.Playlist.Add(Slow);

        await deck.PlayCommand.ExecuteAsync(null);

        Assert.Equal(PlaybackState.Playing, engine.A.State);
        Assert.Same(Waveform.Empty, deck.Waveform);

        engine.ReleaseHeld();
        await Until(() => !ReferenceEquals(deck.Waveform, Waveform.Empty));
        Assert.Equal(128 * 1.0, deck.TrackBpm);
    }

    [Fact]
    public async Task PlayNextStartsAudioWhileAnalysisIsStillRunning()
    {
        var (engine, deck) = NewDeck();
        deck.Playlist.Add(Slow);

        Assert.True(await deck.PlayNextAsync());

        Assert.Equal(PlaybackState.Playing, engine.A.State);
        Assert.Same(Waveform.Empty, deck.Waveform);

        engine.ReleaseHeld();
        await Until(() => !ReferenceEquals(deck.Waveform, Waveform.Empty));
    }

    [Fact]
    public async Task ABackgroundAnalysisFailureIsReportedNotUnobserved()
    {
        var engine = new FakeAudioEngine();
        engine.Throwing.Add(Slow.Path);
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")));
        var refusals = new List<string>();
        vm.DeckA.PlaybackRefused += (_, m) => refusals.Add(m);
        vm.DeckA.Playlist.Add(Slow);

        Assert.True(await vm.DeckA.PlayNextAsync());

        await Until(() => refusals.Count > 0);
        Assert.Equal(PlaybackState.Playing, engine.A.State);
    }
}
