using Mkb.Mixer.App.Services;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

/// <summary>
/// Files that cannot be opened — moved, deleted, on a pulled SD card — must be
/// skipped and reported, never crash the app mid-set.
/// </summary>
public class UnplayableTrackTests
{
    private static Track T(string name) => new($"/m/{name}.mp3", name, Duration: TimeSpan.FromMinutes(3));

    private static (DeckViewModel Deck, FakeDeck Fake, List<string> Messages) Deck(params Track[] queue)
    {
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine());
        var messages = new List<string>();
        deck.PlaybackRefused += (_, why) => messages.Add(why);
        foreach (Track t in queue) deck.Playlist.Add(t);
        return (deck, fake, messages);
    }

    [Fact]
    public async Task AFailedLoadIsReportedAndLeavesTheDeckEmpty()
    {
        Track missing = T("missing");
        var (deck, fake, messages) = Deck();
        fake.Unloadable.Add(missing.Path);

        Assert.False(await deck.LoadAsync(missing));

        Assert.False(deck.HasTrack);
        Assert.Contains("could not open missing", Assert.Single(messages));
    }

    [Fact]
    public async Task PlayReportsWhyTheTopTrackWouldNotOpen()
    {
        Track missing = T("missing");
        var (deck, fake, messages) = Deck(missing);
        fake.Unloadable.Add(missing.Path);

        await deck.PlayCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.PlayCount);
        Assert.Contains("could not open missing", Assert.Single(messages));
    }

    [Fact]
    public async Task PlayNextSkipsTracksThatWillNotOpen()
    {
        Track gone = T("gone"), good = T("good");
        var (deck, fake, messages) = Deck(gone, good);
        fake.Unloadable.Add(gone.Path);

        Assert.True(await deck.PlayNextAsync());

        Assert.Same(good, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
        Assert.Single(messages);
    }

    [Fact]
    public async Task PlayNextGivesUpOnARepeatingQueueOfMissingFiles()
    {
        Track a = T("a"), b = T("b");
        var (deck, fake, _) = Deck(a, b);
        deck.Repeat = true;
        fake.Unloadable.UnionWith([a.Path, b.Path]);

        Assert.False(await deck.PlayNextAsync());

        Assert.Equal(2, fake.LoadCount);
        Assert.Equal(PlaybackState.Stopped, fake.State);
    }

    private static (AutoCue Cue, FakeDeck A, FakeDeck B, Queue<Track> QueueB, List<string> Messages) AutoCueSetup()
    {
        var a = new FakeDeck(DeckId.A) { Duration = TimeSpan.FromMinutes(4) };
        var b = new FakeDeck(DeckId.B) { Duration = TimeSpan.FromMinutes(4) };
        a.Load(T("playing"));
        a.Duration = TimeSpan.FromMinutes(4);
        var queueB = new Queue<Track>();
        var cue = new AutoCue(a, b, deck => deck == DeckId.B && queueB.Count > 0 ? queueB.Dequeue() : null)
        {
            Enabled = true,
            CrossfadeDuration = TimeSpan.FromSeconds(20)
        };
        var messages = new List<string>();
        cue.LoadFailed += (_, why) => messages.Add(why);
        a.Play();
        a.SetRemaining(TimeSpan.FromSeconds(10));
        return (cue, a, b, queueB, messages);
    }

    [Fact]
    public void AutoCueSkipsAnUnopenableTrackAndBringsInTheNext()
    {
        var (cue, _, b, queue, messages) = AutoCueSetup();
        Track gone = T("gone"), good = T("good");
        queue.Enqueue(gone);
        queue.Enqueue(good);
        b.Unloadable.Add(gone.Path);

        cue.Tick(TimeSpan.Zero);

        Assert.Equal(AutoCueState.Transitioning, cue.State);
        Assert.Same(good, b.Track);
        Assert.Equal(PlaybackState.Playing, b.State);
        Assert.Contains("skipped gone", Assert.Single(messages));
    }

    [Fact]
    public void AutoCueStopsTryingWhenEveryQueuedTrackFails()
    {
        var (cue, _, b, queue, messages) = AutoCueSetup();
        // Endless, as a repeating queue of missing files would be.
        for (int i = 0; i < 100; i++)
        {
            Track t = T($"gone{i}");
            queue.Enqueue(t);
            b.Unloadable.Add(t.Path);
        }

        cue.Tick(TimeSpan.Zero);
        cue.Tick(TimeSpan.FromMilliseconds(100));

        Assert.Equal(AutoCueState.Idle, cue.State);
        Assert.Equal(AutoCue.MaxLoadAttempts, b.LoadCount);
        Assert.Equal(AutoCue.MaxLoadAttempts, messages.Count);
    }

    [Fact]
    public void AutoCueTriesAgainForTheNextOutgoingTrack()
    {
        var (cue, a, b, queue, _) = AutoCueSetup();
        for (int i = 0; i < AutoCue.MaxLoadAttempts; i++)
        {
            Track t = T($"gone{i}");
            queue.Enqueue(t);
            b.Unloadable.Add(t.Path);
        }
        cue.Tick(TimeSpan.Zero);

        a.Load(T("another"));
        a.Duration = TimeSpan.FromMinutes(4);
        a.Play();
        a.SetRemaining(TimeSpan.FromSeconds(10));
        Track good = T("good");
        queue.Enqueue(good);
        cue.Tick(TimeSpan.FromSeconds(1));

        Assert.Same(good, b.Track);
    }
}

public class ShutdownAndPersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-robust").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ASavedCrossfadeOfZeroIsRaisedToOneSecond()
    {
        string path = Path.Combine(_dir, "settings.json");
        new SettingsStore(path).Save(new AppSettings { CrossfadeSeconds = 0 });

        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(path));

        Assert.Equal(1, vm.CrossfadeSeconds);
    }

    [Fact]
    public void SavingPlaylistsWhereNothingCanBeWrittenReportsInsteadOfThrowing()
    {
        // A file where the folder should be: creating the folder fails with an IOException.
        string blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "");
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")))
        {
            PlaylistFolder = Path.Combine(blocked, "playlists")
        };

        vm.SaveState();

        Assert.StartsWith("Could not save playlists", vm.StatusMessage);
    }

    private MainViewModel ViewModelWithPlaylistsIn(string folder) =>
        new(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json"))) { PlaylistFolder = folder };

    [Fact]
    public void PlaylistsSavedOnTheWayOutAreRestoredOnTheNextLaunch()
    {
        string folder = Path.Combine(_dir, "playlists");
        var before = ViewModelWithPlaylistsIn(folder);
        before.DeckA.Playlist.Add(new Track("/m/a.mp3", "A"));
        before.DeckB.Playlist.Add(new Track("/m/b.mp3", "B"));
        before.DeckB.Playlist.Add(new Track("/m/c.mp3", "C"));
        before.SaveState();

        var after = ViewModelWithPlaylistsIn(folder);
        after.RestorePlaylists();

        Assert.Equal(["/m/a.mp3"], after.DeckA.Playlist.Select(t => t.Path));
        Assert.Equal(["/m/b.mp3", "/m/c.mp3"], after.DeckB.Playlist.Select(t => t.Path));
    }

    [Fact]
    public void RestoringPlaylistsOnLaunchLeavesTheStatusBarAlone()
    {
        string folder = Path.Combine(_dir, "playlists");
        var before = ViewModelWithPlaylistsIn(folder);
        before.DeckA.Playlist.Add(new Track("/m/a.mp3", "A"));
        before.SaveState();

        var after = ViewModelWithPlaylistsIn(folder);
        string status = after.StatusMessage;
        after.RestorePlaylists();

        Assert.Equal(status, after.StatusMessage);
    }

    [Fact]
    public void RestoringWithNoSavedPlaylistsLeavesTheDecksEmpty()
    {
        var vm = ViewModelWithPlaylistsIn(Path.Combine(_dir, "never-saved"));

        vm.RestorePlaylists();

        Assert.Empty(vm.DeckA.Playlist);
        Assert.Empty(vm.DeckB.Playlist);
    }

    [Fact]
    public void SavingStateQuietlyLeavesTheStatusBarAlone()
    {
        // Android saves on every pause; "Playlists saved" each time would bury real messages.
        var vm = ViewModelWithPlaylistsIn(Path.Combine(_dir, "playlists"));
        vm.StatusMessage = "Headphone cue is off: device gone";

        vm.SaveState();

        Assert.Equal("Headphone cue is off: device gone", vm.StatusMessage);
    }

    [Fact]
    public void SavingPlaylistsByHandStillSaysWhere()
    {
        string folder = Path.Combine(_dir, "playlists");
        var vm = ViewModelWithPlaylistsIn(folder);

        vm.SavePlaylistsCommand.Execute(null);

        Assert.Equal($"Playlists saved to {folder}", vm.StatusMessage);
    }

    [Fact]
    public void ForgettingDeletedTracksRunsInTheBackground()
    {
        string deleted = Path.Combine(_dir, "deleted.mp3");
        File.WriteAllBytes(deleted, new byte[10]);
        var tracks = TrackStore.InMemory();
        tracks.MarkPlayed(deleted, DateTime.UtcNow);
        File.Delete(deleted);
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")), tracks);

        vm.ForgetDeletedTracks();

        Assert.True(SpinWait.SpinUntil(() => tracks.Get(deleted) is null, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SavingAPlaylistLeavesNoTempFileBehind()
    {
        string path = Path.Combine(_dir, "deck-a.m3u");

        M3uPlaylist.Save(path, [new Track("/m/a.mp3", "A")]);
        M3uPlaylist.Save(path, [new Track("/m/b.mp3", "B")]);

        Assert.Equal(["deck-a.m3u"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.Equal("/m/b.mp3", Assert.Single(M3uPlaylist.Load(path)).Path);
    }

    [Fact]
    public async Task DisposeWaitsForADeckAnalysisToStop()
    {
        var engine = new FakeAudioEngine();
        engine.Blocking.Add("/m/long.mp3");
        var queue = new AnalysisQueue(engine, TrackStore.InMemory(), post: a => a());
        Task<TrackAnalysis> analysis = queue.AnalyseForDeckAsync(new Track("/m/long.mp3", "Long"));

        queue.Dispose();

        Assert.True(engine.BlockingAnalysisStopped);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
    }

    [Fact]
    public void DisposeWaitsForTheBackgroundWorkerToStop()
    {
        var engine = new FakeAudioEngine();
        engine.Blocking.Add("/m/long.mp3");
        var queue = new AnalysisQueue(engine, TrackStore.InMemory(), post: a => a());
        queue.QueueFolder([new Track("/m/long.mp3", "Long")]);
        SpinWait.SpinUntil(() => engine.AnalysedPaths.Count > 0, TimeSpan.FromSeconds(5));

        queue.Dispose();

        Assert.True(engine.BlockingAnalysisStopped);
    }
}
