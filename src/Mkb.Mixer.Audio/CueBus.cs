namespace Mkb.Mixer.Audio;

/// <summary>
/// Sums the cued decks' audio for one audio callback. Every deck's tap and the
/// master mixer's cue modifier run on the same audio thread, in that order, inside
/// one callback, so this needs no locking: taps <see cref="Add"/>, then the master
/// <see cref="Drain"/>s.
/// </summary>
public sealed class CueBus
{
    private float[] _sum = new float[4096];
    private int _length;
    private volatile bool _active;

    /// <summary>
    /// Off while no cue mode is on, so taps on cued decks do not pile audio up with
    /// nothing draining it. Switching on starts from silence.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (value && !_active)
            {
                Array.Clear(_sum);
                _length = 0;
            }
            _active = value;
        }
    }

    public void Add(ReadOnlySpan<float> block)
    {
        if (!_active) return;
        if (block.Length > _sum.Length)
            Array.Resize(ref _sum, block.Length);   // rare: the device asked for a bigger block
        for (int i = 0; i < block.Length; i++)
            _sum[i] += block[i];
        _length = Math.Max(_length, block.Length);
    }

    /// <summary>Copies out this callback's cue, padding with silence, and clears for the next.</summary>
    public void Drain(Span<float> destination)
    {
        int n = Math.Min(_length, destination.Length);
        _sum.AsSpan(0, n).CopyTo(destination);
        destination[n..].Clear();
        _sum.AsSpan(0, _length).Clear();
        _length = 0;
    }
}
