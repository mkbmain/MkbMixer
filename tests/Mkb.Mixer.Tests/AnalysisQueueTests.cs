using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class AnalysisQueueTests
{
    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(3));

    private static (AnalysisQueue Queue, FakeAudioEngine Engine, TrackStore Store) Manual()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        return (new AnalysisQueue(engine, store, post: a => a(), manual: true), engine, store);
    }

    [Fact]
    public async Task NextUpTracksRunBeforeLibraryTracks()
    {
        var (queue, engine, _) = Manual();
        queue.QueueFolder([T("l1"), T("l2")]);
        queue.Prefetch(T("n1"));

        await queue.DrainAsync();

        Assert.Equal(["/m/n1.mp3", "/m/l1.mp3", "/m/l2.mp3"], engine.AnalysedPaths);
    }

    [Fact]
    public async Task ATrackRequestedTwiceIsAnalysedOnce()
    {
        var (queue, engine, _) = Manual();
        queue.Prefetch(T("a"));
        queue.Prefetch(T("a"));
        queue.QueueFolder([T("a")]);

        await queue.DrainAsync();

        Assert.Single(engine.AnalysedPaths);
    }

    [Fact]
    public async Task CachedTracksAreSkipped()
    {
        var (queue, engine, store) = Manual();
        store.SetAnalysis("/m/a.mp3", FakeAudioEngine.Analysis(120));

        queue.Prefetch(T("a"));
        await queue.DrainAsync();

        Assert.Empty(engine.AnalysedPaths);
    }

    [Fact]
    public async Task OpeningAnotherFolderDropsOnlyLibraryWork()
    {
        var (queue, engine, _) = Manual();
        queue.QueueFolder([T("l1")]);
        queue.Prefetch(T("n1"));
        queue.QueueFolder([T("l2")]);

        await queue.DrainAsync();

        Assert.Equal(["/m/n1.mp3", "/m/l2.mp3"], engine.AnalysedPaths);
    }

    [Fact]
    public async Task AFailingFileDoesNotStopTheQueue()
    {
        var (queue, engine, store) = Manual();
        engine.Throwing.Add("/m/bad.mp3");
        engine.Analyses["/m/good.mp3"] = FakeAudioEngine.Analysis(124);

        queue.QueueFolder([T("bad"), T("good")]);
        await queue.DrainAsync();

        Assert.Null(store.Get("/m/bad.mp3"));
        Assert.Equal(124, store.Get("/m/good.mp3")!.Bpm);
    }

    [Fact]
    public async Task EmptyResultIsNotCached()
    {
        var (queue, engine, store) = Manual();
        engine.Analyses["/m/a.mp3"] = TrackAnalysis.Empty;

        queue.Prefetch(T("a"));
        await queue.DrainAsync();

        Assert.Null(store.Get("/m/a.mp3"));
    }

    [Fact]
    public async Task DeckAnalysisIsStoredAndAnnounced()
    {
        var (queue, engine, store) = Manual();
        engine.Analyses["/m/a.mp3"] = FakeAudioEngine.Analysis(128);
        var announced = new List<string>();
        queue.Analysed += (_, path) => announced.Add(path);

        await queue.AnalyseForDeckAsync(T("a"));

        Assert.Equal(128, store.Get("/m/a.mp3")!.Bpm);
        Assert.Equal(["/m/a.mp3"], announced);
    }

    [Fact]
    public async Task TheWorkerRunsByItself()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        using var queue = new AnalysisQueue(engine, store, post: a => a());

        queue.Prefetch(T("a"));
        for (int i = 0; i < 100 && store.Get("/m/a.mp3") is null; i++) await Task.Delay(20);

        Assert.True(store.Get("/m/a.mp3")?.IsAnalysed);
    }

    [Fact]
    public async Task ATrackLoadedBehindTheDecksBackGetsItsWaveform()
    {
        // The auto-cue loads the incoming deck on IDeck directly.
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine());
        await deck.LoadAsync(T("first"));

        fake.Load(T("second"));
        deck.Refresh();

        Assert.Equal("second", deck.NowPlaying);
        Assert.NotSame(Waveform.Empty, deck.Waveform);
    }

    [Fact]
    public async Task SavingStateFlushesTheTrackStore()
    {
        string dir = Directory.CreateTempSubdirectory("mkb-flush").FullName;
        try
        {
            string tracks = Path.Combine(dir, "tracks.json");
            var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(dir, "settings.json")),
                new TrackStore(tracks, saveDelay: TimeSpan.FromHours(1)), post: a => a(), manualAnalysis: true);
            await vm.DeckA.LoadAsync(T("a"));

            vm.SaveState();

            Assert.True(File.Exists(tracks));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
