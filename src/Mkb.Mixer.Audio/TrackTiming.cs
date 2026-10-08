namespace Mkb.Mixer.Audio;

/// <summary>What the auto-cue needs to know about a track, from whatever remembers it.</summary>
/// <param name="Start">Where to bring the track in: hot cue 1, else its first sound.</param>
/// <param name="End">Its last real sound.</param>
/// <param name="Bpm">Its BPM at normal speed, after any user correction.</param>
public sealed record TrackTiming(TimeSpan? Start, TimeSpan? End, double? Bpm);

/// <summary>How the auto-cue treats tempo across a transition.</summary>
public enum TempoMatchMode
{
    /// <summary>Each track plays at whatever tempo its deck is set to.</summary>
    Off,
    /// <summary>The incoming track takes the outgoing track's BPM, and keeps it.</summary>
    Match,
    /// <summary>As Match, then eases back to normal speed once the fade is done.</summary>
    MatchAndGlide
}
