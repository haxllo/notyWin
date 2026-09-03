# NotyWin — Windows port progress

Native Windows port of [Noty](https://github.com/aimen08/noty) (MIT).
Repo: `fork/noty`, branch `win` (forked off `main`). Code lives in `windows/`.

This doc covers everything implemented so far (steps 1–3) plus the gaps and
**deviations from the original** (Windows-specific decisions that diverge from
the macOS app).

---

## Build & test

```
cd windows
dotnet build NotyWin.slnx   # 0 errors (1 transitive advisory warning)
dotnet test  NotyWin.slnx   # 89/89 pass
```

Requires .NET 10 SDK + Windows App SDK 2.4 + Microsoft.WindowsAppSDK.WinUI
templates (`dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`).

---

## What was done

### Step 1 — Deck geometry (`NotyWin.Geometry`)

Pure .NET 10 class library. No Win32 / WinUI dependencies. One-to-one port
of `Sources/DeckPanel.swift`.

| File | Mac source | Notes |
|---|---|---|
| `DeckStyle.cs` | `enum DeckStyle` | `Tabs`, `Compact`. |
| `DeckGeom.cs` | `enum DeckGeom` | All 25 metric getters, `Scale`, `S()`, `PillHeight`, `Layout(panelHeight,count,hasMore,style,longestLabel)`. Tabs branch has guard rail, compact branch has chip stacking. |
| `DeckLayout.cs` | `struct DeckLayout` | `ItemHeight`, `Pitch`, `Spacing`, `StackHeight` (adds plus + cog), `Top`, `Center(i)`, `Cap`, `Overflows`. |
| `DeckFrame.cs` | `DeckController.layout(for state:)` | Pure rect math. `Layout(state, display, onLeftEdge, noteCount, noteWidth, edgeWidth, deckYRatio)`. Rest pill placement + fan/expanded full-screen panel. Win32 Y-flip applied to `deckYRatio` so the same `0.0`/`1.0` value lands at the same physical position. |
| `HotZone.cs` | `DeckController.hotZone` | `PanelFrame` + `HotZone.ForPanel(frame, onLeftEdge)`. Width = `FanWidth + 20` against the active edge. |
| `DisplaySet.cs` | `DeckManager.targetDisplayIDs` | `DisplayTarget.Parse("all"\|"main"\|"id:N")`, `DisplaySetResolver.Resolve(target, displays, mainId)`. Pinned-but-gone falls back to main. |
| `DeckStateMachine.cs` | `DeckController.setState` + `pointerEntered/Exited` + idle watch | Pure, no Win32. Inputs: `PointerEntered/Exited/ExitedHotZone, ExpandNote, Collapse, Dismiss, DetachConfirmed, IdleTick, DragStarted/Ended, PointerMoved`. Outputs: `DeckDecision { Next state, Effects[] }`. Configurable: `RestingState`, `FanIdleTimeout`, `NoteIdleTimeout`, `PinnedNoteOpen`, `IsDragging`. |

44 xUnit tests assert the exact point values the Swift code produces at
`scale=1.0`, plus coverage for: short-screen guard rail, `PitchMin/PitchMax`
clamping, the `+N` cap at 14 dashes, shingled-tab negative spacing,
`ExpandedWidth` floor at `FanWidth`, and 13 state-machine transitions.

### Step 2 — Win32 window + multi-display

| File | Mac source | Notes |
|---|---|---|
| `NotyWin.App/Deck/DeckWindow.cs` | `DeckPanel` (NSPanel) | `WS_POPUP \| WS_VISIBLE` + `WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE \| WS_EX_LAYERED`. `ApplyLevel(overFullScreen)` swaps `HWND_TOPMOST` ↔ `HWND_TOP`. `Show` uses `SW_SHOWNOACTIVATE`. `WS_EX_TOOLWINDOW` removes from Alt-Tab (mirrors `.ignoresCycle`); `WS_EX_NOACTIVATE` mirrors `.nonactivatingPanel`. |
| `NotyWin.App/Deck/DeckController.cs` | `DeckController` | One-per-display. Owns `DeckWindow` + `DeckStateMachine` + `DeckModel`. Hosts the 120 ms idle timer, the 150 ms deferred pointer-exit timer, and turns state-machine effects into `Window.ApplyLevel/Show/Hide` + idle-watch start/stop. |
| `NotyWin.App/Deck/DeckManager.cs` | `DeckManager` | Manages the set of `DeckController`s keyed by display id. `RefreshDisplays()` rebuilds against current target; `FocusAt(x,y)` mirrors `NSEvent.mouseLocation` routing; `RefreshAll()` re-applies preferences + level. |
| `NotyWin.App/Deck/DisplayEnumerator.cs` | `NSScreen.screens` | `EnumDisplayMonitors` + `GetMonitorInfo` with both `rcMonitor` and `rcWork`. `DisplayAtPoint(x,y,displays)` for the focused-deck lookup. `MainId()` via `MonitorFromPoint((0,0), MONITOR_DEFAULTTOPRIMARY)`. |
| `NotyWin.App/Deck/DisplayChangeWatcher.cs` | `NSApplication.didChangeScreenParametersNotification` | Hidden window class `NotyWinDisplayWatcher`, fires `Changed` event on `WM_DISPLAYCHANGE` (0x007E). |

### Step 3 — Settings + storage

| File | Mac source | Notes |
|---|---|---|
| `NotyWin.Models/Note.cs` | `struct Note` | Identical fields, plus `Note.DerivedTitle(body)` for first-line title. |
| `NotyWin.Models/NoteColor.cs` | `struct NoteColor` | Same 8 colors as `NoteColor.all` in Core.swift, expressed as `0xAARRGGBB int` (was SwiftUI `Color` on macOS). |
| `NotyWin.Models/NoteTextDirection.cs` | `enum NoteTextDirection` | `Automatic / LeftToRight / RightToLeft` with `ToWire()` / `FromWire()` for DB storage. |
| `NotyWin.Models/Tasks.cs` | `enum Tasks` | `☐ / ☑` markers, `Stripped()`, `Progress(body) → (done, total)`. |
| `NotyWin.Models/PendingDelete.cs` | `struct PendingDelete` | 10-second undo window. |
| `NotyWin.Models/Shortcut.cs` | inline `Shortcut` in Settings.swift | Engine-agnostic: `KeyModifiers` (Shift/Ctrl/Alt/Meta — Meta is Win32 VK_WIN, treated as Cmd on the Mac side) + Win32 VK / Carbon key code. |
| `NotyWin.Models/ISettingsStore.cs` | `enum Settings` (UserDefaults) | `SettingsSnapshot` is a `record` with the same defaults as Swift (deckScale=1.0, edgeWidth=14, deckYRatio=0.5, etc.). All 12 `scXxx` shortcuts mapped to VK codes (`0x1B=Esc, 0x46=F, 0x54=T, 0x50=P, 0xBE=., 0x08=Backspace, 0xBB=+ on Ctrl, 0xBD=- on Ctrl`). `ISettingsStore` interface is the persistence boundary. |
| `NotyWin.Models/INotePersistence.cs` | `final class Store` (SQLite) | `LoadAll() / Upsert(Note) / Delete(id)`. |
| `NotyWin.Models/NoteList.cs` | `final class NoteStore` | Pure in-memory observable list with the same mutation API as `NoteStore`: `Create / UpdateBody / TogglePin / CycleColor / SetColor / SetTextDirection / SetArchived / Delete (with PendingUndo) / UndoDelete / Reorder / Move / Ingest / ReplaceAll`. Same "newest at top" semantics (smallest order). Implements `IObservable<NoteList>`; subscribers receive every change. |
| `NotyWin.Storage/NoteCipher.cs` | `enum Crypto` (CryptoKit) | AES-GCM 256-bit via `System.Security.Cryptography.AesGcm`. Combined format `nonce(12) || cipher || tag(16)` matches the macOS wire format. Key file is **DPAPI-wrapped** (`System.Security.Cryptography.ProtectedData`) — see "Deviations" below. |
| `NotyWin.Storage/SqliteNotePersistence.cs` | `final class Store` | `Microsoft.Data.Sqlite` (WAL, synchronous=NORMAL). Identical schema: `id, title, body BLOB, color, created, modified REAL, archived, sort_order REAL, pinned, text_direction TEXT`. Same migrations: `pinned` and `text_direction` added if missing. Same UPSERT semantics via `ON CONFLICT(id) DO UPDATE`. |
| `NotyWin.Storage/JsonSettingsStore.cs` | `UserDefaults.standard` | Unpackaged: `ApplicationData.Current.LocalSettings` is for packaged apps, so we use `System.Text.Json` to a single file at `%LocalAppData%\Noty\settings.json`. `Changed` event fires after every Save. Corrupt file → fall back to defaults, do not crash. |

27 Models tests + 18 Storage tests = 45 new tests. All green.

#### Deck ↔ Models wiring

- `DeckModel.SyncPreferences(SettingsSnapshot s)` replaces the previous stub `SyncPreferences()`. Reads every preference the deck cares about.
- `DeckManager` takes a `NoteList` + `ISettingsStore` in its constructor. Subscribes to the `NoteList`; every change updates each deck's `Model.NoteCount`.
- `DeckController.Notes` and `DeckController.Settings` properties expose the source objects so the controller (and any future host) can drive operations.

---

## Deviations from the original (Windows-specific)

The user's request was to record anything outside the original. These are
**intentional divergences**, scoped tightly, and documented so the upstream
PR conversation can address them if/when a PR happens.

### 1. DPAPI-wrapped AES key (security improvement)

- **Original** (`Sources/Core.swift`): 32-byte AES key written as a raw file
  with `0o600` permissions next to the database
  (`~/Library/Application Support/Noty/note.key`).
- **Windows**: a raw 32-byte file in `%LocalAppData%\Noty\` is readable by
  anything running as the user, with no permission parity to macOS. We wrap
  the key with `ProtectedData.Protect(DataProtectionScope.CurrentUser)` and
  write the wrapped form to `note.key.dpapi`. On load, `Unprotect` recovers
  the AES key. The plaintext key exists in process memory only; never on disk.
- **Why**: a defence-in-depth improvement that does not change the wire
  format (combined nonce/cipher/tag is byte-compatible with the macOS app).
  A future Mac-Swift build could opt into the same approach via a Keychain
  item that decrypts the same key file.
- **If upstream rejects the deviation**: feature-flag the wrap with
  `NOTY_PLAIN_KEY_FILE=1`; reading a raw file also still works (we just
  never write one).

### 2. `Meta` modifier = Windows key (no `Win` key in the Mac default)

- **Original**: every `Shortcut` uses `modifiers: cmd | ...`. On Windows there
  is no `Cmd`; the closest equivalent in the Win32 API is `VK_LWIN`/`VK_RWIN`,
  which the `RegisterHotKey` API expects as `MOD_WIN`.
- **Our model**: `KeyModifiers.Meta` is what the macOS code calls `cmd`.
  Translation: on Mac registration it becomes `cmd`; on Win32 registration it
  becomes `MOD_WIN`. Defaults match the Swift defaults exactly (`Alt+Meta+N`
  for new note → `Alt+VK_LWIN+N` on Windows, with the OS treating that as
  `Ctrl+Alt+N` in apps that don't differentiate).
- **Why**: keeps the user-facing defaults consistent (`⌥⌘N` ≡ `Alt+Win+N`).
  Real keycaps in the UI will need a small helper to render `Win` as `⌘`
  in shortcuts display strings; the model stays the same.

### 3. `AppLanguage` removed (no Windows equivalent)

- **Original** (`Sources/Settings.swift` lines 6-34, 43-62): `Settings.appLanguage`
  reads/writes `AppleLanguages` in the application domain, optionally
  overriding the system language.
- **Windows**: the standard equivalent is `GetUserDefaultLocaleName`, which is
  system-wide and not per-app. The closest per-app override is the
  packaged-app `Windows.ApplicationModel.Resources.ResourceLoader` flow, which
  doesn't apply to unpackaged builds. We model the other 23 preferences
  identically and skip `appLanguage`; the app will use the OS locale.
- **Why**: no Win32 API parity, and the macOS behaviour is essentially
  "honour the system setting unless overridden", which Windows already does
  per-user.

### 4. `Ink.face` font system simplified

- **Original** (`Sources/Core.swift` lines 87-170): nine named faces
  (Noteworthy, Bradley Hand, Marker Felt, Chalkboard, Avenir Next, etc.),
  filtered to what is installed via `NSFont(name:size:) != nil`. The list is
  filtered at app start and cached.
- **Windows**: the same named faces won't all be present. Win32 font
  enumeration is per-font-family via `EnumFontFamiliesEx` and is much heavier.
  We will not port the `Ink` system this iteration; the editor (step 4) will
  ship with a hand-picked subset (Noteworthy + system default), and the
  font list will be static. This is a known gap, not a design choice.
- **Why**: avoiding a 200-line font-enumeration layer that has no clear UX
  benefit. The user can switch to system default and that's most of the
  win. Reintroduce when needed.

### 5. `SortableStringID` ordering re-densified

- **Original** uses sparse `order: Double` for the deck position; reorder
  rewrites densely on every drag to prevent drift.
- **We do the same** (`NoteList.Reorder` rewrites `0..n-1` for the active
  list). No deviation here, just called out because the test confirms it.
  Implemented identically in the C# port.

### 6. AES-GCM `tagSizeInBytes` is explicit

- **Original** uses CryptoKit's default tag size (16 bytes).
- **Our code** explicitly passes `tagSizeInBytes: 16` to `new AesGcm(key, 16)`
  to suppress the `SYSLIB0053` obsolete warning on the parameterless
  constructor (the .NET team is deprecating it). Wire format unchanged.
- **Why**: clean compile, same bytes on the wire.

### 7. `Microsoft.Data.Sqlite` over `sqlite3` C API

- **Original** uses raw `sqlite3.h` via `import SQLite3` (Apple's
  sqlite3-prebuilt). Bindings are all manual: `sqlite3_prepare_v2`,
  `sqlite3_bind_*`, etc.
- **We use `Microsoft.Data.Sqlite`**: managed ADO.NET provider, parameterised
  commands via `$id`/`$title`/... instead of `sqlite3_bind_*`. Same SQL.
  Migration check is done with `PRAGMA table_info` via a managed reader
  instead of `sqlite3_prepare_v2` + `sqlite3_column_text` walks.
- **Why**: no advantage in reimplementing parameter binding on Windows when a
  maintained wrapper exists. The schema and SQL statements are byte-for-byte
  identical (WAL + synchronous=NORMAL + CREATE TABLE + CREATE INDEX +
  ALTER TABLE migrations + UPSERT). The `Migrate()` method logs nothing;
  the original logs via `NSLog` — we silently skip if the column exists,
  which matches the test expectation.

### 8. `Color` (SwiftUI) → ARGB int

- **Original** uses SwiftUI `Color(.sRGB, red:, green:, blue:, opacity:)`.
- **We model colors as 0xAARRGGBB ints** (`PaperArgb`, `DashArgb`, `InkArgb`).
  The WinUI 3 layer (step 4) will convert to `Microsoft.UI.Colors` / brushes
  at draw time.
- **Why**: a portable color type the Models lib can live in (no UI deps).
  Picked alpha=0xFF explicitly to match the SwiftUI opacity=1.

### 9. `RelativeDateTimeFormatter` / `DateFormatter` (Swift) → not ported

- **Original** has `Fmt.relative`, `Fmt.stamp`, `Fmt.fileStamp` in Core.swift.
  Used by the Library window / archive list.
- **We haven't ported these** — `fmt.relative` lives in Core.swift but is only
  called from LibraryWindow (step 4 work). It will be a `CultureInfo`-aware
  helper in the Storage or Models lib when needed.

### 10. JSON settings file instead of `UserDefaults` / `ApplicationData`

- **Original** reads from `UserDefaults.standard` with a per-key string.
- **Packaged WinUI** apps: would use `ApplicationData.Current.LocalSettings`.
- **Unpackaged WinUI** apps: `ApplicationData` is unavailable.
- **We use a single JSON file** at `%LocalAppData%\Noty\settings.json` via
  `System.Text.Json`. Round-trips every property of `SettingsSnapshot`.
  Corrupt file → defaults. This is the right boundary for an unpackaged
  build; packaged can swap in `ApplicationData`-backed store later.
- **Why**: the unpackaged path was the user-confirmed target.

---

## Gaps (step 4+ work)

Everything below is in the macOS app but not yet ported. Roughly in build order.

### Visible app (the biggest single piece)

- **Pill view** (12 pt coloured dashes).
- **Fan / tabs view** (45 ms stagger, 3° lean, label rotation, shingled).
- **Expanded note editor** (`NSTextView` → `RichEditBox` with Markdown-as-you-type via Win2D layout, or AvaloniaEdit; find bar; ⌘F; ⌘T checkbox; ⌘P pin; ⌘. colour cycle; ⌃± text size; ⌘Z/Y redo).
- **Autocomplete (right-click menu on pill)**. `showContextMenu(at:)` in `DeckController.swift:552-688` — full submenu of new note, all notes, archive, display target list, deck style, font face, text size, deck size, keep open, dock left, show over full-screen, launch at login, export, import, check for updates, settings, quit.
- **Settings window.** Four tabs: Shortcuts, Deck, Notes, Updates.

### Behaviour (continues step 2's hooks)

- **Pointer enter/exit on the panel.** Win32 `WM_MOUSEMOVE` + `TrackMouseEvent`. The state machine already handles the rest.
- **Pill drag** (`beginPillDrag`). Needs `SetCapture` + `WM_MOUSEMOVE`/`WM_LBUTTONUP` pump, hit-tests against current display, snaps to left/right at the cursor's height, updates `deckYRatio` and `deckOnLeftEdge`.
- **Tab drag** (reorder). `DeckViews.swift:260-282` — `dragID` + `dragTarget`, reorders via `NoteList.Reorder`.
- **Detach flow.** `detachExpandedNote(at:)` + `finishDetach()`. Hands the open note to a separate floating HWND; restores to deck on idle.
- **Global mouse-down monitor** (dismisses an unpinned open note when the user clicks in another app). `SetWindowsHookEx(WH_MOUSE_LL, ...)` — needs a low-level mouse hook.
- **Outside-click on the floating note** to dismiss.
- **Pointer-position polling for hot-zone exit.** `OnPointerExited` schedules a 150 ms deferred check; the deferred callback currently fires the state-machine input regardless of cursor position. Needs a `GetCursorPos` call inside the timer to confirm exit before sending `PointerExitedHotZone`.

### System integrations

- **Global hotkeys.** Sources/HotKeys.swift uses Carbon `RegisterEventHotKey`. Win32: `RegisterHotKey(hWnd, id, fsModifiers, vk)` with `WM_HOTKEY`. Re-bindable through Settings.
- **`noty://` URL scheme.** Register in `HKCR\noty\shell\open\command`. Handlers: `new?text=...`, `capture`, `all`, `settings`.
- **Launch at login.** `Microsoft.Windows.AppLifecycle.StartupTask` (packaged) or `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (unpackaged). The app is unpackaged, so registry.
- **Auto-update.** Squirrel.Windows or a custom `GitHubReleaseManifest`. The macOS app uses Sparkle (signed EdDSA appcast); Win32 equivalent: ship a JSON manifest over HTTPS, Authenticode-sign the MSIX or `.exe`.
- **Multi-display rebuild on hot-plug.** `DisplayChangeWatcher` is in place but not wired into `App.xaml.cs` yet.
- **MSIX packaging + Authenticode signing.** The build is unpackaged; eventually needs MSIX for distribution.

### Editor (the big one)

- **Markdown-as-you-type** (`Sources/NoteEditor.swift` ~700 lines). ~12 style rules. `NSTextStorage` + `NSLayoutManager` + a custom `NSTextView`. Win32: `RichEditBox` doesn't have a public storage API — most likely path is a `CanvasControl` (Win2D) with custom layout, or a hosted `TextBox` + `InlineCollection` (which has limited support for arbitrary runs). AvaloniaEdit would be a fallback.
- **Checkbox tasks** (inline `☐`/`☑`).
- **Find bar** (`⌘F`).
- **250 ms autosave** (debounced).

### Library / archive window

- All Notes window (`Sources/LibraryWindow.swift`): search across bodies, editable detail pane, archive/restore.
- Archive window: read-only listing.
- Quick capture box (`⇧⌘Space`): floating, no editor.

### Other

- **Tray icon** (no macOS equivalent — the app is a "menu bar / accessory" app, no dock icon. Win32 apps in WinUI usually have a taskbar entry; the WinUI `MainWindow` is already created and could be hidden, or the app can run as a tray-only app via `Shell_NotifyIcon`).
- **Update checking**: Sparkle parity → GitHub Releases JSON fetch + Authenticode verification.

### Tests still to write

- Window-frame state: confirm `SetWindowPos` gets the right rect for each state.
- `DeckManager` rebuild against display set changes (mock the display snapshot).
- `DisplayEnumerator` integration (run on the actual host machine in CI?).
- `DeckFrame` for the multi-display edge cases (negative-coordinate secondary monitor on Windows).

---

## Files added

```
windows/
  NotyWin.slnx
  PROGRESS.md
  src/
    NotyWin.App/                    # WinUI 3 MVVM (scaffolded, no UI yet)
      Deck/
        DeckWindow.cs
        DeckController.cs
        DeckManager.cs
        DisplayEnumerator.cs
        DisplayChangeWatcher.cs
    NotyWin.Geometry/               # pure .NET 10 lib (deck + state machine)
      DeckStyle.cs
      DeckGeom.cs
      DeckLayout.cs
      DeckFrame.cs
      DeckStateMachine.cs
      HotZone.cs
      DisplaySet.cs
    NotyWin.Models/                 # pure .NET 10 lib (note + settings)
      Note.cs
      NoteColor.cs
      NoteTextDirection.cs
      Tasks.cs
      PendingDelete.cs
      Shortcut.cs
      NoteList.cs                   # in-memory observable list
      ISettingsStore.cs             # SettingsSnapshot record + interface
      INotePersistence.cs           # persistence interface
    NotyWin.Storage/                # net10.0-windows (SQLite + AES + DPAPI)
      NoteCipher.cs
      SqliteNotePersistence.cs
      JsonSettingsStore.cs
  tests/
    NotyWin.Geometry.Tests/         # 44 xUnit tests
    NotyWin.Models.Tests/           # 27 xUnit tests
    NotyWin.Storage.Tests/          # 18 xUnit tests, net10.0-windows
```

---

## Commits on `win`

| Hash | Subject |
|---|---|
| `b0bd314` | scaffold WinUI 3 solution + DeckGeometry parity port |
| `400317b` | deck state machine + multi-display + hot zone |
| (next)   | settings + storage + Models/Storage libs + tests + DPAPI key wrap |
