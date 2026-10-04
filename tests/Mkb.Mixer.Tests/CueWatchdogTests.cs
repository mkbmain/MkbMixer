using Mkb.Mixer.Audio;

namespace Mkb.Mixer.Tests;

public class CueWatchdogTests
{
    [Fact]
    public void IsNeverStalledUntilArmed()
    {
        Assert.False(new CueWatchdog().IsStalled(1_000_000));
    }

    [Fact]
    public void IsStalledOnlyAfterTheThreshold()
    {
        var dog = new CueWatchdog();
        dog.Arm(1000);

        Assert.False(dog.IsStalled(1500));
        Assert.True(dog.IsStalled(1501));
    }

    [Fact]
    public void StampingKeepsItAlive()
    {
        var dog = new CueWatchdog();
        dog.Arm(1000);
        dog.Stamp(1900);

        Assert.False(dog.IsStalled(2300));
        Assert.True(dog.IsStalled(2401));
    }

    [Fact]
    public void DisarmingSilencesIt()
    {
        var dog = new CueWatchdog();
        dog.Arm(1000);
        dog.Disarm();

        Assert.False(dog.IsStalled(9000));
    }

    [Theory]
    [InlineData("Speakers", false, "Speakers", false, true)]    // opened by name
    [InlineData("USB", false, "Speakers", false, false)]
    [InlineData("Speakers", true, null, true, true)]            // opened "default": the default is the room
    [InlineData("USB", false, null, true, false)]
    [InlineData("USB", false, null, false, false)]              // nothing opened
    public void ExcludesTheMasterOutput(string name, bool isDefault, string? master, bool masterDefault, bool expected) =>
        Assert.Equal(expected, CueDeviceRules.IsMasterOutput(name, isDefault, master, masterDefault));
}
