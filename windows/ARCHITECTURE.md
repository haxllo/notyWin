# Architecture

## Boundaries

```text
src/main.rs
  ├── model.rs       note/settings types, palette, task helpers
  ├── storage.rs     SQLite schema, encrypted bodies, JSON settings
  ├── deck.rs        pure geometry and state transitions
  ├── platform/      Win32 monitors, DPI, window styles, hotkeys
  └── ui.rs          Slint model conversion and event wiring

ui/noty.slint        visual tree only; no persistence or Win32 calls
```

`Sources/` remains the macOS reference implementation and is not imported or
modified by this project.

## Verification boundary

The non-Windows build supplies a deterministic fallback so the model, storage,
geometry, and controller tests can run in this workspace. Production behavior
is Windows-only. GNU cross-target compilation verifies Rust/API integration,
but MSVC linking, actual HWND behavior, DPAPI, monitor enumeration, input
activation, and visual fidelity still require a Windows 10/11 x64 session.

## Ownership

`AppState` owns the in-memory note list and settings. It is the only place that
decides whether a note is active, archived, deleted, or expanded. `Store` owns
SQLite and is called through small synchronous mutations on the UI thread only
for short prepared statements; body writes are coalesced with a 250 ms timer.
Editor events that do not change the normalized body are ignored, and the
timer refreshes the projected save status after both successful and failed
writes. Failed body IDs remain queued so a transient persistence error can be
retried without losing the in-memory edit.
Structural mutations are written immediately, and shutdown flushes only pending
note IDs rather than replaying the entire in-memory list. Windows also holds a
per-user-session named mutex, so a second process exits before it can load a
stale snapshot and overwrite newer notes.
The list never reloads the database during an interaction.

`deck.rs` contains no Slint or Win32 types. It turns a display work area,
scale, edge, and note list into a `PanelGeometry`, making positioning and the
shingle guard rail unit-testable without a desktop session.

`platform` is the only module allowed to call `windows-sys`. On Windows it
acquires the single-instance mutex, enumerates `rcWork`, resolves stable
monitor IDs from `MONITORINFOEXW.szDevice`, reads per-monitor DPI, removes
window chrome, applies the non-activating tool-window styles, registers
`RegisterHotKey`, and keeps the edge window above normal application windows.
The display watcher owns a hidden popup thread and joins it on shutdown;
fullscreen state is also reconciled periodically because a foreground-window
change is not guaranteed to emit a display notification. The non-Windows module
supplies one test display and leaves native styling untouched.

The Slint winit backend is configured directly rather than through the default
backend selector, with its `muda` feature enabled for native popup menus. The
app does not add a system-tray UI. The vendored backend keeps the repository's
Slint 1.17.1 Windows compatibility patch, and the repository-vendored Muda
0.19.3 keeps its native popup implementation while loading subclass APIs with
`GetProcAddress` so they do not become static `comctl32.dll` imports. The
foreground timer still checks for
external focus when expanded content must dismiss, but display/fullscreen
reconciliation refreshes the UI only when its display or fullscreen snapshot
changes; the initial fullscreen snapshot is taken before that timer starts.

## Window strategy

One Slint window is maintained per selected display. Rest uses a narrow panel
whose origin is exactly on the selected edge. Fan increases the panel width to
the tab width and, when previews are enabled, reserves the preview
card and gap before any tab is hovered. Expanded grows the panel toward the
screen interior while the selected tab remains the gutter. `SetWindowPos` is
used on Windows so the physical pixel frame is updated atomically instead of
approximating an edge with margins. Because the reserved fan frame is already
at its preview width, changing hover visibility does not change its edge
position.
Fan tabs and the `+N`, new-note, and settings controls are flush with the
outward-facing monitor edge; the single interior gutter is retained for the
panel’s breathing room.
Each display window subscribes to its own preview changes so the native
hit-test regions are refreshed when a preview appears or hides on a secondary
monitor as well as the primary one. The preview callback updates only that
window’s native hit-test state; it does not rebuild note models or reconfigure
unrelated display windows during pointer movement.

Fan context menus use Slint's native Muda backend. The Win32 hit-test subclass
records `WM_ENTERMENULOOP` and `WM_EXITMENULOOP` depth, including popup menus
whose `wParam` is false, so hover-collapse timers and delayed hover-open actions
cannot replace the fan item tree while a menu is tracking. Menu actions still
dispatch through the existing Slint callbacks after the native loop exits.

The window is frameless, a tool window, excluded from Alt-Tab, and configured
not to activate while it is only a pill/fan. The editor requests activation
only after a note is selected. Blank fan pixels are returned as `HTTRANSPARENT`
by a Win32 subclass so the underlying application can receive input; accepted
regions return `HTCLIENT`, and `WM_MOUSEACTIVATE` returns `MA_NOACTIVATE` for
the nonactivating tool window. The Slint tree keeps one persistent deck hover
ancestor around the rest, fan, and control content, while a narrow DPI-scaled
edge bridge overlaps the outer tab edge so direct and diagonal pill-to-tab
handoffs do not lose the click gesture. These are implementation and host-test
boundaries, not proof of real mixed-DPI HWND behavior; transformed tab bounds
and edge pass-through still require Windows validation.

## Persistence

SQLite uses a WAL journal, prepared statements, and an indexed `archived,
sort_order` query. The body column is an AES-GCM blob; on Windows the per-user
key is protected with DPAPI before it is stored beside the database. Legacy
plaintext key files are accepted for migration and rewritten in protected
form. A corrupt database is quarantined, while an unreadable body remains
recoverable metadata and is not overwritten until the body is replaced.
Note saves run in one transaction: an `UPDATE ... WHERE id` handles existing
rows, and a fallback `INSERT` handles new rows without requiring the exact
primary-key/unique constraint needed by SQLite's `ON CONFLICT(id)` syntax.
Settings are a small versioned JSON file, and the deleted Windows prototype's
data is copied into the new directory without mutating the source. There is no
account, telemetry, cloud sync, or required network service.

The editor stores Markdown source as plain text. The preview projection
normalizes headings and task markers for Slint's `StyledText`; it does not
claim to provide rich, span-level Markdown editing.
