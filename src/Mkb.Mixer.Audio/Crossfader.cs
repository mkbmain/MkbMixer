namespace Mkb.Mixer.Audio;

/// <summary>
/// Maps a crossfader position to a gain for each deck.
/// </summary>
/// <remarks>
/// The .NET 2.0 original computed these gains piecewise and got it wrong in three
/// ways: deck A stayed at full gain across the whole left half and then jumped
/// discontinuously from 1.0 to 0.49 at the midpoint, the <c>(100 - v) * 2</c> term
/// overflowed to 200 and was silently clamped, and the two halves were asymmetric.
///
/// This replaces it with the standard constant-power curve, where the gains are a
/// quarter-turn of sine and cosine so that <c>a² + b² = 1</c> at every position.
/// That keeps perceived loudness steady through a fade instead of dipping in the
/// middle, which is what an equal-gain linear fade would do.
/// </remarks>
public static class Crossfader
{
    /// <summary>Leftmost position: deck A alone.</summary>
    public const float DeckAOnly = 0f;

    /// <summary>Rightmost position: deck B alone.</summary>
    public const float DeckBOnly = 1f;

    /// <summary>Both decks at equal, constant-power gain.</summary>
    public const float Centre = 0.5f;

    /// <param name="position">0 is deck A alone, 1 is deck B alone. Values outside are clamped.</param>
    public static (float DeckA, float DeckB) Gains(float position)
    {
        float x = Math.Clamp(position, 0f, 1f);
        double angle = x * Math.PI / 2.0;
        return ((float)Math.Cos(angle), (float)Math.Sin(angle));
    }
}
