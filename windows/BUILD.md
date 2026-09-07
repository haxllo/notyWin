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

The manifest uses Slint's winit backend directly with the optional `muda`
feature enabled for native popup menus. No system-tray UI is added. Muda 0.19.3
is vendored under `vendor/muda` with its subclass entry points resolved at
runtime instead of becoming static `comctl32` imports. The small Slint backend
compatibility patch is also vendored under `vendor/i-slint-backend-winit`, so
clean builds use the same dependency graph.

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

The host suite also covers the fan handoff state and geometry cases used by the
native edge bridge, including transformed tabs, edge controls, and blank fan
areas. The bridge deliberately returns `HTCLIENT` only for accepted regions;
blank fan pixels remain `HTTRANSPARENT`, and the nonactivating window returns
`MA_NOACTIVATE`. These are source-level/host checks, not proof of actual HWND
behavior on Windows.

For a Linux GNU cross-target release smoke test, the PE artifact and import
table can be inspected with:

```bash
cargo build --manifest-path windows/Cargo.toml --release --target x86_64-pc-windows-gnu
objdump -p windows/target/x86_64-pc-windows-gnu/release/noty-win.exe
```

The current audit produces an 11,456,512-byte PE32+ x64 executable with no
static `comctl32.dll` import. The target feature graph also includes `muda`,
`raw-window-handle-06`, and `renderer-femtovg`; the subclass names remain in
the binary only because the vendored runtime loader resolves them by name.
This confirms GNU linking and the import-table constraint only; it is not a
substitute for the MSVC build or Windows runtime checks above.
