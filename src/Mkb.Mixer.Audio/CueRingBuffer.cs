namespace Mkb.Mixer.Audio;

/// <summary>
/// Carries the headphone feed from the master device's audio thread to the cue
/// device's. Single producer, single consumer, lock-free.
/// </summary>
/// <remarks>
/// Two sound cards never run at exactly the same rate, so the fill level drifts.
/// The writer drops what does not fit; the reader pads with silence when it runs
/// short, and jumps to the newest audio when it falls more than
/// <c>highWater</c> samples behind, which keeps the headphone delay bounded
/// instead of growing over a long set. Everything moves in whole frames so left
/// and right never swap.
/// </remarks>
public sealed class CueRingBuffer
{
    private readonly float[] _data;
    private readonly int _highWater;
    private readonly int _channels;
    private long _written;
    private long _read;

    public CueRingBuffer(int capacity, int highWater, int channels)
    {
        if (channels <= 0 || capacity <= 0 || capacity % channels != 0)
            throw new ArgumentException("capacity must be a positive whole number of frames", nameof(capacity));
        _data = new float[capacity];
        _highWater = highWater;
        _channels = channels;
    }

    /// <summary>Samples waiting to be read.</summary>
    public int Count => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>Producer side.</summary>
    /// <returns>How many samples were accepted.</returns>
    public int Write(ReadOnlySpan<float> samples)
    {
        long w = _written;
        int free = _data.Length - (int)(w - Volatile.Read(ref _read));
        int n = Math.Min(free, samples.Length);
        n -= n % _channels;
        for (int i = 0; i < n; i++)
            _data[(int)((w + i) % _data.Length)] = samples[i];
        Volatile.Write(ref _written, w + n);
        return n;
    }

    /// <summary>Consumer side. Always fills <paramref name="destination"/>.</summary>
    public void Read(Span<float> destination)
    {
        long r = _read;
        long w = Volatile.Read(ref _written);
        if (w - r > destination.Length + _highWater)
            r = w - (destination.Length - destination.Length % _channels);   // fell behind: newest whole frames

        int n = (int)Math.Min(w - r, destination.Length);
        n -= n % _channels;
        for (int i = 0; i < n; i++)
            destination[i] = _data[(int)((r + i) % _data.Length)];
        destination[n..].Clear();
        Volatile.Write(ref _read, r + n);
    }
}
