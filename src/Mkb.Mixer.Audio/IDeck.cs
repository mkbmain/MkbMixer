namespace Mkb.Mixer.Audio;

/// <summary>
/// One playback deck. The UI and the mixing logic only ever see this interface,
/// which keeps the SoundFlow dependency confined to <see cref="SoundFlowDeck"/>
/// and lets the tests drive a fake with no sound card present.
/// </summary>
public interface IDeck : IDisposable
{
    DeckId Id { get; }
    Track? Track { get; }
    PlaybackState State { get; }

    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    TimeSpan Remaining => Duration - Position;

    /// <summary>Linear gain 0..1, driven by the crossfader.</summary>
    float Volume { get; set; }
    bool IsMuted { get; set; }

    /// <summary>
    /// Sends this deck to the headphones, taken before the crossfader and mute so
    /// a deck can be pre-listened while the room cannot hear it.
    /// </summary>
    bool IsCued { get; set; }

    /// <summary>
    /// Playback rate, 0.5..1.5 where 1.0 is normal speed. Pitch is always preserved:
    /// the engine time-stretches (WSOLA) rather than resampling, so speeding a track
    /// up does not raise its pitch the way the original's WMP rate control did.
    /// </summary>
    float Tempo { get; set; }

    /// <summary>
    /// Opens a track, replacing whatever was loaded. Throws if the file cannot be
    /// opened or decoded (moved, deleted, on a removed SD card, corrupt); the deck
    /// is then left empty rather than holding a track it cannot play.
    /// </summary>
    void Load(Track track);

    /// <summary>
    /// Opens a track ahead of time, off the calling thread, so that a later
    /// <see cref="Load"/> of it has nothing slow left to do. Opening scans the whole
    /// file, too slow for the UI thread. A failure is not reported here: <see cref="Load"/>
    /// then opens the file itself and throws the real error. Cancelling only stops the
    /// caller waiting; an open cannot be interrupted, and what it opens stays prepared
    /// until <see cref="RetainPrepared"/> or <see cref="ReleasePrepared"/> lets it go, or
    /// newer preparations push it out. If the file has since changed or gone,
    /// <see cref="Load"/> opens it afresh instead.
    /// </summary>
    Task PrepareAsync(Track track, CancellationToken ct = default);

    /// <summary>
    /// Closes every file opened ahead of time, so storage about to go away can be
    /// unmounted. An open still running is discarded when it finishes. The loaded track
    /// is not affected.
    /// </summary>
    void ReleasePrepared();

    /// <summary>
    /// Closes every file opened ahead of time except these, because an open file cannot be
    /// renamed or retagged on Windows and stops a drive ejecting. An open still running is
    /// discarded when it finishes. The loaded track is not affected.
    /// </summary>
    void RetainPrepared(IReadOnlyCollection<string> paths);

    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);

    event EventHandler? TrackEnded;
}
