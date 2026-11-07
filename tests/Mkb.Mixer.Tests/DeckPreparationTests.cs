using Avalonia.Threading;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

/// <summary>
/// Opening a file is too slow for the UI thread, so the deck view model opens tracks
/// ahead of time and its load paths wait for that. Waiting opens a window the old
/// synchronous load did not have, which is what the overlap tests here are about.
/// </summary>
public class DeckPreparationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

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
    public void TheNextTrackIsPreparedWhenTheQueueChanges()
    {
        Track a = T("a"), b = T("b");
        var (deck, fake, _) = Deck();

        deck.Playlist.Add(a);
        Assert.Equal(a.Path, fake.PreparedPaths[^1]);

        deck.Playlist.Insert(0, b);
        Assert.Equal(b.Path, fake.PreparedPaths[^1]);
    }

    [Fact]
    public void AnEmptyQueuePreparesNothing()
    {
        Track a = T("a");
        var (deck, fake, _) = Deck(a);
        int before = fake.PreparedPaths.Count;

        deck.Playlist.Remove(a);

        Assert.Equal(before, fake.PreparedPaths.Count);
    }

    [Fact]
    public void DequeueingPreparesTheFollowingTrackWithoutForgettingTheDequeuedOne()
    {
        // The order the two-chain ruling exists for: the following track is asked for
        // before the caller loads the dequeued one. The real deck's half of this is in
        // SoundFlowDeckPreparationTests.
        Track a = T("a"), b = T("b"), c = T("c");
        var (deck, fake, _) = Deck(a, b, c);
        Assert.Equal(a.Path, fake.PreparedPaths[^1]);

        Assert.Same(a, deck.DequeueNext());

        Assert.Equal(b.Path, fake.PreparedPaths[^1]);
        Assert.Equal(0, fake.LoadCount);
    }

    [Fact]
    public async Task LoadWaitsForThePreparation()
    {
        Track x = T("x");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(x.Path);

        Task<bool> load = deck.LoadAsync(x);

        Assert.Contains(x.Path, fake.PreparedPaths);
        Assert.Equal(0, fake.LoadCount);

        fake.ReleasePreparation(x.Path);
        Assert.True(await load.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(x, fake.Track);
    }

    [Fact]
    public async Task PlayPreparesBeforeLoading()
    {
        Track x = T("x");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(x.Path);
        deck.Playlist.Add(x);

        Task play = deck.PlayCommand.ExecuteAsync(null);

        Assert.Equal(0, fake.LoadCount);
        Assert.Equal(PlaybackState.Stopped, fake.State);

        fake.ReleasePreparation(x.Path);
        await play.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Same(x, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
    }

    [Fact]
    public async Task PlayNextPreparesBeforeLoading()
    {
        Track x = T("x"), y = T("y");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(x.Path);
        deck.Playlist.Add(x);
        deck.Playlist.Add(y);

        Task<bool> play = deck.PlayNextAsync();

        Assert.Equal(0, fake.LoadCount);
        Assert.Equal(y.Path, fake.PreparedPaths[^2]);
        Assert.Equal(x.Path, fake.PreparedPaths[^1]);

        fake.ReleasePreparation(x.Path);
        Assert.True(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(x, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
    }

    [Fact]
    public async Task AFailedPreparationLeavesLoadWorking()
    {
        Track x = T("x");
        var (deck, fake, messages) = Deck();
        fake.Unpreparable.Add(x.Path);

        Assert.True(await deck.LoadAsync(x));

        Assert.Same(x, fake.Track);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task APreparationThatThrowsLeavesTheQueueAndPlayWorking()
    {
        Track x = T("x");
        var (deck, fake, messages) = Deck();
        fake.PrepareThrows.Add(x.Path);

        deck.Playlist.Add(x);   // the fire-and-forget prefetch must not throw out of here
        await deck.PlayCommand.ExecuteAsync(null);

        Assert.Same(x, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task AFailedPrefetchLeavesPlayNextWorking()
    {
        Track x = T("x");
        var (deck, fake, messages) = Deck();
        fake.Unpreparable.Add(x.Path);
        deck.Playlist.Add(x);

        Assert.True(await deck.PlayNextAsync());

        Assert.Same(x, fake.Track);
        Assert.Empty(messages);
    }

    [Fact]
    public async Task ThePreparationFailingAndTheLoadFailingReportsTheLoadsError()
    {
        Track x = T("x");
        var (deck, fake, messages) = Deck();
        fake.Unpreparable.Add(x.Path);
        fake.Unloadable.Add(x.Path);

        Assert.False(await deck.LoadAsync(x));

        Assert.Contains("could not open x", Assert.Single(messages));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheNewestLoadWinsWhicheverPreparationFinishesFirst(bool newestFinishesFirst)
    {
        Track older = T("older"), newest = T("newest");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(older.Path);
        fake.HoldPreparation(newest.Path);

        Task<bool> first = deck.LoadAsync(older);
        Task<bool> second = deck.LoadAsync(newest);

        if (newestFinishesFirst)
        {
            fake.ReleasePreparation(newest.Path);
            Assert.True(await second.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            fake.ReleasePreparation(older.Path);
            Assert.False(await first.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }
        else
        {
            fake.ReleasePreparation(older.Path);
            Assert.False(await first.WaitAsync(Timeout, TestContext.Current.CancellationToken));
            Assert.Equal(0, fake.LoadCount);
            fake.ReleasePreparation(newest.Path);
            Assert.True(await second.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }

        Assert.Same(newest, fake.Track);
        Assert.Equal(1, fake.LoadCount);
    }

    [Fact]
    public async Task APlaySupersededByALoadDoesNotStartPlayback()
    {
        Track top = T("top"), chosen = T("chosen");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(top.Path);
        deck.Playlist.Add(top);

        Task play = deck.PlayCommand.ExecuteAsync(null);
        Assert.True(await deck.LoadAsync(chosen));

        fake.ReleasePreparation(top.Path);
        await play.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Same(chosen, fake.Track);
        Assert.Equal(0, fake.PlayCount);
    }

    [Fact]
    public async Task APlayNextSupersededByALoadDoesNotStartPlayback()
    {
        Track top = T("top"), chosen = T("chosen");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(top.Path);
        deck.Playlist.Add(top);

        Task<bool> play = deck.PlayNextAsync();
        Assert.True(await deck.LoadAsync(chosen));

        fake.ReleasePreparation(top.Path);
        Assert.False(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        Assert.Same(chosen, fake.Track);
        Assert.Equal(0, fake.PlayCount);
    }

    [Fact]
    public async Task AnAutoCueLoadDuringAPreparationIsNotOverwritten()
    {
        // The auto-cue calls Load on the deck directly, from the UI timer.
        Track held = T("held"), cued = T("cued");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(held.Path);

        Task<bool> load = deck.LoadAsync(held);
        fake.Load(cued);
        fake.ReleasePreparation(held.Path);

        Assert.False(await load.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(cued, fake.Track);
    }

    [Fact]
    public async Task PlayPressedWhileALoadIsPreparingPlaysThatTrack()
    {
        // When Load was synchronous, Load then Play could only ever mean this track.
        Track chosen = T("chosen"), top = T("top"), playing = T("playing");
        var (deck, fake, _) = Deck();
        await deck.LoadAsync(playing);
        deck.Playlist.Add(top);
        fake.HoldPreparation(chosen.Path);

        Task<bool> load = deck.LoadAsync(chosen);
        Task play = deck.PlayCommand.ExecuteAsync(null);
        Assert.Equal(PlaybackState.Stopped, fake.State);

        fake.ReleasePreparation(chosen.Path);
        await load.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await play.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Same(chosen, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
        Assert.Equal(1, fake.PlayCount);
    }

    [Fact]
    public async Task APlayWaitingOnALoadIsDroppedWhenANewerLoadArrives()
    {
        Track first = T("first"), second = T("second");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(first.Path);

        Task<bool> load = deck.LoadAsync(first);
        Task play = deck.PlayCommand.ExecuteAsync(null);
        Assert.True(await deck.LoadAsync(second));

        fake.ReleasePreparation(first.Path);
        await load.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await play.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Same(second, fake.Track);
        Assert.Equal(0, fake.PlayCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoppingOrPausingWhileAPlayIsPreparingKeepsItFromStarting(bool stop)
    {
        Track x = T("x");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(x.Path);
        deck.Playlist.Add(x);

        Task play = deck.PlayCommand.ExecuteAsync(null);
        if (stop) deck.StopCommand.Execute(null);
        else deck.PauseCommand.Execute(null);

        fake.ReleasePreparation(x.Path);
        await play.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Same(x, fake.Track);
        Assert.Equal(0, fake.PlayCount);
    }

    [Fact]
    public async Task PressingPlayTwiceWhilePreparingStartsPlaybackOnce()
    {
        Track x = T("x");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(x.Path);
        deck.Playlist.Add(x);

        Task first = deck.PlayCommand.ExecuteAsync(null);
        Assert.True(deck.PlayCommand.CanExecute(null));   // the button must not grey out while a file opens
        Task second = deck.PlayCommand.ExecuteAsync(null);

        fake.ReleasePreparation(x.Path);
        await Task.WhenAll(first, second).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.LoadCount);
        Assert.Equal(1, fake.PlayCount);
        Assert.Equal(PlaybackState.Playing, fake.State);
    }

    [Fact]
    public void ATrackEndingWhileALoadIsPreparingDoesNotOvertakeIt() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), chosen = T("chosen"), queued = T("queued");
        var (deck, fake, _) = Deck(queued);
        fake.Load(playing);
        fake.Play();
        fake.HoldPreparation(chosen.Path);

        Task<bool> load = deck.LoadAsync(chosen);
        fake.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(playing, fake.Track);
        fake.ReleasePreparation(chosen.Path);
        AvaloniaTest.PumpUntil(load);

        Assert.Same(chosen, fake.Track);
        Assert.Equal(1, fake.PlayCount);   // loading a track never started it
        Assert.Equal([queued], deck.Playlist);
    });

    [Fact]
    public void ATrackEndingWithAnEmptyQueueLeavesAWaitingPlayAlone() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), chosen = T("chosen");
        var (deck, fake, _) = Deck();
        fake.Load(playing);
        fake.HoldPreparation(chosen.Path);

        Task<bool> load = deck.LoadAsync(chosen);
        Task play = deck.PlayCommand.ExecuteAsync(null);
        fake.RaiseTrackEnded();
        Dispatcher.UIThread.RunJobs();

        fake.ReleasePreparation(chosen.Path);
        AvaloniaTest.PumpUntil(load);
        AvaloniaTest.PumpUntil(play);

        Assert.Same(chosen, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
    });

    [Fact]
    public void ATrackEndingWithNoLoadRunningStillPlaysTheNext() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), queued = T("queued");
        var (_, fake, _) = Deck(queued);
        fake.Load(playing);
        fake.Play();

        fake.RaiseTrackEnded();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fake.Track != queued && DateTime.UtcNow < deadline)
            Dispatcher.UIThread.RunJobs();

        Assert.Same(queued, fake.Track);
        Assert.Equal(2, fake.PlayCount);
    });

    [Fact]
    public async Task AFailedLoadStopsDequeuingOnceStopIsPressed()
    {
        Track gone = T("gone"), other = T("other");
        var (deck, fake, _) = Deck(gone, other);
        fake.Unloadable.Add(gone.Path);
        fake.HoldPreparation(gone.Path);

        Task<bool> play = deck.PlayNextAsync();
        deck.StopCommand.Execute(null);
        fake.ReleasePreparation(gone.Path);

        Assert.False(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(1, fake.LoadCount);
        Assert.Equal([other], deck.Playlist);
        Assert.Equal(0, fake.PlayCount);
    }

    private static MainViewModel NewMain(FakeAudioEngine engine) =>
        new(engine, new Library.SettingsStore(Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")),
            Library.TrackStore.InMemory(), post: x => x(), manualAnalysis: true);

    [Fact]
    public void StorageGoingAwayReleasesBothDecksAndPreparesNothing()
    {
        var engine = new FakeAudioEngine();
        var vm = NewMain(engine);
        vm.DeckA.Playlist.Add(T("a"));
        vm.DeckB.Playlist.Add(T("b"));
        int a = engine.A.PreparedPaths.Count, b = engine.B.PreparedPaths.Count;

        vm.StorageChanged(available: false);

        Assert.Equal(1, engine.A.ReleasePreparedCount);
        Assert.Equal(1, engine.B.ReleasePreparedCount);
        Assert.Equal(a, engine.A.PreparedPaths.Count);
        Assert.Equal(b, engine.B.PreparedPaths.Count);
    }

    [Fact]
    public async Task StorageComingBackPreparesAndAnalysesBothDecksNextTracksAgain()
    {
        var engine = new FakeAudioEngine();
        var vm = NewMain(engine);
        Track a = T("a"), b = T("b");
        vm.DeckA.Playlist.Add(a);
        vm.DeckB.Playlist.Add(b);
        await vm.Analysis.DrainAsync();
        engine.A.PreparedPaths.Clear();
        engine.B.PreparedPaths.Clear();

        vm.StorageChanged(available: true);

        Assert.Equal([a.Path], engine.A.PreparedPaths);
        Assert.Equal([b.Path], engine.B.PreparedPaths);
        Assert.Equal(0, engine.A.ReleasePreparedCount);
        Assert.Equal(2, vm.Analysis.PendingCount);
    }
}
