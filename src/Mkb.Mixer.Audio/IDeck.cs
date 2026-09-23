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
    /// Playback rate, 0.5..1.5 where 1.0 is normal speed. Pitch is always preserved:
    /// the engine time-stretches (WSOLA) rather than resampling, so speeding a track
    /// up does not raise its pitch the way the original's WMP rate control did.
    /// </summary>
    float Tempo { get; set; }

    void Load(Track track);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan position);

    event EventHandler? TrackEnded;
}
