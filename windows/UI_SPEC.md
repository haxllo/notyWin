# Noty Windows UI specification

This is the implementation contract derived from `screenshots/rest.png`,
`screenshots/fan.png`, `screenshots/open.png`, and
`screenshots/markdown.png`, plus the matching Swift implementation in
`Sources/DeckViews.swift` and `Sources/DeckPanel.swift`.

## States

| State | Surface | Entry | Exit |
|---|---|---|---|
| Rest | 12 pt edge pill, one saturated dash per note | idle or dismiss | pointer enters edge activation zone |
| Fan | five shingled paper tabs, optional `+N`, plus and cog controls | pointer reaches pill | pointer leaves after debounce or a note opens |
| Expanded | selected note at full size with its tab as a dashed gutter | tab click | Close, Escape, archive, or outside click |

The deck is anchored to a real monitor work-area edge. It never uses a fake
screen margin or a normal title bar. The default target is every display, with
the active display determined by the pointer. Settings currently exposes an
all-displays/main-display toggle; controller/storage logic also accepts a
stable `id:<monitor>` target.

## Reference measurements

All dimensions are logical points multiplied by one deck scale (70–180%).

```text
rest pill width:       12
rest dash:              7 × 14, 5 gap, 7 padding
labelled tab:          30 wide, 56–106 pitch, 3° lean
tab label:              9.5 pt, uppercase, 12 pt inset
compact chip:          30 × 24, 6 gap
fan limit:              5 notes before +N
plus control:           28 circle
settings control:       24 circle
expanded note radius:   14, paper gutter 30
editor body inset:      42 top / 30 horizontal after gutter
autosave delay:         250 ms
fan stagger:            42 ms per tab
fan idle timeout:       4 s
note idle timeout:      60 s
```

## Tokens

The eight paper/dash/ink triplets are centralized in `src/model.rs`: Lemon,
Peach, Rose, Lilac, Sky, Mint, Sand, and Slate. The reference uses a warm
paper tint, a darker saturated edge dash, near-black coloured ink, a very soft
shadow, and a dashed gutter separator. Controls use the note's paper/dash
colour instead of introducing Windows accent blue.

Typography defaults to a hand-written-compatible system fallback for body text,
with a clean semibold system face for titles and controls. Tab labels are
uppercase and rotated. The source editor is a native plain-text input; a
separate preview surface renders the supported Markdown styling for headings,
emphasis, strike-through, code, links, quotes, bullets, and task completion.
The preview is intentionally not described as rich editable Markdown.

## Interaction rules

- A first click on a tab opens it without activating the underlying app.
- `Esc` closes the editor, then dismisses the fan.
- A task line is represented as `☐ ` or `☑ ` and remains plain text in storage.
- `Ctrl+T` toggles the current line into a task; clicking the marker toggles it.
- `Ctrl+F` opens the compact Find bar in an expanded editor; matching is
  case-insensitive, navigation wraps, and the current match is selected in
  the source editor.
- Delete is reversible for ten seconds; archive is not destructive.
- Right-clicking a tab exposes pin, archive, cycle colour, and delete.
- `Ctrl+Shift+Space` opens quick capture; Enter saves and Shift+Enter adds a line.
- `Alt+Ctrl+N`, `Alt+Ctrl+A`, and `Alt+Ctrl+L` are the default global actions.

## Current fidelity boundary

The fan uses click-to-open tabs and staggered Slint movement animations. Hover
preview/open, drag reordering, and direct interaction with notes hidden behind
`+N` are not yet implemented. Blank fan areas use Win32 transparent hit
testing, but transformed-tab coordinates, DPI edge cases, and pass-through
behavior still need real Windows verification. Screenshot-level typography and
shadow comparison likewise requires a Windows capture rather than the host
unit-test environment.
