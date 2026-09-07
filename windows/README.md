# Noty for Windows

This directory is a clean native Windows implementation of Noty. It deliberately
does not share UI code with the macOS target in `../Sources/`.

The application is built with Rust, Slint, Win32 APIs, and SQLite. It is an
edge utility rather than a conventional document window: the resting surface is
a small colour pill, the fan is revealed from the active screen edge, and an
expanded note grows out of its selected tab.

## Current implementation

- Rust 2024 Cargo project with Slint UI compiled at build time.
- Direct Slint winit backend with the optional tray/menu integration disabled;
  the pinned backend compatibility patch keeps comctl32 subclass exports out of
  the executable import table.
- Explicit note model and SQLite persistence with encrypted note bodies.
- Rest, fan, and expanded deck states with shingled tabs and colour chips.
- Fan tabs, `+N`, and settings controls terminate at the outward monitor edge;
  the panel keeps its gutter on the interior side instead of leaving a gap at
  the screen edge.
- A persistent deck hover surface with a DPI-scaled native edge bridge, so
  direct and diagonal pill-to-tab handoffs retain click delivery without
  claiming that blank fan pixels are interactive. Optional preview width is
  reserved when the fan opens, so hovering a tab does not move the edge panel.
- Fan tabs, preview cards, and expanded note surfaces round only their left
  corners; the edge-facing right corners remain square.
- Autosaving editor, inline task markers, archive/delete/undo, library search,
  settings, and an inline case-insensitive `Ctrl+F` Find bar with wraparound
  selection.
- Quick Capture preserves the previous deck/library/editor context, accepts
  Enter and Shift+Enter, and restores the previous foreground window when it
  closes.
- Win32 monitor enumeration, DPI-aware edge placement, borderless tool-window
  styling, startup registration, global hotkey registration, and single-instance
  enforcement on Windows.
- A deterministic non-Windows fallback for geometry/model tests; it is not a
  supported production target.

The editable note surface is intentionally a native plain-text `TextInput`.
Markdown is rendered in a separate styled preview surface; task links are
converted to local checkbox actions and HTTP(S)/mailto links are handed to the
Windows shell. This keeps editing reliable without pretending that Slint's
stock text input is a rich Markdown editor.

## Verification boundary

The repository currently verifies formatting, host compilation, 54 host unit
tests, a GNU Windows-target type check, and a GNU release import-table audit.
A real Windows 10/11 x64 session is still required to validate MSVC linking,
Win32 activation and hit testing, global hotkeys, DPAPI, mixed-DPI monitors,
fullscreen suppression, and visual fidelity. The project is therefore not
described as runtime-verified until those checks have been run on Windows.

Editor autosave coalesces only actual body changes, ignores redundant values
fed back during UI refresh, and refreshes the visible status after each
debounced persistence attempt. Failed writes remain queued for retry. Note
writes use a transaction that updates by note ID and inserts only when the row
does not exist, so migrated tables without a matching SQLite uniqueness
constraint do not fail on `ON CONFLICT(id)`. The host tests cover this
lifecycle and legacy schema path; SQLite/encryption errors and the Slint timer
event path still require real Windows runtime validation.

The current interaction model opens a fan tab by click, can show a delayed
preview card on hover, and can optionally open the note after a longer hover.
Preview space is part of the fan frame before hover, so the native edge
position stays fixed while the card appears. The host suite covers the
pre-reserved and hovered geometry as well as the pill-to-tab handoff, including
diagonal entry, while the native hit-test path returns `HTCLIENT` for accepted
regions, `HTTRANSPARENT` for blank fan pixels, and `MA_NOACTIVATE` for the
nonactivating tool window. These checks do not replace real Windows runtime
validation.

### Known UX gaps

- Fan drag reordering is not implemented.
- The `+N` indicator opens Library rather than exposing the hidden notes for
  direct interaction in the fan.
- Markdown editing is plain text with a separate styled preview, not rich
  span-level editing.
- Settings parity is incomplete: shortcut customization, note typography/size,
  edge activation, and per-note text direction are not exposed yet.
- Typography, icons, shadows, and other screenshot-level details still need
  comparison against fresh Windows captures.

Read [BUILD.md](BUILD.md) for build and packaging commands, [UI_SPEC.md](UI_SPEC.md)
for the reference-derived design contract, [ARCHITECTURE.md](ARCHITECTURE.md)
for ownership and platform boundaries, and [PROGRESS.md](PROGRESS.md) for the
current hand-off checkpoint and verification boundary.
