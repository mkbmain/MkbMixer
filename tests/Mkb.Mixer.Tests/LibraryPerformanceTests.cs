using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

/// <summary>The library's indexed lookups, batched search, parallel tag reads and debounced filter.</summary>
public class LibraryPerformanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-perf").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static LibraryViewModel NewLibrary(TrackStore store, Func<string, Track>? read = null) =>
        new(store, new AnalysisQueue(new FakeAudioEngine(), store, manual: true),
            audibleBpm: () => null, readTrack: read) { FilterDelay = TimeSpan.Zero };

    private static string[] Titles(LibraryViewModel lib) => lib.BrowserRows.Select(r => r.Track.Title).ToArray();

    [Fact]
    public async Task ARowThatStartsMatchingIsAddedOnceAndTheSelectionSurvives()
    {
        string[] paths = LibraryRowTests.Files(_dir, "a", "b");
        var store = TrackStore.InMemory();
        store.SetAnalysis(paths[0], FakeAudioEngine.Analysis(120));
        LibraryViewModel lib = NewLibrary(store);
        await lib.BrowseFolderCommand.ExecuteAsync(_dir);
        lib.BpmMin = 110; lib.BpmMax = 130;
        LibraryRow selected = lib.BrowserRows.Single();
        lib.SelectedBrowserRow = selected;

        store.SetAnalysis(paths[1], FakeAudioEngine.Analysis(125));
        lib.RefreshBpm(paths[1]);
        lib.RefreshBpm(paths[1]);

        Assert.Equal(["a", "b"], Titles(lib));
        Assert.Same(selected, lib.SelectedBrowserRow);
        Assert.Same(selected, lib.BrowserRows[0]);
    }

    [Fact]
    public async Task MarkingAPlayedTrackTicksItsRow()
    {
        string[] paths = LibraryRowTests.Files(_dir, "a", "b");
        LibraryViewModel lib = NewLibrary(TrackStore.InMemory());
        await lib.BrowseFolderCommand.ExecuteAsync(_dir);

        lib.MarkPlayed(Track.FromPath(paths[1]));

        Assert.Equal([false, true], lib.BrowserRows.OrderBy(r => r.Track.Title).Select(r => r.IsPlayed));
    }

    [Fact]
    public async Task AFilterThatChangesNothingLeavesTheListAlone()
    {
        string[] paths = LibraryRowTests.Files(_dir, "a", "b");
        var store = TrackStore.InMemory();
        store.SetAnalysis(paths[0], FakeAudioEngine.Analysis(120));
        store.SetAnalysis(paths[1], FakeAudioEngine.Analysis(128));
        LibraryViewModel lib = NewLibrary(store);
        await lib.BrowseFolderCommand.ExecuteAsync(_dir);
        lib.BpmMin = 100;
        int changes = 0;
        lib.BrowserRows.CollectionChanged += (_, _) => changes++;

        lib.BpmMin = 101;
        lib.SearchText = ".mp3";

        Assert.Equal(0, changes);
        Assert.Equal(2, lib.BrowserRows.Count);
    }

    [Fact]
    public async Task ASearchWithManyHitsShowsEachOnce()
    {
        string[] names = Enumerable.Range(0, 130).Select(i => $"hit{i:000}").ToArray();
        LibraryRowTests.Files(_dir, names);
        LibraryRowTests.Files(_dir, "other");
        LibraryViewModel lib = NewLibrary(TrackStore.InMemory());
        lib.CurrentFolder = _dir;
        var messages = new List<string>();
        lib.Status += (_, m) => { lock (messages) messages.Add(m); };
        lib.SearchText = "hit";

        await lib.SearchCommand.ExecuteAsync(null);

        Assert.Equal(names, Titles(lib).Order());
        Assert.Equal("130 match(es) for \"hit\"", messages[^1]);
        Assert.False(lib.IsScanning);
    }

    [Fact]
    public async Task TagsReadInParallelKeepTheFolderOrder()
    {
        LibraryRowTests.Files(_dir, Enumerable.Range(0, 20).Select(i => $"t{i:00}").ToArray());
        string[] expected = Directory.EnumerateFiles(_dir).Select(Path.GetFileNameWithoutExtension).ToArray()!;
        var random = new Random(1);
        LibraryViewModel lib = NewLibrary(TrackStore.InMemory(), path =>
        {
            int ms;
            lock (random) ms = random.Next(10);
            Thread.Sleep(ms);   // out-of-order completion
            return Track.FromPath(path);
        });

        await lib.BrowseFolderCommand.ExecuteAsync(_dir);

        Assert.Equal(expected, Titles(lib));
    }

    [Fact]
    public async Task TypingFiltersOnceTheTypingPauses()
    {
        LibraryRowTests.Files(_dir, "alpha", "beta");
        LibraryViewModel lib = NewLibrary(TrackStore.InMemory());
        await lib.BrowseFolderCommand.ExecuteAsync(_dir);
        lib.FilterDelay = TimeSpan.FromMilliseconds(200);

        lib.SearchText = "b";
        lib.SearchText = "be";
        Assert.Equal(2, lib.BrowserRows.Count);   // not yet

        for (int i = 0; i < 100 && lib.BrowserRows.Count != 1; i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(["beta"], Titles(lib));
    }
}
