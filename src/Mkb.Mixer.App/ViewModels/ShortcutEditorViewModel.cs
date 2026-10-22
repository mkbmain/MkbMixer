using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mkb.Mixer.App.Shortcuts;

namespace Mkb.Mixer.App.ViewModels;

/// <summary>One action in the editor: its label, its current key and whether it is waiting for a new one.</summary>
public sealed partial class ShortcutRow(ShortcutAction action) : ObservableObject
{
    public string Id { get; } = action.Id;
    public string Label { get; } = action.Label;

    [ObservableProperty] private string _gestureText = "none";
    [ObservableProperty] private bool _isCapturing;
}

/// <summary>The rows of one group, "Deck A", "Deck B" or "Mix".</summary>
public sealed record ShortcutGroup(string Name, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// Drives the shortcuts overlay. The view sends it key presses while a row is waiting
/// for a new key, so what is pressed becomes the binding instead of running a shortcut.
/// </summary>
public sealed partial class ShortcutEditorViewModel : ObservableObject
{
    private const KeyModifiers Meaningful = KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta;

    private readonly ShortcutMap _map;
    private readonly Dictionary<string, ShortcutRow> _rows;
    private readonly Dictionary<string, ShortcutAction> _actions;
    private string? _capturingId;

    public ShortcutEditorViewModel(ShortcutMap map)
    {
        _map = map;
        _actions = map.Actions.ToDictionary(a => a.Id);
        _rows = map.Actions.ToDictionary(a => a.Id, a => new ShortcutRow(a));
        Groups = map.Actions.GroupBy(a => a.Group)
            .Select(g => new ShortcutGroup(g.Key, g.Select(a => _rows[a.Id]).ToList()))
            .ToList();
        _map.Changed += (_, _) => Refresh();
        Refresh();
    }

    public IReadOnlyList<ShortcutGroup> Groups { get; }

    /// <summary>The last thing worth telling the person: what swapped, or why a key is risky.</summary>
    [ObservableProperty] private string? _message;

    public bool IsCapturing => _capturingId != null;

    /// <summary>Waits for the next key press to become this action's key.</summary>
    [RelayCommand]
    public void BeginCapture(string id)
    {
        if (!_rows.ContainsKey(id)) return;
        EndCapture();
        _capturingId = id;
        _rows[id].IsCapturing = true;
        Message = null;
        OnPropertyChanged(nameof(IsCapturing));
    }

    /// <summary>Stops waiting for a key without changing anything.</summary>
    public void CancelCapture() => EndCapture();

    /// <summary>
    /// Offers a key press to the editor. Returns true when it was consumed: always while
    /// a row is waiting, so Tab can't move focus and Escape can't also close the editor.
    /// </summary>
    public bool Capture(Key key, KeyModifiers modifiers)
    {
        if (_capturingId is not { } id) return false;
        if (key == Key.Escape) { EndCapture(); return true; }
        if (ShortcutMap.IsReserved(key)) return true; // Tab or a modifier on its own: keep waiting

        modifiers &= Meaningful;
        var other = _map.Assign(id, new KeyGesture(key, modifiers));
        var text = other != null ? $"Swapped with {other.Label}." : null;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
            text = (text == null ? "" : text + " ") + "A key without Ctrl, Alt or Cmd can fire by accident while you type.";
        Message = text;
        EndCapture();
        return true;
    }

    [RelayCommand]
    public void Unbind(string id)
    {
        if (_capturingId == id) EndCapture();
        _map.Unbind(id);
        Message = null;
    }

    [RelayCommand]
    public void ResetToDefaults()
    {
        EndCapture();
        _map.ResetToDefaults();
        Message = "Every key is back to its default.";
    }

    private void EndCapture()
    {
        if (_capturingId is { } id) _rows[id].IsCapturing = false;
        _capturingId = null;
        OnPropertyChanged(nameof(IsCapturing));
    }

    private void Refresh()
    {
        foreach (var action in _map.Actions)
            _rows[action.Id].GestureText = _map.GestureFor(action.Id) is { } g ? Describe(g) : "none";
    }

    // KeyGesture's own text spells out "OemComma" and "D1"; show what is printed on the key.
    private static string Describe(KeyGesture g)
    {
        var parts = new List<string>();
        if (g.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add(OperatingSystem.IsMacOS() ? "Cmd" : "Win");
        parts.Add(g.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)g.Key - (int)Key.D0).ToString(),
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "+",
            _ => g.Key.ToString(),
        });
        return string.Join('+', parts);
    }
}
