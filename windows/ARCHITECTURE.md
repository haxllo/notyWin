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
backend selector, because this app does not use a system tray or native menu.
That keeps the optional muda/comctl32 integration out of the import table. The
vendored backend contains the upstream no-muda Windows cfg correction required
by Slint 1.17.1; the edge subclass APIs used for hit testing are loaded with
`GetProcAddress` when the app starts. The foreground timer still checks for
external focus when expanded content must dismiss, but display/fullscreen
reconciliation refreshes the UI only when its display or fullscreen snapshot
changes; the initial fullscreen snapshot is taken before that timer starts.

## Window strategy

One Slint window is maintained per selected display. Rest uses a narrow panel
whose origin is exactly on the selected edge. Fan increases the panel width to
the tab width. Expanded grows the panel toward the screen interior while the
selected tab remains the gutter. `SetWindowPos` is used on Windows so the
physical pixel frame is updated atomically instead of approximating an edge
with margins.

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
Settings are a small versioned JSON file, and the deleted Windows prototype's
data is copied into the new directory without mutating the source. There is no
account, telemetry, cloud sync, or required network service.

The editor stores Markdown source as plain text. The preview projection
normalizes headings and task markers for Slint's `StyledText`; it does not
claim to provide rich, span-level Markdown editing.
