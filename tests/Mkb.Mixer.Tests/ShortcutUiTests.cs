using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.App.Views;
using Mkb.Mixer.Audio;
using Mkb.Mixer.Library;
using Track = Mkb.Mixer.Audio.Track;

namespace Mkb.Mixer.Tests;

/// <summary>Shortcut keys through the real window: where they fire, and where text entry keeps them.</summary>
public class ShortcutUiTests
{
    private static (Window Window, MainViewModel Vm, FakeAudioEngine Engine) Build(bool phone = false)
    {
        var engine = new FakeAudioEngine();
        var vm = new MainViewModel(engine, new SettingsStore(
            Path.Combine(Path.GetTempPath(), $"mkb-ui-{Guid.NewGuid():N}.json")));
        vm.DeckA.LoadAsync(new Track("/m/a.mp3", "A", Duration: TimeSpan.FromMinutes(3))).Wait();
        Window window = phone
            ? new Window { Content = new PhoneView { DataContext = vm }, Width = 400, Height = 800 }
            : new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, engine);
    }

    private static TextBox SearchBox(Window w) =>
        w.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchBox");

    private static void Tap(Window w, Key key, RawInputModifiers mods = RawInputModifiers.Control)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void CtrlQOnWindowPlaysDeckA() => AvaloniaTest.Run(() =>
    {
        var (window, _, engine) = Build();
        Tap(window, Key.Q);
        Assert.Equal(PlaybackState.Playing, engine.A.State);
    });

    [Fact]
    public void CtrlQWhileSearchBoxFocusedDoesNothing() => AvaloniaTest.Run(() =>
    {
        var (window, vm, engine) = Build();
        TextBox box = SearchBox(window);
        box.Text = "abc";
        box.Focus();
        Dispatcher.UIThread.RunJobs();

        Tap(window, Key.Q);

        Assert.Equal(PlaybackState.Stopped, engine.A.State);
        Assert.Equal("abc", box.Text);
    });

    [Fact]
    public void CtrlQWhileBpmBoxFocusedDoesNothing() => AvaloniaTest.Run(() =>
    {
        // The deck has no BPM text box yet, so stand one in: any TextBox counts as text entry.
        var (window, _, engine) = Build();
        var bpm = new TextBox { Text = "120" };
        ((Panel)window.GetVisualDescendants().OfType<Grid>().First()).Children.Add(bpm);
        Dispatcher.UIThread.RunJobs();
        bpm.Focus();

        Tap(window, Key.Q);

        Assert.Equal(PlaybackState.Stopped, engine.A.State);
        Assert.Equal("120", bpm.Text);
    });

    [Fact]
    public void CtrlFFocusesSearchBox() => AvaloniaTest.Run(() =>
    {
        var (window, _, _) = Build();
        Tap(window, Key.F);
        Assert.True(SearchBox(window).IsFocused);

        var other = new TextBox();
        ((Panel)window.GetVisualDescendants().OfType<Grid>().First()).Children.Add(other);
        Dispatcher.UIThread.RunJobs();
        other.Focus();
        Tap(window, Key.F);
        Assert.True(SearchBox(window).IsFocused);
    });

    [Fact]
    public void EscapeInSearchBoxLeavesItAndReenablesShortcuts() => AvaloniaTest.Run(() =>
    {
        var (window, _, engine) = Build();
        SearchBox(window).Focus();
        Dispatcher.UIThread.RunJobs();

        Tap(window, Key.Escape, RawInputModifiers.None);
        Assert.False(SearchBox(window).IsFocused);

        Tap(window, Key.Q);
        Assert.Equal(PlaybackState.Playing, engine.A.State);
    });

    [Fact]
    public void PhoneViewHasNoShortcutHandling() => AvaloniaTest.Run(() =>
    {
        var (window, _, engine) = Build(phone: true);
        Tap(window, Key.Q);
        Assert.Equal(PlaybackState.Stopped, engine.A.State);
    });
}
