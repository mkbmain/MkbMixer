using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class AutoCueTests
{
    private static readonly TimeSpan Fade = TimeSpan.FromSeconds(20);

    private static (AutoCue Cue, FakeDeck A, FakeDeck B, Queue<Track> QueueB) Setup()
    {
        var a = new FakeDeck(DeckId.A) { Duration = TimeSpan.FromMinutes(4) };
        var b = new FakeDeck(DeckId.B) { Duration = TimeSpan.FromMinutes(4) };
        var queueB = new Queue<Track>();
        var cue = new AutoCue(a, b, deck => deck == DeckId.B && queueB.Count > 0 ? queueB.Dequeue() : null)
        {
            Enabled = true,
            CrossfadeDuration = Fade
        };
        return (cue, a, b, queueB);
    }

    private static Track NextTrack => new("/next.mp3", "Next", Duration: TimeSpan.FromMinutes(4));

    [Fact]
    public void DoesNothingWhenDisabled()
    {
        var (cue, a, b, q) = Setup();
        cue.Enabled = false;
        q.Enqueue(NextTrack);
        a.Play(); a.SetRemaining(TimeSpan.FromSeconds(5));

        cue.Tick(TimeSpan.Zero);

        Assert.Equal(AutoCueState.Idle, cue.State);
        Assert.Equal(0, b.LoadCount);
    }

    [Fact]
    public void DoesNotTriggerWhileTrackHasPlentyLeft()
    {
        var (cue, a, b, q) = Setup();
        q.Enqueue(NextTrack);
        a.Play(); a.SetRemaining(TimeSpan.FromSeconds(90));

        cue.Tick(TimeSpan.Zero);

        Assert.Equal(AutoCueState.Idle, cue.State);
        Assert.Equal(0, b.LoadCount);
    }

    [Fact]
    public void TriggersAtThreshold_LoadsAndPlaysOtherDeck()
    {
        var (cue, a, b, q) = Setup();
        q.Enqueue(NextTrack);
        a.Play(); a.SetRemaining(Fade);

        cue.Tick(TimeSpan.Zero);

        Assert.Equal(AutoCueState.Transitioning, cue.State);
        Assert.Equal(1, b.LoadCount);
        Assert.Equal(PlaybackState.Playing, b.State);
    }

    [Fact]
    public void TriggersEvenWhenATickSkipsPastTheThreshold()
    {
        // The original used `if (timeleft == 20)` against a value sampled by a
        // 1050ms timer. A tick landing on 21 then 19 missed the window entirely
        // and the transition never happened. A >= threshold cannot be skipped.
        var (cue, a, b, q) = Setup();
        q.Enqueue(NextTrack);
        a.Play();

        a.SetRemaining(TimeSpan.FromSeconds(21));
        cue.Tick(TimeSpan.Zero);
        Assert.Equal(AutoCueState.Idle, cue.State);

        a.SetRemaining(TimeSpan.FromSeconds(19));
        cue.Tick(TimeSpan.FromSeconds(1.05));

        Assert.Equal(AutoCueState.Transitioning, cue.State);
        Assert.Equal(1, b.LoadCount);
    }

    [Fact]
    public void DoesNotRetriggerWhileTransitioning()
    {
        var (cue, a, b, q) = Setup();
        q.Enqueue(NextTrack);
        q.Enqueue(NextTrack);
        a.Play(); a.SetRemaining(Fade);

        cue.Tick(TimeSpan.Zero);
        cue.Tick(TimeSpan.FromSeconds(1));
        cue.Tick(TimeSpan.FromSeconds(2));

        Assert.Equal(1, b.LoadCount);
    }

    [Fact]
    public void DoesNotTriggerWhenNothingIsQueued()
    {
        var (cue, a, b, q) = Setup();
        a.Play(); a.SetRemaining(TimeSpan.FromSeconds(5));

        cue.Tick(TimeSpan.Zero);

        Assert.Equal(AutoCueState.Idle, cue.State);
        Assert.Equal(0, b.LoadCount);
    }

    [Fact]
    public void CrossfaderSweepsFromAToBOverTheFadeDuration()
    {
        var (cue, a, b, q) = Setup();
        q.Enqueue(NextTrack);
        a.Play(); a.SetRemaining(Fade);

        cue.Tick(TimeSpan.Zero);
        Assert.Equal(0f, cue.CrossfaderPosition, 3);

        cue.Tick(TimeSpan.FromSeconds(10));
        Assert.Equal(0.5f, cue.CrossfaderPosition, 3);

        cue.Tick(TimeSpan.FromSeconds(20));
        Assert.Equal(1f, cue.CrossfaderPosition, 3);
        Assert.Equal(AutoCueState.Idle, cue.State);
    }

    [Fact]
    public void FadeIsTimeBasedNotTickCountBased()
    {
        // Irregular tick spacing must not change where the fader ends up.
        var (cue, a, _, q) = Setup();
        q.Enqueue(NextTrack);
        a.Play(); a.SetRemaining(Fade);

        cue.Tick(TimeSpan.Zero);
        cue.Tick(TimeSpan.FromSeconds(0.1));
        cue.Tick(TimeSpan.FromSeconds(7.3));
        cue.Tick(TimeSpan.FromSeconds(15));

        Assert.Equal(0.75f, cue.CrossfaderPosition, 3);
    }

    [Fact]
    public void StopsTheOutgoingDeckWhenTheFadeCompletes()
    {
        var (cue, a, _, q) = Setup();
        q.Enqueue(NextTrack);
        a.Play(); a.SetRemaining(Fade);

        cue.Tick(TimeSpan.Zero);
        cue.Tick(Fade);

        Assert.Equal(PlaybackState.Stopped, a.State);
    }

    [Fact]
    public void TransitionsBackFromBToA()
    {
        var a = new FakeDeck(DeckId.A) { Duration = TimeSpan.FromMinutes(4) };
        var b = new FakeDeck(DeckId.B) { Duration = TimeSpan.FromMinutes(4) };
        var queueA = new Queue<Track>();
        queueA.Enqueue(NextTrack);
        var cue = new AutoCue(a, b, d => d == DeckId.A && queueA.Count > 0 ? queueA.Dequeue() : null)
        {
            Enabled = true,
            CrossfadeDuration = Fade,
            CrossfaderPosition = 1f
        };
        b.Play(); b.SetRemaining(Fade);

        cue.Tick(TimeSpan.Zero);
        Assert.Equal(AutoCueState.Transitioning, cue.State);
        Assert.Equal(1, a.LoadCount);

        cue.Tick(Fade);
        Assert.Equal(0f, cue.CrossfaderPosition, 3);
    }
}
