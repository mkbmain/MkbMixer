using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class CueMixingTests
{
    [Fact]
    public void MixAtZeroIsCueOnly()
    {
        var dest = new float[2];
        CueMixing.Blend([0.4f, -0.2f], [0.9f, 0.9f], 0f, dest);
        Assert.Equal(0.4f, dest[0], 5);
        Assert.Equal(-0.2f, dest[1], 5);
    }

    [Fact]
    public void MixAtOneIsMasterOnly()
    {
        var dest = new float[2];
        CueMixing.Blend([0.4f, -0.2f], [0.9f, 0.1f], 1f, dest);
        Assert.Equal(0.9f, dest[0], 5);
        Assert.Equal(0.1f, dest[1], 5);
    }

    [Fact]
    public void TheBlendIsConstantPower()
    {
        var dest = new float[1];
        CueMixing.Blend([1f], [1f], 0.5f, dest);
        Assert.Equal(2 * MathF.Sqrt(0.5f), dest[0], 4);
    }

    [Fact]
    public void SplitPutsMonoMasterLeftAndMonoHeadphonesRight()
    {
        float[] master = [1f, 0.5f, -1f, 0f];
        float[] phones = [0.2f, 0.4f, 0f, 1f];

        CueMixing.Split(master, phones);

        Assert.Equal(new[] { 0.75f, 0.3f, -0.5f, 0.5f }, master);
    }
}

public class CueBusTests
{
    [Fact]
    public void CuedBlocksAreSummed()
    {
        var bus = new CueBus { Active = true };
        bus.Add([0.1f, 0.2f]);
        bus.Add([0.3f, 0.4f]);

        var dest = new float[2];
        bus.Drain(dest);

        Assert.Equal(0.4f, dest[0], 5);
        Assert.Equal(0.6f, dest[1], 5);
    }

    [Fact]
    public void DrainingClearsForTheNextBlock()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);
        bus.Drain(new float[2]);

        var dest = new float[] { 9f, 9f };
        bus.Drain(dest);

        Assert.Equal(new[] { 0f, 0f }, dest);
    }

    [Fact]
    public void ADestinationLongerThanTheBlockIsPaddedWithSilence()
    {
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);

        var dest = new float[] { 9f, 9f, 9f, 9f };
        bus.Drain(dest);

        Assert.Equal(new[] { 1f, 1f, 0f, 0f }, dest);
    }

    [Fact]
    public void AnInactiveBusIgnoresBlocks()
    {
        var bus = new CueBus();
        bus.Add([1f, 1f]);

        var dest = new float[2];
        bus.Drain(dest);

        Assert.Equal(new[] { 0f, 0f }, dest);
    }

    [Fact]
    public void ActivatingDiscardsAnythingAddedWhileInactive()
    {
        // Decks can be cued while the mode is Off. Turning a mode on must not
        // release a burst of audio that piled up in the meantime.
        var bus = new CueBus { Active = true };
        bus.Add([1f, 1f]);
        bus.Active = false;
        bus.Add([1f, 1f]);
        bus.Active = true;

        var dest = new float[2];
        bus.Drain(dest);

        Assert.Equal(new[] { 0f, 0f }, dest);
    }
}

public class CueRingBufferTests
{
    private static float[] Range(int start, int count) =>
        Enumerable.Range(start, count).Select(i => (float)i).ToArray();

    [Fact]
    public void ReadsBackWhatWasWritten()
    {
        var ring = new CueRingBuffer(capacity: 16, highWater: 16, channels: 2);
        ring.Write(Range(0, 4));

        var dest = new float[4];
        ring.Read(dest);

        Assert.Equal(Range(0, 4), dest);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void PadsWithSilenceWhenItRunsShort()
    {
        var ring = new CueRingBuffer(capacity: 16, highWater: 16, channels: 2);
        ring.Write([1f, 2f]);

        var dest = new float[] { 9f, 9f, 9f, 9f };
        ring.Read(dest);

        Assert.Equal(new[] { 1f, 2f, 0f, 0f }, dest);
    }

    [Fact]
    public void DropsWhatDoesNotFitWhenFull()
    {
        var ring = new CueRingBuffer(capacity: 8, highWater: 8, channels: 2);
        Assert.Equal(8, ring.Write(Range(0, 10)));
        Assert.Equal(8, ring.Count);
    }

    [Fact]
    public void SkipsAheadWhenTheReaderFallsTooFarBehind()
    {
        var ring = new CueRingBuffer(capacity: 64, highWater: 8, channels: 2);
        ring.Write(Range(0, 40));

        var dest = new float[4];
        ring.Read(dest);

        Assert.Equal(Range(36, 4), dest);
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void SkipAheadKeepsLeftAndRightAlignedForAnOddDestination()
    {
        var ring = new CueRingBuffer(capacity: 64, highWater: 8, channels: 2);
        ring.Write(Range(0, 40));   // even index = left, odd = right

        var dest = new float[5];
        ring.Read(dest);

        Assert.Equal(Range(36, 4), dest[..4]);   // 2 whole frames, newest ones
        Assert.Equal(0f, dest[4]);
        Assert.Equal(0, ring.Count % 2);
    }

    [Fact]
    public void WrapsAroundInOrder()
    {
        var ring = new CueRingBuffer(capacity: 8, highWater: 8, channels: 2);
        ring.Write(Range(0, 6));
        ring.Read(new float[6]);
        ring.Write(Range(6, 6));

        var dest = new float[6];
        ring.Read(dest);

        Assert.Equal(Range(6, 6), dest);
    }

    [Fact]
    public void KeepsFramesWhole()
    {
        // A dropped half-frame would swap left and right for the rest of the set.
        var ring = new CueRingBuffer(capacity: 8, highWater: 8, channels: 2);
        ring.Write(Range(0, 6));
        Assert.Equal(2, ring.Write(Range(6, 3)));
    }

    [Fact]
    public void RejectsACapacityThatIsNotWholeFrames() =>
        Assert.Throws<ArgumentException>(() => new CueRingBuffer(capacity: 7, highWater: 4, channels: 2));
}
