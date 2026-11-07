using System.Collections.Concurrent;
using System.Diagnostics;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

/// <summary>
/// The real deck's prepared chains, against real files. A context with no output
/// device is enough, so no device is opened and CI runs these; they skip on a machine
/// that cannot make even a context.
/// </summary>
public class SoundFlowDeckPreparationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-prepare").FullName;
    private readonly SoundFlowAudioEngine _engine = new([]);
    private readonly ConcurrentQueue<string> _opens = new();
    private readonly ManualResetEventSlim _release = new();
    private readonly SoundFlowDeck _deck;

    public SoundFlowDeckPreparationTests()
    {
        _deck = (SoundFlowDeck)_engine.DeckA;
        _deck.OpeningHook = path => _opens.Enqueue(path);
    }

    public void Dispose()
    {
        _release.Set();
        _engine.Dispose();
        _release.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string[] Opens => _opens.ToArray();

    /// <summary>Tones start at a zero crossing and then rise, which the position-zero tests rely on.</summary>
    private Track Tone(string name, double seconds = 2)
    {
        string path = Path.Combine(_dir, $"{name}.wav");
        const int rate = 44100, channels = 2;
        int frames = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = frames * channels * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 16000);
            w.Write(s); w.Write(s);
        }
        return Track.FromPath(path);
    }

    /// <summary>Skips unless real decoders can be opened here; a Load is what shows it.</summary>
    private void RequireAudioContext()
    {
        using var probe = new SoundFlowAudioEngine([]);
        var deck = (SoundFlowDeck)probe.DeckB;
        deck.Load(Tone("probe"));
        if (deck.LoadedProvider is null)
            Assert.Skip("no audio context on this machine");
    }

    /// <summary>
    /// .NET takes an advisory lock for FileShare on Unix too, so an exclusive open
    /// fails exactly while something still holds the file, which a delete would not show.
    /// </summary>
    private static bool IsHeld(Track track)
    {
        try
        {
            using var _ = new FileStream(track.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
    }

    /// <summary>Holds the next opens running until <see cref="_release"/> is set, bounded so a failure cannot hang.</summary>
    private void HoldOpens(string? onlyPath = null) => _deck.OpeningHook = path =>
    {
        _opens.Enqueue(path);
        if (onlyPath is null || path == onlyPath)
            _release.Wait(Timeout);
    };

    /// <summary>A fake engine behind it: an analysis against the real one could outlive it when the test disposes it.</summary>
    private DeckViewModel NewViewModel() => new(_deck, new FakeAudioEngine());

    private void AssertUsable(Track track, float tempo)
    {
        TimeStretchProvider provider = Assert.IsType<TimeStretchProvider>(_deck.LoadedProvider);
        Assert.Same(track, _deck.Track);
        Assert.Equal(tempo, provider.Speed);
        Assert.Equal(0, provider.Position);
        Assert.Equal(TimeSpan.Zero, _deck.Position);
        Assert.InRange(_deck.Duration.TotalSeconds, 1.9, 2.1);

        var samples = new float[4096];
        Assert.Equal(samples.Length, provider.ReadBytes(samples));
        Assert.Equal(0f, samples[0], 0.01f);       // the very start of the file
        Assert.Contains(samples, s => s > 0.2f);   // and real audio, not silence
    }

    [Fact]
    public void WithNoAudioContextPreparingCompletesAndDoesNothing()
    {
        var deck = new SoundFlowDeck(DeckId.A, engine: null, output: null, new CueBus(), SoundFlowAudioEngine.OutputFormat);
        var opens = new List<string>();
        deck.OpeningHook = opens.Add;

        Task prepared = deck.PrepareAsync(new Track("/m/a.mp3", "A"), Ct);

        Assert.True(prepared.IsCompletedSuccessfully);
        Assert.Empty(opens);
        Assert.Empty(deck.PreparedPaths);
    }

    [Fact]
    public async Task APreparedLoadUsesTheChainAndTheCurrentTempo()
    {
        RequireAudioContext();
        Track a = Tone("a");
        _deck.Tempo = 0.8f;
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        _deck.Tempo = 1.25f;   // what counts is the tempo now, not at preparation

        _deck.Load(a);

        Assert.Equal([a.Path], Opens);
        AssertUsable(a, 1.25f);
        Assert.Empty(_deck.PreparedPaths);
    }

    [Fact]
    public void AnUnpreparedLoadOpensAsBefore()
    {
        RequireAudioContext();
        Track a = Tone("a");
        _deck.Tempo = 1.25f;

        _deck.Load(a);

        Assert.Equal([a.Path], Opens);
        AssertUsable(a, 1.25f);
    }

    [Fact]
    public async Task ALoadOfADifferentPathOpensItselfAndLeavesThePreparedChain()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);

        _deck.Load(b);

        Assert.Equal([a.Path, b.Path], Opens);
        AssertUsable(b, 1f);
        Assert.Equal([a.Path], _deck.PreparedPaths);
    }

    [Fact]
    public async Task ThePreparedChainIsReplacedFromTheDiskOnceUsed()
    {
        RequireAudioContext();
        Track a = Tone("a");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);

        _deck.Load(a);
        _deck.Load(a);   // a chain is used by at most one Load

        Assert.Equal([a.Path, a.Path], Opens);
        AssertUsable(a, 1f);
    }

    [Fact]
    public async Task TheRealQueueOrderUsesTheDequeuedTracksChainAndKeepsTheNextPrepared()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b"), c = Tone("c");
        var vm = NewViewModel();
        vm.Playlist.Add(a);
        vm.Playlist.Add(b);
        vm.Playlist.Add(c);

        Assert.Same(a, vm.DequeueNext());
        // Both no-ops by now, so all they do is wait for what the view model asked for.
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);
        _deck.Load(a);

        Assert.Equal(1, Opens.Count(p => p == a.Path));
        Assert.Equal(1, Opens.Count(p => p == b.Path));
        Assert.DoesNotContain(c.Path, Opens);
        AssertUsable(a, 1f);
        Assert.Equal([b.Path], _deck.PreparedPaths);
        Assert.True(IsHeld(b));
    }

    [Fact]
    public async Task ThePlayNextPathFindsItsChainReady()
    {
        RequireAudioContext();
        Track a = Tone("a");
        var vm = NewViewModel();
        vm.Playlist.Add(a);

        Assert.True(await vm.PlayNextAsync().WaitAsync(Timeout, Ct));

        Assert.Equal([a.Path], Opens);
        Assert.Same(a, _deck.Track);
    }

    [Fact]
    public async Task PreparingAPathAlreadyPreparingOpensItOnceAndCompletesWithIt()
    {
        RequireAudioContext();
        Track a = Tone("a");
        HoldOpens();

        Task first = _deck.PrepareAsync(a, Ct);
        Task second = _deck.PrepareAsync(a, Ct);
        Assert.False(second.IsCompleted);

        _release.Set();
        await Task.WhenAll(first, second).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);   // and once it is prepared

        Assert.Equal([a.Path], Opens);
    }

    [Fact]
    public async Task ThirdPreparationEvictsAndReleasesTheOldest()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b"), c = Tone("c");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        Assert.True(IsHeld(a));   // or the checks below would prove nothing
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);

        await _deck.PrepareAsync(c, Ct).WaitAsync(Timeout, Ct);

        Assert.Equal([b.Path, c.Path], _deck.PreparedPaths);
        Assert.False(IsHeld(a));
        Assert.True(IsHeld(b));
        Assert.True(IsHeld(c));
    }

    [Fact]
    public async Task AnOpenThatFinishesAfterBeingEvictedReleasesItsFile()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b"), c = Tone("c");
        HoldOpens(onlyPath: a.Path);

        Task evicted = _deck.PrepareAsync(a, Ct);
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(c, Ct).WaitAsync(Timeout, Ct);
        Assert.Equal([b.Path, c.Path], _deck.PreparedPaths);

        _release.Set();
        await evicted.WaitAsync(Timeout, Ct);

        Assert.Equal([b.Path, c.Path], _deck.PreparedPaths);
        Assert.False(IsHeld(a));
    }

    [Fact]
    public async Task AFailedPreparationIsSilentAndLoadReportsTheRealError()
    {
        RequireAudioContext();
        var missing = new Track(Path.Combine(_dir, "missing.wav"), "missing");

        await _deck.PrepareAsync(missing, Ct).WaitAsync(Timeout, Ct);

        Assert.Empty(_deck.PreparedPaths);
        Assert.Throws<FileNotFoundException>(() => _deck.Load(missing));
        Assert.Null(_deck.Track);
    }

    [Fact]
    public async Task AFileRestoredAfterAFailedPreparationCanBePreparedAgain()
    {
        RequireAudioContext();
        Track a = Tone("a");
        string aside = a.Path + ".aside";
        File.Move(a.Path, aside);
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        File.Move(aside, a.Path);

        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);

        Assert.Equal([a.Path], _deck.PreparedPaths);
    }

    [Fact]
    public async Task APreparedFileDeletedBeforeTheLoadReportsTheRealError()
    {
        // Its descriptor still reads on Linux, and on pulled media reads nothing: either
        // way the chain must not stand in for a file that is gone.
        RequireAudioContext();
        Track a = Tone("a");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        File.Delete(a.Path);

        Assert.Throws<FileNotFoundException>(() => _deck.Load(a));

        Assert.Null(_deck.Track);
        Assert.Null(_deck.LoadedProvider);
        Assert.Empty(_deck.PreparedPaths);
    }

    [Fact]
    public async Task APreparedFileReplacedBeforeTheLoadPlaysTheNewContent()
    {
        RequireAudioContext();
        Track a = Tone("a"), replacement = Tone("replacement", seconds: 1);
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        File.Move(replacement.Path, a.Path, overwrite: true);

        _deck.Load(a);

        Assert.Equal([a.Path, a.Path], Opens);
        Assert.InRange(_deck.Duration.TotalSeconds, 0.9, 1.1);
    }

    [Fact]
    public async Task APreparedFileRewrittenAtTheSameSizeIsOpenedAgain()
    {
        RequireAudioContext();
        Track a = Tone("a");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        File.SetLastWriteTimeUtc(a.Path, File.GetLastWriteTimeUtc(a.Path).AddMinutes(-5));

        _deck.Load(a);

        Assert.Equal([a.Path, a.Path], Opens);
        AssertUsable(a, 1f);
    }

    [Fact]
    public async Task ReleasingPreparedChainsClosesTheirFiles()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);
        Assert.True(IsHeld(a));   // or the checks below would prove nothing

        _deck.ReleasePrepared();

        Assert.Empty(_deck.PreparedPaths);
        Assert.False(IsHeld(a));
        Assert.False(IsHeld(b));
    }

    [Fact]
    public async Task ReleasingDiscardsARunningOpenWhenItFinishes()
    {
        RequireAudioContext();
        Track a = Tone("a");
        HoldOpens();
        Task opening = _deck.PrepareAsync(a, Ct);
        Assert.True(SpinWait.SpinUntil(() => Opens.Length > 0, Timeout));

        _deck.ReleasePrepared();
        Assert.Empty(_deck.PreparedPaths);
        _release.Set();
        await opening.WaitAsync(Timeout, Ct);

        Assert.Empty(_deck.PreparedPaths);
        Assert.False(IsHeld(a));
    }

    [Fact]
    public async Task ALoadAfterReleasingOpensTheFileAgain()
    {
        RequireAudioContext();
        Track a = Tone("a");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        _deck.ReleasePrepared();

        _deck.Load(a);

        Assert.Equal([a.Path, a.Path], Opens);
        AssertUsable(a, 1f);
    }

    [Fact]
    public async Task RetainingClosesOnlyTheFilesNotListed()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);
        Assert.True(IsHeld(a));   // or the checks below would prove nothing

        _deck.RetainPrepared([b.Path]);

        Assert.Equal([b.Path], _deck.PreparedPaths);
        Assert.False(IsHeld(a));
        Assert.True(IsHeld(b));
    }

    [Fact]
    public async Task RetainingNothingDiscardsARunningOpenWhenItFinishes()
    {
        RequireAudioContext();
        Track a = Tone("a");
        HoldOpens();
        Task opening = _deck.PrepareAsync(a, Ct);
        Assert.True(SpinWait.SpinUntil(() => Opens.Length > 0, Timeout));

        _deck.RetainPrepared([]);
        Assert.Empty(_deck.PreparedPaths);
        _release.Set();
        await opening.WaitAsync(Timeout, Ct);

        Assert.Empty(_deck.PreparedPaths);
        Assert.False(IsHeld(a));
    }

    [Fact]
    public async Task PreparingAPathAlreadyPreparedMakesItTheNewest()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b"), c = Tone("c");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);

        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(c, Ct).WaitAsync(Timeout, Ct);

        Assert.Equal([a.Path, c.Path], _deck.PreparedPaths);
        Assert.True(IsHeld(a));
        Assert.False(IsHeld(b));
    }

    [Fact]
    public async Task ClearingTheQueueClosesThePreparedFile()
    {
        RequireAudioContext();
        Track a = Tone("a");
        var vm = NewViewModel();
        vm.Playlist.Add(a);
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        Assert.True(IsHeld(a));

        vm.Playlist.Clear();

        Assert.Empty(_deck.PreparedPaths);
        Assert.False(IsHeld(a));
    }

    [Fact]
    public async Task WhenNextUpChangesTheOldFileClosesAndTheNewOneOpens()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b");
        var vm = NewViewModel();
        vm.Playlist.Add(a);
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);

        vm.Playlist.Insert(0, b);
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);

        Assert.Equal([b.Path], _deck.PreparedPaths);
        Assert.False(IsHeld(a));
        Assert.True(IsHeld(b));
    }

    [Fact]
    public async Task ASupersededLoadLeavesItsFileClosed()
    {
        RequireAudioContext();
        Track x = Tone("x"), y = Tone("y");
        var vm = NewViewModel();
        HoldOpens(onlyPath: x.Path);

        Task<bool> first = vm.LoadAsync(x);
        Task<bool> second = vm.LoadAsync(y);
        Assert.True(await second.WaitAsync(Timeout, Ct));
        _release.Set();
        Assert.False(await first.WaitAsync(Timeout, Ct));

        Assert.Empty(_deck.PreparedPaths);
        Assert.False(IsHeld(x));
        Assert.Same(y, _deck.Track);
    }

    [Fact]
    public async Task TheAutoCuesLoadFindsTheDequeuedTracksChainAndOpensNothingItself()
    {
        // The whole reason for preparing: a fresh open here scans the file on the UI timer.
        RequireAudioContext();
        Track x = Tone("x"), y = Tone("y");
        var vm = NewViewModel();
        vm.Playlist.Add(x);
        vm.Playlist.Add(y);
        await _deck.PrepareAsync(x, Ct).WaitAsync(Timeout, Ct);
        var outgoing = new FakeDeck(DeckId.B);
        outgoing.Load(new Track("/m/out.mp3", "out", Duration: TimeSpan.FromMinutes(3)));
        outgoing.SetRemaining(TimeSpan.FromSeconds(5));
        outgoing.Play();
        var autoCue = new AutoCue(_deck, outgoing, _ => vm.DequeueNext()) { Enabled = true };

        autoCue.Tick(TimeSpan.Zero);

        Assert.Same(x, _deck.Track);
        Assert.Equal(1, Opens.Count(p => p == x.Path));
        Assert.Equal([y.Path], _deck.PreparedPaths);
    }

    [Fact]
    public async Task ALoadRunningWhenTheAutoCueDequeuesDoesNotCostTheDequeuedTrackItsChain()
    {
        // Three wanted and two slots, so w has to go, and retaining drops it before y is prepared.
        // Prepare first, or keep all three, and oldest-first eviction takes x instead.
        RequireAudioContext();
        Track x = Tone("x"), y = Tone("y"), w = Tone("w");
        var vm = NewViewModel();
        vm.Playlist.Add(x);
        vm.Playlist.Add(y);
        await _deck.PrepareAsync(x, Ct).WaitAsync(Timeout, Ct);
        HoldOpens(onlyPath: w.Path);
        Task<bool> running = vm.LoadAsync(w);
        // Or a discard could finish this load's wait before the Load below. On the UI thread its
        // continuation could not run until that Load had happened; this test has no such thread.
        Assert.True(SpinWait.SpinUntil(() => Opens.Contains(w.Path), Timeout));

        Assert.Same(x, vm.DequeueNext());
        _deck.Load(x);

        Assert.Equal(1, Opens.Count(p => p == x.Path));
        _release.Set();
        Assert.False(await running.WaitAsync(Timeout, Ct));
        Assert.Same(x, _deck.Track);
        Assert.False(IsHeld(w));
    }

    [Fact]
    public async Task AQueueReorderThatComesBackDoesNotCostTheTrackAboutToLoadItsChain()
    {
        // Next-up goes x, z, x. If x then counts as older than z, the prefetch made as the
        // dequeue commits evicts it, and the Load opens it on the UI thread, inside the gate.
        RequireAudioContext();
        Track x = Tone("x"), y = Tone("y"), z = Tone("z");
        var vm = NewViewModel();
        vm.Playlist.Add(x);
        vm.Playlist.Add(y);
        vm.Playlist.Add(z);
        await _deck.PrepareAsync(x, Ct).WaitAsync(Timeout, Ct);

        vm.Playlist.Move(2, 1);
        vm.Playlist.Move(1, 0);
        await _deck.PrepareAsync(z, Ct).WaitAsync(Timeout, Ct);
        vm.Playlist.Move(0, 1);
        vm.Playlist.Move(1, 2);
        await _deck.PrepareAsync(x, Ct).WaitAsync(Timeout, Ct);
        int opensOfX = Opens.Count(p => p == x.Path);

        Assert.True(await vm.PlayNextAsync().WaitAsync(Timeout, Ct));

        Assert.Same(x, _deck.Track);
        Assert.Equal(opensOfX, Opens.Count(p => p == x.Path));
    }

    [Fact]
    public async Task ALoadStartedBeforeTheNextTickKeepsItsOpenThroughAQueueEdit()
    {
        // Losing it would make the load open the file on the UI thread, inside the gate.
        RequireAudioContext();
        Track x = Tone("x"), y = Tone("y"), z = Tone("z"), other = Tone("other");
        var vm = NewViewModel();
        vm.Playlist.Add(x);
        vm.Playlist.Add(z);
        await _deck.PrepareAsync(x, Ct).WaitAsync(Timeout, Ct);
        Assert.Same(x, vm.DequeueNext());
        _deck.Load(x);
        HoldOpens(onlyPath: y.Path);

        Task<bool> load = vm.LoadAsync(y);
        Assert.True(SpinWait.SpinUntil(() => Opens.Contains(y.Path), Timeout));
        vm.Playlist.Add(other);
        _release.Set();

        Assert.True(await load.WaitAsync(Timeout, Ct));
        Assert.Same(y, _deck.Track);
        Assert.Equal(1, Opens.Count(p => p == y.Path));
    }

    [Fact]
    public async Task DisposeWaitsForADiscardedOpenStillTearingDownItsChain()
    {
        // Only a lower bound on the time: running slow cannot fail this. Dispose has nothing
        // to wait for unless the teardown still counts as running, and then waits its full bound.
        RequireAudioContext();
        Track a = Tone("a");
        HoldOpens();
        using var tearingDown = new ManualResetEventSlim();
        using var finishTearingDown = new ManualResetEventSlim();
        _deck.TearingDownHook = _ =>
        {
            tearingDown.Set();
            finishTearingDown.Wait(Timeout);
        };
        Task opening = _deck.PrepareAsync(a, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => Opens.Length > 0, Timeout));
        _deck.RetainPrepared([]);
        _release.Set();
        Assert.True(tearingDown.Wait(Timeout, Ct));

        var clock = Stopwatch.StartNew();
        _deck.Dispose();
        clock.Stop();

        finishTearingDown.Set();
        await opening.WaitAsync(Timeout, Ct);
        Assert.True(clock.Elapsed.TotalSeconds >= 1.9, $"Dispose returned after {clock.Elapsed.TotalSeconds:0.00}s");
        Assert.False(IsHeld(a));
    }

    [Fact]
    public async Task LoadingAWhileItIsOpeningWaitsAndUsesThatOpen()
    {
        // The auto-cue's Load can arrive mid-open; a second scan would only take longer.
        RequireAudioContext();
        Track a = Tone("a");
        HoldOpens();
        Task opening = _deck.PrepareAsync(a, Ct);
        Assert.True(SpinWait.SpinUntil(() => Opens.Length > 0, Timeout));

        Task load = Task.Run(() => _deck.Load(a), Ct);
        await Task.Delay(50, Ct);
        Assert.False(load.IsCompleted);
        _release.Set();
        await Task.WhenAll(opening, load).WaitAsync(Timeout, Ct);

        Assert.Equal([a.Path], Opens);
        AssertUsable(a, 1f);
    }

    [Fact]
    public async Task CancellingStopsTheWaitButKeepsTheChain()
    {
        RequireAudioContext();
        Track a = Tone("a");
        HoldOpens();
        using var cts = new CancellationTokenSource();

        Task wait = _deck.PrepareAsync(a, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait).WaitAsync(Timeout, Ct);

        _release.Set();
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        Assert.Equal([a.Path], _deck.PreparedPaths);
    }

    [Fact]
    public async Task DisposeReleasesPreparedChains()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b");
        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);
        Assert.True(IsHeld(a));

        _deck.Dispose();

        Assert.False(IsHeld(a));
        Assert.False(IsHeld(b));
        Assert.Empty(_deck.PreparedPaths);
    }

    /// <summary>A UI thread blocked in Dispose runs nothing posted to it, exactly like this.</summary>
    internal sealed class NeverPumps : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
        public override void Send(SendOrPostCallback d, object? state) { }
    }

    [Fact]
    public void DisposeOnABlockedUiThreadWaitsForARunningOpenThenReleasesIt()
    {
        RequireAudioContext();
        Track a = Tone("a");
        HoldOpens();

        SynchronizationContext? before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NeverPumps());
        try
        {
            // No token: with one, this is a wrapper that completes just after the open does,
            // and the check below would race it.
            Task opening = _deck.PrepareAsync(a, CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(() => Opens.Length > 0, Timeout));
            _ = Task.Delay(200, Ct).ContinueWith(_ => _release.Set(), Ct, TaskContinuationOptions.None, TaskScheduler.Default);

            _deck.Dispose();

            Assert.True(opening.IsCompleted, "Dispose returned with the open still running");
        }
        finally { SynchronizationContext.SetSynchronizationContext(before); }

        Assert.False(IsHeld(a));
    }

    [Fact]
    public async Task DisposeWaitsForAnEvictedOpenToo()
    {
        RequireAudioContext();
        Track a = Tone("a"), b = Tone("b"), c = Tone("c");
        HoldOpens(onlyPath: a.Path);
        // No token, for the same reason as above.
        Task evicted = _deck.PrepareAsync(a, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => Opens.Length > 0, Timeout));
        await _deck.PrepareAsync(b, Ct).WaitAsync(Timeout, Ct);
        await _deck.PrepareAsync(c, Ct).WaitAsync(Timeout, Ct);
        _ = Task.Delay(200, Ct).ContinueWith(_ => _release.Set(), Ct, TaskContinuationOptions.None, TaskScheduler.Default);

        _deck.Dispose();

        Assert.True(evicted.IsCompleted, "Dispose returned with the evicted open still running");
        Assert.False(IsHeld(a));
    }

    [Fact]
    public async Task DisposeGivesUpOnAStuckOpenAfterTwoSecondsAndItStillReleasesItsFile()
    {
        RequireAudioContext();
        Track a = Tone("a");
        HoldOpens();
        Task opening = _deck.PrepareAsync(a, Ct);
        Assert.True(SpinWait.SpinUntil(() => Opens.Length > 0, Timeout));

        var clock = Stopwatch.StartNew();
        _deck.Dispose();
        clock.Stop();

        Assert.InRange(clock.Elapsed.TotalSeconds, 1.9, 8);   // the hold itself lasts 10
        Assert.False(opening.IsCompleted);

        _release.Set();
        await opening.WaitAsync(Timeout, Ct);
        Assert.False(IsHeld(a));
    }

    [Fact]
    public async Task PreparingAfterDisposeOpensNothing()
    {
        RequireAudioContext();
        Track a = Tone("a");
        _deck.Dispose();

        await _deck.PrepareAsync(a, Ct).WaitAsync(Timeout, Ct);

        Assert.Empty(Opens);
    }

    [Fact]
    public void LoadingAfterDisposeOpensNothing()
    {
        // A view model load that finishes its wait after shutdown would otherwise
        // build a decoder on an engine about to be freed.
        RequireAudioContext();
        Track a = Tone("a");
        _deck.Dispose();

        _deck.Load(a);

        Assert.Empty(Opens);
        Assert.Null(_deck.LoadedProvider);
    }
}
