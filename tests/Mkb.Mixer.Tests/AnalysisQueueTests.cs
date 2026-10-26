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
    public async Task APromotedLibraryTrackKeepsItsPlaceAmongNextUpTracks()
    {
        var (queue, engine, _) = Manual();
        queue.QueueFolder([T("l1"), T("l2"), T("l3")]);
        queue.Prefetch(T("n1"));
        queue.Prefetch(T("l3"));

        Assert.Equal(4, queue.PendingCount);
        await queue.DrainAsync();

        Assert.Equal(["/m/l3.mp3", "/m/n1.mp3", "/m/l1.mp3", "/m/l2.mp3"], engine.AnalysedPaths);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task ReopeningAFolderKeepsAPromotedTrackNextUpAndOnce()
    {
        var (queue, engine, _) = Manual();
        queue.QueueFolder([T("a"), T("b")]);
        queue.Prefetch(T("b"));
        queue.QueueFolder([T("c"), T("b"), T("c")]);

        Assert.Equal(2, queue.PendingCount);
        await queue.DrainAsync();

        Assert.Equal(["/m/b.mp3", "/m/c.mp3"], engine.AnalysedPaths);
    }

    [Fact]
    public async Task ATrackTakenAndQueuedAgainRunsInItsNewPlace()
    {
        var (queue, engine, _) = Manual();
        // Not stored, so it is decoded again rather than skipped as cached.
        engine.Analyses["/m/a.mp3"] = TrackAnalysis.Empty;
        queue.Prefetch(T("a"));
        await queue.DrainAsync();
        queue.QueueFolder([T("b"), T("a")]);
        await queue.DrainAsync();

        Assert.Equal(["/m/a.mp3", "/m/b.mp3", "/m/a.mp3"], engine.AnalysedPaths);
    }

    [Fact]
    public async Task ADeckLoadJoinsTheWorkersDecodeOfTheSameFile()
    {
        var (queue, engine, store) = Manual();
        var gate = new TaskCompletionSource();
        engine.Gates["/m/a.mp3"] = gate.Task;
        engine.Analyses["/m/a.mp3"] = FakeAudioEngine.Analysis(126);
        queue.Prefetch(T("a"));

        Task drain = queue.DrainAsync();
        Task<TrackAnalysis> deck = queue.AnalyseForDeckAsync(T("a"), TestContext.Current.CancellationToken);
        gate.SetResult();
        await drain;

        Assert.Equal(126, (await deck).Bpm);
        Assert.Single(engine.AnalysedPaths);
        Assert.Equal(126, store.Get("/m/a.mp3")!.Bpm);
    }

    [Fact]
    public async Task DroppingADeckLoadDoesNotCancelTheWorkersDecode()
    {
        var (queue, engine, store) = Manual();
        var gate = new TaskCompletionSource();
        engine.Gates["/m/a.mp3"] = gate.Task;
        queue.Prefetch(T("a"));
        Task drain = queue.DrainAsync();
        using var load = new CancellationTokenSource();

        Task<TrackAnalysis> deck = queue.AnalyseForDeckAsync(T("a"), load.Token);
        load.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deck);
        gate.SetResult();
        await drain;

        Assert.True(store.Get("/m/a.mp3")?.IsAnalysed);
        Assert.Single(engine.AnalysedPaths);
    }

    [Fact]
    public async Task APrefetchedTrackLoadsOnItsDeckWithoutASecondDecode()
    {
        string dir = Directory.CreateTempSubdirectory("mkb-recent").FullName;
        try
        {
            var (queue, engine, _) = Manual();
            var track = new Track(Path.Combine(dir, "a.mp3"), "a");
            File.WriteAllBytes(track.Path, new byte[100]);
            engine.Analyses[track.Path] = FakeAudioEngine.Analysis(122);

            queue.Prefetch(track);
            await queue.DrainAsync();
            TrackAnalysis loaded = await queue.AnalyseForDeckAsync(track, TestContext.Current.CancellationToken);

            Assert.Equal(122, loaded.Bpm);
            Assert.NotSame(Waveform.Empty, loaded.Waveform);
            Assert.Single(engine.AnalysedPaths);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task AChangedFileIsDecodedAgainOnItsDeck()
    {
        string dir = Directory.CreateTempSubdirectory("mkb-recent").FullName;
        try
        {
            var (queue, engine, _) = Manual();
            var track = new Track(Path.Combine(dir, "a.mp3"), "a");
            File.WriteAllBytes(track.Path, new byte[100]);
            CancellationToken ct = TestContext.Current.CancellationToken;

            await queue.AnalyseForDeckAsync(track, ct);
            await queue.AnalyseForDeckAsync(track, ct);
            Assert.Single(engine.AnalysedPaths);

            File.AppendAllText(track.Path, "re-tagged");
            await queue.AnalyseForDeckAsync(track, ct);

            Assert.Equal(2, engine.AnalysedPaths.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task OnlyTheMostRecentResultsAreKept()
    {
        string dir = Directory.CreateTempSubdirectory("mkb-recent").FullName;
        try
        {
            var (queue, engine, _) = Manual();
            CancellationToken ct = TestContext.Current.CancellationToken;
            var tracks = Enumerable.Range(0, AnalysisQueue.RecentCapacity + 1)
                .Select(i => new Track(Path.Combine(dir, $"{i}.mp3"), $"{i}")).ToList();
            foreach (Track t in tracks)
            {
                File.WriteAllBytes(t.Path, new byte[100]);
                await queue.AnalyseForDeckAsync(t, ct);
            }

            await queue.AnalyseForDeckAsync(tracks[^1], ct);
            await queue.AnalyseForDeckAsync(tracks[0], ct);

            Assert.Equal(tracks.Count + 1, engine.AnalysedPaths.Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
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

        await queue.AnalyseForDeckAsync(T("a"), TestContext.Current.CancellationToken);

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
        for (int i = 0; i < 100 && store.Get("/m/a.mp3") is null; i++) await Task.Delay(20, TestContext.Current.CancellationToken);

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
