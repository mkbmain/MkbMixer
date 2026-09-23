using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class CrossfaderTests
{
    [Fact]
    public void FullyLeft_IsDeckAOnly()
    {
        var (a, b) = Crossfader.Gains(0f);
        Assert.Equal(1f, a, 4);
        Assert.Equal(0f, b, 4);
    }

    [Fact]
    public void FullyRight_IsDeckBOnly()
    {
        var (a, b) = Crossfader.Gains(1f);
        Assert.Equal(0f, a, 4);
        Assert.Equal(1f, b, 4);
    }

    [Fact]
    public void Centre_IsEqualAndConstantPower()
    {
        var (a, b) = Crossfader.Gains(0.5f);
        Assert.Equal(a, b, 4);
        // Constant power: the two gains sum in quadrature to 1, so the
        // perceived loudness does not dip through the middle of the fade.
        Assert.Equal(1f, a * a + b * b, 4);
    }

    [Theory]
    [InlineData(0f)] [InlineData(0.25f)] [InlineData(0.5f)]
    [InlineData(0.75f)] [InlineData(1f)] [InlineData(0.1f)] [InlineData(0.9f)]
    public void PowerIsConstantEverywhere(float x)
    {
        var (a, b) = Crossfader.Gains(x);
        Assert.Equal(1f, a * a + b * b, 4);
    }

    [Fact]
    public void IsContinuous_UnlikeTheOriginal()
    {
        // The .NET 2.0 original jumped deck A from gain 1.0 to 0.49 as the
        // slider crossed the midpoint. Assert no discontinuity anywhere.
        float prevA = Crossfader.Gains(0f).DeckA;
        for (int i = 1; i <= 1000; i++)
        {
            float a = Crossfader.Gains(i / 1000f).DeckA;
            Assert.True(Math.Abs(a - prevA) < 0.01f,
                $"discontinuity at x={i / 1000f}: {prevA} -> {a}");
            prevA = a;
        }
    }

    [Fact]
    public void GainsNeverExceedUnity()
    {
        // The original computed (100 - v) * 2, which overflowed to 200 and was
        // silently clamped by Windows Media Player.
        for (int i = 0; i <= 1000; i++)
        {
            var (a, b) = Crossfader.Gains(i / 1000f);
            Assert.InRange(a, 0f, 1f);
            Assert.InRange(b, 0f, 1f);
        }
    }

    [Theory]
    [InlineData(-0.5f, 1f, 0f)]
    [InlineData(1.5f, 0f, 1f)]
    public void PositionIsClamped(float x, float expectedA, float expectedB)
    {
        var (a, b) = Crossfader.Gains(x);
        Assert.Equal(expectedA, a, 4);
        Assert.Equal(expectedB, b, 4);
    }
}
