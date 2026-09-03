# NotyWin — Windows port progress

Native Windows port of [Noty](https://github.com/aimen08/noty) (MIT).
Repo: `fork/noty`, branch `win` (forked off `main`). Code lives in `windows/`.

This doc covers the deck implementation only (steps 1 + 2 of the plan). The
note editor, SQLite store, AES, hotkeys, URL scheme, library window,
export/import, updates, and settings UI are not yet implemented.

---

## Build & test

```
cd windows
dotnet build NotyWin.slnx   # 0 warnings, 0 errors
dotnet test  NotyWin.slnx   # 44/44 pass
```

Requires .NET 10 SDK + Windows App SDK 2.4 + Microsoft.WindowsAppSDK.WinUI
templates (`dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`).

---

## What's done

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

20 xUnit tests assert the exact point values the Swift code produces at
`scale=1.0`, plus coverage for: short-screen guard rail, `PitchMin/PitchMax`
clamping, the `+N` cap at 14 dashes, shingled-tab negative spacing, and
`ExpandedWidth` floor at `FanWidth`.

### Step 2 — Deck state machine + multi-display

| File | Mac source | Notes |
|---|---|---|
| `DeckStateMachine.cs` | `DeckController.setState` + `pointerEntered/Exited` + idle watch | Pure, no Win32. Inputs: `PointerEntered/Exited/ExitedHotZone, ExpandNote, Collapse, Dismiss, DetachConfirmed, IdleTick, DragStarted/Ended, PointerMoved`. Outputs: `DeckDecision { Next state, Effects[] }`. Effects = `Hide, ShowPill, ShowFan, ShowExpanded, StartIdleWatch, StopIdleWatch, CancelExitWork, CancelShrinkWork, ActivateApp, DeactivateApp, DeactivateSiblingDecks`. Configurable: `RestingState`, `FanIdleTimeout`, `NoteIdleTimeout`, `PinnedNoteOpen`, `IsDragging`. |
| `DisplaySet.cs` | `DeckManager.targetDisplayIDs` | `DisplayTarget.Parse("all"\|"main"\|"id:N")`, `DisplaySetResolver.Resolve(target, displays, mainId)`. Pinned-but-gone falls back to main. |
| `DeckController.cs` | `DeckController` | One-per-display. Owns `DeckWindow` + `DeckStateMachine` + `DeckModel`. Hosts the 120 ms idle timer, the 150 ms deferred pointer-exit timer, the `Window.ApplyLevel / Hide / Show` effects, the pill drag (collapsed to Rest first), and the `applyRestingState` (preference change) path. |
| `DeckManager.cs` | `DeckManager` | Manages the set of `DeckController`s keyed by display id. `RefreshDisplays()` rebuilds against current target; `FocusAt(x,y)` mirrors `NSEvent.mouseLocation` routing; `RefreshAll()` re-applies preferences + level. |
| `DisplayEnumerator.cs` | `NSScreen.screens` | `EnumDisplayMonitors` + `GetMonitorInfo` (WPARAM/LPARAM-style, takes `MONITORINFO` with both `rcMonitor` and `rcWork`). `DisplayAtPoint(x,y,displays)` for the focused-deck lookup. `MainId()` via `MonitorFromPoint((0,0), MONITOR_DEFAULTTOPRIMARY)`. |
| `DisplayChangeWatcher.cs` | `NSApplication.didChangeScreenParametersNotification` | Hidden window class `NotyWinDisplayWatcher`, fires `Changed` event on `WM_DISPLAYCHANGE` (0x007E). |
| `DeckWindow.cs` | `DeckPanel` (NSPanel) | `WS_POPUP \| WS_VISIBLE` + `WS_EX_TOOLWINDOW \| WS_EX_NOACTIVATE \| WS_EX_LAYERED`. `ApplyLevel(overFullScreen)` swaps `HWND_TOPMOST` ↔ `HWND_TOP`. `SetFrame(x,y,w,h)` matches `NSPanel.setFrame(_:display:animate:)`. `Show` uses `SW_SHOWNOACTIVATE`. `WS_EX_TOOLWINDOW` removes from Alt-Tab (mirrors `.ignoresCycle`); `WS_EX_NOACTIVATE` mirrors `.nonactivatingPanel`. |

13 state-machine tests + 6 hot-zone/display-set tests = 44 total.

### Translation map (macOS → Win32)

| macOS | Win32 |
|---|---|
| `NSPanel` `.borderless, .nonactivatingPanel` | `WS_POPUP` + `WS_EX_NOACTIVATE` |
| `.statusBar` level (over full-screen) | `HWND_TOPMOST` |
| `.floating` level | `HWND_TOP` |
| `.canJoinAllSpaces + .fullScreenAuxiliary + .stationary` | inherent to top-most (no spaces on Windows) |
| `.ignoresCycle` (hidden from Alt-Tab) | `WS_EX_TOOLWINDOW` |
| `canBecomeKey=true, canBecomeMain=false` | `WS_EX_NOACTIVATE` + `ShowWindow(SW_SHOWNOACTIVATE)` |
| `NSApplication.didChangeScreenParametersNotification` | `WM_DISPLAYCHANGE` on hidden message-only window |
| `NSScreen.screens`, `CGDirectDisplayID` | `EnumDisplayMonitors` + HMONITOR id (cast to `uint`) |
| `NSEvent.mouseLocation` (bottom-up Y) | `GetCursorPos` → top-down Y (flip applied in `DeckFrame`) |
| `NSEvent.pressedMouseButtons & 1` | `GetAsyncKeyState(VK_LBUTTON) & 0x8000` (not yet wired) |
| Carbon `RegisterEventHotKey` | `RegisterHotKey` (not yet wired) |
| `NSCursor.closedHand` | `SetCursor(LoadCursor(NULL, IDC_SIZEALL))` (not yet wired) |

---

## What's done structurally (scaffold)

- WinUI 3 MVVM solution scaffold via `dotnet new winui-mvvm`.
- Class library + xUnit test project wired.
- `NotyWin.App` project compiles as unpackaged WinUI 3 (`net10.0-windows10.0.26100.0`, `win-x64`).
- `Microsoft.WindowsAppSDK` 2.4.0, `CommunityToolkit.Mvvm` 8.4.2 referenced.
- Branch `win` committed with steps 1+2 as `b0bd314`.

---

## Gaps (step 3+ work)

Everything below is in the macOS app but not yet ported. Roughly in build order.

### Core plumbing (blocks the rest)

- **Settings store.** `Sources/Settings.swift` → `ApplicationDataContainer` (Roaming) or `Environment.SpecialFolder.LocalApplicationData` JSON. Holds `deckScale`, `edgeWidth`, `deckYRatio`, `deckOnLeftEdge`, `deckStyle`, `deckAlwaysShown`, `deckPillHidden`, `openOnHover`, `tabPreview`, `displayTarget`, `noteFontSize`, `noteSize`, `markdownStyling`, `showOverFullScreen`, `fanIdleTimeout`, `noteIdleTimeout`, `launchAtLogin`, `checkForUpdates`. Mirror as `DeckModel` properties in the controller (already stubbed, but no `Settings` source).
- **Note model + NoteStore.** `Sources/Note.swift` + `NoteStore.swift` + `Store.swift`. SQLite via `Microsoft.Data.Sqlite`; AES-GCM body encryption via `System.Security.Cryptography.AesGcm`; DPAPI for the master key.
- **App entry / single-instance + protocol activation.** `Main.swift` + `AppDelegate.swift`. App instance registration + `noty://` URL handling via `Microsoft.Windows.AppLifecycle.AppInstance.GetActivatedEventArgs`.

### Deck views (visible app)

- **Pill view** (12 pt coloured dashes).
- **Fan / tabs view** (45 ms stagger, 3° lean, label rotation, shingled).
- **Expanded note editor** (`NSTextView` → `RichEditBox` with Markdown-as-you-type via `IDTextDocument`/Win2D layout, or `AvaloniaEdit` for the bridge; find bar; ⌘F; ⌘T checkbox; ⌘P pin; ⌘. colour cycle; ⌃± text size; ⌘Z/Y redo).
- **Autocomplete (right-click menu on pill)**. `showContextMenu(at:)` in `DeckController.swift:552-688` — full submenu of new note, all notes, archive, display target list, deck style, font face, text size, deck size, keep open, dock left, show over full-screen, launch at login, export, import, check for updates, settings, quit.
- **Settings window.** Four tabs: Shortcuts, Deck, Notes, Updates.

### Behaviour (continues step 2's hooks)

- **Pointer enter/exit on the panel.** Win32 `WM_MOUSEMOVE` + `TrackMouseEvent` (since `WS_EX_NOACTIVATE` means the panel doesn't get `WM_MOUSEHOVER` for free). The state machine already handles the rest.
- **Pill drag** (`beginPillDrag`). Needs `SetCapture` + `WM_MOUSEMOVE`/`WM_LBUTTONUP` pump, hit-tests against current display, snaps to left/right at the cursor's height, updates `deckYRatio` and `deckOnLeftEdge`.
- **Tab drag** (reorder). `DeckViews.swift:260-282` — `dragID` + `dragTarget`, reorders via `NoteStore.reorder(id:by:)`.
- **Detach flow.** `detachExpandedNote(at:)` + `finishDetach()`. Hands the open note to a separate floating HWND; restores to deck on idle.
- **Global mouse-down monitor** (dismisses an unpinned open note when the user clicks in another app). `SetWindowsHookEx(WH_MOUSE_LL, ...)` — needs a low-level mouse hook (cheap; no Accessibility equivalent needed on Windows).
- **Outside-click on the floating note** to dismiss.
- **Idle timer exact timing.** Currently uses `System.Timers.Timer` (120 ms). The Swift `Timer` runs on the main runloop, so 0.12 s is also a polling tick. Behaviour matches.
- **Pointer-position polling for hot-zone exit.** `OnPointerExited` schedules a 150 ms deferred check; the deferred callback currently fires the state-machine input regardless of cursor position. Needs a `GetCursorPos` call inside the timer to confirm exit before sending `PointerExitedHotZone`.

### System integrations

- **Global hotkeys.** `Sources/HotKeys.swift` uses Carbon `RegisterEventHotKey`. Win32: `RegisterHotKey(hWnd, id, fsModifiers, vk)` with `WM_HOTKEY`. Re-bindable through Settings.
- **`noty://` URL scheme.** Register in `HKCR\noty\shell\open\command` (or via `Microsoft.Windows.AppLifecycle.AppActivationArguments`). Handlers: `new?text=...`, `capture`, `all`, `settings`.
- **Launch at login.** `Microsoft.Windows.AppLifecycle.StartupTask` (packaged) or `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (unpackaged). The app is unpackaged, so registry.
- **Auto-update.** Squirrel.Windows or a custom `GitHubReleaseManifest`. The macOS app uses Sparkle (signed EdDSA appcast); Win32 equivalent: ship a JSON manifest over HTTPS, Authenticode-sign the MSIX or `.exe`.
- **Multi-display rebuild on hot-plug.** `DisplayChangeWatcher` is in place but not wired into `App.xaml.cs` yet.
- **MSIX packaging + Authenticode signing.** The build is unpackaged; eventually needs MSIX for distribution.

### Storage + encryption

- **Local DB.** `Microsoft.Data.Sqlite` schema matching `Sources/Store.swift` (`notes`, with title, body, color, archived, pinned, created_at, updated_at). Body column holds AES-GCM ciphertext.
- **AES-GCM.** `System.Security.Cryptography.AesGcm` (available .NET 8+). Key wrap via DPAPI (`ProtectedData.Protect`).
- **Export / import.** Markdown, plain text, single doc, `.stickies` JSON. Identical format to the macOS app.
- **Archive / restore.** `setArchived(id: true/false)`, `reorder(id:by:)`, `cycleColor(id:)`, `togglePin(id:)`, `delete(id:)`.

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

- **Context menu** (right-click pill): the state machine is ready; the menu itself needs `MenuFlyout` XAML.
- **Tray icon** (no macOS equivalent — the app is a "menu bar / accessory" app, no dock icon. Win32 apps in WinUI usually have a taskbar entry; the WinUI `MainWindow` is already created and could be hidden, or the app can run as a tray-only app via `Shell_NotifyIcon`).
- **Update checking**: Sparkle parity (Sparkle over appcast XML) → GitHub Releases JSON fetch + Authenticode verification.

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
  src/
    NotyWin.App/                    # WinUI 3 MVVM (scaffolded, no UI yet)
    NotyWin.Geometry/               # pure .NET 10 lib
      DeckStyle.cs
      DeckGeom.cs
      DeckLayout.cs
      DeckFrame.cs
      DeckStateMachine.cs
      HotZone.cs
      DisplaySet.cs
    NotyWin.App/Deck/               # Win32-only files
      DeckWindow.cs
      DeckController.cs
      DeckManager.cs
      DisplayEnumerator.cs
      DisplayChangeWatcher.cs
  tests/
    NotyWin.Geometry.Tests/         # 44 xUnit tests
      DeckGeomTests.cs              (12)
      DeckFrameTests.cs             (6)
      DeckStateMachineTests.cs      (13)
      HotZoneTests.cs               (13, includes DisplaySetTests)
```

---

## Commits

| Hash | Subject |
|---|---|
| `b0bd314` | `win: scaffold WinUI 3 solution + DeckGeometry parity port` |

(Step 2 to be committed at the end of this turn.)
