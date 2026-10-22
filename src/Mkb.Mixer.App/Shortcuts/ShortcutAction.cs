using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace Mkb.Mixer.App.Shortcuts;

/// <summary>
/// One thing a key can do. The <see cref="Id"/> is what gets saved, so it is never
/// renamed once shipped; <see cref="Label"/> is what the editor shows.
/// </summary>
/// <param name="Group">"Deck A", "Deck B" or "Mix", for grouping in the editor.</param>
/// <param name="Default">The key out of the box, or null for an action that starts unbound.</param>
/// <param name="IsHold">True when the action lasts while the key is down (nudge) rather than firing once.</param>
public sealed record ShortcutAction(string Id, string Group, string Label, KeyGesture? Default, bool IsHold = false);

/// <summary>
/// Every shortcut action and its default key. Every default carries Ctrl, so a stray
/// key press never changes what is playing; the one exception is Focus search, which
/// follows the platform's Cmd+F on macOS.
/// </summary>
public static class ShortcutCatalog
{
    /// <summary>The catalogue for the platform the app is running on.</summary>
    public static IReadOnlyList<ShortcutAction> ForThisPlatform { get; } = Build(OperatingSystem.IsMacOS());

    /// <summary>Builds the catalogue. <paramref name="macOS"/> only changes Focus search to Cmd+F.</summary>
    public static IReadOnlyList<ShortcutAction> Build(bool macOS)
    {
        var list = new List<ShortcutAction>();
        AddDeck(list, "deckA", "Deck A", "Deck A", Key.Q, Key.W, Key.E,
            [Key.D1, Key.D2, Key.D3, Key.D4], Key.A, Key.S, Key.Z);
        AddDeck(list, "deckB", "Deck B", "Deck B", Key.P, Key.O, Key.I,
            [Key.D7, Key.D8, Key.D9, Key.D0], Key.K, Key.L, Key.M);

        const string mix = "Mix";
        list.Add(new("mix.crossfaderToA", mix, "Crossfader toward A", Ctrl(Key.D5)));
        list.Add(new("mix.crossfaderToB", mix, "Crossfader toward B", Ctrl(Key.D6)));
        list.Add(new("mix.crossfaderCentre", mix, "Crossfader to centre", Ctrl(Key.T)));
        list.Add(new("mix.autoCue", mix, "Auto-cue on/off", Ctrl(Key.U)));
        list.Add(new("mix.pauseAll", mix, "Pause everything / resume", Ctrl(Key.B)));
        list.Add(new("mix.focusSearch", mix, "Focus library search",
            new KeyGesture(Key.F, macOS ? KeyModifiers.Meta : KeyModifiers.Control)));
        list.Add(new("mix.editor", mix, "Open shortcuts editor", Ctrl(Key.OemComma)));
        return list;
    }

    private static KeyGesture Ctrl(Key key) => new(key, KeyModifiers.Control);

    private static void AddDeck(List<ShortcutAction> list, string id, string group, string name,
        Key play, Key cue, Key sync, Key[] hotCues, Key slower, Key faster, Key add)
    {
        list.Add(new($"{id}.playPause", group, $"{name} play / pause", Ctrl(play)));
        list.Add(new($"{id}.cue", group, $"{name} headphone cue", Ctrl(cue)));
        list.Add(new($"{id}.sync", group, $"{name} sync", Ctrl(sync)));
        for (var i = 0; i < 4; i++)
            list.Add(new($"{id}.hotCue{i + 1}", group, $"{name} hot cue {i + 1}", Ctrl(hotCues[i])));
        for (var i = 0; i < 4; i++)
            list.Add(new($"{id}.hotCue{i + 1}.clear", group, $"{name} clear hot cue {i + 1}",
                new KeyGesture(hotCues[i], KeyModifiers.Control | KeyModifiers.Shift)));
        list.Add(new($"{id}.nudgeSlower", group, $"{name} nudge slower", Ctrl(slower), IsHold: true));
        list.Add(new($"{id}.nudgeFaster", group, $"{name} nudge faster", Ctrl(faster), IsHold: true));
        list.Add(new($"{id}.addSelected", group, $"{name} add selected track", Ctrl(add)));
    }
}
