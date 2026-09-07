use super::{
    DisplayInfo, FocusRestoreOutcome, FocusTarget, HOTKEY_BINDINGS, HitTestMode, HotkeyAction,
    HotkeyRegistration, HotkeyRegistrationOutcome,
};
use crate::deck::{PanelGeometry, WorkArea};
use std::io;
use std::sync::mpsc::Sender;

pub struct HotkeyHandle;

pub struct DisplayChangeHandle;

pub struct InstanceGuard;

pub fn displays() -> Vec<DisplayInfo> {
    // This path exists for model/geometry tests and local UI iteration only.
    // Production builds use EnumDisplayMonitors in the Windows module.
    vec![DisplayInfo {
        id: 1,
        work_area: WorkArea {
            x: 0,
            y: 0,
            width: 1440,
            height: 900,
            dpi: 96,
        },
        primary: true,
    }]
}

pub fn active_display(displays: &[DisplayInfo]) -> Option<DisplayInfo> {
    displays
        .iter()
        .find(|display| display.primary)
        .cloned()
        .or_else(|| displays.first().cloned())
}

pub fn initialize() {}

pub fn acquire_instance() -> io::Result<Option<InstanceGuard>> {
    Ok(Some(InstanceGuard))
}

pub fn configure_window(
    window: &slint::Window,
    frame: PanelGeometry,
    _edge: bool,
    _activate: bool,
    _show_over_fullscreen: bool,
    _hit_test: HitTestMode,
) {
    window.set_position(slint::PhysicalPosition::new(frame.x, frame.y));
    window.set_size(slint::PhysicalSize::new(frame.width, frame.height));
}

pub fn apply_window_style(
    _window: &slint::Window,
    _activate: bool,
    _show_over_fullscreen: bool,
    _hit_test: HitTestMode,
) {
}

pub fn centre_window(window: &slint::Window, width: u32, height: u32, _display_id: u64) {
    window.set_size(slint::PhysicalSize::new(width, height));
}

pub fn position_capture_window(window: &slint::Window, width: u32, height: u32, _display_id: u64) {
    let display = displays().into_iter().next().expect("fallback display");
    let x = display.work_area.x + (display.work_area.width as i32 - width as i32) / 2;
    let y = display.work_area.y
        + ((display.work_area.height.saturating_sub(height) as f32) * 0.42).round() as i32;
    window.set_position(slint::PhysicalPosition::new(x, y));
    window.set_size(slint::PhysicalSize::new(width, height));
}

pub fn activate_window(_window: &slint::Window) {}

pub fn capture_foreground() -> Option<FocusTarget> {
    None
}

pub fn restore_foreground(_target: Option<FocusTarget>) -> FocusRestoreOutcome {
    FocusRestoreOutcome::Unsupported
}

pub fn register_hotkeys(_sender: Sender<HotkeyAction>) -> HotkeyRegistration {
    HotkeyRegistration::new(
        Some(HotkeyHandle),
        HOTKEY_BINDINGS
            .iter()
            .copied()
            .map(HotkeyRegistrationOutcome::registered)
            .collect(),
    )
}

pub fn report_hotkey_registration(_registration: &HotkeyRegistration) {}

pub fn watch_display_changes(_callback: impl Fn() + Send + 'static) -> Option<DisplayChangeHandle> {
    None
}

pub fn set_launch_at_login(_enabled: bool) -> Result<(), String> {
    Ok(())
}

pub fn display_is_fullscreen(_display_id: u64) -> bool {
    false
}

pub fn foreground_is_external() -> bool {
    false
}

pub fn set_hover_flag(
    _window: &slint::Window,
    _flag: *const std::sync::atomic::AtomicBool,
) {
}

pub fn open_url(_url: &str) -> bool {
    false
}