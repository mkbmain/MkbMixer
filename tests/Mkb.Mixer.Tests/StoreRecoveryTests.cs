using System.Text.Json;
using System.Text.RegularExpressions;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class StoreRecoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-recovery").FullName;
    private string TracksPath => Path.Combine(_dir, "tracks.json");
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        // The read-only test leaves the directory unwritable.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.Delete(_dir, recursive: true);
    }

    private string[] CorruptCopies() => Directory.GetFiles(_dir, "*.corrupt-*");

    private bool DirectoryIsReadOnly()
    {
        if (OperatingSystem.IsWindows()) return false;
        File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        string probe = Path.Combine(_dir, "probe");
        try { File.WriteAllText(probe, ""); File.Delete(probe); return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private const string CorruptPattern = @"tracks\.json\.corrupt-\d{8}-\d{6}$";

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    public void BadTrackFileIsMovedAsideAndStoreStartsEmpty(string content)
    {
        File.WriteAllText(TracksPath, content);

        using var store = new TrackStore(TracksPath);

        Assert.NotNull(store.LoadProblem);
        Assert.Equal("tracks.json", store.LoadProblem.FileName);
        Assert.Matches(CorruptPattern, store.LoadProblem.MovedTo);
        Assert.Equal(content, File.ReadAllText(store.LoadProblem.MovedTo!));
        Assert.False(File.Exists(TracksPath));
        Assert.Empty(store.RecentlyPlayed(10));
    }

    [Fact]
    public void ValidTrackFileLoadsWithoutProblem()
    {
        using (var store = new TrackStore(TracksPath))
            store.Update("/a.mp3", i => i.WithHotCue(1, 5));

        using var reloaded = new TrackStore(TracksPath);

        Assert.Null(reloaded.LoadProblem);
        Assert.Empty(CorruptCopies());
        Assert.Equal(5, reloaded.Get("/a.mp3")!.HotCueSeconds![1]);
    }

    [Fact]
    public void MissingTrackFileIsNotAProblem()
    {
        using var store = new TrackStore(TracksPath);
        Assert.Null(store.LoadProblem);
    }

    [Fact]
    public void SaveAfterRecoveryWritesFreshFileAndKeepsCorruptCopy()
    {
        File.WriteAllText(TracksPath, "{not json");
        using var store = new TrackStore(TracksPath);
        string copy = store.LoadProblem!.MovedTo!;
        byte[] before = File.ReadAllBytes(copy);

        store.Update("/a.mp3", i => i.WithHotCue(0, 1));
        store.Flush();

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(TracksPath));
        Assert.True(doc.RootElement.TryGetProperty("/a.mp3", out _));
        Assert.Equal(before, File.ReadAllBytes(copy));
    }

    [Fact]
    public void SecondCorruptCopyInSameSecondGetsDistinctName()
    {
        var now = new DateTime(2026, 10, 22, 15, 30, 0);
        File.WriteAllText(TracksPath, "first");
        string? one = CorruptFiles.SetAside(TracksPath, now);
        File.WriteAllText(TracksPath, "second");
        string? two = CorruptFiles.SetAside(TracksPath, now);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one, two);
        Assert.Equal("first", File.ReadAllText(one!));
        Assert.Equal("second", File.ReadAllText(two!));
        Assert.EndsWith("tracks.json.corrupt-20261022-153000", one);
    }

    [Fact]
    public void RenameFailureDisablesTrackWrites()
    {
        File.WriteAllText(TracksPath, "{not json");
        if (!DirectoryIsReadOnly()) return;   // root or Windows: cannot be provoked here

        using var store = new TrackStore(TracksPath);
        Assert.NotNull(store.LoadProblem);
        Assert.Null(store.LoadProblem.MovedTo);

        store.Update("/a.mp3", i => i.WithHotCue(0, 1));
        store.Flush();

        Assert.Equal("{not json", File.ReadAllText(TracksPath));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    public void BadSettingsFileIsMovedAsideAndDefaultsUsed(string content)
    {
        File.WriteAllText(SettingsPath, content);
        var store = new SettingsStore(SettingsPath);

        AppSettings loaded = store.Load();

        Assert.Equal(new AppSettings().CrossfadeSeconds, loaded.CrossfadeSeconds);
        Assert.NotNull(store.LoadProblem);
        Assert.Equal("settings.json", store.LoadProblem.FileName);
        Assert.Matches(@"settings\.json\.corrupt-\d{8}-\d{6}$", store.LoadProblem.MovedTo);
        Assert.Equal(content, File.ReadAllText(store.LoadProblem.MovedTo!));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void ValidSettingsFileLoadsWithoutProblem()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { CrossfadeSeconds = 7 });

        AppSettings loaded = store.Load();

        Assert.Equal(7, loaded.CrossfadeSeconds);
        Assert.Null(store.LoadProblem);
        Assert.Empty(CorruptCopies());
    }

    [Theory]
    [InlineData("""{"CrossfadeSeconds":7,"Shortcuts":{"deckA.playPause":5,"deckA.cue":true,"deckA.sync":{},"deckB.cue":"Ctrl+W"}}""")]
    [InlineData("""{"CrossfadeSeconds":7,"Shortcuts":5}""")]
    [InlineData("""{"CrossfadeSeconds":7,"Shortcuts":["Ctrl+W"]}""")]
    public void OddShortcutValuesDoNotCostTheRestOfTheSettings(string content)
    {
        File.WriteAllText(SettingsPath, content);
        var store = new SettingsStore(SettingsPath);

        AppSettings loaded = store.Load();

        Assert.Null(store.LoadProblem);
        Assert.Equal(7, loaded.CrossfadeSeconds);
        Assert.Empty(CorruptCopies());
        if (loaded.Shortcuts is { } shortcuts)
        {
            Assert.Null(shortcuts["deckA.playPause"]);
            Assert.Null(shortcuts["deckA.cue"]);
            Assert.Null(shortcuts["deckA.sync"]);
            Assert.Equal("Ctrl+W", shortcuts["deckB.cue"]);
        }
    }

    [Fact]
    public void MissingSettingsFileIsNotAProblem()
    {
        var store = new SettingsStore(SettingsPath);
        store.Load();
        Assert.Null(store.LoadProblem);
    }

    [Fact]
    public void SaveAfterSettingsRecoveryWritesFreshFile()
    {
        File.WriteAllText(SettingsPath, "{not json");
        var store = new SettingsStore(SettingsPath);
        store.Load();

        store.Save(new AppSettings { CrossfadeSeconds = 9 });

        Assert.Equal(9, new SettingsStore(SettingsPath).Load().CrossfadeSeconds);
        Assert.Equal("{not json", File.ReadAllText(store.LoadProblem!.MovedTo!));
    }

    [Fact]
    public void SaveAfterFailedSettingsRenameLeavesFileUntouched()
    {
        File.WriteAllText(SettingsPath, "{not json");
        if (!DirectoryIsReadOnly()) return;

        var store = new SettingsStore(SettingsPath);
        store.Load();
        Assert.Null(store.LoadProblem!.MovedTo);

        store.Save(new AppSettings { CrossfadeSeconds = 9 });

        Assert.Equal("{not json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void DescribeLoadProblemsCombinesBoth()
    {
        string? text = MainViewModel.DescribeLoadProblems(
            new LoadProblem("tracks.json", "tracks.json.corrupt-20261022-153000"),
            new LoadProblem("settings.json", null));

        Assert.Contains("tracks.json", text);
        Assert.Contains("settings.json", text);
    }

    [Fact]
    public void DescribeLoadProblemsIsNullWhenNothingWentWrong() =>
        Assert.Null(MainViewModel.DescribeLoadProblems(null, null));
}
