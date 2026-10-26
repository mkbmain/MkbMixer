using Mkb.Mixer.Audio;
using SoundFlow.Providers;

namespace Mkb.Mixer.Tests;

public class TimeStretchTests
{
    private const int Rate = 44100;

    /// <summary>Stereo sine, left and right a little apart so a channel swap would show.</summary>
    private static float[] Sine(double seconds, double hz = 440)
    {
        var s = new float[(int)(seconds * Rate) * 2];
        for (int f = 0; f < s.Length / 2; f++)
        {
            s[2 * f] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * f / Rate));
            s[2 * f + 1] = (float)(0.4 * Math.Sin(2 * Math.PI * hz * f / Rate));
        }
        return s;
    }

    private static TimeStretchProvider Stretch(float[] source, float speed) =>
        new(new RawDataProvider(source), 2) { Speed = speed };

    /// <summary>Reads to the end in device-sized chunks, as the player does.</summary>
    private static float[] ReadAll(TimeStretchProvider p, int chunk = 1024)
    {
        var all = new List<float>();
        var buf = new float[chunk];
        int n;
        while ((n = p.ReadBytes(buf)) > 0) all.AddRange(buf.AsSpan(0, n).ToArray());
        return [.. all];
    }

    private static double Frequency(float[] stereo)
    {
        int crossings = 0;
        for (int f = 1; f < stereo.Length / 2; f++)
            if (stereo[2 * (f - 1)] < 0 && stereo[2 * f] >= 0) crossings++;
        return crossings / (stereo.Length / 2.0 / Rate);
    }

    [Fact]
    public void NormalSpeedPassesTheAudioThroughUnchanged()
    {
        float[] source = Sine(1);
        float[] output = ReadAll(Stretch(source, 1f), chunk: 777 * 2);

        Assert.True(output.Length >= source.Length);
        for (int i = 0; i < source.Length; i++)
            Assert.True(Math.Abs(source[i] - output[i]) < 1e-6, $"sample {i}: {source[i]} vs {output[i]}");
    }

    [Theory]
    [InlineData(1.25f)]
    [InlineData(0.8f)]
    [InlineData(1.5f)]
    [InlineData(0.5f)]
    public void SpeedChangesTheLengthButNotThePitch(float speed)
    {
        float[] source = Sine(4);
        float[] output = ReadAll(Stretch(source, speed));

        double expected = source.Length / speed;
        Assert.InRange(output.Length, expected * 0.98, expected * 1.02 + 4 * TimeStretchProvider.Hop);
        Assert.InRange(Frequency(output[..(int)(expected * 0.9)]), 430, 450);
    }

    [Fact]
    public void PositionFollowsTheSourceNotTheOutput()
    {
        var p = Stretch(Sine(4), 1.25f);
        var buf = new float[2 * Rate];   // one second of output
        Assert.Equal(buf.Length, p.ReadBytes(buf));

        double seconds = p.Position / 2.0 / Rate;
        Assert.InRange(seconds, 1.2, 1.3);
    }

    [Fact]
    public void SeekJumpsToTheSourcePosition()
    {
        float[] source = Sine(4);
        var p = Stretch(source, 1f);
        ReadAll(p);

        p.Seek(2 * Rate * 2);
        Assert.Equal(2 * Rate * 2, p.Position);

        var buf = new float[2048];
        Assert.Equal(buf.Length, p.ReadBytes(buf));
        for (int i = 0; i < buf.Length; i++)
            Assert.Equal(source[2 * Rate * 2 + i], buf[i], 1e-6f);
    }

    [Fact]
    public void ChangingSpeedMidTrackDoesNotClick()
    {
        float[] source = Sine(3);
        var p = Stretch(source, 1f);
        var output = new List<float>();
        var buf = new float[1024];
        foreach (float speed in new[] { 1f, 1.1f, 0.9f, 1f, 1.3f, 1f })
        {
            p.Speed = speed;
            for (int k = 0; k < 20; k++)
            {
                int n = p.ReadBytes(buf);
                output.AddRange(buf.AsSpan(0, n).ToArray());
            }
        }

        // A 440 Hz sine at 0.5 moves at most ~0.031 per sample; a splice that
        // lands out of phase would jump far more than that.
        for (int f = 1; f < output.Count / 2; f++)
            Assert.True(Math.Abs(output[2 * f] - output[2 * (f - 1)]) < 0.05,
                $"jump at frame {f}: {output[2 * (f - 1)]} -> {output[2 * f]}");
    }
}

public class TimeStretchDisposeTests
{
    /// <summary>A source whose reads block until released, and that records misuse after disposal.</summary>
    private sealed class SlowSource(float[] data) : SoundFlow.Interfaces.ISoundDataProvider
    {
        private readonly RawDataProvider _inner = new(data);
        public readonly ManualResetEventSlim Reading = new();
        public readonly ManualResetEventSlim Release = new();
        public volatile bool Disposed;
        public volatile bool ReadAfterDispose;

        public int ReadBytes(Span<float> buffer)
        {
            if (Disposed) ReadAfterDispose = true;
            Reading.Set();
            Release.Wait();
            return _inner.ReadBytes(buffer);
        }

        public void Dispose() => Disposed = true;
        public int Position => _inner.Position;
        public int Length => _inner.Length;
        public bool CanSeek => true;
        public SoundFlow.Enums.SampleFormat SampleFormat => _inner.SampleFormat;
        public int SampleRate => 44100;
        public bool IsDisposed => Disposed;
        public SoundFlow.Metadata.Models.SoundFormatInfo? FormatInfo => null;
        public event EventHandler<EventArgs>? EndOfStreamReached { add { } remove { } }
        public event EventHandler<SoundFlow.Interfaces.PositionChangedEventArgs>? PositionChanged { add { } remove { } }
        public void Seek(int offset) => _inner.Seek(offset);
    }

    [Fact]
    public async Task DisposeWaitsForAReadInProgressAndLaterReadsReturnNothing()
    {
        var source = new SlowSource(new float[44100 * 2]);
        var p = new TimeStretchProvider(source, 2);
        var read = Task.Run(() => p.ReadBytes(new float[1024]));
        Assert.True(source.Reading.Wait(TimeSpan.FromSeconds(5)));

        var dispose = Task.Run(p.Dispose);
        Task first = await Task.WhenAny(dispose, Task.Delay(200));
        Assert.False(first == dispose, "disposed the decoder under a running read");

        source.Release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await read;
        Assert.True(source.Disposed);

        Assert.Equal(0, p.ReadBytes(new float[1024]));
        Assert.False(source.ReadAfterDispose);
    }
}
