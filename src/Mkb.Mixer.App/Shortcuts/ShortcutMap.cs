using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Mkb.Mixer.App.Shortcuts;

/// <summary>
/// Which key does which action. Built from the catalogue plus whatever the user
/// saved, and it never lets two actions share a key.
/// </summary>
/// <remarks>
/// Gestures are saved as text in <c>AppSettings.Shortcuts</c>, where a missing id
/// means "the default" and an empty string means "unbound".
/// </remarks>
public sealed class ShortcutMap
{
    private readonly IReadOnlyList<ShortcutAction> _catalog;
    private readonly Dictionary<string, KeyGesture?> _keys = new();

    /// <summary>Raised after any change to the bindings, so the editor and settings can follow.</summary>
    public event EventHandler? Changed;

    private ShortcutMap(IReadOnlyList<ShortcutAction> catalog) => _catalog = catalog;

    public IReadOnlyList<ShortcutAction> Actions => _catalog;

    /// <summary>
    /// Applies saved entries first, in catalogue order, then defaults for the actions
    /// that were not saved, skipping a default whose key is already taken. So a hand-edited
    /// file with a repeated key, or a new default that lands on a key the user already
    /// chose, leaves the later action unbound instead of throwing or stealing the key.
    /// </summary>
    public static ShortcutMap Load(IReadOnlyList<ShortcutAction> catalog, IReadOnlyDictionary<string, string>? saved)
    {
        var map = new ShortcutMap(catalog);
        var handled = new HashSet<string>();
        if (saved != null)
        {
            foreach (var action in catalog)
            {
                if (!saved.TryGetValue(action.Id, out var text)) continue;
                if (text.Length == 0) { map._keys[action.Id] = null; handled.Add(action.Id); continue; }
                var gesture = TryParse(text);
                if (gesture == null) continue; // unreadable: fall back to the default
                handled.Add(action.Id);
                map._keys[action.Id] = map.IsTaken(gesture) ? null : gesture;
            }
        }
        foreach (var action in catalog.Where(a => !handled.Contains(a.Id)))
            map._keys[action.Id] = action.Default != null && !map.IsTaken(action.Default) ? action.Default : null;
        return map;
    }

    public KeyGesture? GestureFor(string id) => _keys.GetValueOrDefault(id);

    /// <summary>The action bound to exactly this key and modifier combination, if any.</summary>
    public ShortcutAction? Find(Key key, KeyModifiers modifiers) =>
        _catalog.FirstOrDefault(a => _keys[a.Id] is { } g && g.Key == key && g.KeyModifiers == modifiers);

    /// <summary>
    /// Binds the gesture to the action. If another action had it, the two swap, so
    /// nothing is silently lost; the other action is returned (it ends up unbound when
    /// the target had no key).
    /// </summary>
    public ShortcutAction? Assign(string id, KeyGesture gesture)
    {
        if (!_keys.TryGetValue(id, out var previous)) return null;
        var other = _catalog.FirstOrDefault(a => a.Id != id && Same(_keys[a.Id], gesture));
        _keys[id] = gesture;
        if (other != null) _keys[other.Id] = previous;
        Changed?.Invoke(this, EventArgs.Empty);
        return other;
    }

    public void Unbind(string id)
    {
        if (!_keys.ContainsKey(id)) return;
        _keys[id] = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetToDefaults()
    {
        foreach (var action in _catalog) _keys[action.Id] = action.Default;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Only what differs from the defaults, so a later change to a default still reaches people who never touched it.</summary>
    public Dictionary<string, string> ToOverrides()
    {
        var result = new Dictionary<string, string>();
        foreach (var action in _catalog)
        {
            var current = _keys[action.Id];
            if (!Same(current, action.Default)) result[action.Id] = current == null ? "" : Format(current);
        }
        return result;
    }

    /// <summary>Escape and Tab get you around the window, and a modifier alone is never a shortcut.</summary>
    public static bool IsReserved(Key key) => key is Key.Escape or Key.Tab
        or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private bool IsTaken(KeyGesture gesture) => _keys.Values.Any(g => Same(g, gesture));

    private static bool Same(KeyGesture? a, KeyGesture? b) =>
        a == null || b == null ? a == b : a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;

    // Written with enum names rather than KeyGesture.ToString(), whose text for
    // keys such as OemComma is display wording that Parse need not accept back.
    private static string Format(KeyGesture g)
    {
        var parts = new List<string>();
        if (g.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(g.Key.ToString());
        return string.Join('+', parts);
    }

    private static KeyGesture? TryParse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= KeyModifiers.Control; break;
                case "shift": modifiers |= KeyModifiers.Shift; break;
                case "alt": modifiers |= KeyModifiers.Alt; break;
                case "meta" or "cmd" or "win": modifiers |= KeyModifiers.Meta; break;
                default: return null;
            }
        }
        // IsDefined first: Enum.TryParse would accept a bare number such as "999".
        if (Enum.TryParse<Key>(parts[^1], true, out var key) && Enum.IsDefined(key) && key != Key.None && !IsReserved(key))
            return new KeyGesture(key, modifiers);
        return null;
    }
}
