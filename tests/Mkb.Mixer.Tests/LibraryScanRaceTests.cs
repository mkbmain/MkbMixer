using System.Collections.Concurrent;
using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

/// <summary>
/// Opening a folder while another is still being read. The tag reader is held
/// open per folder, so the overlap happens the same way every run.
/// </summary>
public class LibraryScanRaceTests : IDisposable
{
    private readonly string _a = Directory.CreateTempSubdirectory("mkb-race-a").FullName;
    private readonly string _b = Directory.CreateTempSubdirectory("mkb-race-b").FullName;
    private readonly ConcurrentDictionary<string, Gate> _gates = new();

    public void Dispose()
    {
        foreach (Gate g in _gates.Values) g.Release.TrySetResult();
        Directory.Delete(_a, recursive: true);
        Directory.Delete(_b, recursive: true);
    }

    private sealed record Gate(TaskCompletionSource Entered, TaskCompletionSource Release);

    /// <summary>Makes reading any file in this folder wait until the returned gate is released.</summary>
    private Gate Hold(string folder) =>
        _gates[folder] = new Gate(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                                  new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private Track Read(string path)
    {
        if (_gates.TryGetValue(Path.GetDirectoryName(path)!, out Gate? gate))
        {
            gate.Entered.TrySetResult();
            gate.Release.Task.Wait(TimeSpan.FromSeconds(10));
        }
        return Track.FromPath(path);
    }

    private LibraryViewModel NewLibrary()
    {
        LibraryRowTests.Files(_a, "from-a");
        LibraryRowTests.Files(_b, "from-b");
        var store = TrackStore.InMemory();
        return new LibraryViewModel(store, new AnalysisQueue(new FakeAudioEngine(), store, manual: true),
            audibleBpm: () => null, readTrack: Read);
    }

    private static string[] Titles(LibraryViewModel lib) => lib.BrowserRows.Select(r => r.Track.Title).ToArray();

    [Fact]
    public async Task AFolderThatFinishesReadingAfterBeingLeftDoesNotReplaceTheNewOne()
    {
        LibraryViewModel lib = NewLibrary();
        Gate a = Hold(_a);

        Task openA = lib.BrowseFolderCommand.ExecuteAsync(_a);
        await a.Entered.Task;   // reading A's only file
        await lib.BrowseFolderCommand.ExecuteAsync(_b);
        a.Release.SetResult();
        await openA;

        Assert.Equal(["from-b"], Titles(lib));
        Assert.Equal(_b, lib.CurrentFolder);
    }

    [Fact]
    public async Task TheScanningIndicatorStaysOnUntilTheNewestFolderIsRead()
    {
        LibraryViewModel lib = NewLibrary();
        Gate a = Hold(_a), b = Hold(_b);

        Task openA = lib.BrowseFolderCommand.ExecuteAsync(_a);
        await a.Entered.Task;
        Task openB = lib.BrowseFolderCommand.ExecuteAsync(_b);
        await b.Entered.Task;
        a.Release.SetResult();
        await openA;

        Assert.True(lib.IsScanning);   // B is still being read

        b.Release.SetResult();
        await openB;

        Assert.False(lib.IsScanning);
        Assert.Equal(["from-b"], Titles(lib));
    }

    [Fact]
    public async Task ASearchSupersededByOpeningAFolderDoesNotSayItWasCancelled()
    {
        LibraryViewModel lib = NewLibrary();
        lib.CurrentFolder = _a;
        var messages = new ConcurrentQueue<string>();
        lib.Status += (_, m) => messages.Enqueue(m);
        Gate a = Hold(_a);

        lib.SearchText = "from";
        Task search = lib.SearchCommand.ExecuteAsync(null);
        await a.Entered.Task;
        await lib.BrowseFolderCommand.ExecuteAsync(_b);
        a.Release.SetResult();
        await search;

        Assert.DoesNotContain("Search cancelled", messages);
        Assert.Equal(["from-b"], Titles(lib));
    }

    [Fact]
    public async Task StoppingASearchSaysSo()
    {
        LibraryViewModel lib = NewLibrary();
        lib.CurrentFolder = _a;
        var messages = new ConcurrentQueue<string>();
        lib.Status += (_, m) => messages.Enqueue(m);
        Gate a = Hold(_a);

        lib.SearchText = "from";
        Task search = lib.SearchCommand.ExecuteAsync(null);
        await a.Entered.Task;
        lib.CancelScanCommand.Execute(null);
        a.Release.SetResult();
        await search;

        Assert.Contains("Search cancelled", messages);
        Assert.False(lib.IsScanning);
        Assert.Empty(lib.BrowserRows);
    }
}
