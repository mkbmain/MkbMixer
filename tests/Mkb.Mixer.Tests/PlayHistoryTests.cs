using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class PlayHistoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-history").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Track T(string name, double seconds = 180) =>
        new($"/m/{name}.mp3", name, Duration: TimeSpan.FromSeconds(seconds));

    private static async Task<(DeckViewModel Deck, FakeDeck Fake, List<Track> Played)> Playing(Track track)
    {
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine());
        var played = new List<Track>();
        deck.TrackPlayed += (_, t) => played.Add(t);
        await deck.LoadAsync(track);
        await deck.PlayCommand.ExecuteAsync(null);
        return (deck, fake, played);
    }

    [Fact]
    public async Task ATrackCountsAsPlayedAfterThirtySecondsOnce()
    {
        var (deck, fake, played) = await Playing(T("a"));

        fake.Position = TimeSpan.FromSeconds(29); deck.Refresh();
        Assert.Empty(played);

        fake.Position = TimeSpan.FromSeconds(30); deck.Refresh();
        fake.Position = TimeSpan.FromSeconds(45); deck.Refresh();
        Assert.Single(played);
    }

    [Fact]
    public async Task AShortTrackCountsAtNinetyPercent()
    {
        var (deck, fake, played) = await Playing(T("short", seconds: 20));

        fake.Position = TimeSpan.FromSeconds(18); deck.Refresh();

        Assert.Single(played);
    }

    [Fact]
    public async Task APausedDeckDoesNotCount()
    {
        var (deck, fake, played) = await Playing(T("a"));
        deck.PauseCommand.Execute(null);

        fake.Position = TimeSpan.FromSeconds(60); deck.Refresh();

        Assert.Empty(played);
    }

    [Fact]
    public async Task ATrackTheAutoCueLoadedCountsToo()
    {
        var (deck, fake, played) = await Playing(T("a"));
        fake.Position = TimeSpan.FromSeconds(31); deck.Refresh();

        fake.Load(T("b")); fake.Play(); deck.Refresh();
        fake.Position = TimeSpan.FromSeconds(31); deck.Refresh();

        Assert.Equal(["a", "b"], played.Select(t => t.Title));
    }

    [Fact]
    public async Task PlayingMarksTheRowAndTheStore()
    {
        string path = LibraryRowTests.Files(_dir, "a")[0];
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(_dir, "s.json")), store,
            post: a => a(), manualAnalysis: true);
        await vm.BrowseFolderCommand.ExecuteAsync(_dir);
        LibraryRow row = vm.BrowserRows.Single();

        await vm.DeckA.LoadAsync(new Track(path, "a", Duration: TimeSpan.FromMinutes(3)));
        await vm.DeckA.PlayCommand.ExecuteAsync(null);
        engine.A.Position = TimeSpan.FromSeconds(31);
        vm.Tick(TimeSpan.Zero);

        Assert.True(row.IsPlayed);
        Assert.Equal(1, store.Get(path)!.PlayCount);
    }

    [Fact]
    public async Task RecentlyPlayedIsNewestFirstAndSkipsMissingFiles()
    {
        string[] paths = LibraryRowTests.Files(_dir, "a", "b", "c");
        var store = TrackStore.InMemory();
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 3; i++) store.MarkPlayed(paths[i], t0.AddHours(i));
        File.Delete(paths[2]);
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")), store,
            post: a => a(), manualAnalysis: true);

        await vm.ShowRecentlyPlayedAsync();

        Assert.Equal(["b", "a"], vm.BrowserRows.Select(r => r.Track.Title));
    }

    [Fact]
    public void RecentlyPlayedIsTheFirstRoot()
    {
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")));

        vm.LoadRoots();

        Assert.True(vm.Roots[0].IsRecentlyPlayed);
        Assert.Empty(vm.Roots[0].Children);
    }
}
