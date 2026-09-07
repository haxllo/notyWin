# Build and release

## Prerequisites

Install a stable Rust toolchain (Rust 1.92 or newer), the MSVC
target, and the Visual Studio Build Tools workload **Desktop development with
C++**. The Windows target is `x86_64-pc-windows-msvc`.

```powershell
rustup toolchain install stable
rustup default stable
rustup target add x86_64-pc-windows-msvc
```

No browser runtime, Node.js, .NET runtime, or external SQLite installation is
required. `rusqlite` builds SQLite into the executable.

The manifest uses Slint's winit backend directly and disables the optional
system-tray/menu integration. This avoids a static comctl32 subclass import;
the native hit-test subclass APIs are resolved at runtime instead. The small
Slint backend compatibility patch is vendored under `vendor/i-slint-backend-winit`
so clean builds use the same dependency graph.

The native project is not a browser preview, so the repository's web Preview
server is intentionally not used. Run Cargo directly from `windows\` or pass
`--manifest-path windows/Cargo.toml` from the repository root.

## Development build

Run from this directory:

```powershell
cargo check
cargo test
cargo run
```

Before a release, run the same checks used by the repository workspace:

```powershell
cargo fmt --manifest-path windows/Cargo.toml -- --check
cargo check --manifest-path windows/Cargo.toml
cargo test --manifest-path windows/Cargo.toml
```

The app writes its local data to `%LOCALAPPDATA%\NotyWin\` on Windows. Tests
use in-memory SQLite and never touch a user's notes.

## Release build

```powershell
cargo build --release --target x86_64-pc-windows-msvc
```

The portable executable is:

```text
target\x86_64-pc-windows-msvc\release\noty-win.exe
```

It can be copied to another Windows 10 or Windows 11 x64 machine. The first
launch creates the data directory and a local encryption key. No network
connection is needed for normal operation.

On Windows, the AES-GCM key is protected with the current user's DPAPI before
it is written to `%LOCALAPPDATA%\NotyWin\note.key`. The note bodies remain
encrypted in SQLite. Moving the data directory to another Windows user or
machine is not a supported key-transfer mechanism.

## Installer

The release executable is intentionally portable. A future signed installer
may wrap that executable, but installation is not needed to run the app. Keep
the installer outside the runtime so the app remains a single small native
process.

## Verification matrix

On a Windows test machine, verify the release binary at 100%, 125%, 150%,
175%, and 200% scaling with one and two displays. The geometry tests cover the
monitor-independent calculations; display and DPI checks require a Windows
session because this repository's Linux CI cannot create real Win32 monitors.

The following Windows-only commands are intentionally listed separately from
the checks above:

```powershell
cargo check --target x86_64-pc-windows-msvc
cargo test --target x86_64-pc-windows-msvc
cargo build --release --target x86_64-pc-windows-msvc
```

They require the MSVC target and Visual Studio C++ toolchain. A Linux
`x86_64-pc-windows-gnu` check can catch Rust target errors, but it is not a
substitute for the MSVC build or a Windows runtime test.

The runtime checklist should cover startup registration, the four global
hotkeys, quick-capture focus/placement/dismissal, Find selection, task links,
external links, monitor add/remove, mixed DPI, fullscreen applications,
single-instance enforcement, forced termination/restart recovery, and idle
CPU usage. Those checks are pending until a real Windows session is available.
