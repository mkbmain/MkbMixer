namespace Mkb.Mixer.Audio;

/// <summary>
/// Notices that the cue device has stopped pulling audio, which is what an
/// unplugged USB output looks like from here. The device's audio thread stamps it
/// on every block; the UI thread asks whether the stamps have stopped. Lock-free
/// and allocation-free, with the clock passed in so tests need no sleeping.
/// </summary>
public sealed class CueWatchdog
{
    /// <summary>How long without a block before the device counts as gone.</summary>
    public const long StallMilliseconds = 500;

    private const long Unarmed = long.MinValue;
    private long _last = Unarmed;

    /// <summary>Starts watching, counting from <paramref name="nowMs"/>.</summary>
    public void Arm(long nowMs) => Volatile.Write(ref _last, nowMs);

    /// <summary>Stops watching, so a torn-down device is never reported.</summary>
    public void Disarm() => Volatile.Write(ref _last, Unarmed);

    /// <summary>Audio thread: the device just consumed a block.</summary>
    public void Stamp(long nowMs) => Volatile.Write(ref _last, nowMs);

    /// <summary>True when armed and no block has arrived for over the threshold.</summary>
    public bool IsStalled(long nowMs)
    {
        long last = Volatile.Read(ref _last);
        return last != Unarmed && nowMs - last > StallMilliseconds;
    }
}

/// <summary>Which playback devices may carry the headphone cue.</summary>
public static class CueDeviceRules
{
    /// <summary>
    /// True for the device the master mix plays on: the one it opened by name, or,
    /// when it opened "the default", whichever device is the default now.
    /// </summary>
    public static bool IsMasterOutput(string name, bool isDefault, string? masterName, bool masterIsDefault) =>
        masterIsDefault ? isDefault : masterName is not null && name == masterName;
}
