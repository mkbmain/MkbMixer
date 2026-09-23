using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Mkb.Mixer.Audio;

/// <summary>
/// <see cref="IAudioEngine"/> on top of SoundFlow's miniaudio backend, which ships
/// natives for Windows, macOS, Linux, FreeBSD, Android and iOS.
/// </summary>
public sealed class SoundFlowAudioEngine : IAudioEngine
{
    private readonly MiniAudioEngine _engine;
    private readonly SoundFlow.Abstracts.Devices.AudioPlaybackDevice? _device;
    private readonly SoundFlowDeck _a;
    private readonly SoundFlowDeck _b;
    private bool _disposed;

    internal static readonly AudioFormat Format = new()
    {
        SampleRate = 44100,
        Channels = 2,
        Format = SampleFormat.F32
    };

    /// <summary>Backends to try, in order, for the host platform.</summary>
    private static MiniAudioBackend[] PreferredBackends()
    {
        if (OperatingSystem.IsWindows())
            return [MiniAudioBackend.Wasapi, MiniAudioBackend.DirectSound, MiniAudioBackend.WinMm];
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
            return [MiniAudioBackend.CoreAudio];
        return [MiniAudioBackend.PulseAudio, MiniAudioBackend.Alsa, MiniAudioBackend.Jack];
    }

    public SoundFlowAudioEngine()
    {
        _engine = new MiniAudioEngine(PreferredBackends());

        try
        {
            _device = _engine.InitializePlaybackDevice(null, Format);
            _device.Start();
            IsOutputAvailable = true;
        }
        catch (Exception e)
        {
            // No sound card, or the daemon is not running. The app still starts so
            // the user can browse and build playlists; it just cannot make noise.
            OutputError = e.Message;
            IsOutputAvailable = false;
        }

        _a = new SoundFlowDeck(DeckId.A, _engine, _device?.MasterMixer);
        _b = new SoundFlowDeck(DeckId.B, _engine, _device?.MasterMixer);
    }

    public IDeck DeckA => _a;
    public IDeck DeckB => _b;
    public bool IsOutputAvailable { get; }
    public string? OutputError { get; }

    public void ApplyCrossfader(float position)
    {
        var (gainA, gainB) = Crossfader.Gains(position);
        _a.Volume = gainA;
        _b.Volume = gainB;
    }

    public Task<Waveform> AnalyseAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var provider = new StreamDataProvider(_engine, Format, stream);

                var samples = new List<float>(capacity: 1 << 20);
                var buffer = new float[16384];
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = provider.ReadBytes(buffer);
                    if (read <= 0) break;
                    samples.AddRange(buffer.AsSpan(0, read));
                }

                return Waveform.FromSamples(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(samples),
                    Format.Channels,
                    Waveform.DefaultBuckets);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return Waveform.Empty;
            }
        }, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _a.Dispose();
        _b.Dispose();
        _device?.Dispose();
        _engine.Dispose();
    }
}
