using Avalonia.Input;
using Mkb.Mixer.App.Shortcuts;

namespace Mkb.Mixer.Tests;

public class ShortcutMapTests
{
    private static ShortcutMap Map(Dictionary<string, string>? saved = null) =>
        ShortcutMap.Load(ShortcutCatalog.Build(false), saved);

    private static KeyGesture G(string s) => KeyGesture.Parse(s);

    [Fact]
    public void CatalogIdsAreUniqueAndEveryDefaultHasCtrl()
    {
        foreach (var mac in new[] { false, true })
        {
            var catalog = ShortcutCatalog.Build(mac);
            Assert.Equal(35, catalog.Count);
            Assert.Equal(35, catalog.Select(a => a.Id).Distinct().Count());
            foreach (var a in catalog.Where(a => a.Default != null))
            {
                var expected = mac && a.Id == "mix.focusSearch" ? KeyModifiers.Meta : KeyModifiers.Control;
                Assert.True(a.Default!.KeyModifiers.HasFlag(expected), a.Id);
            }
        }
    }

    [Fact]
    public void MacFocusSearchUsesCmd()
    {
        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Meta),
            ShortcutCatalog.Build(true).Single(a => a.Id == "mix.focusSearch").Default);
        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Control),
            ShortcutCatalog.Build(false).Single(a => a.Id == "mix.focusSearch").Default);
    }

    [Fact]
    public void DefaultsResolveWhenNothingSaved()
    {
        var map = Map();
        Assert.Equal("deckA.playPause", map.Find(Key.Q, KeyModifiers.Control)?.Id);
        Assert.Equal("deckB.hotCue4.clear", map.Find(Key.D0, KeyModifiers.Control | KeyModifiers.Shift)?.Id);
        Assert.Null(map.Find(Key.Q, KeyModifiers.None));
    }

    [Fact]
    public void NudgeActionsAreHoldAndOthersAreNot()
    {
        var holds = ShortcutCatalog.Build(false).Where(a => a.IsHold).Select(a => a.Id).Order().ToArray();
        Assert.Equal(new[] { "deckA.nudgeFaster", "deckA.nudgeSlower", "deckB.nudgeFaster", "deckB.nudgeSlower" }, holds);
    }

    [Fact]
    public void SavedOverrideReplacesDefault()
    {
        var map = Map(new() { ["deckA.playPause"] = "Ctrl+Y" });
        Assert.Equal(G("Ctrl+Y"), map.GestureFor("deckA.playPause"));
        Assert.Null(map.Find(Key.Q, KeyModifiers.Control));
    }

    [Fact]
    public void EmptyStringMeansUnbound()
    {
        var map = Map(new() { ["deckA.playPause"] = "" });
        Assert.Null(map.GestureFor("deckA.playPause"));
        Assert.Null(map.Find(Key.Q, KeyModifiers.Control));
    }

    [Fact]
    public void UnknownIdsAndUnparseableGesturesAreDropped()
    {
        var map = Map(new() { ["nope"] = "Ctrl+Y", ["deckA.playPause"] = "Ctrl+Banana" });
        Assert.Equal(35, map.Actions.Count);
        Assert.Equal(G("Ctrl+Q"), map.GestureFor("deckA.playPause"));
        Assert.Null(map.GestureFor("nope"));
    }

    [Fact]
    public void NewDefaultClashingWithSavedKeyStartsUnbound()
    {
        var map = Map(new() { ["deckA.sync"] = "Ctrl+Q" });
        Assert.Equal(G("Ctrl+Q"), map.GestureFor("deckA.sync"));
        Assert.Null(map.GestureFor("deckA.playPause"));
    }

    [Fact]
    public void DuplicateSavedGestureLeavesLaterActionUnbound()
    {
        var map = Map(new() { ["deckA.sync"] = "Ctrl+Y", ["deckB.sync"] = "Ctrl+Y" });
        Assert.Equal(G("Ctrl+Y"), map.GestureFor("deckA.sync"));
        Assert.Null(map.GestureFor("deckB.sync"));
        Assert.Equal("deckA.sync", map.Find(Key.Y, KeyModifiers.Control)?.Id);
    }

    [Fact]
    public void AssignSwapsOnConflict()
    {
        var map = Map();
        var swapped = map.Assign("deckA.playPause", G("Ctrl+P"));
        Assert.Equal("deckB.playPause", swapped?.Id);
        Assert.Equal(G("Ctrl+P"), map.GestureFor("deckA.playPause"));
        Assert.Equal(G("Ctrl+Q"), map.GestureFor("deckB.playPause"));
    }

    [Fact]
    public void AssignToFreeKeyReturnsNull()
    {
        var map = Map();
        Assert.Null(map.Assign("deckA.playPause", G("Ctrl+Y")));
        Assert.Equal(G("Ctrl+Y"), map.GestureFor("deckA.playPause"));
    }

    [Fact]
    public void AssignToUnboundActionLeavesOtherUnbound()
    {
        var map = Map(new() { ["deckA.playPause"] = "" });
        var swapped = map.Assign("deckA.playPause", G("Ctrl+P"));
        Assert.Equal("deckB.playPause", swapped?.Id);
        Assert.Null(map.GestureFor("deckB.playPause"));
    }

    [Fact]
    public void UnbindAndResetToDefaults()
    {
        var map = Map();
        map.Unbind("deckA.playPause");
        map.Assign("deckA.sync", G("Ctrl+Y"));
        Assert.Null(map.GestureFor("deckA.playPause"));
        map.ResetToDefaults();
        Assert.Equal(G("Ctrl+Q"), map.GestureFor("deckA.playPause"));
        Assert.Equal(G("Ctrl+E"), map.GestureFor("deckA.sync"));
        Assert.Empty(map.ToOverrides());
    }

    [Fact]
    public void ToOverridesHoldsOnlyChanges()
    {
        var map = Map();
        Assert.Empty(map.ToOverrides());
        map.Assign("deckA.sync", G("Ctrl+Y"));
        map.Unbind("deckB.sync");
        var o = map.ToOverrides();
        Assert.Equal(2, o.Count);
        Assert.Equal("", o["deckB.sync"]);
        Assert.Equal(G("Ctrl+Y"), G(o["deckA.sync"]));
    }

    [Fact]
    public void GestureStringsRoundTrip()
    {
        var map = Map();
        map.Assign("mix.editor", new KeyGesture(Key.OemComma, KeyModifiers.Control));
        map.Assign("deckA.sync", new KeyGesture(Key.D7, KeyModifiers.Control | KeyModifiers.Shift));
        var reloaded = Map(map.ToOverrides());
        Assert.Equal(new KeyGesture(Key.OemComma, KeyModifiers.Control), reloaded.GestureFor("mix.editor"));
        Assert.Equal(new KeyGesture(Key.D7, KeyModifiers.Control | KeyModifiers.Shift), reloaded.GestureFor("deckA.sync"));
        Assert.Equal("deckA.sync", reloaded.Find(Key.D7, KeyModifiers.Control | KeyModifiers.Shift)?.Id);
    }

    [Fact]
    public void ReservedKeys()
    {
        foreach (var k in new[] { Key.Escape, Key.Tab, Key.LeftCtrl, Key.RightCtrl, Key.LeftShift,
                     Key.RightShift, Key.LeftAlt, Key.RightAlt, Key.LWin, Key.RWin })
            Assert.True(ShortcutMap.IsReserved(k), k.ToString());
        Assert.False(ShortcutMap.IsReserved(Key.Q));
    }

    [Fact]
    public void ChangedRaisedOnAssignUnbindReset()
    {
        var map = Map();
        var n = 0;
        map.Changed += (_, _) => n++;
        map.Assign("deckA.sync", G("Ctrl+Y"));
        Assert.Equal(1, n);
        map.Unbind("deckA.sync");
        Assert.Equal(2, n);
        map.ResetToDefaults();
        Assert.Equal(3, n);
    }
}
