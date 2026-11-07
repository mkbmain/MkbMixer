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

    /// <summary>Always picks the highest index it is offered, so shuffle is deterministic.</summary>
    private sealed class LastRandom : Random
    {
        public override int Next(int maxValue) => maxValue - 1;
    }

    /// <summary>Gives a different answer on each call, so a pick made again cannot match the first by luck.</summary>
    private sealed class CyclingRandom : Random
    {
        private int _next = 1;
        public override int Next(int maxValue) => _next++ % maxValue;
    }

    /// <summary>Runs UI jobs for long enough that a wrongly started load or play would have landed.</summary>
    private static void Settle()
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(30);
        while (DateTime.UtcNow < deadline) Dispatcher.UIThread.RunJobs();
    }

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
        Assert.Equal(x.Path, fake.PreparedPaths[^1]);
        Assert.DoesNotContain(y.Path, fake.PreparedPaths);   // x is not taken until its load commits

        fake.ReleasePreparation(x.Path);
        Assert.True(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(x, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
        // Prepare, take from the queue, then load: the following track was already asked for.
        Assert.Equal(y.Path, fake.PreparedPaths[fake.PreparedCountAtLoad - 1]);
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
        Assert.Equal([top], deck.Playlist);
    }

    [Fact]
    public async Task APlayNextSupersededByALoadLeavesTheQueueAsItWas()
    {
        Track x = T("x"), y = T("y"), z = T("z");
        var (deck, fake, _) = Deck(x, y);
        fake.HoldPreparation(x.Path);

        Task<bool> play = deck.PlayNextAsync();
        Assert.True(await deck.LoadAsync(z));
        fake.ReleasePreparation(x.Path);

        Assert.False(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal([x, y], deck.Playlist);
        Assert.Same(z, fake.Track);
        Assert.Equal(0, fake.PlayCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APlayNextSupersededByALoadKeepsTheOrderAndTheShufflePick(bool repeat)
    {
        Track z = T("z");
        Track[] queue = [T("a"), T("b"), T("c"), T("d")];
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine(), new CyclingRandom()) { Repeat = repeat };
        foreach (Track t in queue) deck.Playlist.Add(t);
        deck.Shuffle = true;   // last, or the pick is simply the first track added and is never rolled
        Track pick = deck.PeekNext()!;
        Assert.NotSame(queue[0], pick);
        fake.HoldPreparation(pick.Path);

        Task<bool> play = deck.PlayNextAsync();
        Assert.True(await deck.LoadAsync(z));
        fake.ReleasePreparation(pick.Path);

        Assert.False(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(queue, deck.Playlist);
        Assert.Same(pick, deck.PeekNext());
    }

    [Fact]
    public async Task AnAutoCueTakingTheTrackDuringAPlayNextDoesNotPopASecondOne()
    {
        // The auto-cue pops and then calls Load on the deck directly, from the UI timer.
        Track x = T("x"), y = T("y"), z = T("z");
        var (deck, fake, _) = Deck(x, y, z);
        fake.HoldPreparation(x.Path);

        Task<bool> play = deck.PlayNextAsync();
        Assert.Same(x, deck.DequeueNext());
        fake.Load(x);
        fake.ReleasePreparation(x.Path);

        Assert.False(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal([y, z], deck.Playlist);
        Assert.Same(x, fake.Track);
        Assert.Equal(1, fake.LoadCount);
        Assert.Equal(0, fake.PlayCount);
    }

    [Fact]
    public async Task ATrackRemovedDuringAPlayNextIsNotLoadedAndTheCurrentNextPlays()
    {
        Track x = T("x"), y = T("y"), z = T("z");
        var (deck, fake, _) = Deck(x, y, z);
        fake.HoldPreparation(x.Path);

        Task<bool> play = deck.PlayNextAsync();
        deck.Playlist.Remove(x);
        fake.ReleasePreparation(x.Path);

        Assert.True(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(y, fake.Track);
        Assert.Equal(1, fake.LoadCount);
        Assert.Equal(PlaybackState.Playing, fake.State);
        Assert.Equal([z], deck.Playlist);
    }

    [Fact]
    public async Task AQueueReorderedDuringAPlayNextPlaysTheNewTop()
    {
        Track x = T("x"), y = T("y");
        var (deck, fake, _) = Deck(x, y);
        fake.HoldPreparation(x.Path);

        Task<bool> play = deck.PlayNextAsync();
        deck.Playlist.Move(1, 0);
        fake.ReleasePreparation(x.Path);

        Assert.True(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(y, fake.Track);
        Assert.Equal(1, fake.LoadCount);
        Assert.Equal([x], deck.Playlist);
    }

    [Fact]
    public async Task ShuffleTurnedOnDuringAPlayNextPlaysTheNewPick()
    {
        Track x = T("x"), y = T("y"), z = T("z");
        var fake = new FakeDeck(DeckId.A);
        var deck = new DeckViewModel(fake, new FakeAudioEngine(), new LastRandom());
        foreach (Track t in new[] { x, y, z }) deck.Playlist.Add(t);
        fake.HoldPreparation(x.Path);

        Task<bool> play = deck.PlayNextAsync();
        deck.Shuffle = true;
        fake.ReleasePreparation(x.Path);

        Assert.True(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(z, fake.Track);
        Assert.Equal([x, y], deck.Playlist);
    }

    [Fact]
    public async Task AQueueReplacedDuringAPlayNextGetsAFullPassOfSkips()
    {
        // The pass was sized for a queue of one, which must not stop z being reached.
        Track x = T("x"), gone = T("gone"), z = T("z");
        var (deck, fake, _) = Deck(x);
        fake.Unloadable.Add(gone.Path);
        fake.HoldPreparation(x.Path);

        Task<bool> play = deck.PlayNextAsync();
        deck.Playlist.Remove(x);
        deck.Playlist.Add(gone);
        deck.Playlist.Add(z);
        fake.ReleasePreparation(x.Path);

        Assert.True(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(z, fake.Track);
        Assert.Empty(deck.Playlist);
    }

    [Fact]
    public void APlayNextFollowingAQueueThatKeepsChangingGivesUp() => AvaloniaTest.Run(() =>
    {
        Track[] queue = Enumerable.Range(0, 40).Select(i => T($"t{i}")).ToArray();
        var (deck, fake, messages) = Deck();
        foreach (Track t in queue) fake.HoldPreparation(t.Path);
        foreach (Track t in queue) deck.Playlist.Add(t);

        Task<bool> play = deck.PlayNextAsync();
        for (int i = 0; i < queue.Length - 1 && !play.IsCompleted; i++)
        {
            deck.Playlist.Remove(queue[i]);
            fake.ReleasePreparation(queue[i].Path);
            Settle();
        }

        Assert.True(play.IsCompleted);
        Assert.False(play.Result);
        Assert.NotEmpty(deck.Playlist);
        Assert.Equal(0, fake.LoadCount);
        Assert.Contains("queue kept changing", Assert.Single(messages));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoppingOrPausingDuringAPlayNextLoadsAndConsumesTheTrackButDoesNotStartIt(bool stop)
    {
        Track x = T("x"), y = T("y");
        var (deck, fake, _) = Deck(x, y);
        fake.HoldPreparation(x.Path);

        Task<bool> play = deck.PlayNextAsync();
        if (stop) deck.StopCommand.Execute(null);
        else deck.PauseCommand.Execute(null);
        fake.ReleasePreparation(x.Path);

        Assert.False(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(x, fake.Track);
        Assert.Equal([y], deck.Playlist);
        Assert.Equal(0, fake.PlayCount);
    }

    [Fact]
    public async Task StopPressedAndTheTrackRemovedDuringAPlayNextLoadsNothing()
    {
        Track x = T("x"), y = T("y");
        var (deck, fake, _) = Deck(x, y);
        fake.HoldPreparation(x.Path);

        Task<bool> play = deck.PlayNextAsync();
        deck.StopCommand.Execute(null);
        deck.Playlist.Remove(x);
        fake.ReleasePreparation(x.Path);

        Assert.False(await play.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, fake.LoadCount);
        Assert.Equal([y], deck.Playlist);
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
    public void ATrackEndingDuringALoadThatFailsAdvancesTheQueue() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), bad = T("bad"), queued = T("queued");
        var (deck, fake, _) = Deck(queued);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(bad.Path);

        Task<bool> load = deck.LoadAsync(bad);
        fake.RaiseTrackEnded();
        Settle();
        Assert.Same(playing, fake.Track);   // no overtaking while the load is undecided

        fake.ReleasePreparation(bad.Path);
        AvaloniaTest.PumpUntil(load);
        Settle();

        Assert.Same(queued, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
        Assert.Equal(2, fake.PlayCount);
        Assert.Empty(deck.Playlist);
    });

    [Fact]
    public void ATrackEndingDuringALoadThatSucceedsLeavesTheTrackAndTheQueue() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), chosen = T("chosen"), queued = T("queued");
        var (deck, fake, _) = Deck(queued);
        fake.Load(playing);
        fake.Play();
        fake.HoldPreparation(chosen.Path);

        Task<bool> load = deck.LoadAsync(chosen);
        fake.RaiseTrackEnded();
        Settle();
        fake.ReleasePreparation(chosen.Path);
        AvaloniaTest.PumpUntil(load);
        Settle();

        Assert.Same(chosen, fake.Track);
        Assert.Equal(1, fake.PlayCount);
        Assert.Equal([queued], deck.Playlist);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATrackEndingDuringAFailingLoadDoesNotAdvanceAfterStopOrPause(bool stop) => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), bad = T("bad"), queued = T("queued");
        var (deck, fake, _) = Deck(queued);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(bad.Path);

        Task<bool> load = deck.LoadAsync(bad);
        fake.RaiseTrackEnded();
        Settle();
        if (stop) deck.StopCommand.Execute(null);
        else deck.PauseCommand.Execute(null);
        fake.ReleasePreparation(bad.Path);
        AvaloniaTest.PumpUntil(load);
        Settle();

        Assert.Null(fake.Track);
        Assert.Equal(1, fake.PlayCount);
        Assert.Equal([queued], deck.Playlist);
    });

    [Fact]
    public void PlayPressedDuringAFailingLoadPlaysTheTopTrackWithoutAlsoAdvancing() => AvaloniaTest.Run(() =>
    {
        // Play's nothing-loaded path takes Playlist[0] and does not dequeue.
        Track playing = T("playing"), bad = T("bad"), first = T("first"), second = T("second");
        var (deck, fake, _) = Deck(first, second);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(bad.Path);

        Task<bool> load = deck.LoadAsync(bad);
        fake.RaiseTrackEnded();
        Settle();
        Task play = deck.PlayCommand.ExecuteAsync(null);
        fake.ReleasePreparation(bad.Path);
        AvaloniaTest.PumpUntil(load);
        AvaloniaTest.PumpUntil(play);
        Settle();

        Assert.Same(first, fake.Track);
        Assert.Equal(2, fake.PlayCount);
        Assert.Equal([first, second], deck.Playlist);
    });

    [Fact]
    public void ATrackEndingWaitsForTheLatestLoadWhenThatOneFails() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), older = T("older"), bad = T("bad"), queued = T("queued");
        var (deck, fake, _) = Deck(queued);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(older.Path);
        fake.HoldPreparation(bad.Path);

        Task<bool> first = deck.LoadAsync(older);
        fake.RaiseTrackEnded();
        Settle();
        Task<bool> second = deck.LoadAsync(bad);

        fake.ReleasePreparation(older.Path);
        AvaloniaTest.PumpUntil(first);
        Settle();
        Assert.Equal([queued], deck.Playlist);   // the waiter must move on to the newer load

        fake.ReleasePreparation(bad.Path);
        AvaloniaTest.PumpUntil(second);
        Settle();

        Assert.Same(queued, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
    });

    [Fact]
    public void ATrackEndingWaitsForTheLatestLoadWhenThatOneSucceeds() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), bad = T("bad"), chosen = T("chosen"), queued = T("queued");
        var (deck, fake, _) = Deck(queued);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(bad.Path);
        fake.HoldPreparation(chosen.Path);

        Task<bool> first = deck.LoadAsync(bad);
        fake.RaiseTrackEnded();
        Settle();
        Task<bool> second = deck.LoadAsync(chosen);

        fake.ReleasePreparation(bad.Path);
        AvaloniaTest.PumpUntil(first);
        Settle();
        fake.ReleasePreparation(chosen.Path);
        AvaloniaTest.PumpUntil(second);
        Settle();

        Assert.Same(chosen, fake.Track);
        Assert.Equal(1, fake.PlayCount);
        Assert.Equal([queued], deck.Playlist);
    });

    [Fact]
    public void TwoEndsDuringAFailingLoadAdvanceOnce() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), bad = T("bad"), first = T("first"), second = T("second");
        var (deck, fake, _) = Deck(first, second);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(bad.Path);

        Task<bool> load = deck.LoadAsync(bad);
        fake.RaiseTrackEnded();
        fake.RaiseTrackEnded();
        Settle();
        fake.ReleasePreparation(bad.Path);
        AvaloniaTest.PumpUntil(load);
        Settle();

        Assert.Same(first, fake.Track);
        Assert.Equal(2, fake.PlayCount);
        Assert.Equal([second], deck.Playlist);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALoadFailingAfterItOvertookTheTrackEndsAdvanceStillAdvances(bool failsBeforeTheOvertakenPrepareFinishes) => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), bad = T("bad"), x = T("x"), y = T("y");
        var (deck, fake, _) = Deck(x, y);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(x.Path);
        fake.HoldPreparation(bad.Path);

        fake.RaiseTrackEnded();
        Settle();   // no load was pending at the end, so x is now preparing
        Task<bool> load = deck.LoadAsync(bad);
        if (failsBeforeTheOvertakenPrepareFinishes)
        {
            fake.ReleasePreparation(bad.Path);
            AvaloniaTest.PumpUntil(load);
            fake.ReleasePreparation(x.Path);
        }
        else
        {
            fake.ReleasePreparation(x.Path);
            Settle();
            Assert.Equal([x, y], deck.Playlist);   // overtaken, and bad is still undecided
            fake.ReleasePreparation(bad.Path);
            AvaloniaTest.PumpUntil(load);
        }
        Settle();

        Assert.Same(x, fake.Track);
        Assert.Equal(PlaybackState.Playing, fake.State);
        Assert.Equal(2, fake.PlayCount);
        Assert.Equal([y], deck.Playlist);
    });

    [Fact]
    public void ALoadLandingAfterItOvertookTheTrackEndsAdvanceDoesNotAdvance() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), chosen = T("chosen"), x = T("x"), y = T("y");
        var (deck, fake, _) = Deck(x, y);
        fake.Load(playing);
        fake.Play();
        fake.HoldPreparation(x.Path);

        fake.RaiseTrackEnded();
        Settle();
        Task<bool> load = deck.LoadAsync(chosen);
        AvaloniaTest.PumpUntil(load);
        fake.ReleasePreparation(x.Path);
        Settle();

        Assert.Same(chosen, fake.Track);
        Assert.Equal(1, fake.PlayCount);
        Assert.Equal([x, y], deck.Playlist);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StopOrPauseAfterALoadOvertookTheTrackEndsAdvanceStopsItAdvancing(bool stop) => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), bad = T("bad"), x = T("x"), y = T("y");
        var (deck, fake, _) = Deck(x, y);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(x.Path);
        fake.HoldPreparation(bad.Path);

        fake.RaiseTrackEnded();
        Settle();
        Task<bool> load = deck.LoadAsync(bad);
        fake.ReleasePreparation(x.Path);
        Settle();   // the advance is overtaken and is now waiting on bad
        if (stop) deck.StopCommand.Execute(null);
        else deck.PauseCommand.Execute(null);
        fake.ReleasePreparation(bad.Path);
        AvaloniaTest.PumpUntil(load);
        Settle();

        Assert.Null(fake.Track);
        Assert.Equal(1, fake.PlayCount);
        Assert.Equal([x, y], deck.Playlist);
    });

    [Fact]
    public void AnAdvanceKeptBeingOvertakenByFailingLoadsGivesUpAndSaysSo() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), bad = T("bad"), x = T("x"), y = T("y");
        var (deck, fake, messages) = Deck(x, y);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(x.Path);

        fake.RaiseTrackEnded();
        Settle();
        int prepares = 0;
        for (int i = 0; i < 40; i++)
        {
            prepares = fake.PreparedPaths.Count(p => p == x.Path);
            AvaloniaTest.PumpUntil(deck.LoadAsync(bad));
            fake.ReleasePreparation(x.Path);
            fake.HoldPreparation(x.Path);   // the retry, when it comes, is held again
            Settle();
            if (fake.PreparedPaths.Count(p => p == x.Path) == prepares) break;
        }

        Assert.Equal(prepares, fake.PreparedPaths.Count(p => p == x.Path));
        Assert.True(prepares < 40, "it never stopped retrying");
        Assert.Contains(messages, m => m.Contains("kept being replaced"));
        Assert.Null(fake.Track);
        Assert.Equal([x, y], deck.Playlist);
    });

    [Fact]
    public void ATrackEndingOverARepeatingQueueOfMissingFilesTriesOnePass() => AvaloniaTest.Run(() =>
    {
        // Gives up with the deck empty and nothing pressed, which must not look like being overtaken.
        Track playing = T("playing"), a = T("a"), b = T("b");
        var (deck, fake, _) = Deck(a, b);
        deck.Repeat = true;
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.UnionWith([a.Path, b.Path]);

        fake.RaiseTrackEnded();
        Settle();
        Settle();

        Assert.Equal(3, fake.LoadCount);   // playing, then a and b once each
        Assert.Null(fake.Track);
    });

    [Fact]
    public void TwoEndsWithAnAdvanceOvertakenByAFailingLoadAdvanceOnce() => AvaloniaTest.Run(() =>
    {
        Track playing = T("playing"), bad = T("bad"), x = T("x"), y = T("y");
        var (deck, fake, _) = Deck(x, y);
        fake.Load(playing);
        fake.Play();
        fake.Unloadable.Add(bad.Path);
        fake.HoldPreparation(x.Path);

        fake.RaiseTrackEnded();
        Settle();
        fake.RaiseTrackEnded();
        Settle();
        AvaloniaTest.PumpUntil(deck.LoadAsync(bad));
        fake.ReleasePreparation(x.Path);
        Settle();

        Assert.Same(x, fake.Track);
        Assert.Equal([y], deck.Playlist);
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ClearingTheQueueRetainsNothing()
    {
        var (deck, fake, _) = Deck(T("a"), T("b"));

        deck.Playlist.Clear();

        Assert.Empty(fake.Retained);
    }

    [Fact]
    public void RemovingTheOnlyQueuedTrackRetainsNothing()
    {
        Track a = T("a");
        var (deck, fake, _) = Deck(a);

        deck.Playlist.Remove(a);

        Assert.Empty(fake.Retained);
    }

    [Fact]
    public void WhenNextUpChangesOnlyTheNewOneIsRetained()
    {
        Track a = T("a"), b = T("b");
        var (deck, fake, _) = Deck(a);
        Assert.Equal([a.Path], fake.Retained);

        deck.Playlist.Insert(0, b);

        Assert.Equal([b.Path], fake.Retained);
    }

    [Fact]
    public void ADequeuedTrackIsRetainedForTheLoadAboutToFollow()
    {
        // The auto-cue's order: its synchronous Load runs next, on the UI timer, and
        // would have to open and scan the whole file there if this let it go.
        Track a = T("a"), b = T("b"), c = T("c");
        var (deck, fake, _) = Deck(a, b, c);

        Assert.Same(a, deck.DequeueNext());

        Assert.Equal([a.Path, b.Path], fake.Retained);
    }

    [Fact]
    public void ADequeuedTrackNeverLoadedIsLetGoOnTheNextTick()
    {
        Track a = T("a"), b = T("b");
        var (deck, fake, _) = Deck(a, b);
        deck.DequeueNext();

        deck.Refresh();

        Assert.Equal([b.Path], fake.Retained);
    }

    [Fact]
    public void ATickWithNothingDequeuedChangesNothingRetained()
    {
        // This runs on every UI tick, so it must not tell the deck anything unless something changed.
        Track a = T("a"), x = T("x");
        var (deck, fake, _) = Deck(a);
        fake.HoldPreparation(x.Path);
        _ = deck.LoadAsync(x);
        int before = fake.RetainCount;

        deck.Refresh();

        Assert.Equal(before, fake.RetainCount);
        Assert.Equal([a.Path, x.Path], fake.Retained);
    }

    [Fact]
    public void ANewLoadTakesOverFromADequeuedTrackAlreadyLoaded()
    {
        // The auto-cue has dequeued and loaded a on this tick, and the next tick has not come
        // yet. Anything retained from here must be the new load's target, not a.
        Track a = T("a"), b = T("b"), c = T("c"), y = T("y");
        var (deck, fake, _) = Deck(a, b);
        Assert.Same(a, deck.DequeueNext());
        fake.Load(a);
        fake.HoldPreparation(y.Path);

        _ = deck.LoadAsync(y);
        deck.Playlist.Add(c);

        Assert.Equal([b.Path, y.Path], fake.Retained);
    }

    [Fact]
    public async Task TheTargetOfALoadInProgressIsRetainedWhateverTheQueueDoes()
    {
        Track a = T("a"), x = T("x");
        var (deck, fake, _) = Deck(a);
        fake.HoldPreparation(x.Path);

        Task<bool> load = deck.LoadAsync(x);
        Assert.Equal([a.Path, x.Path], fake.Retained);

        deck.Playlist.Clear();
        Assert.Equal([x.Path], fake.Retained);

        fake.ReleasePreparation(x.Path);
        Assert.True(await load.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task ALoadThatSettlesLeavesOnlyNextUpRetained()
    {
        Track a = T("a"), x = T("x");
        var (deck, fake, _) = Deck(a);

        Assert.True(await deck.LoadAsync(x).WaitAsync(Timeout, Ct));

        Assert.Equal([a.Path], fake.Retained);
    }

    [Fact]
    public async Task ALoadThatFailsLeavesOnlyNextUpRetained()
    {
        Track a = T("a"), x = T("x");
        var (deck, fake, _) = Deck(a);
        fake.Unloadable.Add(x.Path);

        Assert.False(await deck.LoadAsync(x).WaitAsync(Timeout, Ct));

        Assert.Equal([a.Path], fake.Retained);
    }

    [Fact]
    public async Task ASupersededLoadLeavesNoExtraFileRetained()
    {
        Track x = T("x"), y = T("y");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(x.Path);
        fake.HoldPreparation(y.Path);

        Task<bool> first = deck.LoadAsync(x);
        Task<bool> second = deck.LoadAsync(y);
        Assert.Equal([y.Path], fake.Retained);

        fake.ReleasePreparation(x.Path);
        Assert.False(await first.WaitAsync(Timeout, Ct));
        Assert.Equal([y.Path], fake.Retained);   // the old load settling must not cost the new one its file

        fake.ReleasePreparation(y.Path);
        Assert.True(await second.WaitAsync(Timeout, Ct));
        Assert.Empty(fake.Retained);
    }

    [Fact]
    public async Task ALoadOvertakenByTheAutoCuesOwnLoadIsLetGo()
    {
        Track x = T("x"), z = T("z");
        var (deck, fake, _) = Deck();
        fake.HoldPreparation(x.Path);
        Task<bool> load = deck.LoadAsync(x);

        fake.Load(z);
        fake.ReleasePreparation(x.Path);
        Assert.False(await load.WaitAsync(Timeout, Ct));

        Assert.Empty(fake.Retained);
    }

    [Fact]
    public void ADequeuedTrackTakesPrecedenceOverALoadItIsAboutToOvertake()
    {
        // Three wanted, two slots. The auto-cue's Load runs next and overtakes the running
        // load, so that load's file is the one to give up.
        Track a = T("a"), b = T("b"), x = T("x");
        var (deck, fake, _) = Deck(a, b);
        fake.HoldPreparation(x.Path);
        _ = deck.LoadAsync(x);

        Assert.Same(a, deck.DequeueNext());

        Assert.Equal([a.Path, b.Path], fake.Retained);
    }

    [Fact]
    public async Task PlayingNextKeepsTheTrackRetainedUpToItsLoadThenOnlyNextUp()
    {
        Track a = T("a"), b = T("b");
        var (deck, fake, _) = Deck(a, b);

        Assert.True(await deck.PlayNextAsync().WaitAsync(Timeout, Ct));

        Assert.Equal([a.Path, b.Path], fake.RetainedAtLoad);
        Assert.Equal([b.Path], fake.Retained);
    }

    [Fact]
    public void TheStorageComingBackRetainsWhatItPreparesAgain()
    {
        var engine = new FakeAudioEngine();
        var vm = NewMain(engine);
        Track a = T("a");
        vm.DeckA.Playlist.Add(a);

        vm.StorageChanged(available: true);

        Assert.Equal([a.Path], engine.A.Retained);
        Assert.Empty(engine.B.Retained);
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
