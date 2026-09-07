#![cfg_attr(windows, windows_subsystem = "windows")]

slint::include_modules!();

mod deck;
mod model;
mod platform;
mod storage;
mod ui;

use slint::{ComponentHandle, Timer, TimerMode};
use std::{rc::Rc, sync::mpsc, time::Duration};
use ui::{AppState, Controller};

fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    platform::initialize();
    configure_backend()?;
    let Some(_instance_guard) = platform::acquire_instance()? else {
        return Ok(());
    };
    let state = AppState::load()?;
    let window = Rc::new(NotyWindow::new()?);
    let controller = Controller::bind(&window, state);
    let display_ids = controller
        .borrow()
        .state
        .target_display_ids()
        .into_iter()
        .skip(1)
        .collect::<Vec<_>>();
    for display_id in display_ids {
        let secondary = Rc::new(NotyWindow::new()?);
        Controller::attach_window(&controller, &secondary, display_id);
    }
    Controller::refresh(&controller);

    let (hotkey_sender, hotkey_receiver) = mpsc::channel();
    let mut hotkey_registration = platform::register_hotkeys(hotkey_sender);
    if hotkey_registration
        .outcomes()
        .iter()
        .any(|outcome| !outcome.is_registered())
    {
        platform::report_hotkey_registration(&hotkey_registration);
    }
    let hotkey_handle = hotkey_registration.take_handle();

    let weak_controller = std::rc::Rc::downgrade(&controller);
    let hotkey_timer = Timer::default();
    hotkey_timer.start(TimerMode::Repeated, Duration::from_millis(50), move || {
        while let Ok(action) = hotkey_receiver.try_recv() {
            let Some(controller) = weak_controller.upgrade() else {
                break;
            };
            controller.borrow_mut().handle_hotkey(action);
            Controller::refresh(&controller);
        }
    });

    let display_watcher = platform::watch_display_changes({
        let weak_window = window.as_weak();
        move || {
            let weak_window = weak_window.clone();
            let _ = slint::invoke_from_event_loop(move || {
                if let Some(window) = weak_window.upgrade() {
                    window.invoke_display_changed();
                }
            });
        }
    });

    let run_result = slint::run_event_loop_until_quit();
    controller.borrow_mut().shutdown();
    drop(hotkey_timer);
    drop(display_watcher);
    drop(hotkey_handle);
    drop(window);
    drop(controller);
    run_result?;
    Ok(())
}

#[cfg(windows)]
fn configure_backend() -> Result<(), slint::PlatformError> {
    use i_slint_backend_winit::winit::platform::windows::WindowAttributesExtWindows;

    let backend = i_slint_backend_winit::Backend::builder()
        .with_window_attributes_hook(|attributes| {
            attributes
                .with_decorations(false)
                .with_resizable(false)
                .with_visible(false)
                .with_active(false)
                .with_skip_taskbar(true)
        })
        .build()?;
    slint::platform::set_platform(Box::new(backend))
        .map_err(|error| slint::PlatformError::from(error.to_string()))
}

#[cfg(not(windows))]
fn configure_backend() -> Result<(), slint::PlatformError> {
    Ok(())
}
