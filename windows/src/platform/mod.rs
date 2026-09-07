use crate::deck::{PanelGeometry, WorkArea};
use std::sync::mpsc::Sender;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HotkeyAction {
    NewNote,
    AllNotes,
    Archive,
    QuickCapture,
}

#[derive(Clone, Debug, PartialEq)]
pub struct DisplayInfo {
    /// A persisted display identity, not an `HMONITOR` handle. On Windows this
    /// is derived from the GDI device name, so it survives work-area, DPI, and
    /// resolution refreshes. Win32 can renumber `\\.\DISPLAYn` after a hardware
    /// or topology reset; a saved target then deliberately falls back to main.
    pub id: u64,
    pub work_area: WorkArea,
    pub primary: bool,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FocusTarget {
    pub(crate) hwnd: usize,
    pub(crate) process_id: u32,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum FocusRestoreOutcome {
    #[cfg(windows)]
    NotRequested,
    #[cfg(windows)]
    TargetNoLongerValid,
    #[cfg(windows)]
    ForegroundDenied,
    #[cfg(windows)]
    Restored,
    #[cfg(not(windows))]
    Unsupported,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HotkeyBinding {
    pub id: i32,
    pub action: HotkeyAction,
    pub shortcut: &'static str,
    pub description: &'static str,
}

pub const HOTKEY_BINDINGS: [HotkeyBinding; 4] = [
    HotkeyBinding {
        id: 1,
        action: HotkeyAction::NewNote,
        shortcut: "Ctrl+Alt+N",
        description: "New note",
    },
    HotkeyBinding {
        id: 2,
        action: HotkeyAction::AllNotes,
        shortcut: "Ctrl+Alt+A",
        description: "All notes",
    },
    HotkeyBinding {
        id: 3,
        action: HotkeyAction::Archive,
        shortcut: "Ctrl+Alt+L",
        description: "Archive",
    },
    HotkeyBinding {
        id: 4,
        action: HotkeyAction::QuickCapture,
        shortcut: "Ctrl+Shift+Space",
        description: "Quick capture",
    },
];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HotkeyRegistrationStatus {
    Registered,
    #[cfg(windows)]
    Unavailable {
        error_code: Option<u32>,
    },
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HotkeyRegistrationOutcome {
    pub binding: HotkeyBinding,
    pub status: HotkeyRegistrationStatus,
}

impl HotkeyRegistrationOutcome {
    pub(crate) fn registered(binding: HotkeyBinding) -> Self {
        Self {
            binding,
            status: HotkeyRegistrationStatus::Registered,
        }
    }

    #[cfg(windows)]
    pub(crate) fn unavailable(binding: HotkeyBinding, error_code: Option<u32>) -> Self {
        Self {
            binding,
            status: HotkeyRegistrationStatus::Unavailable { error_code },
        }
    }

    pub fn is_registered(self) -> bool {
        matches!(self.status, HotkeyRegistrationStatus::Registered)
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HitTestRect {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

impl HitTestRect {
    pub fn contains(self, x: i32, y: i32) -> bool {
        x >= self.x
            && y >= self.y
            && x < self.x.saturating_add(self.width as i32)
            && y < self.y.saturating_add(self.height as i32)
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum HitTestMode {
    Full,
    Regions(Vec<HitTestRect>),
}

impl HitTestMode {
    pub fn accepts(&self, x: i32, y: i32) -> bool {
        match self {
            Self::Full => true,
            Self::Regions(regions) => regions.iter().copied().any(|region| region.contains(x, y)),
        }
    }
}

#[cfg(not(windows))]
mod fallback;
#[cfg(windows)]
mod windows;

#[cfg(not(windows))]
use fallback as implementation;
#[cfg(windows)]
use windows as implementation;

pub use implementation::DisplayChangeHandle;
pub use implementation::HotkeyHandle;
pub use implementation::InstanceGuard;

pub struct HotkeyRegistration {
    outcomes: Vec<HotkeyRegistrationOutcome>,
    handle: Option<HotkeyHandle>,
}

impl HotkeyRegistration {
    pub(crate) fn new(
        handle: Option<HotkeyHandle>,
        outcomes: Vec<HotkeyRegistrationOutcome>,
    ) -> Self {
        Self { outcomes, handle }
    }

    #[cfg(windows)]
    pub(crate) fn unavailable_all(error_code: Option<u32>) -> Self {
        Self::new(
            None,
            HOTKEY_BINDINGS
                .iter()
                .copied()
                .map(|binding| HotkeyRegistrationOutcome::unavailable(binding, error_code))
                .collect(),
        )
    }

    pub fn outcomes(&self) -> &[HotkeyRegistrationOutcome] {
        &self.outcomes
    }

    #[cfg(windows)]
    pub fn failures(&self) -> impl Iterator<Item = &HotkeyRegistrationOutcome> {
        self.outcomes
            .iter()
            .filter(|outcome| !outcome.is_registered())
    }

    pub fn take_handle(&mut self) -> Option<HotkeyHandle> {
        self.handle.take()
    }
}

pub fn displays() -> Vec<DisplayInfo> {
    implementation::displays()
}

pub fn active_display(displays: &[DisplayInfo]) -> Option<DisplayInfo> {
    implementation::active_display(displays)
}

pub fn initialize() {
    implementation::initialize();
}

pub fn acquire_instance() -> std::io::Result<Option<InstanceGuard>> {
    implementation::acquire_instance()
}

pub fn configure_window(
    window: &slint::Window,
    frame: PanelGeometry,
    edge: bool,
    activate: bool,
    show_over_fullscreen: bool,
    hit_test: HitTestMode,
) {
    implementation::configure_window(
        window,
        frame,
        edge,
        activate,
        show_over_fullscreen,
        hit_test,
    );
}

pub fn apply_window_style(
    window: &slint::Window,
    activate: bool,
    show_over_fullscreen: bool,
    hit_test: HitTestMode,
) {
    implementation::apply_window_style(window, activate, show_over_fullscreen, hit_test);
}

pub fn centre_window(window: &slint::Window, width: u32, height: u32, display_id: u64) {
    implementation::centre_window(window, width, height, display_id);
}

pub fn position_capture_window(window: &slint::Window, width: u32, height: u32, display_id: u64) {
    implementation::position_capture_window(window, width, height, display_id);
}

pub fn activate_window(window: &slint::Window) {
    implementation::activate_window(window);
}

pub fn capture_foreground() -> Option<FocusTarget> {
    implementation::capture_foreground()
}

pub fn restore_foreground(target: Option<FocusTarget>) -> FocusRestoreOutcome {
    implementation::restore_foreground(target)
}

pub fn register_hotkeys(sender: Sender<HotkeyAction>) -> HotkeyRegistration {
    implementation::register_hotkeys(sender)
}

pub fn report_hotkey_registration(registration: &HotkeyRegistration) {
    implementation::report_hotkey_registration(registration);
}

pub fn watch_display_changes(callback: impl Fn() + Send + 'static) -> Option<DisplayChangeHandle> {
    implementation::watch_display_changes(callback)
}

pub fn set_launch_at_login(enabled: bool) -> Result<(), String> {
    implementation::set_launch_at_login(enabled)
}

pub fn display_is_fullscreen(display_id: u64) -> bool {
    implementation::display_is_fullscreen(display_id)
}

pub fn foreground_is_external() -> bool {
    implementation::foreground_is_external()
}

pub fn open_url(url: &str) -> bool {
    implementation::open_url(url)
}
