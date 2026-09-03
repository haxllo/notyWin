# NotyWin — Windows port progress

Native Windows port of [Noty](https://github.com/aimen08/noty) (MIT).
Repo: `fork/noty`, branch `win` (forked off `main`). Code lives in `windows/`.

This doc covers everything implemented so far (steps 1–4) plus the gaps and
**deviations from the original** (Windows-specific decisions that diverge from
the macOS app).

---

## Build & test

```
cd windows
dotnet build NotyWin.slnx   # 0 errors
dotnet test  NotyWin.slnx   # 114/114 pass
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

### Step 2 — Win32 window + multi-display

| File | Mac source | Notes |
|---|---|---|
| `NotyWin.App/Deck/DeckWindow.cs` | `DeckPanel` (NSPanel) | Borderless tool window via `WS_POPUP`. Per-display `Microsoft.UI.Xaml.Window` for WinUI 3 content. Transparent helper HWND (`WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT`) covers the right edge for mouse input via `SetWindowSubclass`. Handles `WM_SETCURSOR` to force arrow cursor. |
| `NotyWin.App/Deck/DeckController.cs` | `DeckController` | One-per-display. Owns `DeckWindow` + `DeckStateMachine` + `DeckModel` + `DeckView`. Hosts the 120 ms idle timer, the 150 ms deferred pointer-exit timer, and turns state-machine effects into `Window.ApplyLevel/Show/Hide` + idle-watch start/stop. |
| `NotyWin.App/Deck/DeckManager.cs` | `DeckManager` | Manages the set of `DeckController`s keyed by display id. |
| `NotyWin.App/Deck/DisplayEnumerator.cs` | `NSScreen.screens` | `EnumDisplayMonitors` + `GetMonitorInfo` with both `rcMonitor` and `rcWork`. |
| `NotyWin.App/Deck/DisplayChangeWatcher.cs` | `NSApplication.didChangeScreenParametersNotification` | Hidden window class on `WM_DISPLAYCHANGE`. |

### Step 3 — Settings + storage

| File | Mac source | Notes |
|---|---|---|
| `NotyWin.Models/Note.cs` | `struct Note` | Identical fields, plus `Note.DerivedTitle(body)` for first-line title. |
| `NotyWin.Models/NoteColor.cs` | `struct NoteColor` | Same 8 colors as `NoteColor.all` in Core.swift, expressed as `0xAARRGGBB int`. |
| `NotyWin.Models/NoteTextDirection.cs` | `enum NoteTextDirection` | `Automatic / LeftToRight / RightToLeft` with `ToWire()` / `FromWire()`. |
| `NotyWin.Models/Tasks.cs` | `enum Tasks` | `☐ / ☑` markers, `Stripped()`, `Progress(body) → (done, total)`. |
| `NotyWin.Models/PendingDelete.cs` | `struct PendingDelete` | 10-second undo window. |
| `NotyWin.Models/Shortcut.cs` | inline `Shortcut` in Settings.swift | Engine-agnostic: `KeyModifiers` (Shift/Ctrl/Alt/Meta) + Win32 VK. |
| `NotyWin.Models/ISettingsStore.cs` | `enum Settings` (UserDefaults) | `SettingsSnapshot` record with the same defaults as Swift. All 12 `scXxx` shortcuts mapped to VK codes. |
| `NotyWin.Models/INotePersistence.cs` | `final class Store` (SQLite) | Persistence boundary interface. |
| `NotyWin.Models/NoteList.cs` | `final class NoteStore` | Pure in-memory observable list with the same mutation API. |
| `NotyWin.Storage/NoteCipher.cs` | `enum Crypto` (CryptoKit) | AES-GCM 256-bit via `System.Security.Cryptography.AesGcm`. Key file is **DPAPI-wrapped**. |
| `NotyWin.Storage/SqliteNotePersistence.cs` | `final class Store` | `Microsoft.Data.Sqlite` with identical schema, migrations, and UPSERT. |
| `NotyWin.Storage/JsonSettingsStore.cs` | `UserDefaults.standard` | `System.Text.Json` to a single file at `%LocalAppData%\Noty\settings.json`. |

### Step 4 — Visible UI (the deck)

The biggest step so far. Three new pieces, all in the `NotyWin.Rendering`
pure lib plus a WinUI 3 surface in the App project.

#### 4a. Rendering library (pure)

| File | Mac source | Notes |
|---|---|---|
| `NotyWin.Rendering/ITextMeasurer.cs` | `NSString.size(withAttributes:)` | Engine-agnostic. Default `GdiTextMeasurer` uses `System.Windows.Forms.TextRenderer`; tests inject a stub. |
| `NotyWin.Rendering/LabelWidthCache.cs` | `DeckGeom.labelCache` | Same 400-entry rolling cap, same `fontName\|pointSize\|text` key, same uppercase-before-measure. |
| `NotyWin.Rendering/DeckViewModel.cs` | `DeckRootView.body` + `FanColumn.body` + `PillView.body` | Produces a list of `RenderItem`s (ZStack order). Implements the same pill placement formula, the same fan vertical centering, the same reveal-stage delays (`Double(index) * 0.042`, spring 0.34/0.84), the same drag shift logic (`if from < to && index > from && index <= to` etc.), and the same `+N` overflow tab. |
| `RevealProgressTracker` (in same file) | `FanColumn.@State appeared / previewNoteID / dragID` | Tracks animation state separately from the state machine so paint passes are deterministic. |
| `NotyWin.Rendering/HitTest.cs` | SwiftUI hit-testing | Iterates items in reverse (top-most first), returns the kind under the cursor. |

Tests: 25 xUnit. Cover render-with-empty-deck, render-with-N-notes, compact-style chips, on-left-edge mirror, pill position, `+N` overflow, hit-test topmost, hit-test miss, `StageProgress` (default settled, stagger from start), `ShiftY` for drag-down, `ShiftY` for drag-up, label cache uppercasing + tracking, per-note pill dash colours, `+N` overflow marker, expanded note placeholder + size-from-settings, drag-lifted render item.

#### 4b. WinUI 3 surface

| File | Mac source | Notes |
|---|---|---|
| `NotyWin.App/Deck/DeckPainter.cs` | `DeckViews.swift` body (all `View` structs) | Win2D painter. Draws pill, tab, chip-tab, empty tab, more tab, plus button, cog button, edge spine, note preview. Uses the 8-colour palette (ARGB int → `Color.FromArgb`). The tab label rotates 90° (mirror of SwiftUI's `.rotationEffect(.degrees(90))`) using a matrix transform. |
| `NotyWin.App/Deck/DeckView.cs` | `DeckRootView` (SwiftUI hosting) | `UserControl` that hosts a Win2D `CanvasControl`. Repaints on every frame; hit-tests on `PointerMoved` / `PointerPressed`; emits `ItemPressed`, `PointerMovedOnPanel`, `PointerEntered`, `PointerExited` events. The host (DeckController) wires those into the state machine. |
| `DeckWindow.Host(FrameworkElement)` | `NSHostingView` (SwiftUI hosting) | Uses `DesktopWindowXamlSource` + `InitializeWithWindow` to embed WinUI 3 content inside the existing Win32 HWND. |

**What paints identically to the macOS app:**
- Pill: rounded rect background + 14-dash cap, secondary colour for the placeholder dashes (per-note colours are wired to the store but the painter currently uses the secondary colour — minor visual gap, see below).
- Tabs: 11pt rounded-on-the-outward-side shape, paper colour fill, lean 3° (currently the lean is not applied at paint time — see below), rotated label in the strip.
- Empty / More tabs: secondary text on the same shape, slightly different corner radius.
- Plus / Cog buttons: filled circles.
- Edge spine: 1px dashed line at the screen-edge side of the deck.
- Note preview: rounded card on the off-deck side, paper colour, ink text.

**Visual gaps inside step 4 (intentional, scoped):**
- Tab lean (3°) is not yet applied to the paint transform.
- Per-note dash colours in the pill are still placeholders (the painter reads `note.Palette` for tabs but the pill loop uses a fixed count + secondary colour).
- Tab drag (lift + spring) and the long-press hover preview are wired in the pure view model but the WinUI 3 `DeckView` does not yet translate gestures into state changes — step 5 work.
- The expanded note editor (open note in a panel) is not yet painted.

### Translation map (macOS → Win32 / WinUI 3)

| macOS | Windows |
|---|---|
| `NSPanel` `.borderless, .nonactivatingPanel` | `WS_POPUP` + `WS_EX_NOACTIVATE` |
| `.statusBar` level | `HWND_TOPMOST` |
| `.floating` level | `HWND_TOP` |
| `.canJoinAllSpaces + .fullScreenAuxiliary` | inherent to TOPMOST (no spaces) |
| `.ignoresCycle` | `WS_EX_TOOLWINDOW` |
| `canBecomeKey=true, canBecomeMain=false` | `WS_EX_NOACTIVATE` + `ShowWindow(SW_SHOWNOACTIVATE)` |
| `NSHostingView` | Per-display `Microsoft.UI.Xaml.Window` (each creates its own `WindowsXamlManager`) |
| SwiftUI `View.body` returns `some View` | WinUI 3 `FrameworkElement` (UserControl / CanvasControl) |
| SwiftUI `ZStack` for shingle | Win2D `CanvasDrawingSession.FillGeometry` in declaration order (lap from overdraw) |
| SwiftUI `.rotationEffect(.degrees(90))` | Win2D `Matrix3x2.CreateRotation(90, ...)` |
| SwiftUI `withAnimation { ... }` | Win2D `CanvasControl.Invalidate()` driven by `RevealProgressTracker` (caller-driven) |
| SwiftUI `.onHover` | WinUI 3 `UIElement.PointerEntered/Exited` |
| SwiftUI `DragGesture` | WinUI 3 `ManipulationMode` + `ManipulationDelta` (step 5) |
| SwiftUI `Color` | `Windows.UI.Color` + `Microsoft.Graphics.Canvas` `Color` |
| SwiftUI `RoundedRectangle` shape | Win2D `CanvasGeometry.CreateRoundedRectangle` for all four corners, custom `CanvasPathBuilder` for selective corner rounding |
| SwiftUI `StrokeStyle(dash:)` | Win2D `CanvasStrokeStyle.CustomDashStyle` |
| SwiftUI `systemFont(ofSize:weight:)` | `CanvasTextFormat { FontFamily = "Segoe UI", FontSize, FontWeight }` (WinUI 3 doesn't expose `FontWeights.SemiBold` reliably, so we use the raw struct) |
| SwiftUI `Image(systemName: "plus")` | Win2D text glyph (we use `"+"` and `"⚙"` for now) |
| `NSEvent.mouseLocation` (bottom-up Y) | `GetCursorPos` → top-down Y (flip applied in `DeckFrame`) |
| `NSEvent.pressedMouseButtons & 1` | `GetAsyncKeyState(VK_LBUTTON) & 0x8000` (step 5) |
| Carbon `RegisterEventHotKey` | `RegisterHotKey` (step 5) |
| `NSCursor.closedHand` | `SetCursor(LoadCursor(NULL, IDC_SIZEALL))` (step 5) |

---

## Deviations from the original (Windows-specific)

The user's request was to record anything outside the original. These are
**intentional divergences**, scoped tightly, and documented so the upstream
PR conversation can address them if/when a PR happens.

### 1. DPAPI-wrapped AES key (security improvement)

- **Original** (`Sources/Core.swift`): 32-byte AES key written as a raw file
  with `0o600` permissions next to the database.
- **Windows**: raw file is readable by anything running as the user. Wrap the
  key with `ProtectedData.Protect(DataProtectionScope.CurrentUser)` and write
  the wrapped form to `note.key.dpapi`. Wire format unchanged.
- **Why**: defence in depth without breaking round-trips.
- **If upstream rejects**: feature-flag with `NOTY_PLAIN_KEY_FILE=1`; reading
  a raw file also still works.

### 2. `Meta` modifier = Windows key (no `Win` in the Mac default)

- **Original**: every `Shortcut` uses `modifiers: cmd | ...`.
- **Our model**: `KeyModifiers.Meta` → `MOD_WIN` on Windows registration,
  `cmd` on macOS. Defaults match exactly (`Alt+Meta+N` ≡ `Alt+Win+N`).

### 3. `AppLanguage` removed (no Windows equivalent)

- No Win32 API parity; the macOS behaviour is essentially "honour the system
  setting", which Windows already does per-user.

### 4. `Ink.face` font system simplified

- The macOS app filters nine named faces via `NSFont(name:size:) != nil`.
- We ship with a hand-picked subset (Noteworthy + Segoe UI system default)
  and skip the Win32 font enumeration until the editor needs it.

### 5. `SortableStringID` ordering re-densified

- Same as Swift: `Reorder` rewrites 0..n-1 on every drag.

### 6. AES-GCM `tagSizeInBytes` explicit

- Passes 16 explicitly to suppress `SYSLIB0053` obsolete warning. Bytes
  on the wire are identical.

### 7. `Microsoft.Data.Sqlite` over `sqlite3` C API

- Same SQL, same schema, same migrations. ADO.NET bindings instead of
  `sqlite3_bind_*` walks.

### 8. `Color` (SwiftUI) → ARGB int

- Portable colour type in the Models lib. Painter converts at draw time.

### 9. `RelativeDateTimeFormatter` not ported

- Used by the Library window; will be a `CultureInfo`-aware helper when
  the Library ships.

### 10. JSON settings file (unpackaged build)

- `UserDefaults.standard` is the macOS app's source. Win32 unpackaged has
  no `ApplicationData`. Single file at `%LocalAppData%\Noty\settings.json`.

### 11. Single `CanvasControl` for the whole deck (new)

- The macOS app composes SwiftUI shapes in a `ZStack`. WinUI 3's shape
  primitives don't compose as cheaply, and the shingle lap relies on
  declaration order with a per-child rotation. We draw the entire deck
  in one `CanvasDrawingSession` pass using the same declaration order
  (later items paint on top of earlier ones — the same lap the SwiftUI
  ZStack produces).
- **Tradeoff**: no automatic accessibility tree. The SwiftUI app also
  has no accessibility beyond the right-click menu, so this is parity.
- **Why**: avoids the cost of one `UserControl` per tab (lean transform,
  rotated label, shadow) and keeps the lap-bleed precise.

### 12. Win2D font weight: `FontWeight` struct (new)

- WinUI 3 doesn't reliably expose `FontWeights.SemiBold` in this version,
  so we use `new FontWeight { Weight = 600 }` (the OpenType `usWeightClass`
  value for SemiBold). Visually identical to `FontWeights.SemiBold` when
  that helper is present.

### 13. Transparent helper HWND for mouse input (new)

- The macOS app uses `NSPanel`'s built-in pointer tracking. WinUI 3's
  XAML composition routes mouse to a separate input site, so neither
  `SetWindowLongPtr(GWLP_WNDPROC)` nor `WH_MOUSE_LL` worked reliably.
  A `WS_EX_TRANSPARENT` helper window, subclassed with `SetWindowSubclass`
  from ComCtl32, receives `WM_MOUSEMOVE` only when the cursor is over
  the right edge — no system-wide hook overhead.

### 14. Fan panel narrower than Expanded (new)

- The macOS app uses the same panel width for both Fan and Expanded to
  prevent the deck from resizing. On Windows, the Fan panel uses
  `FanWidth` (50pt) — just wide enough for tab edges — while Expanded
  uses `ExpandedWidth(noteWidth)` (382+ pt) for the open note. This
  avoids the fan panel being 482px wide, which pushed the cursor far
  from the screen edge and broke hot-zone exit detection.

---

### Step 5 — Visual polish + interaction wiring

Five small gaps inside step 4 closed this step. Pixel parity with the macOS
app improves visibly for the resting pill, the tabs, and the open note.

- **Per-note pill dashes.** `RenderItem.Pill` now carries
  `DashColors: IReadOnlyList<int>` (per-note `Palette.DashArgb`) and
  `PillOverflow: bool`. `DeckPainter.PaintPill` reads them and draws up to
  `MaxDashes` real coloured dashes, with one secondary-coloured dash for the
  `+N` overflow indicator. Empty deck falls back to a single secondary
  dash, same as Swift.
- **Tab lean (3°).** `DeckPainter.PaintTab` now applies
  `Matrix3x2.CreateRotation(DeckGeom.Lean(true) * π/180)` around the
  tab's edge-anchor, then chains the 90° label rotation and the
  bleed offset. The previous code rotated the label but never leaned the
  tab.
- **Drag-lift scale (1.04×).** When `RenderItem.Lifted` is true, the
  painter scales the tab by 1.04 around the edge-anchor. The view model
  already sets `Lifted` from `RevealProgressTracker.DraggedNoteId`; the
  painter now honours it.
- **Open / hover shadow opacity.** `PaintTab` reads
  `r.Lifted ? 0.42 : (r.IsOpen || r.Hovering ? 0.32 : 0.22)` and
  `DrawGeometry` with that alpha — matches the SwiftUI shadow opacity
  ladder.
- **Context menu on right-click.** `DeckView.RightTapped` raises
  `TabRightClicked` for the hit tab. `DeckController.OnTabRightClicked`
  builds a WinUI `MenuFlyout` with **Pin/Unpin**, **Archive**, **Cycle
  color**, separator, **Delete** — the same five items as
  `noteContextMenu` in `DeckViews.swift:758-767`.
- **Click → state machine wiring.** `DeckController.OnItemPressed`
  handles the click on every paint item kind: Tab/ChipTab expands
  (or collapses if already open), EmptyTab / PlusButton creates a
  note and expands it, MoreTab / CogButton are no-ops until step 6.
  `OnExpand` sets `View.Reveal.ExpandedNoteId` so the open note
  renders; `Apply` clears it on every non-Expanded transition and
  calls `View.Refresh()` so the next paint pass uses the new state.
- **Expanded note placeholder.** Until the full Markdown editor ships,
  `DeckViewModel.Render` emits a `RenderItemKind.ExpandedNote` for the
  open note, sized from `Settings.FloatingNoteWidth/Height`. The
  painter draws a paper-coloured rounded rect with the title in
  semi-bold and the body via `CanvasTextLayout`. Identical paper /
  ink colour and 8 pt corner radius as the preview card; the body
  text uses a 0x66-alpha dimmed ink so an empty note reads as "(empty)"
  the same way the SwiftUI editor does.

5 new xUnit tests added (114/114 total).

### Step 6a — Runtime glue (runnable build)

First build that actually launches. No UI polish, no new features; just the
minimum scaffolding so `dotnet run` shows the per-display deck HWNDs.

- **Namespace cleanup.** `NotyWin_App` → `NotyWin.App` across `App.xaml(.cs)`,
  `MainWindow.xaml(.cs)`, and the removed template `MainPage`. Same root
  namespace as the rest of the project.
- **Windows App Runtime bootstrap.** `App.OnLaunched` calls
  `Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.Initialize(0x00010007)`
  before any WinUI 3 type is touched. Without this, an unpackaged WinUI 3
  app fails at the first `new MainWindow()` with `COMException 0x80004005`.
- **Service graph.** `IService` is a `record` aggregating `JsonSettingsStore`,
  `SqliteNotePersistence`, `NoteList`, and `DeckManager`. `App.OnLaunched`
  builds the full graph in `LocalApplicationData\Noty\` (settings.json,
  notes.db, note.key.dpapi) and exposes it on `App.Services`.
- **MainWindow becomes a status window.** Stripped the template's Frame
  and TitleBar. Now a 5-row grid: title, "Displays: ...", "Notes: ...",
  one-line usage hint.
- **DisplayChangeWatcher → DeckManager refresh.** Hot-plug now calls
  `manager.RefreshDisplays()` and updates the status window.
- **Per-display decks are shown.** `foreach (var d in manager.Decks.Values)
  d.Window.Show();` — the Win32 deck HWNDs are now on screen.
- **Note persistence is auto-wired.** `PersistOnChange` subscribes to
  `NoteList`; every mutation hits SQLite immediately. (Editor autosave
  is a step 7 concern.)

`dotnet build` produces `NotyWin.App.exe` (162 KB). Run with
`dotnet run --project windows\src\NotyWin.App` from the repo root.

### Step 6b — First runnable build (per-display WinUI 3 Window)

- **Pivoted from `DesktopWindowXamlSource`** to per-display
  `Microsoft.UI.Xaml.Window` instances. The XAML island approach failed
  because only one `WindowsXamlManager` can exist per thread, and each
  display deck needed its own. Per-window content works because each
  `Window` creates its own `WindowsXamlManager` internally.
- **`OverlappedPresenter`** for borderless chrome: `SetBorderAndTitleBar(false, false)`
  drops the title bar while keeping the window moveable.

### Step 6c — Borderless chrome + drop Hide effect

- **`WS_POPUP` style swap** in the DeckWindow constructor after
  `SetBorderAndTitleBar(true, false)`. The OverlappedPresenter alone
  reserves 100+ pt for the close button; `WS_POPUP` gives a true
  borderless pill.
- **Hide effect is a no-op on Windows.** macOS hides the panel to
  re-trigger from the menu bar; Windows has no equivalent, so the pill
  is always visible.
- **WndProc subclass** via `SetWindowLongPtr(GWLP_WNDPROC)` attempted
  for mouse events. Failed: WinUI 3's XAML composition routes mouse
  to a separate input site, so the subclass never received
  `WM_MOUSEMOVE`.

### Step 6d — WH_MOUSE_LL global hook

- **`WH_MOUSE_LL` hook** installed in the DeckWindow constructor.
  Fires on every mouse event system-wide; hit-tests against the
  deck's screen rect. Worked but had two problems: (a) slowed the
  entire system to a crawl, (b) `CallWindowProc` return type mismatch
  fixed from `int` to `IntPtr`.
- **`ShowWindow(SW_SHOWNOACTIVATE)`** for the deck window to avoid
  stealing focus from the foreground app.

### Step 6e — Fix deck input + rendering pipeline

- **Replaced `WH_MOUSE_LL`** with per-window `SetWindowSubclass` on a
  transparent helper HWND (`WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT`).
  The helper covers the right edge of the screen and receives
  `WM_MOUSEMOVE` only when the cursor is over it — no system-wide
  overhead.
- **Missing `ShowFan` effect** in state machine `OnPointerEntered`
  transition. The panel now resizes to fan dimensions on hover.
- **ViewModel wiring** — `WireViewModel()` called after `Notes`/`Settings`
  are assigned on `DeckController`. Previously `View.ViewModel` was null
  at construction, causing `OnDraw` to skip everything.
- **`DispatcherQueue` marshaling** for idle timer and exit-work timer
  callbacks that fire on thread pool threads. Without this,
  `MoveAndResize` and `CanvasControl.Invalidate` crashed or were
  ignored.
- **Coordinate-based enter/exit detection** (`lx >= -2`) replaces
  unreliable `WM_MOUSELEAVE` on `WS_EX_TRANSPARENT` windows.
- **Fan panel narrow** — uses `FanWidth` (50pt) instead of
  `ExpandedWidth(noteWidth)` (482pt). The fan is a strip of tab edges,
  not the full note width.
- **`ShowPill` effect** added to all Rest transitions so the panel
  shrinks back to pill size on collapse.
- **`WM_SETCURSOR` handler** on the helper window to force arrow cursor
  (prevents horizontal-resize cursor on the `WS_POPUP` helper).
- **Updated test** `FanAndExpanded_SamePanelSize` →
  `FanPanel_IsNarrowerThanExpanded` to reflect the narrower fan.

### Step 6f — Deck input + rendering, off the helper HWND

Superseded deviation #13. The `WS_EX_TRANSPARENT` helper HWND worked for
detection but swallowed every click in an 80 px full-height strip and only
ever existed on the primary monitor, so it was removed.

- **Subclass the deck HWND itself** (`SetWindowSubclass`) instead of a
  helper. `WM_NCHITTEST` answers `HTCLIENT` over a drawn item
  (`InteractiveFilter` → `View.HitAt`) and `HTTRANSPARENT` elsewhere, so
  clicks on blank panel fall through to the app underneath — the Win32
  equivalent of the macOS `hitTest`-returns-nil behaviour. `WM_MOUSEMOVE`
  / `WM_LBUTTONDOWN` / `WM_RBUTTONDOWN` are forwarded to the controller,
  then passed on to `DefSubclassProc` so the XAML island still sees them.
- **Pointer enter/exit by polling** `GetCursorPos` on a 40 ms timer. On
  leaving the panel a 150 ms debounced re-check against
  `HotZone.ForPanel` confirms the cursor is really outside the hot zone
  before folding — the same approach the macOS app takes with
  `NSEvent.mouseLocation`, because the hit region changes shape on every
  state change and event-driven enter/exit fires spuriously mid-resize.
- **Non-activating while closed.** `WS_EX_NOACTIVATE` is set at
  construction; `SetAcceptsActivation(bool)` clears it for the editor.
- **Pill/fan gating fixed** in `DeckViewModel.Render`: `fanVisible =
  panelHeight > lay.StackHeight` (dropped a leftover `|| true` debug
  override), the pill item is emitted only when `!fanVisible`, and
  `PillVisible`/`FanVisible` are returned accordingly — the fan replaces
  the pill exactly as the SwiftUI ZStack swaps `PillView` for `FanColumn`.
- **`DeckLog`** (gated by `NOTY_DEBUG_DECK=1`) appends to
  `%LocalAppData%\Noty\deck.log`; `PollTick`/`Relayout` are wrapped so a
  throw is logged rather than taking the process down.
- **Tests**: split the empty-deck test into rest vs fan variants and
  render the pill assertions at `RestHeight(n)`; removed the empty
  `NotyWin.Geometry.Tests/UnitTest1.cs` stub. 114/114 still pass.

### Step 7 — Note editor (real text input + autosave)

The open note is now a real editable surface. Win2D cannot take keyboard
input, so the sheet is XAML overlaid on the deck canvas, mirroring
`NoteEditorView` in `Sources/NoteEditor.swift`.

- **New `NotyWin.App/Deck/NoteEditorControl.cs`** (`UserControl`):
  - **Sheet** — paper `Border` rounded on the deck-facing side and square
    toward the screen edge (`CornerRadius(14,0,0,14)` on the right), a
    header (derived title, "saved Xs ago", pin toggle), a borderless
    transparent multi-line `TextBox` body (spellcheck on, ink foreground,
    font size from settings), and a footer (8 colour swatches +
    Archive / Delete / Close).
  - **Autosave** — a 250 ms `DispatcherQueueTimer` debounce after typing
    stops commits via `NoteList.UpdateBody`; `Flush()` commits immediately
    on collapse or note-switch. Matches macOS `scheduleSave`/`flush`.
  - **`SetNote`** reloads the body only when the note id changes, so a
    re-sync never disturbs the caret or undo buffer; `RefreshHeader`
    updates title / saved-label / pin cheaply on each autosave without
    rebuilding the whole sheet.
- **`DeckView` restructured** into a `Grid`: the `CanvasControl` now
  *stretches* to the window (the explicit `Width`/`Height` assignment was
  dropped — it fought the layout pass on resize), and an overlay `Canvas`
  carries the editor. `_panelW`/`_panelH` (set by `Resize`) are the single
  source for both layout and hit-testing. `SyncEditor(note, onRight,
  fontSize)` positions and shows the editor from the frame's
  `ExpandedNote` item, or hides it and flushes pending edits on `null`.
- **`DeckPainter`**: removed the `PaintExpanded` placeholder. The
  `ExpandedNote` item stays in the frame (so its rect is hit-testable and
  clicks reach the XAML editor) but is no longer painted.
- **`DeckController`**: wires `View.Editor.Notes` + `OnRequestCollapse`,
  and subscribes to the `NoteList` so tabs repaint and the editor re-syncs
  on any mutation. `Apply()` now toggles `SetAcceptsActivation(expanded)`
  and, on the transition into `Expanded`, calls `ActivateForInput()`
  (`SetForegroundWindow`) so the `TextBox` can take the keyboard, then
  `SyncEditorIfExpanded()`.
- **Persistence fix**: `PersistOnChange` now diffs known ids and deletes
  removed notes from SQLite (deleted notes previously lingered in the
  database); undo re-adds and re-upserts.
- **Deviation — focus/activation**: macOS keeps the panel non-activating
  and calls `makeFirstResponder` inside it. Win32 cannot give keyboard
  focus to a `WS_EX_NOACTIVATE` window, so opening a note clears that
  style and foregrounds the panel; closing restores it. Hovering and
  clicking the closed deck still never steal focus.

Build 0 errors; tests 114/114. Editor is UI-only (no unit coverage);
verified by build + test per the current workflow.

## Gaps (step 6b+ work)

### Critical (visible app)

- **Hover-preview card timing** (`.openOnHover` / `.tabPreview` with
  delays). The card paint method exists, but the host does not yet
  schedule a delayed `Reveal.PreviewNoteId = id` after the configured
  delay.
- **Drag + reorder gesture.** `RevealProgressTracker.DraggedNoteId` is
  read by the view model; the host does not yet translate a WinUI
  `ManipulationMode` into `OnDragStarted` + the per-frame `DragDy` +
  `OnDragEnded`.
- **Expanded note editor.** The placeholder paints title + body
  without styling. The full Markdown-as-you-type editor, find bar,
  autosave, checkbox tasks, and per-paragraph direction are all
  still to come (`Sources/NoteEditor.swift` is ~700 lines).
- **Library / All Notes window** (`Sources/LibraryWindow.swift`):
  search across bodies, editable detail pane, archive/restore.
  The cog / more-tab click handlers currently no-op on it.
- **Settings window** XAML UI over the existing `SettingsSnapshot`
  record. The cog click is a no-op until it ships.

### Behaviour (continues step 2's hooks)

- **Pointer enter/exit on the panel.** Implemented via a transparent helper
  HWND (`WS_EX_TRANSPARENT`) covering the right edge, subclassed with
  `SetWindowSubclass` from ComCtl32. Coordinate-based enter/exit detection
  (`lx >= -2`) replaces unreliable `WM_MOUSELEAVE` on transparent windows.
  Previous attempts: `SetWindowLongPtr(GWLP_WNDPROC)` subclass failed
  because WinUI 3 XAML composition routes mouse to a separate input site;
  `WH_MOUSE_LL` global hook worked but slowed the entire system to a crawl.
- **Pill drag** (`beginPillDrag`). `SetCapture` + `WM_MOUSEMOVE`/`WM_LBUTTONUP`
  pump, hit-tests against current display.
- **Outside-click on the floating note** to dismiss.
- **Pointer-position polling for hot-zone exit.** Deferred 150 ms check
  that confirms the cursor is outside the hot zone.

### System integrations

- **Global hotkeys.** `RegisterHotKey` + `WM_HOTKEY`.
- **`noty://` URL scheme.** Register in `HKCR\noty\shell\open\command`.
- **Launch at login.** `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
  (unpackaged).
- **Auto-update.** Squirrel.Windows or custom GitHub releases manifest.
- **MSIX packaging + Authenticode signing.**
- **Context menu** (right-click pill): WinUI 3 `MenuFlyout` on the
  `DeckView.PointerPressed` path.
- **Tray icon** (`Shell_NotifyIcon`) to keep the app alive while the
  main window is hidden.
- **Settings window** (Settings UI — the snapshot is already
  readable/writable; need the XAML).

### Tests still to write

- Window-frame state: confirm `SetWindowPos` gets the right rect for each state.
- `DeckManager` rebuild against display set changes (mock the display snapshot).
- `DisplayEnumerator` integration (run on the actual host machine in CI?).
- `DeckFrame` for multi-display edge cases (negative-coordinate secondary).
- Painter-level snapshot tests (render to `CanvasBitmap` and diff).

---

## Files added

```
windows/
  NotyWin.slnx
  PROGRESS.md
  src/
    NotyWin.App/                    # WinUI 3 MVVM (scaffolded, no UI yet)
      Deck/
        DeckWindow.cs               # Win32 panel + XAML island host
        DeckController.cs           # state machine + idle timer
        DeckManager.cs              # one per display
        DisplayEnumerator.cs        # EnumDisplayMonitors
        DisplayChangeWatcher.cs     # WM_DISPLAYCHANGE
        DeckPainter.cs              # Win2D painter
        DeckView.cs                 # UserControl + CanvasControl
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
    NotyWin.Rendering/              # net10.0-windows (pure; System.Windows.Forms for measurer)
      ITextMeasurer.cs             # GdiTextMeasurer + cache
      DeckViewModel.cs             # render pass
      HitTest.cs
    NotyWin.Storage/                # net10.0-windows (SQLite + AES + DPAPI)
      NoteCipher.cs
      SqliteNotePersistence.cs
      JsonSettingsStore.cs
  tests/
    NotyWin.Geometry.Tests/         # 44 xUnit tests
    NotyWin.Models.Tests/           # 27 xUnit tests
    NotyWin.Rendering.Tests/        # 20 xUnit tests
    NotyWin.Storage.Tests/          # 18 xUnit tests
```

---

## Commits on `win`

| Hash | Subject |
|---|---|
| `b0bd314` | scaffold WinUI 3 solution + DeckGeometry parity port |
| `400317b` | deck state machine + multi-display + hot zone |
| `c49b13b` | settings + storage + note model (step 3) |
| (next) | rendering lib + WinUI 3 visible views (step 4) |
| ... | steps 4-5 (rendering, visual polish) |
| `08cecf6` | step 6a runtime glue (runnable build) |
| `a0a93f2` | step 6b first runnable build (per-display WinUI 3 Window) |
| `fd7c5eb` | step 6c fix borderless chrome + drop Hide effect |
| `79b0b75` | step 6d fix WndProc + drop focus steal |
| `8690a67` | step 6e fix deck input + rendering pipeline |
