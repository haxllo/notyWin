# Native Windows port progress checkpoint

> This is the durable hand-off for the native Windows replacement. It records
> what is implemented, what was verified in this sandbox, and what still needs
> a real Windows session. It is deliberately not a completion claim.

**Last updated:** 2026-09-07 UTC
**Branch:** `hoplite/koroneia-de38b879`
**Repository:** `haxllo/notyWin`
**Initial native replacement commit:** `fc9f0d8d01720d616b329d11626c8584690893e3`
**Published feature commit:** `7dd34ccc0b5dff6645f789bdee29fdcc6111c8f8`

The old managed WPF/WinUI implementation under `windows/` is deleted. The
replacement is native Rust/Slint/Win32/SQLite. `Sources/` remains untouched.

## Goal and constraints

Recreate the macOS Noty experience as a native Windows edge utility rather than
a Windows-themed redesign. The implementation must not use Electron, a
WebView, Tauri, browser UI, Node.js, .NET, telemetry, accounts, cloud sync, or
a required network connection.

Required product areas are the edge pill, fanned deck, expanded note, library,
settings, Quick Capture, local persistence, encrypted note bodies, autosave,
archive/delete/undo, task handling, keyboard shortcuts, startup behavior,
multi-monitor/DPI behavior, fullscreen suppression, and low idle CPU usage.

## Native project ownership

- `Cargo.toml` / `Cargo.lock` — Rust 2024 dependencies and reproducible lockfile.
- `build.rs` / `ui/noty.slint` — Slint compilation and native visual tree.
- `src/model.rs` — notes, settings, task markers, search, Find, and state data.
- `src/storage.rs` — SQLite schema/migrations, encrypted bodies, DPAPI key
  protection on Windows, legacy migration, atomic file replacement, and recovery.
- `src/deck.rs` — platform-independent deck state and DPI-aware layout.
- `src/ui.rs` — Slint projection, actions, autosave, library/editor/settings,
  Quick Capture, undo, hover lifecycle, and shutdown flushing.
- `src/platform/` — Win32 windows, input, hotkeys, startup, monitors, DPI,
  fullscreen reconciliation, instance guard, and the deterministic test fallback.
- `vendor/i-slint-backend-winit/` — pinned Slint 1.17.1 winit backend with the
  no-muda Windows cfg correction needed by this tray-free app.
- `README.md`, `BUILD.md`, `ARCHITECTURE.md`, `UI_SPEC.md` — user, build,
  ownership, and reference-derived visual contracts.

## Implemented behavior and fixes

### Product and interaction

- Resting edge pill, fanned deck, expanded note, library/all-notes view,
  settings, and Quick Capture are represented in the native UI.
- Editable note source is plain text; Markdown is rendered separately as a
  styled preview because Slint's stock text input is not a rich-span editor.
- Task markers use the reference checkbox behavior, including `Ctrl+T` and
  clickable local task links. HTTP(S)/mailto links use the Windows shell.
- Autosave is coalesced at 250 ms with no unnecessary Save button. Archive,
  delete, undo, search, selection, restored-note ordering, and settings persist.
- Quick Capture preserves deck/library/editor context, supports Enter and
  Shift+Enter, restores the prior foreground window when possible, dismisses
  outside focus, and uses pointer-aware placement and palette colors.
- Native Find supports `Ctrl+F`, inline query editing, case-insensitive counts,
  wraparound navigation, UTF-8-safe source selection, Escape dismissal, and
  controller/Slint state projection.

### Storage and recovery

- `Store::open` quarantines only verified SQLite corruption/not-a-database
  errors; ordinary migration, busy/locked, permission, and disk errors remain
  visible as failures and do not rename the user's database.
- A missing key beside database/WAL/SHM artifacts returns a recovery error
  instead of silently generating a key that cannot decrypt existing notes.
- Schema migration runs under an immediate SQLite transaction, is idempotent,
  stores/checks `PRAGMA user_version`, rejects newer unsupported versions, and
  preserves legacy column backfills.
- New keys and legacy plaintext-key rewraps use atomic replacement. Zero-byte
  legacy bodies load as valid empty notes. Unreadable ciphertext retains its
  original blob until the note is deliberately replaced or deleted.
- Bodies use AES-GCM; Windows key material is protected with current-user
  DPAPI and stored below `%LOCALAPPDATA%\\NotyWin`. Tests use in-memory SQLite.

### Windows/platform foundations

- `build.rs` resolves the Slint file relative to the Cargo package (`ui/noty.slint`);
  the Slint tree compiles with balanced view blocks and explicit tab/control
  hit areas.
- The release executable has no static `comctl32.dll` import. Slint's optional
  muda/tray integration is disabled, the backend's no-muda Windows cfg is
  patched locally, and the hit-test subclass entry points are looked up with
  `LoadLibraryW`/`GetProcAddress` at runtime. Missing exports degrade to the
  default window procedure instead of preventing startup.
- Hover routing uses one persistent Slint deck hover ancestor around the
  rest/fan/control content rather than a redundant tab-hover callback and state.
  A narrow DPI-scaled edge bridge overlaps the outer tab edge, keeping direct
  and diagonal pill-to-tab moves connected while blank fan pixels remain
  pass-through. Accepted native regions return `HTCLIENT`, blank fan pixels
  return `HTTRANSPARENT`, and the nonactivating tool window returns
  `MA_NOACTIVATE`. Entering a tab does not rebuild the Slint models, so it
  cannot consume its click gesture.
- Fan tabs show a preview card immediately on tab entry when `tab_preview` is enabled.
  The card includes the note title, task progress, pin state, and a body
  snippet; a 150 ms handoff grace period keeps it reachable while the pointer
  moves from a tab into the card. Preview width is reserved when the fan opens,
  so preview visibility changes the painted card and hit-test regions on either
  edge without moving the native fan or claiming the blank gap. Fan tabs,
  preview cards, and expanded note surfaces explicitly round only their left
  corners and clip expanded content to that boundary.
  Fan tabs, `+N`, new-note, and settings controls are flush with the outward
  monitor edge while retaining the interior gutter.
  Each per-display window refreshes its native preview hit-test regions when
  its own preview appears or hides without rebuilding the Slint note model or
  reconfiguring unrelated display windows.
  `open_on_hover` suppresses the preview and retains its 450 ms note-opening
  behavior.
  Repeated hover events do not restart an active collapse timer; pill-to-fan,
  transformed-tab, edge-control, and blank fan hit-test cases have host
  coverage.
- Startup visibility/configuration ordering, primary-window hover flags,
  screen-edge placement, 200 ms fan collapse timing, and active-display gating
  were corrected. The fan layout and Slint edge geometry share the same
  dimensions.
- Win32 monitor enumeration, per-monitor DPI awareness, `WM_DPICHANGED`
  refresh routing, borderless tool windows, startup registration, global
  hotkeys, single-instance protection, display watching, fullscreen
  reconciliation, and event-loop lifetime are represented.

## Verification completed in this sandbox

All commands below passed on 2026-09-07:

```text
cargo fmt --manifest-path windows/Cargo.toml --all -- --check
cargo check --manifest-path windows/Cargo.toml
cargo test --manifest-path windows/Cargo.toml       # 54 passed, 0 failed
cargo check --manifest-path windows/Cargo.toml --target x86_64-pc-windows-gnu
git diff --check
cargo build --manifest-path windows/Cargo.toml --release --target x86_64-pc-windows-gnu
objdump -p windows/target/x86_64-pc-windows-gnu/release/noty-win.exe # no comctl32.dll import
```

The current GNU release artifact is an 11,360,768-byte stripped PE32+ x64 GUI
executable at `windows/target/x86_64-pc-windows-gnu/release/noty-win.exe`.
The import-table audit reports no static `comctl32.dll` dependency. This proves
the GNU target can link a PE artifact and avoids the reported loader failure;
it does not prove MSVC compatibility or Windows runtime behavior.

The editor save lifecycle now ignores redundant normalized body events, avoids
writing the editor value back during an unchanged refresh, and refreshes the
visible `Saved`/`Couldn’t save` status after each debounced flush. Failed body
IDs remain queued for retry. Host regressions cover unchanged events,
successful coalesced saves, retryable failures, and editing a legacy table
without an `id` uniqueness constraint. The Slint timer callback, SQLite,
encryption, and status behavior still need authoritative Windows runtime
validation.

## Known limitations and open verification

No Windows 10/11 session or MSVC toolchain is available in this sandbox. The
following must not be described as verified until tested on Windows:

- `cargo check --target x86_64-pc-windows-msvc`, `cargo test --target
  x86_64-pc-windows-msvc`, and `cargo build --release --target
  x86_64-pc-windows-msvc`;
- actual HWND creation/visibility, subclass lifetime, `WM_NCHITTEST` blank-area
  pass-through, transformed tabs, z-order, hidden-window teardown, hotkeys,
  startup registration, DPAPI, foreground restoration, and idle CPU usage;
- one/two monitors at 100%, 125%, 150%, 175%, and 200%, mixed DPI, display
  add/remove, primary-display changes, and fullscreen applications;
- forced termination/restart recovery, legacy migration with real user data,
  external links, Quick Capture focus behavior, and persistence across relaunch;
- fresh Windows screenshots for rest, fan, expanded note, library, settings,
  and Quick Capture. Repository screenshots are historical references only.

Known fidelity follow-ups are fan drag reordering, rich editable Markdown spans,
incomplete Windows settings parity, and Windows
screenshot-level typography/icon/shadow comparison. Settings parity
gaps include shortcut customization, note typography/size, edge activation,
and per-note text direction. Static follow-ups also include stronger note-ID associated data for
ciphertext row swapping, physical display identity beyond `\\.\\DISPLAYn`
fallback, a fully visible hotkey-registration error surface, and real runtime
proof for pass-through semantics.

## Windows hand-off checklist

1. Build the MSVC target using the commands in `BUILD.md`.
2. Launch the release executable, create/edit/archive/delete/undo notes, force
   terminate it, and confirm encrypted persistence after restart.
3. Exercise pill/fan/editor/library/settings/Quick Capture, Find, tasks,
   external links, Enter/Escape, Shift+Enter, and all global shortcuts.
4. Check startup/single-instance behavior, launch-at-login, foreground restore,
   one/two displays, mixed DPI, display changes, fullscreen suppression, and
   idle CPU usage.
5. Capture the six reference states at 100%, 125%, 150%, 175%, and 200% and
   compare them to the macOS reference before calling the port complete.

## Do not repeat

- Do not restore the deleted WPF/WinUI/.NET implementation or installer.
- Do not edit `Sources/`; it is the macOS reference and is intentionally
  untouched.
- Do not use the managed web Preview for this desktop-native application.
- Do not treat GNU cross-compilation, host tests, or the PE artifact as proof of
  MSVC linking, DPAPI behavior, Win32 hit testing, monitor behavior, or visual
  fidelity.
- Do not weaken or delete tests to make validation pass. Fix the implementation
  and update this checkpoint when new evidence changes the boundary.
