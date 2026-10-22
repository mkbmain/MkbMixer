using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mkb.Mixer.App.Shortcuts;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.App.Views;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class ShortcutEditorTests
{
    private static ShortcutMap NewMap() => ShortcutMap.Load(ShortcutCatalog.Build(false), null);

    private static ShortcutRow Row(ShortcutEditorViewModel e, string id) =>
        e.Groups.SelectMany(g => g.Rows).Single(r => r.Id == id);

    [Fact]
    public void CaptureAssignsAndShowsNewGesture()
    {
        var map = NewMap();
        var editor = new ShortcutEditorViewModel(map);
        editor.BeginCapture("deckA.playPause");
        Assert.True(Row(editor, "deckA.playPause").IsCapturing);

        Assert.True(editor.Capture(Key.G, KeyModifiers.Control));

        Assert.Equal("Ctrl+G", Row(editor, "deckA.playPause").GestureText);
        Assert.False(Row(editor, "deckA.playPause").IsCapturing);
        Assert.False(editor.IsCapturing);
        Assert.Equal(Key.G, map.GestureFor("deckA.playPause")!.Key);
    }

    [Fact]
    public void CaptureConflictSwapsAndSaysWhich()
    {
        var editor = new ShortcutEditorViewModel(NewMap());
        editor.BeginCapture("deckA.playPause");
        editor.Capture(Key.P, KeyModifiers.Control); // deck B's play / pause

        Assert.Contains("Deck B", editor.Message);
        Assert.Contains("play / pause", editor.Message);
        Assert.Equal("Ctrl+Q", Row(editor, "deckB.playPause").GestureText);
    }

    [Fact]
    public void EscapeCancelsCapture()
    {
        var map = NewMap();
        var editor = new ShortcutEditorViewModel(map);
        editor.BeginCapture("deckA.playPause");

        Assert.True(editor.Capture(Key.Escape, KeyModifiers.None));

        Assert.False(editor.IsCapturing);
        Assert.Equal(Key.Q, map.GestureFor("deckA.playPause")!.Key);
        Assert.Equal("Ctrl+Q", Row(editor, "deckA.playPause").GestureText);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.Tab)]
    public void ReservedKeysKeepWaitingAndAreNeverAssigned(Key key)
    {
        var map = NewMap();
        var editor = new ShortcutEditorViewModel(map);
        editor.BeginCapture("deckA.playPause");

        Assert.True(editor.Capture(key, KeyModifiers.Control));

        Assert.True(editor.IsCapturing);
        Assert.Equal(Key.Q, map.GestureFor("deckA.playPause")!.Key);
    }

    [Fact]
    public void KeysAreNotConsumedWhenNothingIsCapturing()
    {
        var editor = new ShortcutEditorViewModel(NewMap());
        Assert.False(editor.Capture(Key.G, KeyModifiers.Control));
    }

    [Fact]
    public void BareKeyIsAcceptedWithWarning()
    {
        var map = NewMap();
        var editor = new ShortcutEditorViewModel(map);
        editor.BeginCapture("deckA.playPause");
        editor.Capture(Key.G, KeyModifiers.None);

        Assert.Equal(KeyModifiers.None, map.GestureFor("deckA.playPause")!.KeyModifiers);
        Assert.False(string.IsNullOrWhiteSpace(editor.Message));
        Assert.Contains("accident", editor.Message);
    }

    [Fact]
    public void UnbindShowsNone()
    {
        var editor = new ShortcutEditorViewModel(NewMap());
        editor.UnbindCommand.Execute("deckA.playPause");
        Assert.Equal("none", Row(editor, "deckA.playPause").GestureText);
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        var editor = new ShortcutEditorViewModel(NewMap());
        editor.UnbindCommand.Execute("deckA.playPause");
        editor.BeginCapture("deckA.cue");
        editor.Capture(Key.G, KeyModifiers.Control);

        editor.ResetToDefaultsCommand.Execute(null);

        Assert.Equal("Ctrl+Q", Row(editor, "deckA.playPause").GestureText);
        Assert.Equal("Ctrl+W", Row(editor, "deckA.cue").GestureText);
    }

    [Fact]
    public void RowsGroupedDeckADeckBMix()
    {
        var editor = new ShortcutEditorViewModel(NewMap());
        Assert.Equal(["Deck A", "Deck B", "Mix"], editor.Groups.Select(g => g.Name));
        Assert.Equal("Ctrl+,", Row(editor, "mix.editor").GestureText);
        Assert.Equal("Ctrl+1", Row(editor, "deckA.hotCue1").GestureText);
    }

    private static (MainViewModel Vm, string Path) NewVm()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mkb-ui-{Guid.NewGuid():N}.json");
        return (new MainViewModel(new FakeAudioEngine(), new SettingsStore(path)), path);
    }

    [Fact]
    public void CloseSavesOverrides()
    {
        var (vm, path) = NewVm();
        vm.OpenShortcutEditorCommand.Execute(null);
        vm.ShortcutEditor.BeginCapture("deckA.playPause");
        vm.ShortcutEditor.Capture(Key.G, KeyModifiers.Control);

        vm.CloseShortcutEditorCommand.Execute(null);

        Assert.False(vm.IsShortcutEditorOpen);
        var loaded = new SettingsStore(path).Load();
        Assert.Equal("Ctrl+G", loaded.Shortcuts!["deckA.playPause"]);
    }

    [Fact]
    public void CloseWhileCapturingCancelsTheCapture()
    {
        var (vm, _) = NewVm();
        vm.OpenShortcutEditorCommand.Execute(null);
        vm.ShortcutEditor.BeginCapture("deckA.playPause");
        vm.CloseShortcutEditorCommand.Execute(null);
        Assert.False(vm.ShortcutEditor.IsCapturing);
    }

    private static (Window, MainViewModel, string) Build()
    {
        var (vm, path) = NewVm();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, path);
    }

    private static void Tap(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static Button KeysButton(Window w) =>
        w.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Keys");

    [Fact]
    public void KeysButtonOpensOverlayAndCtrlCommaToo() => AvaloniaTest.Run(() =>
    {
        var (window, vm, _) = Build();
        var overlay = window.GetVisualDescendants().OfType<ShortcutEditorView>().Single();
        Assert.False(overlay.IsEffectivelyVisible);

        KeysButton(window).Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsShortcutEditorOpen);
        Assert.True(overlay.IsEffectivelyVisible);

        Tap(window, Key.Escape);
        Assert.False(vm.IsShortcutEditorOpen);
        Assert.False(overlay.IsEffectivelyVisible);

        Tap(window, Key.OemComma, RawInputModifiers.Control);
        Assert.True(vm.IsShortcutEditorOpen);
        Assert.True(overlay.IsEffectivelyVisible);

        Tap(window, Key.OemComma, RawInputModifiers.Control);
        Assert.False(vm.IsShortcutEditorOpen);
    });

    [Fact]
    public void KeysPressedWhileCapturingGoToTheEditor() => AvaloniaTest.Run(() =>
    {
        var (window, vm, path) = Build();
        vm.OpenShortcutEditorCommand.Execute(null);
        vm.ShortcutEditor.BeginCapture("deckA.playPause");

        Tap(window, Key.Escape);   // cancels the capture, editor stays open
        Assert.True(vm.IsShortcutEditorOpen);
        Assert.False(vm.ShortcutEditor.IsCapturing);

        vm.ShortcutEditor.BeginCapture("deckA.playPause");
        Tap(window, Key.G, RawInputModifiers.Control);
        Assert.Equal("Ctrl+G", Row(vm.ShortcutEditor, "deckA.playPause").GestureText);

        Tap(window, Key.Escape);   // nothing waiting: closes and saves
        Assert.False(vm.IsShortcutEditorOpen);
        Assert.Equal("Ctrl+G", new SettingsStore(path).Load().Shortcuts!["deckA.playPause"]);
    });

    [Fact]
    public void RendersEditorOverlayToPng() => AvaloniaTest.Run(() =>
    {
        var (window, vm, _) = Build();
        vm.OpenShortcutEditorCommand.Execute(null);
        vm.ShortcutEditor.BeginCapture("deckA.cue");
        vm.ShortcutEditor.Capture(Key.G, KeyModifiers.None);
        vm.ShortcutEditor.BeginCapture("deckA.sync");
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        string output = Environment.GetEnvironmentVariable("MKB_EDITOR_SNAPSHOT")
                        ?? Path.Combine(Path.GetTempPath(), "mkb-mixer-shortcuts.png");
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame captured");
#pragma warning disable CS0618
        frame.Save(output);
#pragma warning restore CS0618
        Assert.True(new FileInfo(output).Length > 10_000);
    });
}
