using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class AutoCueTimingTests
{
    internal static readonly Track Current = new("/m/now.mp3", "now", Duration: TimeSpan.FromMinutes(4));
    internal static readonly Track Next = new("/m/next.mp3", "next", Duration: TimeSpan.FromMinutes(4));

    internal static (AutoCue Cue, FakeDeck A, FakeDeck B, Dictionary<string, TrackTiming> Timings) Setup()
    {
        var a = new FakeDeck(DeckId.A);
        var b = new FakeDeck(DeckId.B);
        var timings = new Dictionary<string, TrackTiming>();
        bool queued = true;
        Track? Dequeue(DeckId deck)
        {
            // Hands out Next exactly once, to deck B.
            if (deck != DeckId.B || !queued) return null;
            queued = false;
            return Next;
        }
        var cue = new AutoCue(a, b, Dequeue, t => timings.GetValueOrDefault(t.Path))
        {
            Enabled = true,
            CrossfadeDuration = TimeSpan.FromSeconds(20)
        };
        a.Load(Current);
        a.Play();
        return (cue, a, b, timings);
    }

    [Fact]
    public void TheFadeIsTimedToTheLastSound()
    {
        var (cue, a, _, timings) = Setup();
        timings[Current.Path] = new TrackTiming(null, TimeSpan.FromSeconds(210), null);

        a.Position = TimeSpan.FromSeconds(189);
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(AutoCueState.Idle, cue.State);

        a.Position = TimeSpan.FromSeconds(190);
        cue.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoCueState.Transitioning, cue.State);
    }

    [Fact]
    public void ASpedUpTrackStartsItsFadeOnTime()
    {
        // 25 s of track at 1.25x is 20 s of real time.
        var (cue, a, _, _) = Setup();
        a.Tempo = 1.25f;

        a.SetRemaining(TimeSpan.FromSeconds(26));
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(AutoCueState.Idle, cue.State);

        a.SetRemaining(TimeSpan.FromSeconds(25));
        cue.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(AutoCueState.Transitioning, cue.State);
    }

    [Fact]
    public void TheIncomingTrackStartsAtItsStartPoint()
    {
        var (cue, a, b, timings) = Setup();
        timings[Next.Path] = new TrackTiming(TimeSpan.FromSeconds(4.5), null, null);
        a.SetRemaining(TimeSpan.FromSeconds(10));

        cue.Tick(TimeSpan.Zero);

        Assert.Same(Next, b.Track);
        Assert.Equal(TimeSpan.FromSeconds(4.5), b.Position);
    }

    [Fact]
    public void WithoutAStartPointItStartsFromTheTop()
    {
        var (cue, a, b, _) = Setup();
        a.SetRemaining(TimeSpan.FromSeconds(10));

        cue.Tick(TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, b.Position);
    }

    [Fact]
    public void SeekingPastTheLastSoundStartsTheFadeAtOnce()
    {
        var (cue, a, _, timings) = Setup();
        timings[Current.Path] = new TrackTiming(null, TimeSpan.FromSeconds(210), null);
        cue.CrossfaderPosition = 0.2f;

        a.Position = TimeSpan.FromSeconds(225);
        cue.Tick(TimeSpan.Zero);

        Assert.Equal(AutoCueState.Transitioning, cue.State);
        Assert.Equal(0.2f, cue.CrossfaderPosition, 3);
    }

    [Fact]
    public async Task TheMixerBringsTheNextTrackInAtHotCueOne()
    {
        var engine = new FakeAudioEngine();
        var store = TrackStore.InMemory();
        store.Update(Next.Path, i => i.WithHotCue(0, 12) with { FirstSoundSeconds = 3 });
        var vm = new MainViewModel(engine, new SettingsStore(Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")),
            store, post: a => a(), manualAnalysis: true)
        {
            CrossfaderPosition = 0f,
            AutoCueEnabled = true,
            CrossfadeSeconds = 20
        };
        vm.DeckB.Playlist.Add(Next);
        await vm.DeckA.LoadAsync(Current);
        await vm.DeckA.PlayCommand.ExecuteAsync(null);
        engine.A.SetRemaining(TimeSpan.FromSeconds(15));

        vm.Tick(TimeSpan.Zero);

        Assert.Same(Next, engine.B.Track);
        Assert.Equal(TimeSpan.FromSeconds(12), engine.B.Position);
    }
}
