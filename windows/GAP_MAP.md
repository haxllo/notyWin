# NotyWin Gap Map — Swift vs Windows

## Status Legend
- ✅ Complete — fully implemented and working
- ⚠️ Partial — implemented but incomplete or not wired
- ❌ Missing — not started

---

## Core Architecture

| Feature | Swift | Windows | Status |
|---|---|---|---|
| Accessory app (no dock/taskbar) | `setActivationPolicy(.accessory)` | `WS_EX_TOOLWINDOW` | ✅ |
| Main menu (for edit shortcuts) | Programmatic NSMenu | N/A (Win32 handles natively) | ✅ |
| Service graph | AppDelegate singleton | `IService` record | ✅ |
| Crash logging | stderr | `%LocalAppData%\Noty\crash.log` | ✅ |
| Startup logging | N/A | `%LocalAppData%\Noty\startup.log` | ✅ |

## Data Layer

| Feature | Swift | Windows | Status |
|---|---|---|---|
| SQLite persistence | `Store.swift` (C API) | `SqliteNotePersistence.cs` (ADO.NET) | ✅ |
| AES-GCM body encryption | CryptoKit | `System.Security.Cryptography.AesGcm` | ✅ |
| DPAPI key wrapping | N/A (raw file) | `ProtectedData.Protect` | ✅ |
| Note model | `struct Note` | `record Note` | ✅ |
| NoteList (observable) | `NoteStore` (`@Published`) | `NoteList` (`IObservable`) | ✅ |
| Settings store | `UserDefaults` | `JsonSettingsStore` | ✅ |
| Pending delete (10s undo) | `PendingDelete` + Timer | `PendingDelete` record | ✅ |
| Note CRUD | Full | Full | ✅ |
| Derived title | First line, strip markers | Same logic | ✅ |
| Task progress | `taskProgress` | `Tasks.Progress` | ✅ |
| Color palette (8 colors) | `NoteColor.all` | `NoteColor.All` | ✅ |
| Text direction | `NoteTextDirection` | `NoteTextDirection` | ✅ |

## Deck System

| Feature | Swift | Windows | Status |
|---|---|---|---|
| State machine | `DeckController.setState` | `DeckStateMachine` (pure) | ✅ |
| Multi-display | `DeckManager` | `DeckManager` | ✅ |
| Display enumeration | `NSScreen.screens` | `EnumDisplayMonitors` | ✅ |
| Display change detection | `NSNotification` | `WM_DISPLAYCHANGE` hidden window | ✅ |
| Display targeting (all/main/pinned) | `DisplayTarget` | `DisplaySetResolver` | ✅ |
| Hot zone | `DeckController.hotZone` | `HotZone.ForPanel` | ✅ |
| Borderless panel | `NSPanel(.borderless)` | `WS_POPUP` + `WS_EX_NOACTIVATE` | ✅ |
| Always-on-top | `.statusBar` level | `HWND_TOPMOST` | ✅ |
| Non-activating | `canBecomeKey` override | `WS_EX_NOACTIVATE` toggle | ✅ |
| Click-through (blank regions) | `hitTest` returns nil | `WM_NCHITTEST` → `HTTRANSPARENT` | ✅ |
| Pointer enter/exit polling | `NSEvent.mouseLocation` 120ms | `GetCursorPos` 40ms | ✅ |
| Exit debounce (150ms) | Timer | `System.Threading.Timer` | ✅ |
| Idle watch (120ms poll) | Timer | `System.Timers.Timer` | ✅ |
| Fan idle timeout (4s) | State machine | State machine | ✅ |
| Note idle timeout (60s) | State machine | State machine | ✅ |
| Deck geometry (all metrics) | `DeckGeom` | `DeckGeom` | ✅ |
| Deck layout | `DeckLayout` | `DeckLayout` | ✅ |
| Deck frame | `DeckController.layout` | `DeckFrame.Layout` | ✅ |
| Deck scale (70-180%) | `Settings.deckScale` | `SettingsSnapshot.DeckScale` | ✅ |
| Left/right edge | `deckOnLeftEdge` | `DeckOnLeftEdge` | ✅ |
| Y position ratio | `deckYRatio` | `DeckYRatio` | ✅ |

## Visual Rendering

| Feature | Swift | Windows | Status |
|---|---|---|---|
| Pill (resting state) | SwiftUI `PillView` | Win2D `PaintPill` | ✅ |
| Per-note dash colors | `note.palette.dash` | `DashColors` list | ✅ |
| Pill overflow indicator | `+N` secondary dash | Same | ✅ |
| Fan tabs (labelled) | SwiftUI `VerticalTab` | Win2D `PaintTab` | ✅ |
| Tab lean (3°) | `.rotationEffect` | `Matrix3x2.CreateRotation` | ✅ |
| Tab shadow (state-dependent) | `.shadow()` | `DrawGeometry` alpha | ✅ |
| Tab label (rotated, uppercase) | SwiftUI text | `CanvasTextLayout` + rotation | ✅ |
| Pin indicator dot | Circle | `FillCircle` | ✅ |
| Chip tabs (compact) | `ChipTab` | `PaintChipTab` | ✅ |
| Empty tab | `EmptyTab` | `PaintEmptyTab` | ✅ |
| More tab (+N) | `MoreTab` | `PaintMoreTab` | ✅ |
| Plus button | `PlusButton` | `PaintPlus` | ✅ |
| Cog button | `CogButton` | `PaintCog` | ✅ |
| Edge spine (dashed) | `StrokeStyle(dash:)` | `CanvasStrokeStyle` | ✅ |
| Note preview card | `NotePreviewCard` | `PaintPreview` | ⚠️ Basic — missing task progress, body preview, pin icon |
| Expanded note editor | `NoteEditorView` | `NoteEditorControl` | ✅ |
| Editor paper gradient | `LinearGradient` | Flat paper color | ⚠️ Missing gradient |
| Editor shadow | 28pt radius | Not applied | ⚠️ Missing shadow |
| Editor border | 0.5pt ink@0.07 | 0.5pt black@0.07 | ✅ |
| Editor corner radius | 14pt uneven | 14pt uneven | ✅ |
| Editor gutter | Tab-width strip | Not painted | ⚠️ Missing gutter |

## Note Editor

| Feature | Swift | Windows | Status |
|---|---|---|---|
| RichEditBox body | NSTextView (TextKit 1) | `RichEditBox` (TOM) | ✅ |
| Markdown-as-you-type | 7 regex passes | `EditorStyleEngine` (same 7 passes) | ✅ |
| Heading styling | Bold + size bump | Same | ✅ |
| Bold/Italic/Code/Strike | Full | Full | ✅ |
| Blockquotes | Dim + italic | Same | ✅ |
| Bullets | Dim marker | Same | ✅ |
| Links (underline + click) | Full | Full | ✅ |
| Completed task dimming | Strike + 45% ink | Same | ✅ |
| Task checkboxes (☐/☑) | Inline markers | Same | ✅ |
| Toggle task (Ctrl+T) | `⌘T` | `Ctrl+T` | ✅ |
| Task Enter continuation | Return inserts ☐ | Same | ✅ |
| Find bar | `⌘F` | `Ctrl+F` | ✅ |
| Text direction | Per-paragraph | Per-paragraph | ✅ |
| Autosave (250ms debounce) | `scheduleSave` | `DispatcherQueueTimer` | ✅ |
| Immediate flush | `flush()` | `Flush()` | ✅ |
| Ctrl+click links | `⌘-click` | `Ctrl+click` | ✅ |
| Header (title, saved, pin) | SwiftUI | XAML | ✅ |
| Footer (swatches, buttons) | SwiftUI | XAML | ✅ |
| Color swatches (8) | Circles | Ellipses | ✅ |
| Pin toggle | Button | Button | ✅ |
| Direction menu | Menu | `MenuFlyout` | ✅ |
| Archive/Delete/Close | Buttons | Buttons | ✅ |
| Spell checking | Enabled | Enabled | ✅ |
| IME composition handling | Deferred styling | Deferred styling | ✅ |

## Interactions

| Feature | Swift | Windows | Status |
|---|---|---|---|
| Hover → fan open | Pointer enter | Poll → `PointerEntered` | ✅ |
| Click tab → expand | Tap gesture | `OnLeftButtonDown` → `OnExpand` | ✅ |
| Click tab → collapse (toggle) | Same | Same | ✅ |
| Click empty/plus → create | Same | Same | ✅ |
| Right-click tab → context menu | `noteContextMenu` | `MenuFlyout` | ✅ |
| Context menu: Pin/Archive/Cycle/Delete | Full | Full | ✅ |
| Escape to dismiss | Key handler | ❌ Not wired | ❌ |
| Tab drag to reorder | `DragGesture` | State machine ready, no gesture | ❌ |
| Hover preview card (180ms delay) | `.openOnHover` | `PreviewNoteId` exists, not triggered | ❌ |
| Open-on-hover (400ms delay) | Setting consumed | Setting stored, not consumed | ❌ |
| DeckPillHidden | Setting consumed | Setting stored, not consumed | ❌ |
| Pill drag to reposition | `⌥-drag` | Not implemented | ❌ |
| Note detach to floating | Gutter drag 40pt | Not implemented | ❌ |
| Outside click dismiss | `NSEvent` monitor | Not implemented | ❌ |
| Fan stagger animation (42ms) | Spring + delay | Data model ready, no timer | ❌ |
| Pill reveal animation | Spring | Data model ready, no timer | ❌ |
| Tab hover shadow deepen | `.shadow()` change | `Hovering` flag read | ✅ |
| Tab press scale (0.97×) | `TabPressStyle` | Not implemented | ❌ |
| Plus/Cog hover scale (1.08×) | `.onHover` | Not implemented | ❌ |

## Missing Windows Features

| Feature | Swift Source | Status |
|---|---|---|
| **Settings UI window** | `SettingsWindow.swift` (4 tabs) | ❌ |
| **Library/All Notes window** | `LibraryWindow.swift` | ❌ |
| **Quick Capture** | `QuickCapture.swift` | ❌ |
| **Floating Note** | `FloatingNote.swift` | ❌ |
| **Undo Toast** | `UndoToast.swift` | ❌ |
| **Export/Import** | `ExportImport.swift` | ❌ |
| **Global hotkeys** | `HotKeys.swift` (Carbon) | ❌ |
| **URL scheme** | `noty://` handler | ❌ |
| **System tray icon** | N/A (macOS menu bar) | ❌ |
| **Launch at login** | `SMAppService` | ❌ |
| **Auto-update** | Sparkle | ❌ |
| **Localization** | `L10n.swift` | ❌ |
| **Inno Setup installer** | N/A (DMG on macOS) | ❌ |

## Settings Inventory

| Setting | Swift Key | Windows Key | Status |
|---|---|---|---|
| Deck style | `deckStyle` | `DeckStyle` | ✅ Stored + synced |
| Deck scale | `deckScale` | `DeckScale` | ✅ Stored + synced |
| Deck edge | `deckOnLeftEdge` | `DeckOnLeftEdge` | ✅ Stored + synced |
| Deck Y position | `deckYRatio` | `DeckYRatio` | ✅ Stored + synced |
| Display target | `displayTarget` | `DisplayTarget` | ✅ Stored + synced |
| Edge width | `edgeWidth` | `EdgeWidth` | ✅ Stored + synced |
| Keep deck open | `deckAlwaysShown` | `DeckAlwaysShown` | ✅ Stored + synced |
| Hide pill | `deckPillHidden` | `DeckPillHidden` | ⚠️ Stored, not consumed |
| Hover preview | `tabPreview` | `TabPreview` | ⚠️ Stored, not consumed |
| Hover-to-open | `openOnHover` | `OpenOnHover` | ⚠️ Stored, not consumed |
| Over full-screen | `showOverFullScreen` | `ShowOverFullScreen` | ✅ Stored + synced |
| Launch at login | SMAppService | `LaunchAtLogin` | ❌ No implementation |
| Note font | `noteFontName` | `NoteFontName` | ✅ Stored |
| Note font size | `noteFontSize` | `NoteFontSize` | ✅ Stored + synced |
| Note size | `noteSizeIndex` | `NoteSizeIndex` | ✅ Stored |
| Markdown styling | `markdownStyling` | `MarkdownStyling` | ✅ Stored + synced |
| 13 keyboard shortcuts | `sc*` | `Sc*` | ⚠️ Stored, not registered |
| Check for updates | Sparkle | `CheckForUpdatesAutomatically` | ❌ No implementation |

## Priority Implementation Order

### Phase 1: Fix Existing UI/UX (parity with what's built)
1. Fan stagger animation timer
2. Hover preview card trigger
3. OpenOnHover wiring
4. DeckPillHidden consumption
5. Escape key to dismiss
6. Cog/MoreTab click handlers
7. Note preview card enrichment (task progress, body, pin)
8. Editor visual polish (gradient, shadow, gutter)

### Phase 2: Core Missing Features
1. Undo toast notification
2. Global hotkeys (RegisterHotKey)
3. System tray icon
4. Settings UI window
5. Quick Capture window

### Phase 3: Advanced Features
1. Library/All Notes window
2. Export/Import
3. Floating Note (detach)
4. Tab drag reorder
5. Launch at login
6. URL scheme

### Phase 4: Distribution
1. Inno Setup installer script
2. Final UI/UX audit
