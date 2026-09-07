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

The repository currently verifies formatting, host compilation, 45 host unit
tests, a GNU Windows-target type check, and a release import-table audit. A real Windows 10/11 x64 session
is still required to validate MSVC linking, Win32 activation and hit testing,
global hotkeys, DPAPI, mixed-DPI monitors, fullscreen suppression, and visual
fidelity. The project is therefore not described as runtime-verified until
those checks have been run on Windows.

The current interaction model opens a fan tab by click. Hover preview/open,
drag reordering, and direct interaction with notes hidden behind `+N` remain
follow-up fidelity work rather than silently unsupported claims.

Read [BUILD.md](BUILD.md) for build and packaging commands, [UI_SPEC.md](UI_SPEC.md)
for the reference-derived design contract, [ARCHITECTURE.md](ARCHITECTURE.md)
for ownership and platform boundaries, and [PROGRESS.md](PROGRESS.md) for the
current hand-off checkpoint and verification boundary.
