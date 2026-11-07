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

    /// <summary>Paths that fail to open, as a moved or corrupt file would.</summary>
    public HashSet<string> Unloadable { get; } = [];

    /// <summary>Every path asked for, in order, repeats included.</summary>
    public List<string> PreparedPaths { get; } = [];

    /// <summary>Paths whose preparation faults, as a moved or corrupt file would.</summary>
    public HashSet<string> Unpreparable { get; } = [];

    /// <summary>Paths whose preparation throws before returning a task at all.</summary>
    public HashSet<string> PrepareThrows { get; } = [];

    private readonly Dictionary<string, TaskCompletionSource> _heldPreparations = [];

    /// <summary>Keeps preparations of this path running until <see cref="ReleasePreparation"/>, as a slow disk would.</summary>
    public void HoldPreparation(string path) =>
        _heldPreparations[path] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleasePreparation(string path)
    {
        _heldPreparations[path].SetResult();
        _heldPreparations.Remove(path);
    }

    public Task PrepareAsync(Track track, CancellationToken ct = default)
    {
        PreparedPaths.Add(track.Path);
        if (PrepareThrows.Contains(track.Path))
            throw new IOException($"Could not prepare '{track.Path}'.");
        if (Unpreparable.Contains(track.Path))
            return Task.FromException(new IOException($"Could not prepare '{track.Path}'."));
        return _heldPreparations.TryGetValue(track.Path, out TaskCompletionSource? held)
            ? held.Task
            : Task.CompletedTask;
    }

    public int ReleasePreparedCount { get; private set; }

    public void ReleasePrepared() => ReleasePreparedCount++;

    public void Load(Track track)
    {
        LoadCount++;
        if (Unloadable.Contains(track.Path))
        {
            Track = null;
            Duration = Position = TimeSpan.Zero;
            State = PlaybackState.Stopped;
            throw new FileNotFoundException($"Could not find file '{track.Path}'.", track.Path);
        }
        Track = track; Duration = track.Duration; Position = TimeSpan.Zero;
    }
    /// <summary>Mimics a backend that restarts a stopped player from the top when it plays.</summary>
    public bool RewindsOnPlayFromStop { get; set; }

    public void Play()
    {
        if (RewindsOnPlayFromStop && State == PlaybackState.Stopped) Position = TimeSpan.Zero;
        State = PlaybackState.Playing;
        PlayCount++;
    }
    public void Pause() => State = PlaybackState.Paused;
    public void Stop() { State = PlaybackState.Stopped; Position = TimeSpan.Zero; }
    public void Seek(TimeSpan position) => Position = position;
    public event EventHandler? TrackEnded;
    public void RaiseTrackEnded() => TrackEnded?.Invoke(this, EventArgs.Empty);
    public void Dispose() { }
}
