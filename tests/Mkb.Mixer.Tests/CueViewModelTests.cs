using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class CueViewModelTests
{
    private static string NewSettingsPath() =>
        Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json");

    private static MainViewModel Vm(FakeAudioEngine engine, string? path = null) =>
        new(engine, new SettingsStore(path ?? NewSettingsPath()));

    [Fact]
    public void CueIsOffAndDeckCueButtonsUnavailableByDefault()
    {
        var vm = Vm(new FakeAudioEngine());

        Assert.Equal(CueMode.Off, vm.CueMode);
        Assert.False(vm.DeckA.IsCueAvailable);
        Assert.False(vm.DeckB.IsCueAvailable);
    }

    [Fact]
    public void ChoosingSplitRoutesTheEngineAndEnablesDeckCue()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);

        vm.CueMode = CueMode.Split;

        Assert.Equal(CueMode.Split, engine.CueMode);
        Assert.True(vm.DeckA.IsCueAvailable);
        Assert.True(vm.DeckB.IsCueAvailable);
    }

    [Fact]
    public void ChoosingDeviceListsDevicesAndPicksTheFirst()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);

        vm.CueMode = CueMode.Device;

        Assert.Equal(new[] { "Fake headphones", "Fake USB" }, vm.CueDevices);
        Assert.True(vm.IsCueDeviceMode);
        Assert.Equal("Fake headphones", vm.CueDevice);
        Assert.Equal("Fake headphones", engine.CueDevice);
    }

    [Fact]
    public void PickingAnotherDeviceReroutes()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);
        vm.CueMode = CueMode.Device;

        vm.CueDevice = "Fake USB";

        Assert.Equal("Fake USB", engine.CueDevice);
    }

    [Fact]
    public void AFailedModeFallsBackToOffAndSaysWhy()
    {
        var engine = new FakeAudioEngine { CueFailure = "device busy" };
        var vm = Vm(engine);

        vm.CueMode = CueMode.Split;

        Assert.Equal(CueMode.Off, vm.CueMode);
        Assert.Equal(CueMode.Off, engine.CueMode);
        Assert.False(vm.DeckA.IsCueAvailable);
        Assert.Contains("device busy", vm.StatusMessage);
    }

    [Fact]
    public void CueingADeckReachesTheEngineDeck()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);

        vm.DeckB.IsCued = true;

        Assert.True(engine.B.IsCued);
        Assert.False(engine.A.IsCued);
    }

    [Fact]
    public void TheCueMixKnobReachesTheEngine()
    {
        var engine = new FakeAudioEngine();
        var vm = Vm(engine);

        vm.CueMix = 0.7f;

        Assert.Equal(0.7f, engine.CueMix);
    }

    [Fact]
    public void CueSettingsAreRememberedAndReapplied()
    {
        string path = NewSettingsPath();
        var vm = Vm(new FakeAudioEngine(), path);
        vm.CueMode = CueMode.Device;
        vm.CueDevice = "Fake USB";
        vm.CueMix = 0.25f;
        vm.SaveState();

        var engine = new FakeAudioEngine();
        var reloaded = Vm(engine, path);

        Assert.Equal(CueMode.Device, reloaded.CueMode);
        Assert.Equal("Fake USB", engine.CueDevice);
        Assert.Equal(0.25f, engine.CueMix);
    }

    [Fact]
    public void RestoringAMissingCueDeviceFallsBackToOff()
    {
        // The USB headphones were unplugged between runs. Picking another device
        // instead could put the headphone feed on the room speakers.
        string path = NewSettingsPath();
        var vm = Vm(new FakeAudioEngine(), path);
        vm.CueMode = CueMode.Device;
        vm.CueDevice = "Fake USB";
        vm.SaveState();

        var engine = new FakeAudioEngine();
        engine.CueDeviceList.Remove("Fake USB");
        var reloaded = Vm(engine, path);

        Assert.Equal(CueMode.Off, reloaded.CueMode);
        Assert.Equal(CueMode.Off, engine.CueMode);
        Assert.Contains("Fake USB", reloaded.StatusMessage);
    }
}
