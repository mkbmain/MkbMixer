namespace Mkb.Mixer.Audio;

/// <summary>How the headphone cue reaches the headphones.</summary>
public enum CueMode
{
    /// <summary>No cue: the master output is normal stereo.</summary>
    Off,

    /// <summary>
    /// One stereo output shared through a splitter cable: the room mix folded to
    /// mono on the left, the headphone feed folded to mono on the right. Works on
    /// any device, phones included.
    /// </summary>
    Split,

    /// <summary>The headphone feed in stereo on a second playback device.</summary>
    Device
}
