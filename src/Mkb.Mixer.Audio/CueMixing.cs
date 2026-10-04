namespace Mkb.Mixer.Audio;

/// <summary>The arithmetic behind the headphone cue, kept free of SoundFlow so it can be tested.</summary>
public static class CueMixing
{
    /// <summary>
    /// Blends the cue with the master for the headphones. Uses the crossfader's
    /// constant-power curve so the level holds steady as the knob turns.
    /// </summary>
    /// <param name="mix">0 is cue only, 1 is master only.</param>
    public static void Blend(ReadOnlySpan<float> cue, ReadOnlySpan<float> master, float mix, Span<float> destination)
    {
        var (cueGain, masterGain) = Crossfader.Gains(mix);
        for (int i = 0; i < destination.Length; i++)
            destination[i] = cue[i] * cueGain + master[i] * masterGain;
    }

    /// <summary>
    /// Rewrites interleaved stereo <paramref name="master"/> in place: left becomes
    /// the master folded to mono, right becomes the headphone feed folded to mono.
    /// </summary>
    public static void Split(Span<float> master, ReadOnlySpan<float> headphones)
    {
        for (int i = 0; i + 1 < master.Length; i += 2)
        {
            float room = (master[i] + master[i + 1]) * 0.5f;
            float phones = (headphones[i] + headphones[i + 1]) * 0.5f;
            master[i] = room;
            master[i + 1] = phones;
        }
    }
}
