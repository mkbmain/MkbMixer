using SoundFlow.Abstracts;
using SoundFlow.Structs;

namespace Mkb.Mixer.Audio;

// The headphone cue's SoundFlow plumbing. Kept thin: the arithmetic lives in
// CueMixing, CueBus and CueRingBuffer, which are tested without a sound card.

/// <summary>
/// Sits on a deck's player. SoundFlow runs modifiers before the component's
/// volume, so this sees the deck before the crossfader and mute are applied.
/// </summary>
internal sealed class CueTap(CueBus bus) : SoundModifier
{
    private volatile bool _cued;

    public override string Name { get; set; } = "Cue tap";

    public bool IsCued
    {
        get => _cued;
        set => _cued = value;
    }

    public override void Process(Span<float> buffer, int channels)
    {
        if (_cued) bus.Add(buffer);
    }

    public override float ProcessSample(float sample, int channel) => sample;
}

/// <summary>
/// Split mode, on the master mixer: left becomes the room mix in mono, right the
/// headphone feed in mono. Runs after every deck in the same callback, so the bus
/// holds exactly this block's cue.
/// </summary>
internal sealed class CueSplitModifier(CueBus bus, Func<float> mix) : SoundModifier
{
    private float[] _cue = [];
    private float[] _phones = [];

    public override string Name { get; set; } = "Cue split";

    public override void Process(Span<float> buffer, int channels)
    {
        Span<float> cue = CueScratch.Get(ref _cue, buffer.Length);
        bus.Drain(cue);
        if (channels != 2) return;   // split needs a left and a right; leave anything else alone

        Span<float> phones = CueScratch.Get(ref _phones, buffer.Length);
        CueMixing.Blend(cue, buffer, mix(), phones);
        CueMixing.Split(buffer, phones);
    }

    public override float ProcessSample(float sample, int channel) => sample;
}

/// <summary>
/// Device mode, on the master mixer: hands the headphone feed to the cue device
/// through the ring buffer and leaves the room mix untouched.
/// </summary>
internal sealed class CueFeedModifier(CueBus bus, CueRingBuffer ring, Func<float> mix) : SoundModifier
{
    private float[] _cue = [];
    private float[] _phones = [];

    public override string Name { get; set; } = "Cue feed";

    public override void Process(Span<float> buffer, int channels)
    {
        Span<float> cue = CueScratch.Get(ref _cue, buffer.Length);
        bus.Drain(cue);
        Span<float> phones = CueScratch.Get(ref _phones, buffer.Length);
        CueMixing.Blend(cue, buffer, mix(), phones);
        ring.Write(phones);
    }

    public override float ProcessSample(float sample, int channel) => sample;
}

/// <summary>Device mode, on the cue device's mixer: plays what the feed wrote.</summary>
internal sealed class CueSource(
    AudioEngine engine, AudioFormat format, CueRingBuffer ring, CueWatchdog watchdog)
    : SoundComponent(engine, format)
{
    public override string Name { get; set; } = "Cue";

    protected override void GenerateAudio(Span<float> buffer, int channels)
    {
        watchdog.Stamp(Environment.TickCount64);   // a device that stops asking has gone
        ring.Read(buffer);
    }
}

internal static class CueScratch
{
    /// <summary>A reusable buffer, grown only when a device asks for a bigger block.</summary>
    public static Span<float> Get(ref float[] array, int length)
    {
        if (array.Length < length) array = new float[length];
        return array.AsSpan(0, length);
    }
}
