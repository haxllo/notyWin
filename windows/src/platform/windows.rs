use super::{
    DisplayInfo, FocusRestoreOutcome, FocusTarget, HOTKEY_BINDINGS, HitTestMode, HotkeyAction,
    HotkeyBinding, HotkeyRegistration, HotkeyRegistrationOutcome, HotkeyRegistrationStatus,
};
use crate::deck::{PanelGeometry, WorkArea};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use std::{
    ffi::c_void,
    mem, ptr,
    sync::{
        Mutex, OnceLock,
        mpsc::{self, Sender},
    },
    thread,
};
use windows_sys::{
    Win32::{
        Foundation::{
            CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, HWND, LPARAM, LRESULT, POINT,
            RECT,
        },
        Graphics::Gdi::{
            EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITOR_DEFAULTTONEAREST,
            MONITORINFO, MONITORINFOEXW, MonitorFromPoint,
        },
        System::Registry::{
            HKEY, HKEY_CURRENT_USER, KEY_SET_VALUE, REG_SZ, RegCloseKey, RegCreateKeyExW,
            RegDeleteValueW, RegSetValueExW,
        },
        System::{
            LibraryLoader::{GetModuleHandleW, GetProcAddress},
            Threading::{CreateMutexW, GetCurrentProcessId, GetCurrentThreadId},
        },
        UI::{
            HiDpi::{
                DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, GetDpiForMonitor, MDT_EFFECTIVE_DPI,
                PROCESS_PER_MONITOR_DPI_AWARE, SetProcessDpiAwareness,
            },
            Input::KeyboardAndMouse::{
                MOD_ALT, MOD_CONTROL, MOD_NOREPEAT, MOD_SHIFT, RegisterHotKey, TME_LEAVE,
                TRACKMOUSEEVENT, TrackMouseEvent, UnregisterHotKey,
            },
            Controls::WM_MOUSELEAVE,
            Shell::ShellExecuteW,
            WindowsAndMessaging::{
                CREATESTRUCTW, CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW,
                DestroyWindow, DispatchMessageW, GWL_EXSTYLE, GWL_STYLE, GWLP_USERDATA,
                GetClassNameW, GetCursorPos, GetDesktopWindow, GetForegroundWindow, GetMessageW,
                GetShellWindow, GetWindowLongPtrW, GetWindowRect,
                GetWindowThreadProcessId, HTTRANSPARENT, HWND_NOTOPMOST, HWND_TOP, HWND_TOPMOST,
                IsIconic, IsWindow, IsWindowVisible, MB_ICONWARNING, MB_OK, MSG, MessageBoxW,
                PM_NOREMOVE, PeekMessageW, PostThreadMessageW, RegisterClassW, SW_SHOWNORMAL,
                SWP_FRAMECHANGED, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOOWNERZORDER, SWP_NOSIZE,
                SetForegroundWindow, SetWindowLongPtrW, SetWindowPos, TranslateMessage, WM_APP,
                WM_DISPLAYCHANGE, WM_DPICHANGED, WM_HOTKEY, WM_MOUSEMOVE,
                WM_NCCREATE, WM_NCDESTROY,
                WM_NCHITTEST, WM_QUIT, WM_SETTINGCHANGE, WNDCLASSW, WS_CAPTION, WS_EX_APPWINDOW,
                WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW, WS_MAXIMIZEBOX, WS_MINIMIZEBOX, WS_POPUP,
                WS_SYSMENU, WS_THICKFRAME,
            },
        },
    },
    core::BOOL,
};

const VK_N: u32 = 0x4E;
const VK_A: u32 = 0x41;
const VK_L: u32 = 0x4C;
const VK_SPACE: u32 = 0x20;

pub fn initialize() {
    // Set the process mode before Slint creates its first HWND so mixed-DPI
    // monitor geometry stays in physical pixels.
    unsafe {
        if !set_process_dpi_awareness_v2() {
            let _ = SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
        }
        load_comctl32();
    }
}

type SubclassProc =
    unsafe extern "system" fn(HWND, u32, usize, isize, usize, usize) -> LRESULT;
type FnDefSubclassProc =
    unsafe extern "system" fn(HWND, u32, usize, isize) -> LRESULT;
type FnGetWindowSubclass =
    unsafe extern "system" fn(HWND, Option<SubclassProc>, usize, *mut usize) -> i32;
type FnSetWindowSubclass =
    unsafe extern "system" fn(HWND, Option<SubclassProc>, usize, usize) -> i32;
type FnRemoveWindowSubclass =
    unsafe extern "system" fn(HWND, Option<SubclassProc>, usize) -> i32;

struct Comctl32Fns {
    def_subclass_proc: FnDefSubclassProc,
    get_window_subclass: FnGetWindowSubclass,
    set_window_subclass: FnSetWindowSubclass,
    remove_window_subclass: FnRemoveWindowSubclass,
}

static COMCTL32: OnceLock<Comctl32Fns> = OnceLock::new();

unsafe fn load_comctl32() {
    let name = widestring("comctl32.dll");
    let module = unsafe { GetModuleHandleW(name.as_ptr()) };
    if module.is_null() {
        return;
    }
    macro_rules! load {
        ($sym:expr) => {{
            let bytes = concat!($sym, "\0").as_bytes();
            unsafe { GetProcAddress(module, bytes.as_ptr()) }
                .map(|p| unsafe { mem::transmute(p) })
        }};
    }
    let Some(def_subclass_proc) = load!("DefSubclassProc") else { return };
    let Some(get_window_subclass) = load!("GetWindowSubclass") else { return };
    let Some(set_window_subclass) = load!("SetWindowSubclass") else { return };
    let Some(remove_window_subclass) = load!("RemoveWindowSubclass") else { return };
    let _ = COMCTL32.set(Comctl32Fns {
        def_subclass_proc,
        get_window_subclass,
        set_window_subclass,
        remove_window_subclass,
    });
}

unsafe fn set_process_dpi_awareness_v2() -> bool {
    let module_name = widestring("user32.dll");
    let module = unsafe { GetModuleHandleW(module_name.as_ptr()) };
    if module.is_null() {
        return false;
    }
    let name = b"SetProcessDpiAwarenessContext\0";
    let Some(procedure) = (unsafe { GetProcAddress(module, name.as_ptr()) }) else {
        return false;
    };
    let set_context: unsafe extern "system" fn(*mut c_void) -> BOOL =
        unsafe { mem::transmute(procedure) };
    unsafe { set_context(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) != 0 }
}

pub struct InstanceGuard {
    handle: HANDLE,
}

impl Drop for InstanceGuard {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.handle);
        }
    }
}

pub fn acquire_instance() -> std::io::Result<Option<InstanceGuard>> {
    let name = widestring("Local\\NotyWin.SingleInstance");
    let handle = unsafe { CreateMutexW(ptr::null(), 1, name.as_ptr()) };
    if handle.is_null() {
        return Err(std::io::Error::last_os_error());
    }
    if unsafe { GetLastError() } == ERROR_ALREADY_EXISTS {
        unsafe {
            let _ = CloseHandle(handle);
        }
        return Ok(None);
    }
    Ok(Some(InstanceGuard { handle }))
}

pub fn displays() -> Vec<DisplayInfo> {
    let mut result: Vec<DisplayInfo> = Vec::new();
    let ptr = &mut result as *mut Vec<DisplayInfo> as LPARAM;
    unsafe {
        EnumDisplayMonitors(ptr::null_mut(), ptr::null(), Some(enumerate_monitor), ptr);
    }
    result.sort_by_key(|display| (!display.primary, display.id));
    result
}

pub fn active_display(displays: &[DisplayInfo]) -> Option<DisplayInfo> {
    let mut point = POINT { x: 0, y: 0 };
    let monitor = unsafe {
        if GetCursorPos(&mut point) == 0 {
            return displays
                .iter()
                .find(|display| display.primary)
                .cloned()
                .or_else(|| displays.first().cloned());
        }
        MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST)
    };
    let id = unsafe { monitor_device_id(monitor) }.unwrap_or_default();
    displays
        .iter()
        .find(|display| display.id == id)
        .cloned()
        .or_else(|| displays.iter().find(|display| display.primary).cloned())
        .or_else(|| displays.first().cloned())
}

unsafe extern "system" fn enumerate_monitor(
    monitor: HMONITOR,
    _dc: HDC,
    _clip: *mut RECT,
    data: LPARAM,
) -> BOOL {
    let result = unsafe { &mut *(data as *mut Vec<DisplayInfo>) };
    let Some(info) = (unsafe { monitor_info(monitor) }) else {
        return 1;
    };
    let rect = info.monitorInfo.rcWork;
    let id = unsafe { monitor_device_id(monitor) }.unwrap_or_default();
    let mut dpi = 96;
    let mut dpi_y = 96;
    if unsafe { GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dpi, &mut dpi_y) } != 0 {
        dpi = 96;
    }
    result.push(DisplayInfo {
        id,
        work_area: WorkArea {
            x: rect.left,
            y: rect.top,
            width: (rect.right - rect.left).max(1) as u32,
            height: (rect.bottom - rect.top).max(1) as u32,
            dpi: dpi.max(96),
        },
        primary: info.monitorInfo.dwFlags != 0,
    });
    1
}
unsafe fn monitor_info(monitor: HMONITOR) -> Option<MONITORINFOEXW> {
    let mut info: MONITORINFOEXW = unsafe { mem::zeroed() };
    info.monitorInfo.cbSize = mem::size_of::<MONITORINFOEXW>() as u32;
    if unsafe {
        GetMonitorInfoW(
            monitor,
            &mut info as *mut MONITORINFOEXW as *mut MONITORINFO,
        )
    } == 0
    {
        None
    } else {
        Some(info)
    }
}

unsafe fn monitor_device_id(monitor: HMONITOR) -> Option<u64> {
    let info = unsafe { monitor_info(monitor) }?;
    let end = info
        .szDevice
        .iter()
        .position(|character| *character == 0)
        .unwrap_or(info.szDevice.len());
    // The GDI device name is stable while the active Windows display topology
    // is refreshed, unlike HMONITOR. Windows may renumber it after a hardware
    // or topology reset; retain the established persisted-ID behavior and let
    // the controller choose its documented primary-display fallback then.
    (end > 0).then(|| stable_display_id(&info.szDevice[..end]))
}

fn stable_display_id(device_name: &[u16]) -> u64 {
    let mut hash = 0xcbf29ce484222325u64;
    for character in device_name {
        hash ^= *character as u64;
        hash = hash.wrapping_mul(0x100000001b3);
    }
    if hash == 0 { 1 } else { hash }
}

struct MonitorSearch {
    id: u64,
    monitor: HMONITOR,
}

unsafe extern "system" fn find_monitor(
    monitor: HMONITOR,
    _dc: HDC,
    _clip: *mut RECT,
    data: LPARAM,
) -> BOOL {
    let search = unsafe { &mut *(data as *mut MonitorSearch) };
    if unsafe { monitor_device_id(monitor) } == Some(search.id) {
        search.monitor = monitor;
        0
    } else {
        1
    }
}

fn monitor_for_display_id(display_id: u64) -> Option<HMONITOR> {
    let mut search = MonitorSearch {
        id: display_id,
        monitor: ptr::null_mut(),
    };
    let data = &mut search as *mut MonitorSearch as LPARAM;
    unsafe {
        EnumDisplayMonitors(ptr::null_mut(), ptr::null(), Some(find_monitor), data);
    }
    (!search.monitor.is_null()).then_some(search.monitor)
}

pub fn configure_window(
    window: &slint::Window,
    frame: PanelGeometry,
    _edge: bool,
    activate: bool,
    show_over_fullscreen: bool,
    hit_test: HitTestMode,
) {
    window.set_position(slint::PhysicalPosition::new(frame.x, frame.y));
    window.set_size(slint::PhysicalSize::new(frame.width, frame.height));
    if let Some(hwnd) = hwnd_for(window) {
        unsafe {
            update_window_subclass(hwnd, hit_test.clone(), WindowLayer::Deck);
            apply_window_style_to_hwnd(hwnd, Some(frame), activate, show_over_fullscreen, hit_test);
        }
    }
}

pub fn apply_window_style(
    window: &slint::Window,
    activate: bool,
    show_over_fullscreen: bool,
    hit_test: HitTestMode,
) {
    if let Some(hwnd) = hwnd_for(window) {
        unsafe {
            apply_window_style_to_hwnd(hwnd, None, activate, show_over_fullscreen, hit_test);
        }
    }
}

pub fn set_hover_flag(window: &slint::Window, flag: *const std::sync::atomic::AtomicBool) {
    let Some(hwnd) = hwnd_for(window) else { return };
    let Some(fns) = COMCTL32.get() else { return };
    let mut reference_data = 0usize;
    if unsafe {
        (fns.get_window_subclass)(
            hwnd,
            Some(hit_test_subclass),
            HIT_TEST_SUBCLASS_ID,
            &mut reference_data,
        )
    } != 0 && reference_data != 0
    {
        unsafe {
            (*(reference_data as *mut HitTestState)).hover_flag = Some(flag);
        }
    }
}

unsafe fn apply_window_style_to_hwnd(
    hwnd: HWND,
    frame: Option<PanelGeometry>,
    activate: bool,
    show_over_fullscreen: bool,
    hit_test: HitTestMode,
) {
    let style = unsafe { GetWindowLongPtrW(hwnd, GWL_STYLE) };
    let without_chrome = style
        & !(WS_CAPTION as isize
            | WS_THICKFRAME as isize
            | WS_MINIMIZEBOX as isize
            | WS_MAXIMIZEBOX as isize
            | WS_SYSMENU as isize);
    unsafe {
        SetWindowLongPtrW(hwnd, GWL_STYLE, without_chrome);
    }

    let mut extended = (unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) }
        | WS_EX_TOOLWINDOW as isize)
        & !(WS_EX_APPWINDOW as isize);
    if !activate {
        extended |= WS_EX_NOACTIVATE as isize;
    } else {
        extended &= !(WS_EX_NOACTIVATE as isize);
    }
    unsafe {
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, extended);
    }
    unsafe {
        update_hit_test_subclass(hwnd, hit_test);
    }
    let mut flags = SWP_NOOWNERZORDER | SWP_FRAMECHANGED | SWP_NOACTIVATE;
    let (x, y, width, height) = if let Some(frame) = frame {
        (frame.x, frame.y, frame.width as i32, frame.height as i32)
    } else {
        flags |= SWP_NOMOVE | SWP_NOSIZE;
        (0, 0, 0, 0)
    };
    // Fullscreen suppression is decided by the controller before a deck is
    // shown. Keep deck windows topmost independently so normal applications
    // cannot cover the edge pill while the suppression preference is off.
    let insert_after = if window_layer(hwnd) == WindowLayer::Deck || show_over_fullscreen {
        HWND_TOPMOST
    } else {
        HWND_NOTOPMOST
    };
    unsafe {
        SetWindowPos(hwnd, insert_after, x, y, width, height, flags);
    }
}

const HIT_TEST_SUBCLASS_ID: usize = 0x4E_54;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum WindowLayer {
    Application,
    Deck,
}

struct HitTestState {
    mode: HitTestMode,
    layer: WindowLayer,
    hover_flag: Option<*const std::sync::atomic::AtomicBool>,
    mouse_tracked: bool,
}

unsafe extern "system" fn hit_test_subclass(
    hwnd: HWND,
    message: u32,
    wparam: usize,
    lparam: isize,
    _subclass_id: usize,
    reference_data: usize,
) -> LRESULT {
    if message == WM_DPICHANGED {
        request_display_refresh();
    }
    if message == WM_NCHITTEST {
        let state = reference_data as *const HitTestState;
        if !state.is_null() {
            let mut window_rect = RECT::default();
            if unsafe { GetWindowRect(hwnd, &mut window_rect) } != 0 {
                let screen_x = (lparam as u32 as u16) as i16 as i32;
                let screen_y = ((lparam as u32 >> 16) as u16) as i16 as i32;
                let local_x = screen_x - window_rect.left;
                let local_y = screen_y - window_rect.top;
                if !unsafe { (*state).mode.accepts(local_x, local_y) } {
                    return HTTRANSPARENT as LRESULT;
                }
            }
        }
    }
    if message == WM_MOUSEMOVE && reference_data != 0 {
        let state = reference_data as *mut HitTestState;
        unsafe {
            if !(*state).mouse_tracked {
                (*state).mouse_tracked = true;
                let mut tme = TRACKMOUSEEVENT {
                    cbSize: mem::size_of::<TRACKMOUSEEVENT>() as u32,
                    dwFlags: TME_LEAVE,
                    hwndTrack: hwnd,
                    dwHoverTime: 0,
                };
                TrackMouseEvent(&mut tme);
            }
            if let Some(flag_ptr) = (*state).hover_flag {
                (*flag_ptr).store(true, std::sync::atomic::Ordering::Relaxed);
            }
        }
    }
    if message == WM_MOUSELEAVE && reference_data != 0 {
        let state = reference_data as *mut HitTestState;
        unsafe {
            (*state).mouse_tracked = false;
            if let Some(flag_ptr) = (*state).hover_flag {
                (*flag_ptr).store(false, std::sync::atomic::Ordering::Relaxed);
            }
        }
    }

    let Some(fns) = COMCTL32.get() else { return 0 };
    let result = unsafe { (fns.def_subclass_proc)(hwnd, message, wparam, lparam) };
    if message == WM_NCDESTROY && reference_data != 0 {
        unsafe {
            let _ = (fns.remove_window_subclass)(hwnd, Some(hit_test_subclass), HIT_TEST_SUBCLASS_ID);
            drop(Box::from_raw(reference_data as *mut HitTestState));
        }
    }
    result
}

unsafe fn update_hit_test_subclass(hwnd: HWND, mode: HitTestMode) {
    let layer = window_layer(hwnd);
    unsafe {
        update_window_subclass(hwnd, mode, layer);
    }
}

unsafe fn update_window_subclass(hwnd: HWND, mode: HitTestMode, layer: WindowLayer) {
    let Some(fns) = COMCTL32.get() else { return };
    let mut reference_data = 0usize;
    if unsafe {
        (fns.get_window_subclass)(
            hwnd,
            Some(hit_test_subclass),
            HIT_TEST_SUBCLASS_ID,
            &mut reference_data,
        )
    } != 0
    {
        if reference_data != 0 {
            unsafe {
                (*((reference_data) as *mut HitTestState)).mode = mode;
                (*((reference_data) as *mut HitTestState)).layer = layer;
            }
        }
        return;
    }
    let state = Box::into_raw(Box::new(HitTestState {
        mode,
        layer,
        hover_flag: None,
        mouse_tracked: false,
    }));
    if unsafe {
        (fns.set_window_subclass)(
            hwnd,
            Some(hit_test_subclass),
            HIT_TEST_SUBCLASS_ID,
            state as usize,
        )
    } == 0
    {
        unsafe {
            drop(Box::from_raw(state));
        }
    }
}

fn window_layer(hwnd: HWND) -> WindowLayer {
    let mut reference_data = 0usize;
    if let Some(fns) = COMCTL32.get() {
        if unsafe {
            (fns.get_window_subclass)(
                hwnd,
                Some(hit_test_subclass),
                HIT_TEST_SUBCLASS_ID,
                &mut reference_data,
            )
        } != 0
            && reference_data != 0
        {
            return unsafe { (*(reference_data as *const HitTestState)).layer };
        }
    }
    WindowLayer::Application
}

fn mark_as_application_window(window: &slint::Window) {
    if let Some(hwnd) = hwnd_for(window) {
        unsafe {
            update_window_subclass(hwnd, HitTestMode::Full, WindowLayer::Application);
        }
    }
}

pub fn centre_window(window: &slint::Window, width: u32, height: u32, display_id: u64) {
    mark_as_application_window(window);
    if let Some(display) = displays()
        .into_iter()
        .find(|display| display.id == display_id)
        .or_else(|| displays().into_iter().find(|display| display.primary))
    {
        let scale = display.work_area.logical_scale();
        let width = (width as f32 * scale).round() as u32;
        let height = (height as f32 * scale).round() as u32;
        let x = display.work_area.x + (display.work_area.width as i32 - width as i32) / 2;
        let y = display.work_area.y + (display.work_area.height as i32 - height as i32) / 2;
        window.set_position(slint::PhysicalPosition::new(x, y));
        window.set_size(slint::PhysicalSize::new(width, height));
        return;
    }
    window.set_size(slint::PhysicalSize::new(width, height));
}

pub fn position_capture_window(window: &slint::Window, width: u32, height: u32, display_id: u64) {
    mark_as_application_window(window);
    if let Some(display) = displays()
        .into_iter()
        .find(|display| display.id == display_id)
        .or_else(|| displays().into_iter().find(|display| display.primary))
    {
        let scale = display.work_area.logical_scale();
        let width = (width as f32 * scale).round() as u32;
        let height = (height as f32 * scale).round() as u32;
        let x = display.work_area.x + (display.work_area.width as i32 - width as i32) / 2;
        // Keep capture near the visual centre without covering the edge deck.
        let y = display.work_area.y
            + ((display.work_area.height.saturating_sub(height) as f32) * 0.42).round() as i32;
        window.set_position(slint::PhysicalPosition::new(x, y));
        window.set_size(slint::PhysicalSize::new(width, height));
        return;
    }
    window.set_size(slint::PhysicalSize::new(width, height));
}

pub fn activate_window(window: &slint::Window) {
    let Some(hwnd) = hwnd_for(window) else { return };
    unsafe {
        let extended = GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & !(WS_EX_NOACTIVATE as isize);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, extended);
        SetWindowPos(
            hwnd,
            HWND_TOP,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED,
        );
        SetForegroundWindow(hwnd);
    }
}

pub fn capture_foreground() -> Option<FocusTarget> {
    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd.is_null() || unsafe { IsWindow(hwnd) } == 0 {
        return None;
    }
    let mut process_id = 0;
    if unsafe { GetWindowThreadProcessId(hwnd, &mut process_id) } == 0 || process_id == 0 {
        return None;
    }
    Some(FocusTarget {
        hwnd: hwnd as usize,
        process_id,
    })
}

pub fn foreground_is_external() -> bool {
    unsafe {
        let foreground = GetForegroundWindow();
        if foreground.is_null() {
            return true;
        }
        let mut process_id = 0;
        GetWindowThreadProcessId(foreground, &mut process_id);
        process_id != GetCurrentProcessId()
    }
}

pub fn restore_foreground(target: Option<FocusTarget>) -> FocusRestoreOutcome {
    let Some(target) = target else {
        return FocusRestoreOutcome::NotRequested;
    };
    let hwnd = target.hwnd as HWND;
    unsafe {
        if hwnd.is_null()
            || IsWindow(hwnd) == 0
            || IsWindowVisible(hwnd) == 0
            || IsIconic(hwnd) != 0
        {
            return FocusRestoreOutcome::TargetNoLongerValid;
        }
        let mut process_id = 0;
        if GetWindowThreadProcessId(hwnd, &mut process_id) == 0 || process_id != target.process_id {
            return FocusRestoreOutcome::TargetNoLongerValid;
        }
        if SetForegroundWindow(hwnd) == 0 || GetForegroundWindow() != hwnd {
            // Windows can deny a focus steal even for a valid HWND. Do not
            // bypass that policy or disturb the user's current foreground.
            return FocusRestoreOutcome::ForegroundDenied;
        }
    }
    FocusRestoreOutcome::Restored
}

pub fn open_url(url: &str) -> bool {
    let url = url.trim();
    let Some((scheme, _)) = url.split_once(':') else {
        return false;
    };
    if !matches!(
        scheme.to_ascii_lowercase().as_str(),
        "http" | "https" | "mailto"
    ) {
        return false;
    }
    let operation = widestring("open");
    let target = widestring(url);
    unsafe {
        ShellExecuteW(
            ptr::null_mut(),
            operation.as_ptr(),
            target.as_ptr(),
            ptr::null(),
            ptr::null(),
            SW_SHOWNORMAL,
        ) as usize
            > 32
    }
}

pub fn display_is_fullscreen(display_id: u64) -> bool {
    unsafe {
        let foreground = GetForegroundWindow();
        if foreground.is_null() {
            return false;
        }
        let shell = GetShellWindow();
        if foreground == GetDesktopWindow() || foreground == shell {
            return false;
        }
        let mut process_id = 0;
        GetWindowThreadProcessId(foreground, &mut process_id);
        if process_id == GetCurrentProcessId() {
            return false;
        }
        let mut shell_process_id = 0;
        if !shell.is_null() {
            GetWindowThreadProcessId(shell, &mut shell_process_id);
        }
        let mut class_name = [0u16; 256];
        let class_len = GetClassNameW(foreground, class_name.as_mut_ptr(), class_name.len() as i32);
        if is_shell_desktop_class(&class_name[..class_len as usize], process_id, shell_process_id) {
            return false;
        }
        if IsIconic(foreground) != 0 || IsWindowVisible(foreground) == 0 {
            return false;
        }
        let style = GetWindowLongPtrW(foreground, GWL_STYLE) as u32;
        if style & WS_POPUP == 0 && style & (WS_CAPTION | WS_THICKFRAME) != 0 {
            return false;
        }
        // HMONITOR values are process-local handles and are not safe to persist.
        // Resolve the current handle from the stable device-name ID on every poll.
        let Some(monitor) = monitor_for_display_id(display_id) else {
            return false;
        };
        let mut window_rect = RECT {
            left: 0,
            top: 0,
            right: 0,
            bottom: 0,
        };
        if GetWindowRect(foreground, &mut window_rect) == 0 {
            return false;
        }
        let mut monitor_info = MONITORINFO {
            cbSize: mem::size_of::<MONITORINFO>() as u32,
            ..mem::zeroed()
        };
        if GetMonitorInfoW(monitor, &mut monitor_info) == 0 {
            return false;
        }
        window_rect.left <= monitor_info.rcMonitor.left
            && window_rect.top <= monitor_info.rcMonitor.top
            && window_rect.right >= monitor_info.rcMonitor.right
            && window_rect.bottom >= monitor_info.rcMonitor.bottom
    }
}

fn is_shell_desktop_class(class_name: &[u16], process_id: u32, shell_process_id: u32) -> bool {
    // Explorer also owns normal app windows. Require both a desktop/taskbar
    // class and shell ownership; class names alone can belong to other apps.
    shell_process_id != 0
        && process_id == shell_process_id
        && ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"]
            .iter()
            .any(|name| class_name.iter().copied().eq(name.encode_utf16()))
}

#[cfg(test)]
mod fullscreen_tests {
    use super::is_shell_desktop_class;

    #[test]
    fn excludes_desktop_and_taskbar_classes_owned_by_shell() {
        for name in ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"] {
            let class_name: Vec<u16> = name.encode_utf16().collect();
            assert!(is_shell_desktop_class(&class_name, 42, 42), "{name}");
        }
    }

    #[test]
    fn preserves_explorer_folders_and_fullscreen_app_classes() {
        for name in [
            "CabinetWClass",
            "ExploreWClass",
            "Chrome_WidgetWin_1",
            "SDL_app",
            "WorkerWApp",
            "",
        ] {
            let class_name: Vec<u16> = name.encode_utf16().collect();
            assert!(!is_shell_desktop_class(&class_name, 42, 42), "{name}");
        }
    }

    #[test]
    fn requires_known_shell_ownership_even_for_desktop_class_names() {
        for name in ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"] {
            let class_name: Vec<u16> = name.encode_utf16().collect();
            for (process_id, shell_process_id) in [(7, 42), (0, 42), (42, 0), (0, 0)] {
                assert!(
                    !is_shell_desktop_class(&class_name, process_id, shell_process_id),
                    "{name}: process={process_id}, shell={shell_process_id}"
                );
            }
        }
    }
}

const DISPLAY_REFRESH_MESSAGE: u32 = WM_APP + 0x4E;

fn display_refresh_thread() -> &'static Mutex<Option<u32>> {
    static THREAD_ID: OnceLock<Mutex<Option<u32>>> = OnceLock::new();
    THREAD_ID.get_or_init(|| Mutex::new(None))
}

fn set_display_refresh_thread(thread_id: u32) {
    *display_refresh_thread()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(thread_id);
}

fn clear_display_refresh_thread(thread_id: u32) {
    let mut registered_thread = display_refresh_thread()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if *registered_thread == Some(thread_id) {
        *registered_thread = None;
    }
}

fn request_display_refresh() {
    let thread_id = *display_refresh_thread()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    if let Some(thread_id) = thread_id {
        unsafe {
            let _ = PostThreadMessageW(thread_id, DISPLAY_REFRESH_MESSAGE, 0, 0);
        }
    }
}

struct DisplayWatcherState {
    callback: Box<dyn Fn() + Send>,
}

pub struct DisplayChangeHandle {
    thread_id: u32,
    join: Option<thread::JoinHandle<()>>,
}

impl Drop for DisplayChangeHandle {
    fn drop(&mut self) {
        clear_display_refresh_thread(self.thread_id);
        unsafe {
            if self.thread_id != 0 {
                let _ = PostThreadMessageW(self.thread_id, WM_QUIT, 0, 0);
            }
        }
        if let Some(join) = self.join.take() {
            let _ = join.join();
        }
    }
}

unsafe extern "system" fn display_watcher_window_proc(
    hwnd: HWND,
    message: u32,
    wparam: usize,
    lparam: isize,
) -> LRESULT {
    if message == WM_NCCREATE {
        let create = unsafe { &*(lparam as *const CREATESTRUCTW) };
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, create.lpCreateParams as isize);
        }
    } else if message == WM_DISPLAYCHANGE || message == WM_SETTINGCHANGE || message == WM_DPICHANGED
    {
        let state = unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) } as *mut DisplayWatcherState;
        if !state.is_null() {
            (unsafe { &*state }.callback)();
        }
    } else if message == WM_NCDESTROY {
        unsafe {
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
        }
    }
    unsafe { DefWindowProcW(hwnd, message, wparam, lparam) }
}

pub fn watch_display_changes(callback: impl Fn() + Send + 'static) -> Option<DisplayChangeHandle> {
    let (ready_sender, ready_receiver) = mpsc::sync_channel(1);
    let join = thread::Builder::new()
        .name("noty-display-watcher".to_owned())
        .spawn(move || unsafe {
            let state = Box::into_raw(Box::new(DisplayWatcherState {
                callback: Box::new(callback),
            }));
            let class_name = widestring("NotyDisplayWatcher");
            let class = WNDCLASSW {
                style: CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc: Some(display_watcher_window_proc),
                hInstance: GetModuleHandleW(ptr::null()),
                lpszClassName: class_name.as_ptr(),
                ..WNDCLASSW::default()
            };
            let _ = RegisterClassW(&class);
            let hwnd = CreateWindowExW(
                0,
                class_name.as_ptr(),
                class_name.as_ptr(),
                WS_POPUP,
                0,
                0,
                0,
                0,
                ptr::null_mut(),
                ptr::null_mut(),
                class.hInstance,
                state.cast(),
            );
            if hwnd.is_null() {
                drop(Box::from_raw(state));
                let _ = ready_sender.send(None);
                return;
            }

            let thread_id = GetCurrentThreadId();
            let _ = ready_sender.send(Some(thread_id));
            let mut message: MSG = mem::zeroed();
            while GetMessageW(&mut message, ptr::null_mut(), 0, 0) > 0 {
                if message.message == DISPLAY_REFRESH_MESSAGE {
                    (state.as_ref().expect("display watcher state").callback)();
                    continue;
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            DestroyWindow(hwnd);
            drop(Box::from_raw(state));
        })
        .ok()?;
    let Some(thread_id) = ready_receiver.recv().ok().flatten() else {
        let _ = join.join();
        return None;
    };
    set_display_refresh_thread(thread_id);
    Some(DisplayChangeHandle {
        thread_id,
        join: Some(join),
    })
}

pub struct HotkeyHandle {
    thread_id: u32,
    join: Option<thread::JoinHandle<()>>,
}

impl Drop for HotkeyHandle {
    fn drop(&mut self) {
        unsafe {
            if self.thread_id != 0 {
                let _ = PostThreadMessageW(self.thread_id, WM_QUIT, 0, 0);
            }
        }
        if let Some(join) = self.join.take() {
            let _ = join.join();
        }
    }
}

fn hotkey_key(binding: HotkeyBinding) -> (u32, u32) {
    match binding.action {
        HotkeyAction::NewNote => (MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, VK_N),
        HotkeyAction::AllNotes => (MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, VK_A),
        HotkeyAction::Archive => (MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, VK_L),
        HotkeyAction::QuickCapture => (MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_SPACE),
    }
}

pub fn register_hotkeys(sender: Sender<HotkeyAction>) -> HotkeyRegistration {
    let (ready_sender, ready_receiver) = mpsc::sync_channel(1);
    let join = match thread::Builder::new()
        .name("noty-hotkeys".to_owned())
        .spawn(move || unsafe {
            let thread_id = GetCurrentThreadId();
            let mut message: MSG = mem::zeroed();
            let _ = PeekMessageW(&mut message, ptr::null_mut(), 0, 0, PM_NOREMOVE);
            let mut registered = Vec::new();
            let mut outcomes = Vec::with_capacity(HOTKEY_BINDINGS.len());
            for binding in HOTKEY_BINDINGS {
                let (modifiers, key) = hotkey_key(binding);
                if RegisterHotKey(ptr::null_mut(), binding.id, modifiers, key) != 0 {
                    registered.push(binding.id);
                    outcomes.push(HotkeyRegistrationOutcome::registered(binding));
                } else {
                    outcomes.push(HotkeyRegistrationOutcome::unavailable(
                        binding,
                        Some(GetLastError()),
                    ));
                }
            }
            let any_registered = !registered.is_empty();
            if ready_sender
                .send((thread_id, outcomes, any_registered))
                .is_err()
            {
                for id in registered {
                    UnregisterHotKey(ptr::null_mut(), id);
                }
                return;
            }
            if !any_registered {
                return;
            }
            loop {
                let status = GetMessageW(&mut message, ptr::null_mut(), 0, 0);
                if status <= 0 {
                    break;
                }
                if message.message == WM_HOTKEY {
                    if let Some(binding) = HOTKEY_BINDINGS
                        .iter()
                        .find(|binding| binding.id == message.wParam as i32)
                    {
                        let _ = sender.send(binding.action);
                    }
                }
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            for id in registered {
                UnregisterHotKey(ptr::null_mut(), id);
            }
        }) {
        Ok(join) => join,
        Err(error) => {
            return HotkeyRegistration::unavailable_all(
                error
                    .raw_os_error()
                    .and_then(|error_code| u32::try_from(error_code).ok()),
            );
        }
    };
    let (thread_id, outcomes, any_registered) = match ready_receiver.recv() {
        Ok(ready) => ready,
        Err(_) => {
            let _ = join.join();
            return HotkeyRegistration::unavailable_all(None);
        }
    };
    let handle = if any_registered {
        Some(HotkeyHandle {
            thread_id,
            join: Some(join),
        })
    } else {
        let _ = join.join();
        None
    };
    HotkeyRegistration::new(handle, outcomes)
}

pub fn report_hotkey_registration(registration: &HotkeyRegistration) {
    let failures = registration
        .failures()
        .map(|outcome| match outcome.status {
            HotkeyRegistrationStatus::Registered => String::new(),
            HotkeyRegistrationStatus::Unavailable {
                error_code: Some(error_code),
            } => format!(
                "• {} ({}) is unavailable (Windows error {error_code}).",
                outcome.binding.shortcut, outcome.binding.description
            ),
            HotkeyRegistrationStatus::Unavailable { error_code: None } => format!(
                "• {} ({}) is unavailable.",
                outcome.binding.shortcut, outcome.binding.description
            ),
        })
        .collect::<Vec<_>>();
    if failures.is_empty() {
        return;
    }
    let message = widestring(&format!(
        "Some global shortcuts could not be registered. Noty will continue without them.\r\n\r\n{}",
        failures.join("\r\n")
    ));
    let title = widestring("Noty shortcuts");
    unsafe {
        let _ = MessageBoxW(
            ptr::null_mut(),
            message.as_ptr(),
            title.as_ptr(),
            MB_ICONWARNING | MB_OK,
        );
    }
}

pub fn set_launch_at_login(enabled: bool) -> Result<(), String> {
    let subkey = widestring("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
    let value_name = widestring("NotyWin");
    let executable = if enabled {
        let executable = std::env::current_exe().map_err(|error| error.to_string())?;
        Some(widestring(&format!("\"{}\"", executable.display())))
    } else {
        None
    };
    unsafe {
        let mut key: HKEY = ptr::null_mut();
        let status = RegCreateKeyExW(
            HKEY_CURRENT_USER,
            subkey.as_ptr(),
            0,
            ptr::null_mut(),
            0,
            KEY_SET_VALUE,
            ptr::null(),
            &mut key,
            ptr::null_mut(),
        );
        if status != 0 {
            return Err(format!("could not open startup registry key: {status}"));
        }
        let result = if enabled {
            let executable = executable.as_ref().expect("enabled startup path");
            let bytes = std::slice::from_raw_parts(
                executable.as_ptr() as *const u8,
                executable.len() * mem::size_of::<u16>(),
            );
            RegSetValueExW(
                key,
                value_name.as_ptr(),
                0,
                REG_SZ,
                bytes.as_ptr(),
                bytes.len() as u32,
            )
        } else {
            RegDeleteValueW(key, value_name.as_ptr())
        };
        RegCloseKey(key);
        if result == 0 || (!enabled && result == 2) {
            Ok(())
        } else {
            Err(format!("could not update startup registration: {result}"))
        }
    }
}

fn hwnd_for(window: &slint::Window) -> Option<HWND> {
    let window_handle = window.window_handle();
    let handle = window_handle.window_handle().ok()?;
    match handle.as_raw() {
        RawWindowHandle::Win32(handle) => Some(handle.hwnd.get() as HWND),
        _ => None,
    }
}

fn widestring(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}
