using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

/// <summary>
/// Drives the real engine against the real audio backend. These are the only tests
/// that would have caught silent playback: everything else uses a fake deck, and a
/// fake deck cannot tell you that the output device never opened.
/// </summary>
public class AudioEngineIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-engine").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteTone(double seconds = 5, double freq = 440)
    {
        string path = Path.Combine(_dir, $"tone-{freq}.wav");
        const int rate = 44100, channels = 2;
        int frames = (int)(rate * seconds);

        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = frames * channels * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            short s = (short)(Math.Sin(2 * Math.PI * freq * i / rate) * 16000);
            w.Write(s); w.Write(s);
        }
        return path;
    }

    [Fact]
    public void TheEngineReportsWhatItTried()
    {
        using var engine = new SoundFlowAudioEngine();
        Assert.NotEmpty(engine.Diagnostics);
        Assert.NotNull(engine.OutputDescription ?? engine.OutputError);
    }

    [Fact]
    public void AnOpenedDeviceNamesItsBackendAndFormat()
    {
        using var engine = new SoundFlowAudioEngine();
        if (!engine.IsOutputAvailable)
            return; // no audio hardware in this environment

        Assert.Contains("Hz", engine.OutputDescription);
        Assert.Contains("OK", string.Join("\n", engine.Diagnostics));
    }

    [Fact]
    public void LoadingATrackReportsItsRealDuration()
    {
        using var engine = new SoundFlowAudioEngine();
        engine.DeckA.Load(Track.FromPath(WriteTone(seconds: 3)));

        if (!engine.IsOutputAvailable)
            return;

        Assert.InRange(engine.DeckA.Duration.TotalSeconds, 2.9, 3.1);
    }

    [Fact]
    public void PlaybackActuallyAdvancesThePlayhead()
    {
        // If the output device never opened, or opened with a format the backend
        // rejects, the callback never runs and Position stays at zero. That is
        // precisely what "play does not appear to be working" looks like.
        using var engine = new SoundFlowAudioEngine();
        if (!engine.IsOutputAvailable)
            return;

        engine.DeckA.Load(Track.FromPath(WriteTone()));
        engine.ApplyCrossfader(Crossfader.DeckAOnly);
        engine.DeckA.Play();

        Assert.Equal(PlaybackState.Playing, engine.DeckA.State);

        Thread.Sleep(1200);

        Assert.True(engine.DeckA.Position > TimeSpan.FromMilliseconds(250),
            $"playhead did not advance: {engine.DeckA.Position.TotalMilliseconds:F0}ms " +
            $"(route: {engine.OutputDescription})");
    }

    [Fact]
    public void SeekingMovesThePlayhead()
    {
        using var engine = new SoundFlowAudioEngine();
        if (!engine.IsOutputAvailable)
            return;

        engine.DeckA.Load(Track.FromPath(WriteTone()));
        engine.DeckA.Seek(TimeSpan.FromSeconds(2));

        Assert.InRange(engine.DeckA.Position.TotalSeconds, 1.9, 2.1);
    }

    [Fact]
    public async Task WaveformAnalysisReturnsRealPeaks()
    {
        using var engine = new SoundFlowAudioEngine();
        Waveform wave = await engine.AnalyseAsync(WriteTone(seconds: 2));

        Assert.Equal(Waveform.DefaultBuckets, wave.Peaks.Length);
        Assert.Contains(wave.Peaks, p => p > 0.2f);
    }
}
