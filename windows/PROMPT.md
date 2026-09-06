# Task: Recreate Noty for Windows — 1:1 Native Port

Build a Windows-native application that recreates the macOS application **Noty** as closely as technically possible.

The goal is **not to make a Windows-inspired alternative**. The goal is to reproduce the existing Noty application's visual design, layout, animations, interaction model, states, behavior, keyboard shortcuts, persistence model, and overall feel as a Windows application.

## 1. Reference Material

The original application's screenshots are located at:

`C:\Users\mshab\Projects\noty\screenshots`

Before writing implementation code:

1. Recursively inspect every screenshot in that directory.
2. Identify every distinct UI state.
3. Identify dimensions, spacing, typography, colors, borders, shadows, radii, animations, positioning, layering, and transitions.
4. Compare screenshots against each other to infer interaction states.
5. Create an internal UI specification from the screenshots before implementing the UI.
6. Do NOT invent a different Windows UI merely because Windows conventions are different.
7. When something is ambiguous, inspect all screenshots and infer the behavior that best explains them.

The screenshots are the visual source of truth.

If the original Noty source repository is available locally, inspect it as well. Use its implementation to understand behavior, but do not blindly translate Swift/SwiftUI code into another language.

---

# 2. Platform / Technology Requirements

Target:

* Windows 10
* Windows 11
* x64
* Native desktop application
* No browser
* No Electron
* No Tauri/WebView UI
* No heavyweight JavaScript runtime
* No unnecessary background processes

## Preferred stack

Use:

* **Rust** for application/backend/system integration
* **Slint** for the UI layer
* Win32 APIs through Rust where required
* SQLite for persistent structured data if appropriate
* Windows APIs for:

  * global hotkeys
  * monitor enumeration
  * window positioning
  * startup registration
  * taskbar/tray integration
  * foreground-window detection
  * fullscreen detection
  * DPI awareness
  * native window behavior

The application should compile into a normal native Windows executable.

Avoid:

* Electron
* Chromium
* React
* Next.js
* Flutter unless there is a demonstrated technical reason Slint cannot reproduce the required UI
* .NET unless a specific Windows API requirement makes it substantially better
* large UI frameworks with unnecessary runtime overhead

The priority order is:

1. Visual fidelity
2. Interaction fidelity
3. Responsiveness
4. Low memory usage
5. Low CPU usage
6. Native Windows integration
7. Maintainability

Do not sacrifice visual fidelity merely to make the implementation easier.

---

# 3. Important Architecture Principle

Build the Windows version entirely inside the existing `windows/` directory.

Do NOT create, move, or modify the existing macOS implementation in `Sources/`.

Use this structure:

```text
notyWin/
├── Sources/        # Existing macOS Swift implementation — reference only
├── screenshots/    # UI reference screenshots
└── windows/        # Windows Rust + Slint implementation
    ├── Cargo.toml
    ├── build.rs
    ├── src/
    ├── assets/
    └── README.md
```

The `windows/` project must be independently buildable with Cargo. Keep all Windows-specific source code, assets, configuration, and build files inside `windows/`.

---

# 4. Reproduce the Noty Interaction Model

The application should behave like the original Noty.

The primary interaction is an **edge-mounted note deck**.

The application should normally be almost invisible.

At rest:

* A very small edge indicator/pill is visible.
* It should consume virtually no screen space.
* Notes are represented by their corresponding visual indicators.
* The application should not behave like a conventional desktop window.

When the pointer reaches the configured edge activation area:

* The deck should reveal itself.
* Notes should fan/shingle out.
* The animation should be smooth.
* Individual notes should appear with the same staggered timing as the reference.
* The deck should occupy as little screen real estate as possible.

When a note is selected:

* It should expand into the corresponding editor.
* The selected note's tab should remain visually connected to the expanded editor.
* The expanded note should feel like it originated from the edge deck rather than appearing as an unrelated window.

When the user dismisses the note:

* Return to the deck.
* Preserve deck state where appropriate.
* Moving the pointer away should collapse the deck according to the original behavior.

Do not simply implement this as:

```text
open normal window
show notes
close normal window
```

The edge/deck interaction is a fundamental part of the application.

---

# 5. Window Behavior

The application should use borderless/custom windows where necessary.

Requirements:

* No conventional title bar for the primary Noty UI.
* No unnecessary taskbar entry.
* No visible application window chrome.
* Correct z-order.
* Correct activation behavior.
* Correct mouse activation behavior.
* Correct focus behavior.
* Correct keyboard focus.
* Correct interaction with other applications.
* Correct behavior when another application is fullscreen.
* Correct behavior across virtual desktops where technically possible.
* Correct DPI scaling.
* Correct multi-monitor behavior.

Do not use fake margins to simulate screen-edge positioning.

The actual window geometry should be calculated from the monitor's working area and physical pixel/DPI information.

---

# 6. Multi-Monitor Support

Implement proper multi-monitor support.

For every connected display:

* Determine its work area.
* Determine DPI/scaling.
* Create/manage the appropriate edge activation/deck state.
* The deck should activate on the display where the pointer interacts with the edge.
* Other displays should remain dormant unless their own edge is activated.

Handle:

* monitor connection
* monitor disconnection
* resolution changes
* DPI changes
* display rearrangement
* primary monitor changes

Do not hardcode a single 1920×1080 monitor.

---

# 7. DPI / Scaling

The application must be properly DPI aware.

Test at:

* 100%
* 125%
* 150%
* 175%
* 200%

The UI must remain visually consistent.

Do not simply scale the entire application bitmap.

Text, spacing, icons, and geometry must be calculated correctly.

---

# 8. Animation

Animation quality is extremely important.

Analyze the screenshots and, where possible, the original application's behavior to reproduce:

* fan-out animation
* note stagger
* note expansion
* note collapse
* hover states
* selection transitions
* pill/deck transitions
* settings transitions
* deletion/undo transitions
* any other visible animation

Avoid generic framework animations if they produce visibly different motion.

Implement custom easing/timing where required.

Animations should:

* remain smooth at 60 FPS+
* avoid blocking the UI thread
* avoid unnecessary allocations
* not trigger continuous CPU usage when idle

Use GPU-backed rendering where beneficial.

---

# 9. Visual Fidelity

Treat visual fidelity as a measurable engineering requirement.

Do NOT use generic controls when they visually differ from the reference.

For example, do not use a standard Windows:

* Button
* TextBox
* CheckBox
* Menu
* Tab
* Window frame

if the reference uses a custom visual equivalent.

Build custom components.

Match:

* exact dimensions
* corner radius
* padding
* spacing
* font
* font size
* font weight
* line height
* letter spacing
* opacity
* shadows
* borders
* separators
* icon dimensions
* icon alignment
* hover behavior
* pressed behavior
* selected behavior
* disabled behavior

Avoid "close enough".

---

# 10. Typography

Inspect the screenshots carefully and determine the closest available font.

Do not substitute a random Windows system font merely because it is available.

If the original uses a bundled font:

* bundle the appropriate equivalent if legally permissible
* otherwise identify the closest metrically compatible font

Typography must match the reference in:

* glyph size
* line height
* weight
* baseline
* letter spacing
* rendering scale

---

# 11. Colors

Extract approximate colors from the screenshots.

Create a centralized design-token system:

```text
colors/
spacing/
radius/
typography/
shadows/
animation/
```

Do not scatter literal colors throughout the UI code.

Example:

```text
backgroundPrimary
backgroundSecondary
textPrimary
textSecondary
border
accent
hover
selected
danger
```

The actual values should come from the reference screenshots.

---

# 12. Note Model

Implement a proper note model.

At minimum support:

* unique ID
* title
* body
* color
* creation timestamp
* modification timestamp
* archived state
* pinned state
* ordering
* task state where applicable

Follow the behavior visible in the reference application.

Autosave should be automatic.

There must be no "Save" button unless the reference explicitly has one.

---

# 13. Persistence

Use local-first storage.

No cloud backend.

No account.

No analytics.

No telemetry.

No network dependency for normal operation.

SQLite is preferred for structured persistence.

Database operations must never block the UI thread.

Use:

* transactions
* prepared statements
* indexed queries where useful
* debounced writes
* atomic updates

The application should be usable immediately after launch without waiting for a database initialization process.

---

# 14. Search / All Notes

If the reference screenshots show an All Notes/library interface, reproduce it.

Match:

* window size
* search field
* list layout
* selected item
* preview/detail pane
* archive behavior
* keyboard navigation
* empty states
* hover states

Search should operate locally and should feel instantaneous.

---

# 15. Settings

Reproduce the Settings interface shown in the reference material.

Settings should include only functionality present in the reference unless Windows necessarily requires an equivalent implementation.

Examples may include:

* keyboard shortcuts
* note appearance
* note size
* edge activation distance
* deck style
* launch at startup
* fullscreen behavior
* markdown behavior
* other settings visible in the reference

Settings should take effect immediately where the original does.

---

# 16. Global Keyboard Shortcuts

Implement system-wide hotkeys using Windows APIs.

Do not implement global shortcuts by polling the keyboard.

Hotkeys should work when another application is focused.

Implement the equivalent of the original shortcuts.

Make shortcuts configurable if the reference application supports configurable shortcuts.

Handle:

* registration
* conflict detection
* unregistration
* application shutdown
* re-registration after settings changes

---

# 17. Windows Integration

Use Windows-native APIs where appropriate.

Implement:

* startup at login
* system tray/menu integration if required
* monitor detection
* DPI awareness
* fullscreen detection
* foreground window detection
* global hotkeys
* native window positioning
* appropriate window styles
* correct focus/activation behavior

The application should feel like a native Windows utility, not a ported web application.

---

# 18. Performance Requirements

The application should be extremely lightweight.

Target:

### Idle

CPU:

```text
~0% or as close to 0% as reasonably possible
```

Memory:

```text
Keep baseline memory usage low.
Avoid embedding Chromium/WebView.
```

### Interaction

Opening the deck should feel instantaneous.

Target:

```text
pointer enters edge
        ↓
state transition immediately begins
        ↓
animation remains smooth
        ↓
note editor ready for typing
```

Do not perform expensive synchronous work during this interaction.

Never:

* scan the entire filesystem
* reload the database unnecessarily
* recreate the entire UI tree
* allocate large objects repeatedly
* perform blocking disk operations
* perform network operations

during an interaction.

---

# 19. Startup

Startup should be extremely fast.

The application should:

1. initialize Windows integration
2. load minimal state
3. initialize note store
4. register hotkeys
5. create edge/deck infrastructure
6. become interactive

Avoid splash screens.

Avoid visible console windows.

Avoid opening a normal application window at startup.

If configured to launch at Windows startup, it should start silently.

---

# 20. Accessibility

Do not sacrifice the custom UI.

However, implement reasonable accessibility semantics where the framework permits:

* keyboard navigation
* logical focus order
* accessible labels
* screen-reader-compatible controls where practical

Accessibility must not change the visual design.

---

# 21. Error Handling

The application should fail gracefully.

Never crash because:

* a monitor disappears
* a hotkey cannot be registered
* the database is temporarily unavailable
* a note cannot be written
* DPI changes
* another application is fullscreen
* a malformed note is loaded

Log technical failures to a local log file.

Do not expose debug UI to normal users.

---

# 22. Project Quality

Write production-quality code.

Requirements:

* no placeholder implementations
* no TODO-based core functionality
* no fake data in production paths
* no hardcoded monitor resolution
* no arbitrary sleep-based synchronization
* no polling loops unless technically unavoidable
* no unnecessary dependencies
* no duplicated application state
* no giant monolithic source file

Use strong Rust types and clear ownership boundaries.

---

# 23. Development Process

Do NOT immediately start coding the entire application.

Follow this sequence:

## Phase 1 — Reverse engineer

Inspect:

```text
C:\Users\mshab\Projects\noty\screenshots
```

Create:

```text
docs/UI_SPEC.md
docs/BEHAVIOR_SPEC.md
docs/WINDOW_SPEC.md
docs/ARCHITECTURE.md
```

Document:

* every screen/state
* dimensions
* colors
* typography
* interactions
* animations
* transitions
* keyboard shortcuts
* window behavior
* persistence behavior

## Phase 2 — Skeleton

Create the Rust/Slint application.

Implement:

* application lifecycle
* Windows integration
* monitor detection
* DPI handling
* basic edge window

Do not implement every feature yet.

## Phase 3 — Visual implementation

Implement the UI from the screenshots.

Build the smallest possible component system.

## Phase 4 — Interaction

Implement:

* edge activation
* deck animation
* note selection
* note editing
* dismissal
* keyboard interaction
* global hotkeys

## Phase 5 — Persistence

Implement:

* SQLite
* autosave
* loading
* archive
* delete
* undo
* ordering
* settings

## Phase 6 — Windows integration

Implement:

* startup
* tray/menu
* multi-monitor
* fullscreen behavior
* global shortcuts
* DPI changes

## Phase 7 — Fidelity testing

Compare the Windows implementation against every reference screenshot.

For every mismatch, fix:

1. geometry
2. spacing
3. typography
4. colors
5. shadows
6. animation
7. interaction

Do not stop at "looks similar".

---

# 24. Screenshot-Based Validation

Create a repeatable visual validation workflow.

Capture screenshots of the Windows implementation for every major state.

Compare them against:

```text
C:\Users\mshab\Projects\noty\screenshots
```

If possible, generate image overlays/difference images.

Use those comparisons to iteratively correct the UI.

Pay particular attention to:

* 1–3 px alignment errors
* incorrect window dimensions
* incorrect corner radii
* wrong shadow spread
* wrong text baseline
* wrong tab overlap
* incorrect animation timing
* incorrect edge positioning

---

# 25. Important: Do Not "Improve" the Design

Do not redesign Noty.

Do not:

* add Windows-style title bars
* add unnecessary menus
* add unnecessary settings
* add unnecessary animations
* change the color palette
* make the deck larger
* change the note layout
* add a dashboard
* add a sidebar unless it exists in the reference
* add cloud synchronization
* add accounts
* add telemetry
* add unnecessary system-tray UI

The objective is:

```text
macOS Noty
     ↓
same visual language
     ↓
same interaction model
     ↓
same behavior
     ↓
Windows-native implementation
```

Not:

```text
macOS Noty
     ↓
Windows-inspired notes application
```

---

# 26. Windows-Specific Adaptation

Only adapt things that inherently require Windows-specific behavior.

For example:

macOS:

```text
NSPanel
NSStatusItem
Carbon global hotkey
CGDirectDisplayID
SwiftUI/AppKit
```

Windows equivalents should use:

```text
Win32 HWND
Windows monitor APIs
RegisterHotKey / appropriate low-level mechanism
Win32 window styles
Windows DPI APIs
Windows startup mechanisms
```

The underlying implementation can differ completely.

The user-facing behavior should not.

---

# 27. Testing Matrix

Before considering the project complete, test:

### Displays

* 1 monitor
* 2 monitors
* mixed-resolution monitors
* mixed-DPI monitors
* monitor hot-plugging

### Scaling

* 100%
* 125%
* 150%
* 175%
* 200%

### Windows

* Windows 10
* Windows 11

### Applications

Test while:

* browser is focused
* VS Code is focused
* fullscreen video is playing
* a game is running
* another maximized application is active

### Interaction

Test:

* mouse
* keyboard
* global hotkeys
* rapid open/close
* rapid note switching
* typing continuously
* creating/deleting notes
* application restart

### Persistence

Test:

* normal shutdown
* forced termination
* restart
* database corruption handling
* thousands of notes

---

# 28. Final Deliverable

Produce a complete buildable project.

The final project must include:

```text
README.md
BUILD.md
ARCHITECTURE.md
UI_SPEC.md
```

And provide:

```text
development build
release build
installer or portable executable
```

The application must run without requiring the development environment.

---

# 29. Definition of Done

Do not declare the project complete merely because:

* it compiles
* notes can be created
* the UI resembles the screenshots

It is complete only when:

* the application visually matches the reference
* edge behavior matches
* deck behavior matches
* animations match
* note interaction matches
* keyboard behavior matches
* global shortcuts work
* persistence works
* multi-monitor behavior works
* DPI scaling works
* fullscreen behavior works
* startup behavior works
* CPU/memory usage is low
* there are no visible placeholder components
* there are no major deviations from the reference screenshots

When uncertain about an implementation decision, prioritize:

**reference screenshot → original Noty behavior → native Windows behavior → implementation convenience**

Never prioritize implementation convenience over fidelity.
