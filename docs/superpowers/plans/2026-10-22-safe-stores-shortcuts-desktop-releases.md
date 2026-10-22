# Safe stores, keyboard shortcuts, desktop releases — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Never overwrite an unreadable `tracks.json`/`settings.json`, add configurable keyboard shortcuts to the desktop/tablet layout, and publish self-contained desktop builds alongside the APK on `v*` tags.

**Architecture:** The store fix lives in the Library project (`TrackStore`, `SettingsStore`, plus a small helper that moves bad files aside). Shortcuts are layered:
- a pure model (`ShortcutCatalog` and `ShortcutMap`, in the App project)
- a `ShortcutRouter` that turns key presses into existing view-model calls
- `MainView` key handling, and an overlay editor

Desktop releases are a new CI matrix job and a separate `release` job.

**Tech Stack:** .NET 10, Avalonia 12.1.4, CommunityToolkit.Mvvm, xunit 2.9.3 with Avalonia.Headless, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-22-safe-stores-shortcuts-desktop-releases-design.md`. Read it first; this plan does not repeat its behaviour tables.

## Global Constraints

- Every default shortcut needs `Ctrl`. No default is a bare key.
- macOS defaults use the Control key, not Cmd. The one exception is *Focus search*, which is `Cmd+F` on macOS.
- Escape and Tab can't be bound, and neither can a modifier key on its own.
- Shortcuts are off while a `TextBox` has focus, which includes the inside of a `NumericUpDown`. The exception is *Focus search*.
- Shortcuts and the editor exist in `MainView` only. `PhoneView` gets neither.
- Each shortcut runs the same code path as its on-screen control.
- The Library project must not reference Avalonia. Shortcuts are stored there as `Dictionary<string,string>`.
- Action IDs are stable strings and are never renamed once shipped.
- A store file that can't be read is renamed to `<name>.corrupt-<yyyyMMdd-HHmmss>` and never deleted. If the rename fails, that store doesn't write for the rest of the session.
- Desktop archives are named `mkb-mixer-<version>-<rid>`. They're self-contained, not single-file, and not trimmed.
- Match the surrounding code style: XML doc comments that explain *why*, file-scoped namespaces, `[RelayCommand]`/`[ObservableProperty]`.
- CI builds with `TreatWarningsAsErrors`, so leave no new warnings.

## Review Focus

These are the inputs most likely to cause trouble that no single requirement covers. Each one has a test in the task named after it.

1. **Ctrl released before the letter while nudging.** The nudge must still end. Owned by Task 4 (`NudgeEndsWhenModifierReleasedFirst`).
2. **The window loses focus mid-nudge**, for example by alt-tabbing away. The tempo must return to where it was. Owned by Task 4 (`ReleaseAllEndsHeldNudge`) and Task 5, which wires it up.
3. **A hand-edited `settings.json` gives two actions the same key.** The first in catalogue order keeps it, the other becomes unbound, and nothing throws. Owned by Task 3 (`DuplicateSavedGestureLeavesLaterActionUnbound`).
4. **Two corrupt loads in the same second.** The second must not overwrite the first `.corrupt-` copy. Owned by Task 1 (`SecondCorruptCopyInSameSecondGetsDistinctName`).
5. **A shortcut whose action isn't possible right now**, such as a hot cue with no track, sync unavailable, or cue mode off. It must do nothing and not throw. Owned by Task 4 (`UnavailableActionsAreNoOps`).

---

### Task 1: Safe stores

**Files:**
- Create: `src/Mkb.Mixer.Library/CorruptFiles.cs`
- Modify: `src/Mkb.Mixer.Library/TrackStore.cs` (`Load`, `Flush`, the class remarks)
- Modify: `src/Mkb.Mixer.Library/Settings.cs` (`SettingsStore.Load`, `Save`)
- Modify: `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (end of the main constructor)
- Test: `tests/Mkb.Mixer.Tests/StoreRecoveryTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record LoadProblem(string FileName, string? MovedTo)`. `MovedTo` is null when the rename failed and writes are off.
  - `internal static class CorruptFiles { public static string? SetAside(string path, DateTime now); }` returns the new path, or null if it couldn't rename.
  - `TrackStore.LoadProblem { get; }` and `SettingsStore.LoadProblem { get; private set; }` (set by `Load()`).
  - `internal static string? MainViewModel.DescribeLoadProblems(LoadProblem? tracks, LoadProblem? settings)`.
- Tests reach `internal` members through `InternalsVisibleTo`. Check that the Library and App projects already grant it to `Mkb.Mixer.Tests`, and add it if not.

- [ ] **Step 1: Write the failing tests** in `StoreRecoveryTests` (temp directory per test class, as `TrackStoreTests` does):
  - `MalformedTrackFileIsMovedAsideAndStoreStartsEmpty`: write `{not json` to `tracks.json` and construct a `TrackStore`. Assert:
    - `LoadProblem.MovedTo` matches `tracks.json.corrupt-\d{8}-\d{6}`
    - the moved file's content is `{not json`
    - `tracks.json` no longer exists
    - `RecentlyPlayed(10)` is empty
  - `EmptyTrackFileIsMovedAside`: a zero-byte file gives the same outcome.
  - `ValidTrackFileLoadsWithoutProblem`: round trip one hot cue. `LoadProblem` is null and the directory has no `*.corrupt-*` file.
  - `MissingTrackFileIsNotAProblem`: `LoadProblem` is null.
  - `SaveAfterRecoveryWritesFreshFileAndKeepsCorruptCopy`: after recovery, `Update` and then `Flush()`. Assert that `tracks.json` parses and the corrupt copy's bytes are unchanged.
  - `SecondCorruptCopyInSameSecondGetsDistinctName`: call `CorruptFiles.SetAside` twice with the same `now` on two bad files written to the same path. Assert both paths exist and differ.
  - `RenameFailureDisablesWrites`: make the directory read-only (`File.SetUnixFileMode`). Then check whether a probe file can still be created there. If it can (root, or Windows), return early, because this check can't run on this machine. Otherwise assert:
    - `LoadProblem.MovedTo` is null
    - `Flush()` after `Update` leaves the original bytes untouched
  - The same malformed, empty, valid and missing cases for `SettingsStore.Load()`. Also test that `Save` after a failed rename leaves the file untouched.
  - `DescribeLoadProblemsCombinesBoth`: with both problems set, the text mentions both file names. With neither set, the result is null.

- [ ] **Step 2: Run them and confirm they fail.**
  Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~StoreRecoveryTests"`
  Expected: compile errors (`LoadProblem` is not defined).

- [ ] **Step 3: Implement.**
  - `CorruptFiles.SetAside` renames to `<path>.corrupt-<now:yyyyMMdd-HHmmss>`. If that name is taken, it appends `-2`, `-3`, and so on. It catches `IOException` and `UnauthorizedAccessException` and returns null.
  - In both stores, any read or parse failure (`JsonException`, `IOException`, `UnauthorizedAccessException`) calls `SetAside(path, DateTime.Now)` and records a `LoadProblem`. When `MovedTo` is null, a `_writesDisabled` flag makes `Flush`/`Save` return without writing.
  - `TrackStore.Load` becomes an instance method, or returns the problem as well, so it can record it. Choose either.
  - At the very end of the `MainViewModel` constructor: if `DescribeLoadProblems(tracks.LoadProblem, settingsStore.LoadProblem)` returns text, set `StatusMessage` to it, so it shows over the audio and cue messages.
  - Wording follows the spec's examples, using the file name only (`Path.GetFileName`).
  - Update the `TrackStore` remarks, which currently say a corrupt file "starts the store empty".

- [ ] **Step 4: Run the full suite.**
  Run: `dotnet test tests/Mkb.Mixer.Tests --filter "Category!=Audio"`
  Expected: all pass, including the 299 existing tests.

- [ ] **Step 5: Commit.** Message: `Keep an unreadable tracks or settings file instead of overwriting it`.

---

### Task 2: Desktop builds in CI and on releases

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `README.md` (add a *Downloads* subsection under *Running*)

**Interfaces:** none. This task is independent of the code tasks.

- [ ] **Step 1: Add a `desktop` job** (`needs: test`) with this matrix:
  - `{rid: linux-x64, os: ubuntu-latest, archive: tar.gz}`
  - `{rid: win-x64, os: windows-latest, archive: zip}`
  - `{rid: osx-arm64, os: macos-latest, archive: tar.gz}`
  - `{rid: osx-x64, os: macos-latest, archive: tar.gz, smoke: false}`

  All steps use `shell: bash`, which works on the Windows runner too.
  - **Version:** on a `v*` ref, `VERSION=${GITHUB_REF_NAME#v}`; otherwise `VERSION=dev`.
  - **Publish:** `dotnet publish src/Mkb.Mixer.App.Desktop -c Release -r $RID --self-contained -p:Version=$VERSION` (plus `-p:Version` only when tagged, if `dev` isn't a valid version), into `out/mkb-mixer-$VERSION-$RID/`.
  - **Smoke test** (skipped when `smoke: false`): run `./Mkb.Mixer.App.Desktop --audio-info > audio-info.txt` inside the publish folder, then `cat` the file and `grep -q "miniaudio compiled-in backends"`. On Windows, bash runs the `.exe` synchronously and the redirect captures the output of the `WinExe` program. A non-zero exit or a missing line fails the job.
  - **Archive:** `tar -czf` for tar.gz, and `7z a -tzip` on Windows. The archive holds the folder, not loose files.
  - **Upload:** `actions/upload-artifact@v7` with `name: mkb-mixer-${{ matrix.rid }}` and `if-no-files-found: error`.

- [ ] **Step 2: Move the release into its own job.**
  - Delete the *Attach to GitHub release* step from `apk`, and drop that job's `contents: write`.
  - Add a `release` job: `needs: [apk, desktop]`, `if: startsWith(github.ref, 'refs/tags/v')`, `permissions: contents: write`. It runs `actions/download-artifact@v7` with `merge-multiple: true` into `dist/`, then `gh release create "$GITHUB_REF_NAME" dist/* --generate-notes --verify-tag`. It needs a checkout for `--verify-tag`.

- [ ] **Step 3: Check the workflow syntax.**
  Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml'))"`. If `actionlint` is available, run that instead.
  Expected: no errors. A real run happens on the PR. Note in the PR that the matrix has to go green there.

- [ ] **Step 4: Locally publish linux-x64 and smoke-test it** with the same commands as CI.
  Expected: the folder contains the miniaudio native library, and `--audio-info` prints the backends line.

- [ ] **Step 5: Add the README *Downloads* note.** Cover which archive to pick, `./Mkb.Mixer.App.Desktop` after extracting on Linux and macOS, and the unsigned-macOS steps (right-click → *Open*, or `xattr -dr com.apple.quarantine <folder>`). Also update the paragraph that currently says a tag attaches only the APK.

- [ ] **Step 6: Commit.** Message: `Publish self-contained desktop builds and attach them to releases`.

---

### Task 3: Shortcut model

**Files:**
- Create: `src/Mkb.Mixer.App/Shortcuts/ShortcutAction.cs` (the record and `ShortcutCatalog`)
- Create: `src/Mkb.Mixer.App/Shortcuts/ShortcutMap.cs`
- Modify: `src/Mkb.Mixer.Library/Settings.cs` (`AppSettings.Shortcuts`)
- Test: `tests/Mkb.Mixer.Tests/ShortcutMapTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ShortcutAction(string Id, string Group, string Label, KeyGesture? Default, bool IsHold = false)`. `Group` is `"Deck A"`, `"Deck B"` or `"Mix"`.
  - `public static class ShortcutCatalog { public static IReadOnlyList<ShortcutAction> Build(bool macOS); public static IReadOnlyList<ShortcutAction> ForThisPlatform { get; } }`
  - **Action IDs, exactly:**
    - per deck `d` in `deckA` and `deckB`: `{d}.playPause`, `{d}.cue`, `{d}.sync`, `{d}.hotCue1`…`{d}.hotCue4`, `{d}.hotCue1.clear`…`{d}.hotCue4.clear`, `{d}.nudgeSlower`, `{d}.nudgeFaster`, `{d}.addSelected`
    - mix: `mix.crossfaderToA`, `mix.crossfaderToB`, `mix.crossfaderCentre`, `mix.autoCue`, `mix.pauseAll`, `mix.focusSearch`, `mix.editor`
  - Defaults are exactly the spec's tables. The two nudge actions per deck have `IsHold = true`.
  - `public sealed class ShortcutMap`:
    - `static ShortcutMap Load(IReadOnlyList<ShortcutAction> catalog, IReadOnlyDictionary<string,string>? saved)`
    - `IReadOnlyList<ShortcutAction> Actions`
    - `KeyGesture? GestureFor(string id)`
    - `ShortcutAction? Find(Key key, KeyModifiers modifiers)`
    - `ShortcutAction? Assign(string id, KeyGesture gesture)`: returns the action it swapped with, or null
    - `void Unbind(string id)`
    - `void ResetToDefaults()`
    - `Dictionary<string,string> ToOverrides()`
    - `static bool IsReserved(Key key)`
    - `event EventHandler? Changed`
  - `AppSettings.Shortcuts`: `public Dictionary<string, string>? Shortcuts { get; set; }`.

- [ ] **Step 1: Write the failing tests:**
  - `CatalogIdsAreUniqueAndEveryDefaultHasCtrl`: 35 actions (14 per deck plus 7 mix), unique IDs. Every non-null `Default` includes `KeyModifiers.Control`, except `mix.focusSearch` on macOS, which is `Meta`.
  - `MacFocusSearchUsesCmd`: `Build(true)` gives `Meta+F`. `Build(false)` gives `Ctrl+F`.
  - `DefaultsResolveWhenNothingSaved`: `Find(Key.Q, Control)` gives `deckA.playPause`, and `Find(Key.D0, Control|Shift)` gives `deckB.hotCue4.clear`.
  - `SavedOverrideReplacesDefault`, and `EmptyStringMeansUnbound`.
  - `UnknownIdsAndUnparseableGesturesAreDropped`.
  - `NewDefaultClashingWithSavedKeyStartsUnbound`: save `deckA.sync = "Ctrl+Q"`. Then `deckA.sync` is `Ctrl+Q` and `deckA.playPause` is unbound.
  - `DuplicateSavedGestureLeavesLaterActionUnbound` (Review Focus 3).
  - `AssignSwapsOnConflict`: assigning `Ctrl+P` to `deckA.playPause` returns `deckB.playPause`, and B now has `Ctrl+Q`.
  - `AssignToUnboundActionLeavesOtherUnbound`: when the target had no key, the action that lost its key ends up unbound.
  - `UnbindAndResetToDefaults`.
  - `ToOverridesHoldsOnlyChanges`, and `GestureStringsRoundTrip`, including `Ctrl+OemComma` and `Ctrl+Shift+D7`.
  - `ReservedKeys`: Escape, Tab and every modifier key are reserved. `Key.Q` is not.
  - `ChangedRaisedOnAssignUnbindReset`.

- [ ] **Step 2: Run them and confirm they fail.**
  Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~ShortcutMapTests"`
  Expected: compile errors.

- [ ] **Step 3: Implement.**
  - Store gestures as `KeyGesture.ToString()` and read them back with `KeyGesture.Parse`. If `ToString()` doesn't round-trip a key such as `OemComma`, write a small formatter of your own instead. The round-trip test decides.
  - `Find` compares `Key` and `KeyModifiers` exactly.
  - The load order is: saved entries in catalogue order, then defaults for any action not saved, skipping a default whose key is already taken.

- [ ] **Step 4: Run the tests and the full suite.** Expected: all pass.

- [ ] **Step 5: Commit.** Message: `Add the shortcut catalogue and key map`.

---

### Task 4: Shortcut router

**Files:**
- Create: `src/Mkb.Mixer.App/Shortcuts/ShortcutRouter.cs`
- Modify: `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs`
- Modify: `src/Mkb.Mixer.App/ViewModels/DeckViewModel.cs`
- Test: `tests/Mkb.Mixer.Tests/ShortcutRouterTests.cs`

**Interfaces:**
- Consumes: `ShortcutMap` and `ShortcutCatalog` from Task 3, and `AppSettings.Shortcuts`.
- Produces:
  - **MainViewModel:**
    - `ShortcutMap Shortcuts { get; }`, loaded from `_settings.Shortcuts` with `ShortcutCatalog.ForThisPlatform`. `SaveState` writes `Shortcuts.ToOverrides()` back.
    - `ShortcutRouter ShortcutKeys { get; }`
    - `void TogglePauseAll()`: `if (!PauseAll()) ResumePaused();`
    - `void StepCrossfader(int direction)`: ±0.1, clamped to 0..1
    - `void CentreCrossfader()`: sets `Crossfader.Centre`
    - `event EventHandler? FocusSearchRequested`
    - `[ObservableProperty] bool _isShortcutEditorOpen`
  - **DeckViewModel:**
    - `void TogglePlayPause()`: pause if `State == Playing`, otherwise `PlayCommand`
    - `void ToggleCue()`: only when `IsCueAvailable`
    - `void SyncIfPossible()`: `SyncCommand.CanExecute` gates it
  - **ShortcutRouter:**
    - `public sealed class ShortcutRouter(MainViewModel vm)`
    - `bool KeyDown(Key key, KeyModifiers modifiers, bool inTextEntry)`: returns true when handled
    - `bool KeyUp(Key key)`
    - `void ReleaseAll()`

- [ ] **Step 1: Write the failing tests.** Use a `MainViewModel` on `FakeAudioEngine` with both decks loaded, the way `PauseAllTests` does.
  - `PlayPauseTogglesTheRightDeck`: `KeyDown(Q, Control)` plays A, and pressing it again pauses A. `KeyDown(P, Control)` plays B.
  - `HotCueSetsThenJumps`, and `ShiftHotCueClears`.
  - `CrossfaderStepsClampAndCentre`: from 0.05, `Ctrl+5` gives 0. Ten presses of `Ctrl+6` give 1. `Ctrl+T` gives 0.5.
  - `AutoCueToggles`. `PauseAllTogglesWholeMix`.
  - `AddSelectedAddsLibrarySelectionToDeck`: set `Library.SelectedBrowserRow`, press `Ctrl+M`, and B's playlist gains the track.
  - `FocusSearchRaisesEvent`, including when `inTextEntry` is true.
  - `NothingElseFiresInTextEntry`: `KeyDown(Q, Control, inTextEntry: true)` returns false and A stays stopped.
  - `NothingFiresWhileEditorOpen`, except that `mix.editor` closes the editor again.
  - `EditorShortcutOpensEditor`.
  - `NudgeHoldsUntilKeyUpAndIgnoresRepeats`: down, down again (an auto-repeat), up. Tempo is ×0.96 while held, then back to 1.0.
  - `NudgeEndsWhenModifierReleasedFirst` (Review Focus 1): `KeyDown(A, Control)`, `KeyUp(LeftCtrl)`, `KeyUp(A)`. Tempo is restored.
  - `ReleaseAllEndsHeldNudge` (Review Focus 2).
  - `UnavailableActionsAreNoOps` (Review Focus 5): with an empty deck, a hot cue, sync and cue (with cue mode Off) do nothing and don't throw.
  - `UnboundKeyIsNotHandled`: returns false.

- [ ] **Step 2: Run them and confirm they fail.**
  Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~ShortcutRouterTests"`
  Expected: compile errors.

- [ ] **Step 3: Implement.** The router keeps a `HashSet<Key>` of keys currently down. A `KeyDown` for a key already in the set is an auto-repeat: hold actions ignore it, and other actions ignore it too, so a held `Ctrl+Q` doesn't flicker. `KeyUp(key)` ends a nudge that was started by that key, whatever the modifiers are at that moment. Hot cue *N* uses `HotCueCommand`/`ClearHotCueCommand` with `deck.HotCues[N-1]`. Nudge uses `BeginNudge(-1 or +1)` and `EndNudge()`.

- [ ] **Step 4: Run the tests and the full suite.** Expected: all pass.

- [ ] **Step 5: Commit.** Message: `Route keyboard shortcuts to the decks and mixer`.

---

### Task 5: Keys in MainView

**Files:**
- Modify: `src/Mkb.Mixer.App/Views/MainView.axaml` (name the search box `SearchBox`, and add `Focusable="True"` on the root so focus can rest there)
- Modify: `src/Mkb.Mixer.App/Views/MainView.axaml.cs`
- Test: `tests/Mkb.Mixer.Tests/ShortcutUiTests.cs`

**Interfaces:**
- Consumes: `MainViewModel.ShortcutKeys`, `FocusSearchRequested` and `IsShortcutEditorOpen` from Task 4.

- [ ] **Step 1: Write the failing headless tests.** Build the window the way `UiSmokeTests.Build()` does, and send keys with Avalonia.Headless's `KeyPress`/`KeyRelease` extensions on the window.
  - `CtrlQOnWindowPlaysDeckA`
  - `CtrlQWhileSearchBoxFocusedDoesNothing`, and its text is unchanged
  - `CtrlQWhileBpmBoxFocusedDoesNothing`
  - `CtrlFFocusesSearchBox`, including from inside a BPM box
  - `EscapeInSearchBoxLeavesItAndReenablesShortcuts`
  - `PhoneViewHasNoShortcutHandling`: build a `PhoneView` in a window and press `Ctrl+Q`. Deck A stays stopped.

- [ ] **Step 2: Run them and confirm they fail.**
  Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~ShortcutUiTests"`

- [ ] **Step 3: Implement in `MainView.axaml.cs`.**
  - `AddHandler(KeyDownEvent, …, RoutingStrategies.Tunnel)` and the same for `KeyUpEvent`.
  - `inTextEntry` is `TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox`.
  - Escape while in a `TextBox` focuses `this` and marks the event handled.
  - Set `e.Handled` from the router's return value.
  - Subscribe to `FocusSearchRequested`, which focuses and selects all in `SearchBox`.
  - If the top level is a `Window`, call `ShortcutKeys.ReleaseAll()` on `Deactivated`.
  - Subscribe once `DataContext` is a `MainViewModel`, in the existing `Loaded` handler.

- [ ] **Step 4: Run the tests and the full suite.** Expected: all pass, and the existing `UiSmokeTests` still pass.

- [ ] **Step 5: Commit.** Message: `Handle shortcut keys in the desktop and tablet layout`.

---

### Task 6: Shortcuts editor

**Files:**
- Create: `src/Mkb.Mixer.App/ViewModels/ShortcutEditorViewModel.cs`
- Create: `src/Mkb.Mixer.App/Views/ShortcutEditorView.axaml` and `.axaml.cs`
- Modify: `src/Mkb.Mixer.App/Views/MainView.axaml` (a **Keys** button after *Load playlists*, and the overlay spanning all rows, shown when `IsShortcutEditorOpen`)
- Modify: `src/Mkb.Mixer.App/Views/MainView.axaml.cs` (while capturing, send keys to the editor instead of the router)
- Modify: `src/Mkb.Mixer.App/ViewModels/MainViewModel.cs` (`ShortcutEditor` property, `OpenShortcutEditorCommand`, `CloseShortcutEditorCommand`)
- Modify: `README.md` (a *Keyboard shortcuts* section with the default table, and how to change them)
- Test: `tests/Mkb.Mixer.Tests/ShortcutEditorTests.cs`

**Interfaces:**
- Consumes: `ShortcutMap` (Task 3), and `IsShortcutEditorOpen` and the router (Task 4).
- Produces:
  - `ShortcutEditorViewModel(ShortcutMap map)`
  - `IReadOnlyList<ShortcutGroupRow> Groups`, where each row exposes `Id`, `Label`, `GestureText` (`"none"` when unbound) and `IsCapturing`
  - `string? Message`
  - `bool IsCapturing`
  - `void BeginCapture(string id)`
  - `bool Capture(Key key, KeyModifiers modifiers)`: returns true when it consumed the key
  - `[RelayCommand] Unbind(string id)` and `[RelayCommand] ResetToDefaults()`
- `CloseShortcutEditorCommand` sets `IsShortcutEditorOpen = false`, writes `Shortcuts.ToOverrides()` into settings, and calls `_settingsStore.Save(_settings)`.

- [ ] **Step 1: Write the failing tests:**
  - `CaptureAssignsAndShowsNewGesture`
  - `CaptureConflictSwapsAndSaysWhich`: `Message` contains `Deck B` and `play / pause`
  - `EscapeCancelsCapture`, and the binding is unchanged
  - `ModifierAloneKeepsWaiting`, and `TabIsIgnoredWhileCapturing`
  - `BareKeyIsAcceptedWithWarning`: `Message` is non-empty and mentions the accident risk
  - `UnbindShowsNone`, and `ResetRestoresDefaults`
  - `RowsGroupedDeckADeckBMix`
  - `CloseSavesOverrides`: after assigning and closing, a fresh `SettingsStore.Load()` has the override
  - A headless UI test `KeysButtonOpensOverlayAndCtrlCommaToo`, with no binding errors, in the style of `WindowRendersWithoutBindingErrors`

- [ ] **Step 2: Run them and confirm they fail.**
  Run: `dotnet test tests/Mkb.Mixer.Tests --filter "FullyQualifiedName~ShortcutEditorTests"`

- [ ] **Step 3: Implement.**
  - The view model and overlay follow the existing `panel` styles.
  - The overlay has a dimmed backdrop with the panel centred, and the list scrolls.
  - Escape with no capture in progress closes the editor.
  - The labels match the spec's table wording.

- [ ] **Step 4: Run the full suite.**
  Run: `dotnet test tests/Mkb.Mixer.Tests --filter "Category!=Audio"`
  Expected: all pass. Also open the PNG that `UiSmokeTests` writes, or run the desktop app, and check that the overlay looks right.

- [ ] **Step 5: Commit.** Message: `Let people choose their own shortcut keys`.
