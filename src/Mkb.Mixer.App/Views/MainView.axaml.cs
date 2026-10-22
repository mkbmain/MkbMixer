using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Views;

/// <summary>
/// The whole mixer UI. A desktop <see cref="MainWindow"/> hosts it; on Android it
/// is the activity's content directly, since there are no windows.
/// </summary>
public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        // Styles.axaml keys the tablet layout off this class.
        if (OperatingSystem.IsAndroid())
        {
            Classes.Add("touch");
            Resources["ScrollBarSize"] = 22.0;
            Resources["ScrollBarThickness"] = 22.0;
        }

        Loaded += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.Library.LoadRoots();
            vm.Start();   // the transport clock must be created on the UI thread
            AttachShortcuts(vm);
            Focus();   // rest focus here so keys have a route through this view
        };

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
    }

    private MainViewModel? _shortcutVm;

    private void AttachShortcuts(MainViewModel vm)
    {
        if (_shortcutVm is not null) return;   // Loaded can fire again if the view is re-attached
        _shortcutVm = vm;
        vm.FocusSearchRequested += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        };

        // A key released while another window has focus never reaches us, so end any held nudge.
        if (TopLevel.GetTopLevel(this) is Window window)
            window.Deactivated += (_, _) => vm.ShortcutKeys.ReleaseAll();
    }

    private bool InTextEntry() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // The editor comes first: while a row waits for a key, every key is an answer to it,
        // and with nothing waiting Escape closes the editor.
        if (_shortcutVm is { IsShortcutEditorOpen: true } editing)
        {
            if (editing.ShortcutEditor.Capture(e.Key, e.KeyModifiers)) { e.Handled = true; return; }
            if (e.Key == Key.Escape && !InTextEntry())
            {
                editing.CloseShortcutEditorCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Escape && InTextEntry())
        {
            Focus();
            e.Handled = true;
            return;
        }
        if (_shortcutVm is null) return;
        e.Handled = _shortcutVm.ShortcutKeys.KeyDown(e.Key, e.KeyModifiers, InTextEntry());
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_shortcutVm is null) return;
        e.Handled = _shortcutVm.ShortcutKeys.KeyUp(e.Key);
    }
}
