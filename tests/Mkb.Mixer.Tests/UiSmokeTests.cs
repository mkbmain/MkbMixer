using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.App.Views;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;
using Track = Mkb.Mixer.Audio.Track;

namespace Mkb.Mixer.Tests;

/// <summary>
/// Renders the real window headlessly. This catches XAML binding mistakes that a
/// compile cannot, and writes a PNG so the layout can be eyeballed.
/// </summary>
public class UiSmokeTests
{
    private static (MainWindow Window, MainViewModel Vm, FakeAudioEngine Engine) Build()
    {
        var engine = new FakeAudioEngine();
        string settings = Path.Combine(Path.GetTempPath(), $"mkb-ui-{Guid.NewGuid():N}.json");
        var vm = new MainViewModel(engine, new SettingsStore(settings));
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        return (window, vm, engine);
    }

    [Fact]
    public void WindowRendersWithoutBindingErrors() => AvaloniaTest.Run(() =>
    {
        var (window, _, _) = Build();
        window.Show();
        Assert.True(window.IsVisible);
        Assert.Equal("MKB Music Mixer", window.Title);
    });

    [Fact]
    public void DecksShowShuffleRepeatAndCueToggles() => AvaloniaTest.Run(() =>
    {
        var (window, vm, _) = Build();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        List<ToggleButton> toggles = window.GetLogicalDescendants().OfType<ToggleButton>().ToList();
        List<ToggleButton> cues = toggles.Where(t => t.Content as string == "CUE").ToList();
        Assert.Equal(2, cues.Count);
        Assert.Equal(2, toggles.Count(t => t.Content as string == "⇄"));
        Assert.Equal(2, toggles.Count(t => t.Content as string == "↻"));
        Assert.All(cues, c => Assert.False(c.IsEffectivelyEnabled));

        vm.CueMode = CueMode.Split;
        Dispatcher.UIThread.RunJobs();

        Assert.All(cues, c => Assert.True(c.IsEffectivelyEnabled));
    });

    [Fact]
    public void CueDeviceSurvivesSwitchingModesWithLiveComboBox() => AvaloniaTest.Run(() =>
    {
        var (window, vm, _) = Build();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        vm.CueMode = CueMode.Device;
        Dispatcher.UIThread.RunJobs();
        vm.CueDevice = "Fake USB";
        Dispatcher.UIThread.RunJobs();
        vm.CueMode = CueMode.Split;
        Dispatcher.UIThread.RunJobs();
        vm.CueMode = CueMode.Device;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Fake USB", vm.CueDevice);
    });

    [Fact]
    public void RendersPopulatedLayoutToPng() => AvaloniaTest.Run(() =>
    {
        var (window, vm, engine) = Build();

        // Give both decks a loaded track, a waveform and a playlist.
        var a = new Track("/music/sandstorm.mp3", "Sandstorm", "Darude", "Before the Storm", TimeSpan.FromSeconds(224));
        var b = new Track("/music/kalimba.mp3", "Kalimba", "Mr. Scruff", "Ninja Tuna", TimeSpan.FromSeconds(348));

        engine.A.Duration = a.Duration;
        engine.B.Duration = b.Duration;
        vm.DeckA.LoadAsync(a).Wait();
        vm.DeckB.LoadAsync(b).Wait();
        engine.A.Position = TimeSpan.FromSeconds(96);
        engine.A.Play();
        engine.B.Position = TimeSpan.FromSeconds(18);
        vm.DeckA.Refresh();
        vm.DeckB.Refresh();
        vm.DeckB.IsMuted = true;

        foreach (var t in new[]
                 {
                     new Track("/m/1.mp3", "Sandstorm", "Darude", "Before the Storm", TimeSpan.FromSeconds(224)),
                     new Track("/m/2.mp3", "Get Up", "Technotronic", "Pump Up the Jam", TimeSpan.FromSeconds(203)),
                 })
            vm.DeckA.Playlist.Add(t);

        vm.DeckB.Playlist.Add(new Track("/m/3.mp3", "Kalimba", "Mr. Scruff", "Ninja Tuna", TimeSpan.FromSeconds(348)));

        vm.BrowserTracks.Add(a);
        vm.BrowserTracks.Add(b);
        vm.BrowserTracks.Add(new Track("/m/4.mp3", "Windowlicker", "Aphex Twin", "Windowlicker", TimeSpan.FromSeconds(366)));
        vm.CrossfaderPosition = 0.35f;
        vm.AutoCueEnabled = true;
        vm.StatusMessage = "3 track(s) in Demo";
        vm.CueMode = CueMode.Device;
        vm.CueDevice = "Fake USB";
        vm.DeckB.IsCued = true;
        vm.DeckA.Shuffle = true;

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // An expanded folder tree, so the snapshot exercises navigation too.
        string demo = Path.Combine(Path.GetTempPath(), "mkb-demo-library");
        foreach (var sub in new[] { "Albums", "Albums/1999", "Singles", "Mixes" })
            Directory.CreateDirectory(Path.Combine(demo, sub));
        var root = new FolderNode(demo) { IsExpanded = true };
        root.Children.First(c => c.Name == "Albums").IsExpanded = true;
        vm.Roots.Insert(0, root);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        string output = Environment.GetEnvironmentVariable("MKB_UI_SNAPSHOT")
                        ?? Path.Combine(Path.GetTempPath(), "mkb-mixer-ui.png");
        using Bitmap frame = window.CaptureRenderedFrame()
                             ?? throw new InvalidOperationException("no frame captured");
        // The BitmapEncoderOptions overload is the non-obsolete one, but the plain
        // path overload is all this snapshot needs.
#pragma warning disable CS0618
        frame.Save(output);
#pragma warning restore CS0618

        Assert.True(new FileInfo(output).Length > 10_000, "rendered frame looks empty");
    });

    /// <summary>
    /// Renders each tab of the phone layout at a typical phone size, writing one PNG
    /// per tab next to the desktop snapshot.
    /// </summary>
    [Fact]
    public void RendersPhoneLayoutToPng() => AvaloniaTest.Run(() =>
    {
        var engine = new FakeAudioEngine();
        string settings = Path.Combine(Path.GetTempPath(), $"mkb-ui-{Guid.NewGuid():N}.json");
        var vm = new MainViewModel(engine, new SettingsStore(settings));

        var a = new Track("/music/sandstorm.mp3", "Sandstorm", "Darude", "Before the Storm", TimeSpan.FromSeconds(224));
        var b = new Track("/music/kalimba.mp3", "Kalimba", "Mr. Scruff", "Ninja Tuna", TimeSpan.FromSeconds(348));
        engine.A.Duration = a.Duration;
        engine.B.Duration = b.Duration;
        vm.DeckA.LoadAsync(a).Wait();
        vm.DeckB.LoadAsync(b).Wait();
        engine.A.Position = TimeSpan.FromSeconds(96);
        vm.DeckA.Refresh();
        vm.DeckB.Refresh();
        vm.DeckA.Playlist.Add(new Track("/m/2.mp3", "Get Up", "Technotronic", "Pump Up the Jam", TimeSpan.FromSeconds(203)));
        vm.DeckB.Playlist.Add(new Track("/m/4.mp3", "Windowlicker", "Aphex Twin", "Windowlicker", TimeSpan.FromSeconds(366)));
        vm.BrowserTracks.Add(a);
        vm.BrowserTracks.Add(b);
        vm.CueMode = CueMode.Split;
        vm.DeckA.IsCued = true;

        var phone = new PhoneView { DataContext = vm };
        // A common phone size in device-independent pixels.
        var window = new Avalonia.Controls.Window { Content = phone, Width = 393, Height = 852 };
        window.Show();

        var tabs = phone.GetLogicalDescendants().OfType<Avalonia.Controls.TabControl>().Single();
        string dir = Path.GetDirectoryName(Environment.GetEnvironmentVariable("MKB_UI_SNAPSHOT")
                                           ?? Path.Combine(Path.GetTempPath(), "x.png"))!;
        for (int i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            string output = Path.Combine(dir, $"mkb-mixer-phone-{i}.png");
            using Bitmap frame = window.CaptureRenderedFrame()
                                 ?? throw new InvalidOperationException("no frame captured");
#pragma warning disable CS0618
            frame.Save(output);
#pragma warning restore CS0618
            Assert.True(new FileInfo(output).Length > 5_000, $"tab {i} looks empty");
        }
    });
}
