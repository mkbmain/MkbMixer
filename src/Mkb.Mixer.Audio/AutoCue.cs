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
///
/// Given track timings it fades to finish at the outgoing track's last real sound and brings the next one in at its first.
/// </remarks>
/// <param name="timing">Start, end and BPM per track, if known. Without it the
/// auto-cue fades on the file's length and starts tracks from the top.</param>
public sealed class AutoCue(
    IDeck deckA,
    IDeck deckB,
    Func<DeckId, Track?> dequeueNext,
    Func<Track, TrackTiming?>? timing = null)
{
    private TimeSpan _fadeStartedAt;
    private float _fadeFrom;
    private float _fadeTo;
    private IDeck? _outgoing;

    /// <summary>Tempo matching only bends a track this far; further apart, it would sound wrong.</summary>
    public const double MaxTempoMatch = 0.08;

    public static readonly TimeSpan GlideDuration = TimeSpan.FromSeconds(8);

    public TempoMatchMode TempoMatch { get; set; }

    private IDeck? _incoming;
    private bool _matched;
    private IDeck? _glideDeck;
    private float _glideFrom;
    private TimeSpan _glideStartedAt;

    /// <summary>The user has taken over this deck's tempo: stop any glide on it, now or after the fade.</summary>
    public void CancelGlide(DeckId deck)
    {
        if (_glideDeck?.Id == deck) _glideDeck = null;
        if (_incoming?.Id == deck) _matched = false;
    }

    private bool TryMatchTempo(IDeck outgoing, IDeck incoming, Track next)
    {
        if (TempoMatch == TempoMatchMode.Off) return false;
        double? outBpm = outgoing.Track is { } t ? Timing(t)?.Bpm : null;
        double? inBpm = Timing(next)?.Bpm;
        if (outBpm is not > 0 || inBpm is not > 0) return false;

        double ratio = outBpm.Value * outgoing.Tempo / inBpm.Value;
        if (Math.Abs(ratio - 1) > MaxTempoMatch) return false;
        incoming.Tempo = (float)ratio;
        return true;
    }

    private void AdvanceGlide(TimeSpan now)
    {
        if (_glideDeck is null) return;
        double t = Math.Clamp((now - _glideStartedAt) / GlideDuration, 0, 1);
        _glideDeck.Tempo = t >= 1 ? 1f : (float)(_glideFrom + (1 - _glideFrom) * t);
        if (t >= 1) _glideDeck = null;
    }

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
        AdvanceGlide(now);
        if (!Enabled)
            return;

        if (State == AutoCueState.Transitioning)
        {
            AdvanceFade(now);
            return;
        }

        IDeck? active = ActiveDeck();
        if (active is null || WallTimeLeft(active) > CrossfadeDuration)
            return;

        IDeck incoming = active.Id == DeckId.A ? deckB : deckA;
        if (dequeueNext(incoming.Id) is not { } next)
            return; // nothing queued, so let the current track simply run out

        incoming.Load(next);
        _matched = TryMatchTempo(active, incoming, next);
        _incoming = incoming;
        incoming.Play();
        // After Play: some backends restart a stopped player from the top.
        if (Timing(next)?.Start is { } start && start > TimeSpan.Zero)
            incoming.Seek(start);

        _outgoing = active;
        _fadeFrom = CrossfaderPosition;
        _fadeTo = incoming.Id == DeckId.B ? Crossfader.DeckBOnly : Crossfader.DeckAOnly;
        _fadeStartedAt = now;
        State = AutoCueState.Transitioning;

        AdvanceFade(now);
    }

    /// <summary>
    /// Real time until the deck's last real sound. Divided by tempo: the original
    /// compared track time with the fade length, so a sped-up track faded late.
    /// </summary>
    private TimeSpan WallTimeLeft(IDeck deck)
    {
        TimeSpan end = deck.Duration;
        if (deck.Track is { } t && Timing(t)?.End is { } last && (end <= TimeSpan.Zero || last < end))
            end = last;
        float tempo = deck.Tempo > 0 ? deck.Tempo : 1f;
        return (end - deck.Position) / tempo;
    }

    private TrackTiming? Timing(Track track) => timing?.Invoke(track);

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
        if (_matched && TempoMatch == TempoMatchMode.MatchAndGlide && _incoming is not null)
        {
            _glideDeck = _incoming;
            _glideFrom = _incoming.Tempo;
            _glideStartedAt = now;
        }
        _incoming = null;
        _matched = false;
    }
}
