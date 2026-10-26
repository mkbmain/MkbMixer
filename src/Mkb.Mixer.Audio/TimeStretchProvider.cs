using SoundFlow.Enums;
using SoundFlow.Interfaces;
using SoundFlow.Metadata.Models;

namespace Mkb.Mixer.Audio;

/// <summary>
/// Changes a track's speed without changing its pitch (WSOLA), sitting between the
/// decoder and the <c>SoundPlayer</c> so the player itself always runs at 1.0.
/// </summary>
/// <remarks>
/// SoundFlow's own <c>PlaybackSpeed</c> does the same job but costs roughly ten
/// times the CPU of normal playback: its similarity search runs in double precision
/// over 257 offsets per hop, and its resampler shifts a whole buffer down by one
/// frame for every frame it outputs. That is enough to stutter a phone. This one
/// searches a mono mix, coarse then fine, and at exactly 1.0 skips the search and
/// passes the audio through unchanged, so moving the slider back to the centre
/// costs nothing and is bit-exact.
/// <para>
/// <see cref="Position"/> and <see cref="Length"/> are in source samples, so the
/// player's <c>Duration</c> and <c>Seek</c> keep working in track time; the
/// player's own <c>Time</c> counts output samples and is wrong away from 1.0.
/// </para>
/// </remarks>
internal sealed class TimeStretchProvider : ISoundDataProvider
{
    /// <summary>
    /// Frames crossfaded between segments, and output per step. 23 ms at 44.1 kHz,
    /// the same window as SoundFlow's Fast preset that the app has always used.
    /// </summary>
    internal const int Hop = 512;
    /// <summary>How far either side of the nominal position to look for a match.</summary>
    internal const int Radius = 128;
    /// <summary>The coarse pass tries every Nth offset, comparing every Nth frame.</summary>
    private const int Coarse = 4;
    private const int Fine = 2;
    /// <summary>Holds the furthest reach of a step (natural continuation or search) with room to spare.</summary>
    private const int CapacityFrames = 8 * Hop + 4 * Radius;

    private readonly ISoundDataProvider _source;
    private readonly int _channels;
    private readonly Lock _gate = new();
    private readonly float[] _fadeIn = new float[Hop];

    // Decoded source audio, interleaved, with a mono mix alongside for the search.
    private readonly float[] _in;
    private readonly float[] _mono = new float[CapacityFrames];
    private int _inStart;          // source frame held at _in[0]
    private int _inFrames;
    private int _endFrame = int.MaxValue;   // source frame count, once the decoder runs dry

    // The step being handed out.
    private readonly float[] _out;
    private int _outFrames;
    private int _outRead;
    private int _outSourceFrame;

    private double _nominal;       // where the next segment would start at exactly this speed
    private int _prev = -1;        // where the previous segment started; -1 after a seek
    private volatile float _speed = 1f;
    private volatile int _position;     // published for the UI, which must not wait on the audio thread

    public TimeStretchProvider(ISoundDataProvider source, int channels)
    {
        _source = source;
        _channels = channels;
        _in = new float[CapacityFrames * channels];
        _out = new float[Hop * channels];
        // Raised cosine: fade-in and fade-out sum to exactly one, so identical
        // segments crossfade to themselves.
        for (int i = 0; i < Hop; i++)
            _fadeIn[i] = 0.5f - 0.5f * MathF.Cos(MathF.PI * i / Hop);
    }

    /// <summary>Playback rate; safe to set from any thread while audio is running.</summary>
    public float Speed
    {
        get => _speed;
        set => _speed = value;
    }

    /// <summary>The source sample about to be heard next.</summary>
    public int Position
    {
        get => _position;
    }

    public int Length => _source.Length;
    public bool CanSeek => _source.CanSeek;
    public SampleFormat SampleFormat => _source.SampleFormat;
    public int SampleRate => _source.SampleRate;
    public bool IsDisposed { get; private set; }
    public SoundFormatInfo? FormatInfo => _source.FormatInfo;

    public event EventHandler<EventArgs>? EndOfStreamReached
    {
        add => _source.EndOfStreamReached += value;
        remove => _source.EndOfStreamReached -= value;
    }

    public event EventHandler<PositionChangedEventArgs>? PositionChanged
    {
        add => _source.PositionChanged += value;
        remove => _source.PositionChanged -= value;
    }

    public int ReadBytes(Span<float> buffer)
    {
        lock (_gate)
        {
            if (IsDisposed) return 0;
            int written = 0;
            int wanted = buffer.Length - buffer.Length % _channels;
            while (written < wanted)
            {
                if (_outRead == _outFrames)
                {
                    _outSourceFrame += _outRead;
                    _outRead = _outFrames = 0;
                    if (!Step()) break;
                }
                int frames = Math.Min(_outFrames - _outRead, (wanted - written) / _channels);
                _out.AsSpan(_outRead * _channels, frames * _channels).CopyTo(buffer[written..]);
                _outRead += frames;
                written += frames * _channels;
            }
            _position = (_outSourceFrame + _outRead) * _channels;
            return written;
        }
    }

    public void Seek(int offset)
    {
        lock (_gate)
        {
            if (IsDisposed) return;
            _source.Seek(offset);
            int frame = offset / _channels;
            _inStart = _outSourceFrame = frame;
            _inFrames = _outFrames = _outRead = 0;
            _endFrame = int.MaxValue;
            _nominal = frame;
            _prev = -1;
            _position = frame * _channels;
        }
    }

    /// <summary>Produces the next <see cref="Hop"/> frames into <see cref="_out"/>.</summary>
    /// <returns>False once the track has been played out.</returns>
    private bool Step()
    {
        float speed = _speed;
        int best;
        if (_prev < 0)
        {
            best = (int)Math.Round(_nominal);
            if (best >= _endFrame || !Fill(best + Hop)) return false;
            _in.AsSpan((best - _inStart) * _channels, Hop * _channels).CopyTo(_out);
        }
        else
        {
            int natural = _prev + Hop;
            if (Math.Abs(speed - 1f) < 0.0005f)
            {
                // Straight on: the crossfade below is then of a segment with itself.
                best = natural;
                _nominal = natural;
                if (natural >= _endFrame || !Fill(natural + Hop)) return false;
            }
            else
            {
                int nominal = (int)Math.Round(_nominal);
                if (Math.Min(nominal, natural) >= _endFrame) return false;
                if (!Fill(Math.Max(nominal + Radius, natural) + Hop)) return false;
                best = Search(natural, Math.Max(nominal - Radius, _inStart), nominal + Radius);
            }
            Crossfade(natural, best);
        }
        _outSourceFrame = best;
        _outFrames = Hop;
        _prev = best;
        _nominal += Hop * (double)speed;
        Discard(Math.Min(_prev + Hop, (int)Math.Round(_nominal) - Radius));
        return true;
    }

    /// <summary>
    /// The offset in [lo, hi] whose start best continues the previous segment, by
    /// normalised correlation on the mono mix: every 4th offset first, then the
    /// neighbours of the winner.
    /// </summary>
    private int Search(int natural, int lo, int hi)
    {
        int best = lo;
        float bestScore = float.NegativeInfinity;
        for (int c = lo; c <= hi; c += Coarse)
        {
            float s = Score(natural, c, Coarse);
            if (s > bestScore) { bestScore = s; best = c; }
        }
        int centre = best;
        bestScore = Score(natural, centre, Fine);
        for (int c = Math.Max(lo, centre - Coarse + 1); c <= Math.Min(hi, centre + Coarse - 1); c++)
        {
            if (c == centre) continue;
            float s = Score(natural, c, Fine);
            if (s > bestScore) { bestScore = s; best = c; }
        }
        return best;
    }

    private float Score(int target, int candidate, int stride)
    {
        ReadOnlySpan<float> t = _mono.AsSpan(target - _inStart, Hop);
        ReadOnlySpan<float> c = _mono.AsSpan(candidate - _inStart, Hop);
        float dot = 0, energy = 1e-9f;
        for (int i = 0; i < Hop; i += stride)
        {
            dot += t[i] * c[i];
            energy += c[i] * c[i];
        }
        return dot / MathF.Sqrt(energy);
    }

    private void Crossfade(int from, int to)
    {
        ReadOnlySpan<float> a = _in.AsSpan((from - _inStart) * _channels, Hop * _channels);
        ReadOnlySpan<float> b = _in.AsSpan((to - _inStart) * _channels, Hop * _channels);
        for (int i = 0, k = 0; i < Hop; i++)
        {
            float f = _fadeIn[i];
            for (int ch = 0; ch < _channels; ch++, k++)
                _out[k] = a[k] + (b[k] - a[k]) * f;
        }
    }

    /// <summary>
    /// Decodes until source frames up to <paramref name="endFrame"/> are held. Past
    /// the end of the track the buffer reads as silence, so the last segments can
    /// still be searched and crossfaded.
    /// </summary>
    /// <returns>False if the request does not fit, which only a bad seek could cause.</returns>
    private bool Fill(int endFrame)
    {
        if (endFrame - _inStart > CapacityFrames) return false;
        while (_inStart + _inFrames < endFrame)
        {
            int start = _inFrames;
            int want = (endFrame - _inStart - _inFrames) * _channels;
            Span<float> dest = _in.AsSpan(start * _channels, want);
            int got = _endFrame == int.MaxValue ? _source.ReadBytes(dest) : 0;
            got -= got % _channels;
            if (got == 0)
            {
                if (_endFrame == int.MaxValue) _endFrame = _inStart + _inFrames;
                dest.Clear();
                got = want;
            }
            _inFrames += got / _channels;
            for (int f = start; f < _inFrames; f++)
            {
                float m = 0;
                for (int ch = 0; ch < _channels; ch++) m += _in[f * _channels + ch];
                _mono[f] = m;
            }
        }
        return true;
    }

    /// <summary>Drops held frames before <paramref name="keepFrom"/>, a hop at a time.</summary>
    private void Discard(int keepFrom)
    {
        int drop = Math.Min(keepFrom - _inStart, _inFrames);
        if (drop < Hop) return;
        int keep = _inFrames - drop;
        _in.AsSpan(drop * _channels, keep * _channels).CopyTo(_in);
        _mono.AsSpan(drop, keep).CopyTo(_mono);
        _inStart += drop;
        _inFrames = keep;
    }

    /// <summary>
    /// Waits for a read in progress. SoundFlow's mixer works from a snapshot of its
    /// players, so removing one does not stop a callback already running it; freeing
    /// the decoder under that callback would read freed native memory.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _source.Dispose();
        }
    }
}
