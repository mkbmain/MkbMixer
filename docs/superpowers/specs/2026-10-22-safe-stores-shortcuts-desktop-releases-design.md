# Safe stores, keyboard shortcuts, desktop releases — design

*2026-10-22*

## Context

Three independent improvements, done together:

1. **Safe stores.** A `tracks.json` or `settings.json` that can't be read is
   currently replaced by an empty one on the next save. That silently throws away
   every BPM correction, hot cue and play history entry. Keep the bad file instead.
2. **Keyboard shortcuts.** The app has no keyboard handling at all. Add shortcuts
   for the main mixing actions, and let people choose their own keys.
3. **Desktop releases.** A `v*` tag publishes an Android APK only. Also publish
   ready-to-run Windows, macOS and Linux builds, so people can use the app without
   the .NET SDK.

The app is meant for a wide audience, not one person's habits (as in the
2026-10-08 design). Defaults must make sense to someone opening it for the first
time. Anything only some people want can be configured.

Each part can ship and be reviewed on its own. Suggested order: 1, 3, 2.

---

## 1. Safe stores

### Behaviour

When `TrackStore` or `SettingsStore` finds a file it can't use, it moves that file
aside and starts fresh. "Can't use" means the JSON is malformed, or the file is
empty, or reading it throws `IOException` or `UnauthorizedAccessException`.

- The bad file is renamed to `<name>.corrupt-<yyyyMMdd-HHmmss>` in the same
  directory, for example `tracks.json.corrupt-20261022-153000`. It is never deleted.
- If the rename itself fails, that store does not write to disk for the rest of the
  session. The app still runs normally from memory. This is what guarantees the
  original is never overwritten.
- A missing file is not an error. A first run behaves exactly as it does today.
- A file that parses but has odd values is not an error either. It goes through
  `Normalised()` and the hot cue resizing as it does today.

### Telling the user

Each store exposes what happened during its load: nothing, moved aside (with the
new path), or could not move aside (writes disabled). At startup `MainViewModel`
turns that into the status bar message, for example:

- `Couldn't read saved track data; kept it as tracks.json.corrupt-20261022-153000 and started fresh.`
- `Couldn't read saved track data and couldn't move it aside; changes this session won't be saved.`

If both stores report a problem, combine them into one message. The exact wording
is up to the builder.

### Tests

Unit tests for both stores, each in a temp directory:
- malformed JSON
- an empty file
- a valid file, which loads unchanged and is not renamed
- a missing file
- a rename that can't happen, for example a read-only directory, where it's
  feasible on the test platform; skip it where it isn't
- after a rename, a later save writes a fresh file and leaves the `.corrupt-` copy
  untouched

---

## 2. Keyboard shortcuts

### Where they work

Shortcuts work in the desktop/tablet layout (`MainView`). That covers desktop
windows and Android tablets with a keyboard attached. The phone layout
(`PhoneView`) gets no shortcuts and no editor.

Keys are handled at the top level of `MainView`, in the tunnelling phase, so a
shortcut works whichever button or list has focus.

**Typing is never hijacked.** While a text-entry control has focus (the search
box, and the three number boxes: the BPM range and the crossfade seconds),
shortcuts are off and keys go to that control. The only
exception is *Focus search*. Pressing Escape in a text box moves focus out of it,
which turns shortcuts back on.

Escape and Tab can't be bound, since they're needed to get around the window, and
neither can a modifier key pressed on its own.

### Actions and default keys

**Every default needs `Ctrl`.** No default is a bare key, so a stray key press
never changes what's playing. That covers someone who thinks they're typing in the
search box when they aren't. (Your requirement.)

The defaults mirror each other: the left hand works deck A, the right hand works
deck B, and the mix controls sit in the middle.

| Action | Deck A | Deck B |
|---|---|---|
| Play / pause (toggles) | `Ctrl+Q` | `Ctrl+P` |
| Headphone cue on/off | `Ctrl+W` | `Ctrl+O` |
| Sync | `Ctrl+E` | `Ctrl+I` |
| Hot cue 1–4 (same as tapping the button) | `Ctrl+1`…`Ctrl+4` | `Ctrl+7`…`Ctrl+0` |
| Clear hot cue 1–4 | `Ctrl+Shift+1`…`4` | `Ctrl+Shift+7`…`0` |
| Nudge slower (hold) | `Ctrl+A` | `Ctrl+K` |
| Nudge faster (hold) | `Ctrl+S` | `Ctrl+L` |
| Add selected library track to this deck's playlist | `Ctrl+Z` | `Ctrl+M` |

| Action | Default |
|---|---|
| Crossfader a step toward A (10% of its travel) | `Ctrl+5` |
| Crossfader a step toward B | `Ctrl+6` |
| Crossfader to centre | `Ctrl+T` |
| Auto-cue on/off | `Ctrl+U` |
| Pause everything / resume what was paused | `Ctrl+B` |
| Focus the library search box | `Ctrl+F` (`Cmd+F` on macOS) |
| Open the shortcuts editor | `Ctrl+,` |

**macOS uses the Control key, not Cmd**, except for *Focus search*, which follows
the platform's `Cmd+F`. macOS already claims `Cmd+Q`, `Cmd+W`, `Cmd+H` and `Cmd+M`
(quit, close, hide, minimise). It also uses `Ctrl+arrows` and `Ctrl+Space`, which
is why none of the defaults use those.

People may still choose a bare key for an action in the editor. *My call:* the
editor allows it, and shows a one-line note that a bare key can fire by accident.

**Releasing keys in any order.** Nudge is held with a modifier. The nudge ends when
its key comes up, whether or not `Ctrl` is still held at that point.

Each action runs the same code path as its on-screen control. Nothing gets a
separate implementation, so shortcuts can't drift from the buttons. Notes:

- **Play / pause** plays when the deck isn't playing and pauses when it is.
- **Nudge** calls `BeginNudge` when the key goes down and `EndNudge` when it comes
  up. Auto-repeat key-downs are ignored. If the window loses focus while a nudge
  key is held, the nudge ends.
- **Sync** does nothing when sync isn't currently available, the same as the
  disabled button.
- **Pause everything** uses `MainViewModel.PauseAll()` and `ResumePaused()`, the
  same pair the Android notification uses. With something playing it pauses;
  otherwise it resumes what it paused.
- **Crossfader steps** clamp at the ends.

### Choosing your own keys

A **Keys** button in `MainView`'s top bar, beside *Load playlists*, opens the
shortcuts editor. `Ctrl+,` opens it too. The editor is an overlay panel inside
`MainView`, not a separate window, so it works the same on desktop and on Android
tablets.

The editor:
- Lists every action, grouped *Deck A*, *Deck B*, *Mix*, with its current key, or
  *none*.
- To change a key, click the action and press the new key or combination. While
  waiting for that key press, the row says so, and Escape cancels.
- **Conflicts swap.** If the new key already belongs to another action, the two
  actions trade keys. A line in the editor says which action moved, for example
  `Swapped with Deck B play / pause`.
- Each row has a way to remove its key, leaving the action unbound.
- **Reset to defaults** restores every key.
- **Close** (or Escape when nothing is waiting for a key) dismisses it.
- Shortcuts are off while the editor is open.
- Changes take effect immediately and are saved with the other settings.

### Storage

`AppSettings` gains a map from action ID to key, holding only the actions the user
has changed. An empty string means "deliberately unbound". Storing only changes
means a future version can add actions or adjust a default without overwriting
anyone's choices.

- Action IDs are stable strings, for example `deckA.playPause`,
  `deckB.hotCue3.clear` and `mix.crossfaderCentre`. They're never renamed once
  shipped.
- Keys are stored as strings in a format Avalonia can parse back (for example
  `KeyGesture` text). The Library project stays free of Avalonia: it stores
  strings, and the App project parses them.
- On load, unknown action IDs and keys that don't parse are dropped. Any action
  missing from the map gets its default, unless another action is already using
  that key, in which case it starts unbound.

### Tests

- The resolver: defaults, merging the saved changes, swapping on conflict,
  unbinding, reset, dropping unknown or unparseable entries, and a new default
  that clashes with a saved key.
- Key routing: a key press triggers the right action on the right deck. Nothing
  fires while a text box has focus. *Focus search* fires anyway. Hold and release
  for nudge, with repeats ignored. The existing headless Avalonia setup
  (`AvaloniaTestSetup`, `UiSmokeTests`) can drive this.
- Settings round trip with changed keys.

### Docs

A *Keyboard shortcuts* section in the README with the default table, and a line
saying the **Keys** button (or `Ctrl+,`) changes them.

---

## 3. Desktop releases

### What gets published

A `desktop` CI job, run after `test`, builds self-contained publishes of
`src/Mkb.Mixer.App.Desktop` in Release for:

| RID | Runner | Archive |
|---|---|---|
| `linux-x64` | `ubuntu-latest` | `.tar.gz` |
| `win-x64` | `windows-latest` | `.zip` |
| `osx-arm64` | `macos-latest` | `.tar.gz` |
| `osx-x64` | `macos-latest` | `.tar.gz` |

Each RID builds on its own OS so that CI also proves the app builds on Windows and
macOS. Each build runs `--audio-info` as a smoke test that the app and its native
audio library load. Runners have no sound card, so "no devices" is the expected
output and isn't a failure; a crash or missing native library is. If a runner
can't run it at all, the builder may skip it and note why.

- Archives are named `mkb-mixer-<version>-<rid>`, matching the APK's naming. The
  version comes from the tag when there is one (`v1.2.3` → `1.2.3`), the same way
  `MKB_VERSION` works for the APK. Otherwise the dev version is used.
- Each push and PR uploads the archives as run artifacts, as the APK is now.
- Self-contained, not single-file. The miniaudio native library has to sit beside
  the executable. Trimming stays off, since Avalonia and reflection-based JSON don't
  trim safely without extra work.
- SoundFlow 1.4.1 ships native runtimes for all four RIDs (checked), so none is
  dropped.
- *Builder's call, settled in review:* the Desktop project is `WinExe`, so on
  Windows the smoke test reads redirected stdout and the exit code rather than
  relying on a console. `macos-latest` is arm64, so the `osx-x64` build is
  published there but its smoke test is skipped unless Rosetta is available.

### The release

Creating the GitHub release moves out of the `apk` job into a `release` job. It
needs `apk` and `desktop`, runs only on `v*` tags, downloads all the artifacts, and
runs one `gh release create` with the APK and the four desktop archives. That way a
release is never created with some of its files missing.

### Docs

The README's *Running* section gains a short *Downloads* note:
- which archive to pick for which system
- on Linux and macOS, run `./Mkb.Mixer.App.Desktop` after extracting
- macOS builds aren't signed, so the first launch needs right-click → *Open*, or
  `xattr -dr com.apple.quarantine <folder>`

---

## Non-goals

- Shortcuts on the phone layout, MIDI controllers, and binding mouse buttons or
  the scroll wheel.
- More than one key per action, or user-defined actions or macros.
- Installers, a macOS `.app` bundle, code signing or notarisation, a Linux
  AppImage/Flatpak, and auto-update.
- Repairing a corrupt store file, or recovering anything from it.

## Calls I made

You said "your guess is as good as mine", so these are my choices, not things you
asked for:

- the default key layout (within your rule that every default needs a modifier)
- swapping keys on conflict rather than refusing the key
- the editor being an overlay
- Escape and Tab being reserved
- moving a corrupt file aside rather than keeping a single `.bak`
- macOS shipped as a plain folder rather than an `.app`

## Left to the builder

- how the code is laid out: names, where the shortcut map and resolver live, and
  how the overlay is built
- exact status message and editor wording
- whether the step size and per-action details above need small adjustments once
  they're tried with real hands on a real keyboard (say what changed)
