using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

/// <summary>An in-memory <see cref="IDeck"/> so the mixing logic can be tested with no audio device.</summary>
public sealed class FakeDeck(DeckId id) : IDeck
{
    public DeckId Id { get; } = id;
    public Track? Track { get; private set; }
    public PlaybackState State { get; private set; } = PlaybackState.Stopped;
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public float Volume { get; set; } = 1f;
    public bool IsMuted { get; set; }
    public bool IsCued { get; set; }
    public float Tempo { get; set; } = 1f;

    public int LoadCount { get; private set; }
    public int PlayCount { get; private set; }

    /// <summary>Test helper: position the playhead so this much time is left.</summary>
    public void SetRemaining(TimeSpan remaining) => Position = Duration - remaining;

    public void Load(Track track) { Track = track; Duration = track.Duration; Position = TimeSpan.Zero; LoadCount++; }
    public void Play() { State = PlaybackState.Playing; PlayCount++; }
    public void Pause() => State = PlaybackState.Paused;
    public void Stop() { State = PlaybackState.Stopped; Position = TimeSpan.Zero; }
    public void Seek(TimeSpan position) => Position = position;
    public event EventHandler? TrackEnded;
    public void RaiseTrackEnded() => TrackEnded?.Invoke(this, EventArgs.Empty);
    public void Dispose() { }
}
