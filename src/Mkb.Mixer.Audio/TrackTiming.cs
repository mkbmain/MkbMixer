namespace Mkb.Mixer.Audio;

/// <summary>What the auto-cue needs to know about a track, from whatever remembers it.</summary>
/// <param name="Start">Where to bring the track in: hot cue 1, else its first sound.</param>
/// <param name="End">Its last real sound.</param>
/// <param name="Bpm">Its BPM at normal speed, after any user correction.</param>
public sealed record TrackTiming(TimeSpan? Start, TimeSpan? End, double? Bpm);
