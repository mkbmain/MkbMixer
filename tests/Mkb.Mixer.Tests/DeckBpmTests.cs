using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class DeckBpmTests
{
    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(4));

    private static (DeckViewModel A, DeckViewModel B, FakeAudioEngine Engine, TrackStore Store) Pair()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        var queue = new AnalysisQueue(engine, store, post: a => a(), manual: true);
        var a = new DeckViewModel(engine.A, engine, analysis: queue);
        var b = new DeckViewModel(engine.B, engine, analysis: queue);
        a.Other = b;
        b.Other = a;
        return (a, b, engine, store);
    }

    [Fact]
    public async Task ShowsTheBpmAsHeard()
    {
        var (a, _, engine, _) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(120);
        await a.LoadAsync(T("x"));

        a.Tempo = 1.1;

        Assert.Equal(120, a.TrackBpm);
        Assert.Equal(132, a.HeardBpm!.Value, 3);
        Assert.Equal("132.0 BPM", a.BpmText);
    }

    [Fact]
    public async Task AnUnknownBpmShowsADash()
    {
        var (a, _, _, _) = Pair();
        await a.LoadAsync(T("x"));

        Assert.Null(a.TrackBpm);
        Assert.Equal("— BPM", a.BpmText);
    }

    [Fact]
    public async Task SyncMatchesTheOtherDeckAsHeard()
    {
        var (a, b, engine, _) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(120);
        engine.Analyses["/m/y.mp3"] = FakeAudioEngine.Analysis(120);
        await a.LoadAsync(T("x"));
        await b.LoadAsync(T("y"));
        b.Tempo = 1.05;

        a.SyncCommand.Execute(null);

        Assert.Equal(1.05, a.Tempo, 4);
        Assert.Equal(126, a.HeardBpm!.Value, 3);
    }

    [Fact]
    public async Task SyncStaysWithinTheSliderRange()
    {
        var (a, b, engine, _) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(100);
        engine.Analyses["/m/y.mp3"] = FakeAudioEngine.Analysis(170);
        await a.LoadAsync(T("x"));
        await b.LoadAsync(T("y"));

        a.SyncCommand.Execute(null);

        Assert.Equal(1.5, a.Tempo, 4);
    }

    [Fact]
    public async Task SyncIsDisabledUntilBothBpmsAreKnown()
    {
        var (a, b, engine, _) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(120);
        await a.LoadAsync(T("x"));
        a.Refresh();
        Assert.False(a.SyncCommand.CanExecute(null));
        Assert.Equal("Nothing is loaded on the other deck", a.SyncHint);

        await b.LoadAsync(T("y"));   // no BPM found
        a.Refresh();
        Assert.False(a.SyncCommand.CanExecute(null));
        Assert.Equal("The other deck's BPM isn't known yet", a.SyncHint);
    }

    [Fact]
    public async Task NudgeReturnsExactlyToTheTempoItStartedFrom()
    {
        var (a, _, _, _) = Pair();
        await a.LoadAsync(T("x"));
        a.Tempo = 1.07;

        a.BeginNudge(+1);
        Assert.Equal(1.07 * 1.04, a.Tempo, 6);
        a.BeginNudge(+1);   // a second press while held changes nothing
        a.EndNudge();

        Assert.Equal(1.07, a.Tempo);
    }

    [Fact]
    public async Task HalvingTheBpmIsRememberedForTheTrack()
    {
        var (a, _, engine, store) = Pair();
        engine.Analyses["/m/x.mp3"] = FakeAudioEngine.Analysis(140);
        await a.LoadAsync(T("x"));

        a.HalveBpmCommand.Execute(null);

        Assert.Equal(70, a.TrackBpm);
        Assert.Equal(0.5, store.Get("/m/x.mp3")!.BpmMultiplier);

        var again = new DeckViewModel(new FakeDeck(DeckId.B), engine,
            analysis: new AnalysisQueue(engine, store, post: x => x(), manual: true));
        await again.LoadAsync(T("x"));
        Assert.Equal(70, again.TrackBpm);
    }
}
