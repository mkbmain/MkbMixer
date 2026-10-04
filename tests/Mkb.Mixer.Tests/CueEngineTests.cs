using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class CueAdapterTests
{
    [Fact]
    public void ACuedTapCopiesTheDeckIntoTheBusAndLeavesItUnchanged()
    {
        var bus = new CueBus { Active = true };
        var tap = new CueTap(bus) { IsCued = true };
        float[] block = [0.5f, -0.5f];

        tap.Process(block, 2);

        Assert.Equal(new[] { 0.5f, -0.5f }, block);
        var cue = new float[2];
        bus.Drain(cue);
        Assert.Equal(new[] { 0.5f, -0.5f }, cue);
    }

    [Fact]
    public void AnUncuedTapContributesNothing()
    {
        var bus = new CueBus { Active = true };
        new CueTap(bus).Process([0.5f, 0.5f], 2);

        var cue = new float[2];
        bus.Drain(cue);
        Assert.Equal(new[] { 0f, 0f }, cue);
    }

    [Fact]
    public void SplitSendsTheRoomLeftAndTheCueRight()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);
        var split = new CueSplitModifier(bus, () => 0f);   // headphones: cue only
        float[] master = [0.5f, 0.3f];

        split.Process(master, 2);

        Assert.Equal(0.4f, master[0], 5);
        Assert.Equal(1f, master[1], 5);
    }

    [Fact]
    public void SplitLeavesANonStereoOutputAlone()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f]);
        float[] master = [0.5f];

        new CueSplitModifier(bus, () => 0f).Process(master, 1);

        Assert.Equal(new[] { 0.5f }, master);
    }

    [Fact]
    public void TheFeedSendsTheBlendToTheRingWithoutTouchingTheRoom()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);
        var ring = new CueRingBuffer(capacity: 16, highWater: 16, channels: 2);
        float[] master = [0.2f, 0.2f];

        new CueFeedModifier(bus, ring, () => 0f).Process(master, 2);

        Assert.Equal(new[] { 0.2f, 0.2f }, master);
        var phones = new float[2];
        ring.Read(phones);
        Assert.Equal(new[] { 1f, 1f }, phones);
    }

    [Fact]
    public void CueSurvivesLoadingANewTrack()
    {
        // Each Load builds a new SoundFlow player; the deck's tap must move with it.
        var deck = new SoundFlowDeck(DeckId.A, engine: null, output: null, new CueBus());
        deck.IsCued = true;

        deck.Load(new Track("/m/next.mp3", "Next"));

        Assert.True(deck.IsCued);
    }
}

public class CueEngineTests
{
    [Fact]
    public void WithNoAudioOutputOnlyOffIsAccepted()
    {
        using var engine = new SoundFlowAudioEngine([]);

        Assert.False(engine.TrySetCue(CueMode.Split, null, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(CueMode.Off, engine.CueMode);
        Assert.True(engine.TrySetCue(CueMode.Off, null, out _));
    }

    [Fact]
    public void CueMixIsClamped()
    {
        using var engine = new SoundFlowAudioEngine([]);
        engine.CueMix = 3f;
        Assert.Equal(1f, engine.CueMix);
    }
}
