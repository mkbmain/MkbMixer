using System;
using System.Collections.Generic;
using Avalonia.Input;
using Mkb.Mixer.App.ViewModels;

namespace Mkb.Mixer.App.Shortcuts;

/// <summary>
/// Turns key events into the same view-model calls the on-screen controls make.
/// The view feeds it key down/up events; it decides whether a key is a shortcut.
/// </summary>
/// <remarks>
/// It remembers which keys are down so an auto-repeat is not a second press, and
/// so a held nudge ends when its letter is released, even if Ctrl came up first.
/// </remarks>
public sealed class ShortcutRouter(MainViewModel vm)
{
    private const string EditorId = "mix.editor";
    private const string DeckAPrefix = "deckA.";
    private const string DeckBPrefix = "deckB.";

    private readonly HashSet<Key> _down = [];
    private readonly Dictionary<Key, DeckViewModel> _nudging = [];

    /// <returns>True when the key was a shortcut, so the view should mark it handled.</returns>
    public bool KeyDown(Key key, KeyModifiers modifiers, bool inTextEntry)
    {
        if (vm.Shortcuts.Find(key, modifiers) is not { } action) return false;

        // Typing must keep working in a text box; only "focus search" is allowed to
        // take the keyboard back from it.
        if (inTextEntry && action.Id != "mix.focusSearch") return false;

        // While the editor is open, only the key that opened it may do anything.
        if (vm.IsShortcutEditorOpen && action.Id != EditorId) return false;

        // Auto-repeat: swallow it so the key does not leak into the focused control,
        // but do not run the action again.
        if (!_down.Add(key)) return true;

        // An id nothing handles (say, a renamed one) must not swallow its key for nothing.
        return Run(action, key);
    }

    /// <summary>Ends a nudge started by <paramref name="key"/>, whatever modifiers are held now.</summary>
    public bool KeyUp(Key key)
    {
        _down.Remove(key);
        if (!_nudging.Remove(key, out DeckViewModel? deck)) return false;
        deck.EndNudge();
        return true;
    }

    /// <summary>Ends anything held, for when the window loses focus and key-up events never arrive.</summary>
    public void ReleaseAll()
    {
        _down.Clear();
        foreach (DeckViewModel deck in _nudging.Values) deck.EndNudge();
        _nudging.Clear();
    }

    /// <returns>False when no handler knows the action's id.</returns>
    private bool Run(ShortcutAction action, Key key)
    {
        string id = action.Id;
        if (id.StartsWith(DeckAPrefix, StringComparison.Ordinal))
            return RunDeck(vm.DeckA, id[DeckAPrefix.Length..], key);
        if (id.StartsWith(DeckBPrefix, StringComparison.Ordinal))
            return RunDeck(vm.DeckB, id[DeckBPrefix.Length..], key);
        return RunMix(id);
    }

    private bool RunDeck(DeckViewModel deck, string name, Key key)
    {
        switch (name)
        {
            case "playPause": deck.TogglePlayPause(); break;
            case "cue": deck.ToggleCue(); break;
            case "sync": deck.SyncIfPossible(); break;
            case "nudgeSlower": StartNudge(deck, key, -1); break;
            case "nudgeFaster": StartNudge(deck, key, +1); break;
            case "addSelected": (deck == vm.DeckA ? vm.AddToDeckACommand : vm.AddToDeckBCommand).Execute(null); break;
            default:
                if (name.StartsWith("hotCue", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(6, 1), out int n) && n is >= 1 and <= 4)
                {
                    HotCueSlot slot = deck.HotCues[n - 1];
                    if (name.EndsWith(".clear", StringComparison.Ordinal)) deck.ClearHotCueCommand.Execute(slot);
                    else deck.HotCueCommand.Execute(slot);
                    break;
                }
                return false;
        }
        return true;
    }

    private void StartNudge(DeckViewModel deck, Key key, int direction)
    {
        deck.BeginNudge(direction);
        _nudging[key] = deck;
    }

    private bool RunMix(string id)
    {
        switch (id)
        {
            case "mix.crossfaderToA": vm.StepCrossfader(-1); break;
            case "mix.crossfaderToB": vm.StepCrossfader(+1); break;
            case "mix.crossfaderCentre": vm.CentreCrossfader(); break;
            case "mix.autoCue": vm.AutoCueEnabled = !vm.AutoCueEnabled; break;
            case "mix.pauseAll": vm.TogglePauseAll(); break;
            case "mix.focusSearch": vm.RequestFocusSearch(); break;
            case EditorId:
                // Closing goes through its command so it always saves.
                if (vm.IsShortcutEditorOpen) vm.CloseShortcutEditorCommand.Execute(null);
                else vm.OpenShortcutEditorCommand.Execute(null);
                break;
            default: return false;
        }
        return true;
    }
}
