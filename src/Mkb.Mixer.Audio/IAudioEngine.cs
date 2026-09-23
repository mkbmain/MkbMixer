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

    /// <summary>Applies a crossfader position to both decks' gains.</summary>
    void ApplyCrossfader(float position);

    /// <summary>Decodes a file to amplitude peaks for display, off the calling thread.</summary>
    Task<Waveform> AnalyseAsync(string path, CancellationToken ct = default);
}
