using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class LibraryRowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-lib").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Writes empty files; tags fail to read, so titles fall back to file names.</summary>
    internal static string[] Files(string dir, params string[] names) => names.Select(n =>
    {
        string path = Path.Combine(dir, n + ".mp3");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }).ToArray();

    [Fact]
    public void UnknownBpmSortsAfterEveryKnownOne()
    {
        var rows = new[] { new LibraryRow(Track.FromPath("/a.mp3")) { Bpm = 128 },
                           new LibraryRow(Track.FromPath("/b.mp3")),
                           new LibraryRow(Track.FromPath("/c.mp3")) { Bpm = 90 } };

        Assert.Equal(["c", "a", "b"], rows.OrderBy(r => r.BpmSortKey).Select(r => r.Track.Title));
    }

    [Fact]
    public void RowsShowBpmAndPlayedState()
    {
        var row = new LibraryRow(Track.FromPath("/a.mp3"));
        Assert.Equal("—", row.BpmText);
        Assert.Equal("", row.PlayedMark);

        row.Bpm = 127.96;
        row.IsPlayed = true;

        Assert.Equal("128.0", row.BpmText);
        Assert.Equal("✓", row.PlayedMark);
        Assert.True(row.RowOpacity < 1);
    }

    [Fact]
    public async Task OpeningAFolderShowsCachedBpms()
    {
        string[] paths = Files(_dir, "a", "b");
        var store = TrackStore.InMemory();
        store.SetAnalysis(paths[0], FakeAudioEngine.Analysis(122));
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")),
            store, post: a => a(), manualAnalysis: true);

        await vm.BrowseFolderCommand.ExecuteAsync(_dir);

        Assert.Equal(2, vm.BrowserRows.Count);
        Assert.Equal(122, vm.BrowserRows.Single(r => r.Track.Path == paths[0]).Bpm);
        Assert.Null(vm.BrowserRows.Single(r => r.Track.Path == paths[1]).Bpm);
    }
}
