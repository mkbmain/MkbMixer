namespace Mkb.Mixer.Audio;

/// <summary>
/// Owns the output device and the two decks. The only type in the app that knows
/// which audio library is in use, so swapping SoundFlow for something else means
/// writing one new class behind this interface.
/// </summary>
public interface IAudioEngine : IDisposable
{
    IDeck DeckA { get; }
    IDeck DeckB { get; }

    /// <summary>True when an output device opened successfully.</summary>
    bool IsOutputAvailable { get; }

    /// <summary>Why the output device could not be opened, if it could not.</summary>
    string? OutputError { get; }

    /// <summary>The backend and device actually in use, for the status bar.</summary>
    string? OutputDescription { get; }

    /// <summary>What was tried, in order, and what happened. For troubleshooting.</summary>
    IReadOnlyList<string> Diagnostics { get; }

    /// <summary>How the headphone cue is currently routed.</summary>
    CueMode CueMode { get; }

    /// <summary>The cue device's name in <see cref="Audio.CueMode.Device"/> mode, otherwise null.</summary>
    string? CueDevice { get; }

    /// <summary>
    /// Why the cue device looks gone (unplugged, say) while in Device mode, else
    /// null. The caller should switch the cue off so the stream cannot be moved to
    /// the room speakers.
    /// </summary>
    string? CueFault { get; }

    /// <summary>What the headphones hear: 0 is cued decks only, 1 is the room mix only.</summary>
    float CueMix { get; set; }

    /// <summary>Real playback devices the cue can go to. Dummy sinks are left out.</summary>
    IReadOnlyList<string> CueDeviceNames();

    /// <summary>
    /// Switches cue routing. On failure the cue is left Off and
    /// <paramref name="error"/> says why.
    /// </summary>
    bool TrySetCue(CueMode mode, string? deviceName, out string? error);

    /// <summary>Applies a crossfader position to both decks' gains.</summary>
    void ApplyCrossfader(float position);

    /// <summary>Decodes a file to amplitude peaks for display, off the calling thread.</summary>
    Task<Waveform> AnalyseAsync(string path, CancellationToken ct = default);
}
