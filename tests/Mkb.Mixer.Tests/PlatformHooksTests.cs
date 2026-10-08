using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

/// <summary>
/// The two things the Android host reads off the view model: whether anything is
/// audible (to keep the process alive in the background), and which storage
/// volumes to offer as browser roots.
/// </summary>
public class PlatformHooksTests : IDisposable
{
    private readonly string _card = Directory.CreateTempSubdirectory("mkb-card").FullName;

    public void Dispose() => Directory.Delete(_card, recursive: true);

    private static MainViewModel NewVm(FakeAudioEngine engine) =>
        new(engine, new SettingsStore(Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")));

    [Fact]
    public void IsAnyDeckPlayingFollowsEitherDeck()
    {
        var engine = new FakeAudioEngine();
        var vm = NewVm(engine);
        engine.B.Load(new Track("/m/b.mp3", "B", Duration: TimeSpan.FromMinutes(3)));

        vm.Tick(TimeSpan.Zero);
        Assert.False(vm.IsAnyDeckPlaying);

        engine.B.Play();
        vm.Tick(TimeSpan.FromSeconds(1));
        Assert.True(vm.IsAnyDeckPlaying);
        Assert.Equal("B", vm.PlayingSummary);

        engine.B.Pause();
        vm.Tick(TimeSpan.FromSeconds(2));
        Assert.False(vm.IsAnyDeckPlaying);
    }

    [Fact]
    public void PlayingSummaryNamesBothDecksDuringATransition()
    {
        var engine = new FakeAudioEngine();
        var vm = NewVm(engine);
        engine.A.Load(new Track("/m/a.mp3", "Out", "X", Duration: TimeSpan.FromMinutes(3)));
        engine.B.Load(new Track("/m/b.mp3", "In", "Y", Duration: TimeSpan.FromMinutes(3)));
        engine.A.Play();
        engine.B.Play();

        vm.Tick(TimeSpan.Zero);

        Assert.Equal("X — Out → Y — In", vm.PlayingSummary);
    }

    [Fact]
    public void PlatformRootsReplaceTheDefaultsAndKeepTheirNames()
    {
        var vm = NewVm(new FakeAudioEngine());
        vm.PlatformRoots = () => [new StorageRoot(_card, "SanDisk SD card"), new StorageRoot("/nope/missing", "Gone")];

        vm.LoadRoots();

        var root = Assert.Single(vm.Roots.Where(r => !r.IsRecentlyPlayed));
        Assert.Equal(_card, root.Path);
        Assert.Equal("SanDisk SD card", root.Name);
    }
}
