# Native Windows port progress checkpoint

> This file is the durable hand-off point for future compaction or another
> coding session. Read it before inspecting or changing `windows/`. It records
> what is already done, what is only statically reviewed, what still needs a
> real Windows machine, and work that must not be repeated.

**Last updated:** 2026-09-06 UTC
**Branch:** `hoplite/koroneia-de38b879`
**Repository:** `haxllo/notyWin`
**Observed base HEAD:** `2ed091c7201535e743cf69d64a1a0192f72b71ef`
**Worktree:** intentionally dirty; the old managed `windows/` implementation
is deleted and the replacement native Rust implementation is currently in the
worktree. `Sources/` is intentionally untouched.

## Goal and non-negotiable constraints

Recreate the macOS Noty experience as a native Windows application, not as a
Windows-themed redesign. The replacement must use Rust, Slint, Win32, and
SQLite. It must not use Electron, a WebView, Tauri, browser UI, Node.js, .NET,
telemetry, accounts, cloud sync, or a required network connection.

Required product areas are the edge pill, fanned deck, expanded note, library,
settings, Quick Capture, local persistence, encrypted note bodies, autosave,
archive/delete/undo, task handling, keyboard shortcuts, startup behavior,
multi-monitor/DPI behavior, fullscreen suppression, and low idle CPU usage.

## Current native project

The replacement project is under `windows/`:

- `Cargo.toml` / `Cargo.lock` — Rust 2024 manifest and locked dependencies.
- `build.rs` / `ui/noty.slint` — Slint compilation and native UI definition.
- `src/model.rs` — note, deck, settings, task, search, Find, and state models.
- `src/storage.rs` — SQLite schema, AES-GCM body encryption, key handling,
  migration, and persistence.
- `src/deck.rs` — deck state, layout, displays, edge placement, and timers.
- `src/ui.rs` — Slint projection, actions, autosave, library/editor/settings,
  Quick Capture, undo, and lifecycle coordination.
- `src/platform/mod.rs` — platform boundary and shared geometry contracts.
- `src/platform/windows.rs` — Win32 windows, DPAPI, hotkeys, startup,
  monitors/DPI, instance guard, hit testing, and display watching.
- `src/platform/fallback.rs` — deterministic non-Windows geometry/model test
  fallback; it is not a supported production target.
- `README.md`, `BUILD.md`, `ARCHITECTURE.md`, `UI_SPEC.md` — project contract,
  build boundary, ownership map, and reference-derived visual specification.

## Implemented in the current worktree

These items are present in the native replacement. Presence is not the same as
Windows runtime verification; see the verification boundary below.

### Product behavior

- Resting edge pill, fan, expanded note, library/all-notes view, settings, and
  Quick Capture flows are represented in the native UI.
- Note bodies are editable plain text and rendered separately as styled Markdown
  preview. Slint's stock text input is not presented as a rich-text editor.
- Styled preview supports the Markdown subset provided by Slint, including
  emphasis, strong text, strike, code, lists, and links. Task markers are
  converted to local clickable `noty-task:` links; HTTP(S)/mailto links are
  handed to the Windows shell.
- Inline tasks use the reference `☐`/`☑` behavior. `Ctrl+T` adds/removes a task
  marker and checkbox interaction cycles completion.
- Autosave is coalesced by a 250 ms timer; there is no unnecessary Save button.
- Archive, delete, undo, search, note selection, settings, and restored-note
  ordering are implemented.
- Quick Capture preserves the prior deck/library/editor context, accepts Enter
  and Shift+Enter, restores the previous foreground window, dismisses on
  outside focus, and uses pointer-aware placement/palette styling.
- Native Find supports `Ctrl+F`, an inline bar, case-insensitive match counts,
  wraparound previous/next navigation, editable selection, Escape dismissal,
  controller state, and Slint refresh bindings.

### Persistence and recovery foundations

- SQLite persistence is local-first and uses an in-memory database in tests.
- Note bodies use AES-GCM encryption. Windows key material is protected with
  DPAPI and stored below `%LOCALAPPDATA%\NotyWin`.
- Legacy data migration exists and is intended to preserve source data.
- Pending body writes, retry/coalescing, incremental shutdown flushing,
  restored-note ordering, and single-instance protection are represented.
- The key creation path is atomic for new keys; unreadable ciphertext is
  surfaced internally as a state that still needs stronger UI/recovery behavior
  (listed below).

### Platform/lifecycle foundations

- Win32 monitor enumeration uses stable device identities rather than persisted
  raw `HMONITOR` values.
- DPI-aware edge placement, display tracking, hidden-display watching, and
  fullscreen reconciliation are implemented in the platform boundary.
- Borderless/tool-window setup, startup registration, global hotkey registration,
  and instance guarding are implemented.
- Close-request handling uses `run_event_loop_until_quit()` so the application
  does not exit merely because its last visible window is hidden/closed.
- Timer callbacks use cancellation/generation checks where queued callbacks can
  outlive a timer stop.

## Verification already completed

The following commands were run before this checkpoint and passed:

```text
cargo fmt --manifest-path windows/Cargo.toml -- --check
cargo check --manifest-path windows/Cargo.toml
cargo test --manifest-path windows/Cargo.toml       # 31 host tests passed
cargo check --manifest-path windows/Cargo.toml --target x86_64-pc-windows-gnu
git diff --check
```

There is also a locally produced GNU-target release PE artifact at:

```text
windows/target/x86_64-pc-windows-gnu/release/noty-win.exe
```

It is approximately 12 MiB and is useful only as evidence that the GNU target
can produce a PE executable. It is not evidence of MSVC compatibility or
Windows runtime correctness. The only known compiler output at this checkpoint
is dead-code warnings for transition helpers and fallback/test-only functions.

## Review findings not fixed yet

The following items came from a static storage review. They are action items,
not completed behavior, and should be addressed before calling persistence
finished:

1. `Store::open` currently quarantines the database for every open/setup error.
   Restrict quarantine to verified corruption/not-a-database cases; do not
   destroy or rename data for `BUSY`, `LOCKED`, permission, disk-full, or
   migration errors. Add locked, read-only, and damaged-database tests.
2. Do not create a replacement encryption key when `notes.db`/WAL/SHM exists but
   `note.key` is missing. Return a recovery error so existing ciphertext cannot
   be silently made unreadable and overwritten.
3. Make schema migration crash-safe: column additions and legacy backfills must
   be transactional or independently idempotent, with a schema/version marker.
4. Rewrap of a legacy plaintext key must use the atomic temp-file/sync/replace
   path used for new keys; a partial direct write can destroy the only key.
5. Handle zero-byte legacy note bodies as valid empty notes, and visibly surface
   malformed/unreadable bodies instead of making them look like ordinary blank
   editors that lose the protection state when typed into.
6. Autosave currently performs synchronous SQLite writes from a Slint timer.
   Decide whether the requirement permits that or move writes behind a worker /
   queue; make shutdown await a bounded final flush and retain/report failed
   writes instead of allowing the event loop to exit immediately after a retry
   is scheduled.
7. Archive ordering needs sub-second precision and a deterministic tie-breaker;
   second-granular timestamps can reorder same-second archives after reopen.
8. Authenticate the owning note ID (or equivalent immutable metadata) as AES-GCM
   associated data so a valid ciphertext cannot be swapped between rows.

When fixing these, add focused tests first or alongside each change. Do not
discard the review findings merely because the current 31 tests pass.

### UI/controller review findings

These findings came from a separate static review of Slint and controller
behavior. They are not runtime-verified and no files were changed by the
review:

1. Find navigation focuses the editor after selecting a match, so the next
   character edits the note instead of the query; Escape also does not restore
   editor focus. Add a headless focus test for Enter/next and Escape.
2. Find and `Ctrl+T` remain available while Preview hides the source editor.
   Switch to Edit or make both actions explicitly no-op in Preview, with a
   test that prevents mutation of a stale cursor line.
3. The secondary editor callback updates model state without refreshing all
   windows, leaving titles, task progress, and headers stale. Add a two-window
   refresh test.
4. Archive Restore overlaps the Preview/Edit control in archive detail mode.
   Fix the hit regions/render order and assert that the overlapping coordinate
   fires only `restore-note`.
5. The unpinned editor can close after 60 seconds even while the user is typing.
   Editing must rearm/cancel the idle-close timer; add a mock-time test.
6. Find byte ranges are unsafe for Unicode case folding and become stale after
   edits. Use source-valid ranges and remap the active match; cover non-ASCII
   text and edits before a later match.
7. The `+N` fan control overlaps the fifth tab at some scales. Derive the
   control from the same layout plan as the tabs and test 70%, 100%, and 180%.
8. Quick Capture currently requests activation, contrary to the intended
   low-friction foreground-preserving flow. Extract/test the activation policy
   and verify it on Windows.

### Win32/deck review findings

These findings came from a static review of platform and deck lifecycle code:

1. `HTTRANSPARENT` does not reliably pass fan blank-area input through to an
   application in another process. Replace it with a native input/window-region
   design that gives interactive elements input while preserving pass-through.
2. `HWND_NOTOPMOST` when fullscreen suppression is off lets a maximized normal
   app cover the edge pill. Keep the deck above normal apps and suppress it
   explicitly only for fullscreen applications.
3. High-DPI and small-height layouts can render five tabs beyond the HWND and
   overlap controls because the container is capped without reducing the tab
   plan. Return one shared visible-item/layout plan and assert all bounds at
   100–200% DPI, small displays, five-plus notes, and maximum deck scale.
4. Hashing `\\.\DISPLAYn` is not a stable physical-monitor identity across
   topology changes. Persist a display-config/EDID-backed identity with a
   defined fallback and test dock/undock, reorder, primary-switch, and restart.
5. The display watcher does not route `WM_DPICHANGED` into geometry refresh.
   Add a per-window DPI notification path and test a live DPI change.
6. Quick Capture focus restoration stores only a reusable raw HWND and ignores
   activation failure. Store process identity, validate the handle, and handle
   failed foreground activation safely.
7. Global hotkey registration failures are only written to stderr, which is not
   visible in the GUI build. Return per-binding results and surface unavailable
   shortcuts in settings or an equivalent in-app status.

The three reviews all reported the current host suite still passes (31 tests)
and did not modify source files. These findings are now recorded here so they
are not rediscovered after compaction.

## Known fidelity gaps

These are intentionally recorded rather than silently treated as complete:

- Hover preview/open timing is not complete.
- Fan drag reordering is not complete.
- Notes hidden behind `+N` cannot yet be directly opened/interacted with.
- Rich editable Markdown spans, heading/task-list syntax accepted by Slint's
  parser, completed-task styling, checkbox hit testing, Enter behavior inside
  task lists, and editor scrolling need further fidelity work.
- Typography, geometry, colors, shadows, spacing, animation, transformed tabs,
  screen-edge placement, and mixed-DPI rendering still need screenshot-level
  comparison on Windows.
- The current settings surface exposes an all/main-style display target even
  though controller logic also supports `id:<monitor>` targets.

## Windows-only verification still required

No real Windows 10/11 session or MSVC toolchain is available in this sandbox.
Do not claim completion until a Windows x64 machine verifies at least:

```powershell
cargo check --target x86_64-pc-windows-msvc
cargo test --target x86_64-pc-windows-msvc
cargo build --release --target x86_64-pc-windows-msvc
```

The runtime pass must cover:

- pre-map Winit attributes, inactive creation, first-map native styles,
  taskbar suppression, close behavior, hidden-window destruction, and event-loop
  lifetime;
- Win32 subclass lifetime, `WM_NCHITTEST`, blank-area pass-through, hidden-note
  and delete-toast controls, transformed tabs, screen edges, and animation;
- one/two displays at 100%, 125%, 150%, 175%, and 200% scaling, display add/
  remove, mixed DPI, and fullscreen applications;
- global hotkeys, launch-at-login, single-instance enforcement, idle CPU usage,
  and teardown/thread joining;
- Quick Capture activation, focus retention, pointer-aware placement, DPI
  scaling, outside-click/focus dismissal, context/foreground restoration,
  Enter/Escape, and Shift+Enter;
- Find selection, preview mode, external Markdown links/task links, DPAPI key
  protection, legacy migration, unreadable ciphertext, forced termination and
  restart recovery, and persistence across relaunch;
- reference screenshots for rest, fan, expanded note, library, settings, and
  Quick Capture. Capture fresh screenshots on Windows; repository screenshots
  are historical reference material, not current evidence.

## Next session: ordered work plan

1. Read this file and inspect only the relevant native file before editing.
2. The storage, UI/controller, and Win32/deck reviews are already recorded
   above; do not launch duplicate reviews unless code changes invalidate them.
3. Fix the storage review items in small groups, running focused tests after
   each group and the full host suite before moving on.
4. Re-run the exact validation block above after native code changes. Do not
   rerun it in a loop if no code changed.
5. Use a real Windows MSVC/runtime environment for platform and visual checks.
6. Update this file's date, verification block, open findings, and next action
   before handing off or ending a session. If the worktree changes materially,
   update the checkpoint in the same turn.

## Do not repeat

- Do not restore the deleted WPF/WinUI/.NET implementation or its installer.
- Do not edit `Sources/`; it is the macOS reference and is intentionally
  untouched.
- Do not replace the native stack with Electron, WebView, Tauri, browser UI,
  Node.js, or .NET.
- Do not use the managed web Preview for this desktop-native project.
- Do not treat GNU cross-compilation, host tests, or the PE artifact as proof of
  MSVC linking, DPAPI behavior, Win32 hit testing, monitor behavior, or visual
  fidelity.
- Do not claim the listed fidelity gaps or Windows-only checks are complete
  without fresh evidence.
- Do not create another progress/checkpoint file; update this one instead.
- Do not weaken or delete tests to make validation pass. Fix the implementation
  and record the new evidence here.
