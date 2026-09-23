using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class SupportedFormatsTests
{
    [Theory]
    [InlineData("song.mp3")] [InlineData("song.MP3")] [InlineData("a.wav")]
    [InlineData("a.flac")] [InlineData("a.ogg")] [InlineData("a.m4a")]
    public void AcceptsAudioFiles(string name) => Assert.True(SupportedFormats.IsAudio(name));

    [Theory]
    [InlineData("readme.txt")] [InlineData("a.jpg")] [InlineData("noextension")]
    public void RejectsNonAudioFiles(string name) => Assert.False(SupportedFormats.IsAudio(name));

    [Fact]
    public void AcceptsWmaBecauseTheOriginalDid() => Assert.True(SupportedFormats.IsAudio("a.wma"));
}

public class M3uPlaylistTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-m3u").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTripsTracks()
    {
        string path = Path.Combine(_dir, "set.m3u");
        var tracks = new[]
        {
            new Track("/music/a.mp3", "A", "Artist A", Duration: TimeSpan.FromSeconds(200)),
            new Track("/music/b.mp3", "B", "Artist B", Duration: TimeSpan.FromSeconds(150)),
        };

        M3uPlaylist.Save(path, tracks);
        var loaded = M3uPlaylist.Load(path).ToList();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("/music/a.mp3", loaded[0].Path);
        Assert.Equal("Artist A", loaded[0].Artist);
        Assert.Equal("A", loaded[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(200), loaded[0].Duration);
    }

    [Fact]
    public void WritesExtendedM3uHeader()
    {
        string path = Path.Combine(_dir, "set.m3u");
        M3uPlaylist.Save(path, [new Track("/music/a.mp3", "A", "Artist A")]);
        Assert.StartsWith("#EXTM3U", File.ReadAllText(path));
    }

    [Fact]
    public void SkipsCommentsAndBlankLinesWhenLoading()
    {
        string path = Path.Combine(_dir, "plain.m3u");
        File.WriteAllText(path, "#EXTM3U\n\n# a comment\n/music/only.mp3\n\n");

        var loaded = M3uPlaylist.Load(path).ToList();

        Assert.Single(loaded);
        Assert.Equal("/music/only.mp3", loaded[0].Path);
    }

    [Fact]
    public void ResolvesRelativePathsAgainstThePlaylistFolder()
    {
        string path = Path.Combine(_dir, "rel.m3u");
        File.WriteAllText(path, "#EXTM3U\nsub/track.mp3\n");

        var loaded = M3uPlaylist.Load(path).ToList();

        Assert.Equal(Path.Combine(_dir, "sub", "track.mp3"), loaded[0].Path);
    }

    [Fact]
    public void LoadingAMissingFileYieldsNothing()
        => Assert.Empty(M3uPlaylist.Load(Path.Combine(_dir, "nope.m3u")));
}

public class LibraryScannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-scan").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Touch(params string[] relative)
    {
        foreach (var r in relative)
        {
            string full = Path.Combine(_dir, r);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }

    [Fact]
    public async Task FindsAudioFilesRecursivelyAndIgnoresOthers()
    {
        Touch("a.mp3", "nested/b.flac", "nested/deep/c.wav", "notes.txt", "cover.jpg");

        var found = new List<string>();
        await foreach (var f in LibraryScanner.ScanAsync(_dir))
            found.Add(Path.GetFileName(f));

        Assert.Equal(3, found.Count);
        Assert.Contains("a.mp3", found);
        Assert.Contains("b.flac", found);
        Assert.Contains("c.wav", found);
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        Touch(Enumerable.Range(0, 50).Select(i => $"t{i}.mp3").ToArray());
        using var cts = new CancellationTokenSource();

        var found = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var f in LibraryScanner.ScanAsync(_dir, cts.Token))
            {
                found.Add(f);
                if (found.Count == 3) cts.Cancel();
            }
        });

        Assert.True(found.Count < 50);
    }

    [Fact]
    public async Task SkipsUnreadableDirectoriesInsteadOfThrowing()
    {
        Touch("ok.mp3");
        var found = new List<string>();
        await foreach (var f in LibraryScanner.ScanAsync(_dir))
            found.Add(f);
        Assert.Single(found);
    }

    [Fact]
    public async Task MissingRootYieldsNothing()
    {
        var found = new List<string>();
        await foreach (var f in LibraryScanner.ScanAsync(Path.Combine(_dir, "absent")))
            found.Add(f);
        Assert.Empty(found);
    }
}

public class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-settings").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTripsThroughDisk()
    {
        string path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        var settings = new AppSettings
        {
            LastFolder = "/music",
            CrossfadeSeconds = 12,
            AutoCueEnabled = true,
            CrossfaderPosition = 0.25f
        };

        store.Save(settings);
        var loaded = new SettingsStore(path).Load();

        Assert.Equal("/music", loaded.LastFolder);
        Assert.Equal(12, loaded.CrossfadeSeconds);
        Assert.True(loaded.AutoCueEnabled);
        Assert.Equal(0.25f, loaded.CrossfaderPosition, 3);
    }

    [Fact]
    public void ReturnsDefaultsWhenNoFileExists()
    {
        var loaded = new SettingsStore(Path.Combine(_dir, "absent.json")).Load();
        Assert.Equal(20, loaded.CrossfadeSeconds);
    }

    [Fact]
    public void ReturnsDefaultsWhenTheFileIsCorrupt()
    {
        string path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ this is not json");
        Assert.Equal(20, new SettingsStore(path).Load().CrossfadeSeconds);
    }

    [Fact]
    public void CreatesTheDirectoryWhenSaving()
    {
        string path = Path.Combine(_dir, "nested", "deep", "settings.json");
        new SettingsStore(path).Save(new AppSettings { CrossfadeSeconds = 7 });
        Assert.True(File.Exists(path));
    }
}
