namespace Mkb.Mixer.Audio;

public enum AutoCueState { Idle, Transitioning }

/// <summary>
/// Automatically fades from the deck that is running out to the other deck,
/// pulling the next track from that deck's playlist.
/// </summary>
/// <remarks>
/// The .NET 2.0 original drove this from a 1050ms timer using
/// <c>if (timeleft_player1 == 20)</c>. Because the remaining time was sampled on a
/// drifting timer, a tick landing on 21 and then 19 skipped the window and the
/// transition silently never happened. The same timer also nudged the fader by
/// fixed ±5 steps while separately slamming it to 1, so the two behaviours fought.
///
/// This version is an explicit state machine driven by wall-clock time: the trigger
/// is a <c>&gt;=</c> threshold guarded by the state, so it fires exactly once and
/// cannot be skipped, and the fade interpolates on elapsed time so its shape does
/// not depend on how often <see cref="Tick"/> happens to be called.
/// </remarks>
public sealed class AutoCue(IDeck deckA, IDeck deckB, Func<DeckId, Track?> dequeueNext)
{
    private TimeSpan _fadeStartedAt;
    private float _fadeFrom;
    private float _fadeTo;
    private IDeck? _outgoing;

    public bool Enabled { get; set; }

    /// <summary>How long before the end of a track the fade begins, and how long it lasts.</summary>
    public TimeSpan CrossfadeDuration { get; set; } = TimeSpan.FromSeconds(20);

    public AutoCueState State { get; private set; } = AutoCueState.Idle;

    /// <summary>0 is deck A alone, 1 is deck B alone.</summary>
    public float CrossfaderPosition { get; set; }

    /// <summary>Raised whenever the auto-cue moves the fader, so the UI can follow along.</summary>
    public event EventHandler<float>? CrossfaderMoved;

    /// <param name="now">A monotonic clock reading. Only differences between calls matter.</param>
    public void Tick(TimeSpan now)
    {
        if (!Enabled)
            return;

        if (State == AutoCueState.Transitioning)
        {
            AdvanceFade(now);
            return;
        }

        IDeck? active = ActiveDeck();
        if (active is null || active.Remaining > CrossfadeDuration)
            return;

        IDeck incoming = active.Id == DeckId.A ? deckB : deckA;
        if (dequeueNext(incoming.Id) is not { } next)
            return; // nothing queued, so let the current track simply run out

        incoming.Load(next);
        incoming.Play();

        _outgoing = active;
        _fadeFrom = CrossfaderPosition;
        _fadeTo = incoming.Id == DeckId.B ? Crossfader.DeckBOnly : Crossfader.DeckAOnly;
        _fadeStartedAt = now;
        State = AutoCueState.Transitioning;

        AdvanceFade(now);
    }

    /// <summary>Whichever deck is currently playing and in front on the fader.</summary>
    private IDeck? ActiveDeck()
    {
        IDeck preferred = CrossfaderPosition <= Crossfader.Centre ? deckA : deckB;
        IDeck other = ReferenceEquals(preferred, deckA) ? deckB : deckA;

        if (preferred.State == PlaybackState.Playing) return preferred;
        if (other.State == PlaybackState.Playing) return other;
        return null;
    }

    private void AdvanceFade(TimeSpan now)
    {
        double elapsed = (now - _fadeStartedAt).TotalSeconds;
        double total = CrossfadeDuration.TotalSeconds;
        float t = total <= 0 ? 1f : (float)Math.Clamp(elapsed / total, 0.0, 1.0);

        CrossfaderPosition = _fadeFrom + (_fadeTo - _fadeFrom) * t;
        CrossfaderMoved?.Invoke(this, CrossfaderPosition);

        if (t < 1f)
            return;

        _outgoing?.Stop();
        _outgoing = null;
        State = AutoCueState.Idle;
    }
}
