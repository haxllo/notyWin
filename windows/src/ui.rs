use crate::deck::{DeckState, PanelGeometry};
use crate::model::{
    DeckStyle, MAX_PILL_DASHES, Note, PendingDelete, Settings, toggle_task_at_cursor,
    toggle_task_line as toggle_task_line_body,
};
use crate::platform::{self, DisplayInfo, HitTestMode, HitTestRect, HotkeyAction};
use crate::storage::{Store, data_directory, load_settings, migrate_legacy_data, save_settings};
use crate::{NoteData, NotyWindow};
use slint::{
    CloseRequestResponse, ComponentHandle, ModelRc, SharedString, Timer, TimerMode, VecModel,
};
use std::{
    cell::RefCell,
    rc::{Rc, Weak as RcWeak},
    time::{Duration, Instant},
};

const FAN_COLLAPSE_DELAY: Duration = Duration::from_millis(200);

pub struct Controller {
    pub state: AppState,
    persist_timer: Timer,
    pending_note_ids: Vec<String>,
    hover_timer: Timer,
    delete_timer: Timer,
    foreground_timer: Timer,
    deck_hovered: bool,
    hovered_display_id: Option<u64>,
    idle_timer_armed: bool,
    hover_generation: u64,
    windows: Vec<(Rc<NotyWindow>, u64)>,
    self_weak: RcWeak<RefCell<Controller>>,
    activation_requested: bool,
    previous_foreground: Option<platform::FocusTarget>,
    fullscreen_state: Vec<(u64, bool)>,
}

pub struct AppState {
    pub notes: Vec<Note>,
    pub settings: Settings,
    pub store: Store,
    pub settings_path: std::path::PathBuf,
    pub deck_state: DeckState,
    pub view: View,
    pub expanded_id: Option<String>,
    pub selected_id: Option<String>,
    pub fan_show_all: bool,
    pub markdown_preview: bool,
    pub save_state: SaveState,
    pub find_visible: bool,
    pub find_query: String,
    pub find_match_count: usize,
    pub find_match_index: i32,
    pub find_selection_start: i32,
    pub find_selection_end: i32,
    pub library_archive: bool,
    pub library_query: String,
    pub capture_body: String,
    capture_origin: Option<CaptureOrigin>,
    pub pending_deletes: Vec<PendingDelete>,
    pub displays: Vec<DisplayInfo>,
    pub active_display_id: Option<u64>,
}

#[derive(Clone, Debug)]
struct CaptureOrigin {
    view: View,
    deck_state: DeckState,
    expanded_id: Option<String>,
    selected_id: Option<String>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum View {
    Deck,
    Library,
    Settings,
    Capture,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SaveState {
    Saved,
    Saving,
    Error,
}

impl SaveState {
    fn label(self) -> &'static str {
        match self {
            Self::Saved => "Saved",
            Self::Saving => "Saving…",
            Self::Error => "Couldn’t save",
        }
    }
}

impl View {
    fn as_str(self) -> &'static str {
        match self {
            Self::Deck => "deck",
            Self::Library => "library",
            Self::Settings => "settings",
            Self::Capture => "capture",
        }
    }
}

impl AppState {
    pub fn load() -> Result<Self, Box<dyn std::error::Error + Send + Sync>> {
        let directory = data_directory()?;
        migrate_legacy_data(&directory)?;
        let settings_path = directory.join("settings.json");
        let mut settings = load_settings(&settings_path)?;
        let mut store = Store::open(&directory)?;
        let mut notes = store.load_notes()?;
        if notes.is_empty() && !settings.welcome_shown {
            let welcome = Note::new(
                "Welcome to Noty.\n\nMove the pointer to the screen edge to wake the deck.",
                4,
                0.0,
            );
            store.save_note(&welcome)?;
            notes.push(welcome);
            settings.welcome_shown = true;
            save_settings(&settings_path, &settings)?;
        }
        let displays = platform::displays();
        let pointer_display_id = platform::active_display(&displays).map(|display| display.id);
        let active_display_id = preferred_display_id(&settings, &displays, pointer_display_id);
        // Notes open in the editable source view; preview is an explicit reading mode.
        let markdown_preview = false;
        Ok(Self {
            notes,
            settings,
            store,
            settings_path,
            deck_state: DeckState::Rest,
            view: View::Deck,
            expanded_id: None,
            selected_id: None,
            fan_show_all: false,
            markdown_preview,
            save_state: SaveState::Saved,
            find_visible: false,
            find_query: String::new(),
            find_match_count: 0,
            find_match_index: -1,
            find_selection_start: -1,
            find_selection_end: -1,
            library_archive: false,
            library_query: String::new(),
            capture_body: String::new(),
            capture_origin: None,
            pending_deletes: Vec::new(),
            displays,
            active_display_id,
        })
    }

    fn active_notes(&self) -> Vec<Note> {
        let mut notes: Vec<Note> = self
            .notes
            .iter()
            .filter(|note| !note.archived)
            .cloned()
            .collect();
        notes.sort_by(|left, right| left.order.total_cmp(&right.order));
        notes
    }

    fn archive_notes(&self) -> Vec<Note> {
        let mut notes: Vec<Note> = self
            .notes
            .iter()
            .filter(|note| note.archived)
            .cloned()
            .collect();
        notes.sort_by(|left, right| right.modified_at.cmp(&left.modified_at));
        notes
    }

    fn library_notes(&self) -> Vec<Note> {
        let query = self.library_query.trim().to_lowercase();
        let source = if self.library_archive {
            self.archive_notes()
        } else {
            self.active_notes()
        };
        if query.is_empty() {
            return source;
        }
        source
            .into_iter()
            .filter(|note| {
                note.title.to_lowercase().contains(&query)
                    || note.body.to_lowercase().contains(&query)
            })
            .collect()
    }

    fn library_note_available(&self, id: &str) -> bool {
        self.notes
            .iter()
            .any(|note| note.id == id && note.archived == self.library_archive)
    }

    fn selected_note(&self) -> Option<&Note> {
        let id = self
            .expanded_id
            .as_deref()
            .or(self.selected_id.as_deref())?;
        if self.view == View::Library && !self.library_note_available(id) {
            return None;
        }
        self.notes
            .iter()
            .find(|note| note.id == id && (self.view != View::Deck || !note.archived))
    }

    fn selected_note_mut(&mut self) -> Option<&mut Note> {
        let id = self
            .expanded_id
            .as_deref()
            .or(self.selected_id.as_deref())?
            .to_owned();
        let visible = match self.view {
            View::Deck => self
                .notes
                .iter()
                .any(|note| note.id == id && !note.archived),
            View::Library => self.library_note_available(&id),
            View::Settings | View::Capture => false,
        };
        if !visible {
            return None;
        }
        self.notes.iter_mut().find(|note| note.id == id)
    }

    fn reconcile_selection(&mut self) {
        match self.view {
            View::Library => {
                self.expanded_id = None;
                let visible_ids = self
                    .library_notes()
                    .into_iter()
                    .map(|note| note.id)
                    .collect::<Vec<_>>();
                if !self
                    .selected_id
                    .as_ref()
                    .is_some_and(|id| self.library_note_available(id))
                {
                    self.selected_id = visible_ids.into_iter().next();
                }
            }
            View::Deck => {
                let active_ids = self
                    .active_notes()
                    .into_iter()
                    .map(|note| note.id)
                    .collect::<Vec<_>>();
                if self
                    .expanded_id
                    .as_ref()
                    .is_some_and(|id| !active_ids.iter().any(|active| active == id))
                {
                    self.expanded_id = None;
                }
                if self.expanded_id.is_none()
                    && self
                        .selected_id
                        .as_ref()
                        .is_some_and(|id| !active_ids.iter().any(|active| active == id))
                {
                    self.selected_id = None;
                }
            }
            View::Settings | View::Capture => {}
        }
    }

    fn save_note(&mut self, note: &Note) -> bool {
        if let Err(error) = self.store.save_note(note) {
            eprintln!("Noty: could not save note {}: {error}", note.id);
            self.save_state = SaveState::Error;
            return false;
        }
        self.save_state = SaveState::Saved;
        true
    }

    fn save_preferences(&mut self) {
        self.settings.normalize();
        if let Err(error) = save_settings(&self.settings_path, &self.settings) {
            eprintln!("Noty: could not save settings: {error}");
        }
    }

    pub(crate) fn target_display_ids(&self) -> Vec<u64> {
        target_display_ids(&self.settings, &self.displays)
    }
}

impl Controller {
    pub fn bind(ui: &Rc<NotyWindow>, state: AppState) -> Rc<RefCell<Self>> {
        Self::install_close_handler(ui);
        let controller = Rc::new_cyclic(|weak| {
            RefCell::new(Self {
                state,
                persist_timer: Timer::default(),
                pending_note_ids: Vec::new(),
                hover_timer: Timer::default(),
                delete_timer: Timer::default(),
                foreground_timer: Timer::default(),
                deck_hovered: false,
                hovered_display_id: None,
                idle_timer_armed: false,
                hover_generation: 0,
                windows: Vec::new(),
                self_weak: weak.clone(),
                activation_requested: false,
                previous_foreground: None,
                fullscreen_state: Vec::new(),
            })
        });
        let primary_display_id = controller
            .borrow()
            .state
            .target_display_ids()
            .first()
            .copied()
            .unwrap_or(0);
        controller
            .borrow_mut()
            .windows
            .push((ui.clone(), primary_display_id));
        let weak_controller = Rc::downgrade(&controller);
        let weak_ui = ui.as_weak();

        {
            let weak = weak_controller.clone();
            ui.on_deck_hovered(move |inside| {
                if let Some(controller) = weak.upgrade() {
                    let display_id = weak_ui
                        .upgrade()
                        .and_then(|ui| ui.get_display_id().parse::<u64>().ok());
                    let should_refresh = controller
                        .borrow_mut()
                        .handle_deck_hover(inside, display_id);
                    if should_refresh {
                        Controller::refresh(&controller);
                    }
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_display_changed(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::sync_displays(&controller);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_escape_pressed(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().handle_escape();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_back_to_deck(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().return_to_deck();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_undo_delete(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().undo_delete();
                    Controller::refresh(&controller);
                }
            });
        }

        {
            let weak = weak_controller.clone();
            ui.on_note_clicked(move |id| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().open_note(id.to_string());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_note_context_action(move |id, action| {
                if let Some(controller) = weak.upgrade() {
                    controller
                        .borrow_mut()
                        .handle_note_context_action(id.to_string(), action.to_string());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_create_note(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().create_note(true);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_close_note(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().close_note();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_archive_note(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().archive_selected();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_delete_note(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().delete_selected();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_cycle_colour(move |colour| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().set_selected_colour(colour);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_toggle_pin(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().toggle_selected_pin();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_editor_changed(move |body| {
                if let Some(controller) = weak.upgrade() {
                    controller
                        .borrow_mut()
                        .update_selected_body(body.to_string());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_capture_body_changed(move |body| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().state.capture_body = body.to_string();
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_toggle_task(move |cursor| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().toggle_task_at_cursor(cursor);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_task_link_clicked(move |link| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().handle_task_link(link.to_string());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_markdown_preview_changed(move |preview| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().state.markdown_preview = preview;
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_find_visibility_changed(move |visible| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().set_find_visibility(visible);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_find_query_changed(move |query| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().set_find_query(query.to_string());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_find_next(move |forward| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().find_next(forward);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_reveal_more_notes(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().reveal_more_notes();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_open_library(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().open_library(false);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_open_settings(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().open_settings();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_open_capture(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().open_capture();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_restore_note(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().restore_selected();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_query_changed(move |query| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().state.library_query = query.to_string();
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_note_selected(move |id| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().state.selected_id = Some(id.to_string());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_selection_move(move |delta| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().move_library_selection(delta);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_selection_command(move |command| {
                if let Some(controller) = weak.upgrade() {
                    controller
                        .borrow_mut()
                        .move_library_selection_command(command.as_str());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_body_changed(move |body| {
                if let Some(controller) = weak.upgrade() {
                    controller
                        .borrow_mut()
                        .update_selected_body(body.to_string());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_mode_changed(move |archive| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().state.library_archive = archive;
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_settings_changed(move |name, value| {
                if let Some(controller) = weak.upgrade() {
                    let mut controller = controller.borrow_mut();
                    controller.update_setting(name.to_string(), value);
                    drop(controller);
                    Controller::refresh(&weak.upgrade().expect("controller still alive"));
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_settings_scale_changed(move |scale| {
                if let Some(controller) = weak.upgrade() {
                    let mut controller = controller.borrow_mut();
                    controller.state.settings.deck_scale = scale;
                    controller.state.save_preferences();
                    drop(controller);
                    Controller::refresh(&weak.upgrade().expect("controller still alive"));
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_display_target_changed(move |target| {
                if let Some(controller) = weak.upgrade() {
                    controller
                        .borrow_mut()
                        .update_display_target(target.to_string());
                    Controller::sync_displays(&controller);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_save_capture(move |body| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().save_capture(body.to_string());
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller;
            ui.on_cancel_capture(move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().return_to_deck();
                    Controller::refresh(&controller);
                }
            });
        }

        controller.borrow_mut().sync_fullscreen_state();

        let weak = Rc::downgrade(&controller);
        controller.borrow_mut().foreground_timer.start(
            TimerMode::Repeated,
            Duration::from_millis(500),
            move || {
                if let Some(controller) = weak.upgrade() {
                    let should_poll = {
                        let controller = controller.borrow();
                        controller.state.view == View::Deck
                            && !controller.state.settings.show_over_fullscreen
                    };
                    let should_dismiss = {
                        let controller = controller.borrow();
                        controller.state.view == View::Deck
                            && controller.state.expanded_id.is_some()
                            && !controller.expanded_note_is_pinned()
                            && platform::foreground_is_external()
                    };
                    let should_dismiss_capture = {
                        let controller = controller.borrow();
                        controller.state.view == View::Capture && platform::foreground_is_external()
                    };
                    if should_dismiss {
                        controller.borrow_mut().dismiss_expanded_note();
                        Controller::refresh(&controller);
                    } else if should_dismiss_capture {
                        controller.borrow_mut().return_to_deck();
                        Controller::refresh(&controller);
                    } else if should_poll {
                        let displays_changed = Controller::sync_displays(&controller);
                        let fullscreen_changed = controller.borrow_mut().sync_fullscreen_state();
                        if displays_changed || fullscreen_changed {
                            Controller::refresh(&controller);
                        }
                    }
                }
            },
        );

        Controller::refresh(&controller);
        controller
    }

    pub fn attach_window(controller: &Rc<RefCell<Self>>, ui: &Rc<NotyWindow>, display_id: u64) {
        Self::install_close_handler(ui);
        controller
            .borrow_mut()
            .windows
            .push((ui.clone(), display_id));
        let weak_controller = Rc::downgrade(controller);
        let weak_ui = ui.as_weak();

        {
            let weak = weak_controller.clone();
            ui.on_deck_hovered(move |inside| {
                if let Some(controller) = weak.upgrade() {
                    let display_id = weak_ui
                        .upgrade()
                        .and_then(|ui| ui.get_display_id().parse::<u64>().ok());
                    let should_refresh = controller
                        .borrow_mut()
                        .handle_deck_hover(inside, display_id);
                    if should_refresh {
                        Controller::refresh(&controller);
                    }
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_escape_pressed(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.handle_escape());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_back_to_deck(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.return_to_deck());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_undo_delete(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.undo_delete());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_note_clicked(move |id| {
                if let Some(controller) = weak.upgrade() {
                    let id = id.to_string();
                    Controller::dispatch(&controller, |controller| controller.open_note(id));
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_note_context_action(move |id, action| {
                if let Some(controller) = weak.upgrade() {
                    let id = id.to_string();
                    let action = action.to_string();
                    Controller::dispatch(&controller, |controller| {
                        controller.handle_note_context_action(id, action)
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_create_note(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.create_note(true));
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_close_note(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.close_note());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_archive_note(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.archive_selected());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_delete_note(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.delete_selected());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_cycle_colour(move |colour| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.set_selected_colour(colour)
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_toggle_pin(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.toggle_selected_pin()
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_editor_changed(move |body| {
                if let Some(controller) = weak.upgrade() {
                    let body = body.to_string();
                    Controller::dispatch(&controller, |controller| {
                        controller.update_selected_body(body)
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_capture_body_changed(move |body| {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().state.capture_body = body.to_string();
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_toggle_task(move |cursor| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.toggle_task_at_cursor(cursor)
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_task_link_clicked(move |link| {
                if let Some(controller) = weak.upgrade() {
                    let link = link.to_string();
                    Controller::dispatch(&controller, |controller| {
                        controller.handle_task_link(link);
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_markdown_preview_changed(move |preview| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.state.markdown_preview = preview;
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_find_visibility_changed(move |visible| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.set_find_visibility(visible);
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_find_query_changed(move |query| {
                if let Some(controller) = weak.upgrade() {
                    let query = query.to_string();
                    Controller::dispatch(&controller, |controller| {
                        controller.set_find_query(query);
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_find_next(move |forward| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.find_next(forward);
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_reveal_more_notes(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.reveal_more_notes();
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_open_library(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.open_library(false));
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_open_settings(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.open_settings());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_open_capture(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.open_capture());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_restore_note(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.restore_selected());
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_query_changed(move |query| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.state.library_query = query.to_string();
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_note_selected(move |id| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.state.selected_id = Some(id.to_string());
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_selection_move(move |delta| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.move_library_selection(delta);
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_selection_command(move |command| {
                if let Some(controller) = weak.upgrade() {
                    let command = command.to_string();
                    Controller::dispatch(&controller, |controller| {
                        controller.move_library_selection_command(&command);
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_body_changed(move |body| {
                if let Some(controller) = weak.upgrade() {
                    let body = body.to_string();
                    Controller::dispatch(&controller, |controller| {
                        controller.update_selected_body(body)
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_library_mode_changed(move |archive| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.state.library_archive = archive;
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_settings_changed(move |name, value| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.update_setting(name.to_string(), value);
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_settings_scale_changed(move |scale| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.state.settings.deck_scale = scale;
                        controller.state.save_preferences();
                    });
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_display_target_changed(move |target| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.update_display_target(target.to_string());
                    });
                    Controller::sync_displays(&controller);
                    Controller::refresh(&controller);
                }
            });
        }
        {
            let weak = weak_controller.clone();
            ui.on_save_capture(move |body| {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| {
                        controller.save_capture(body.to_string())
                    });
                }
            });
        }
        {
            let weak = weak_controller;
            ui.on_cancel_capture(move || {
                if let Some(controller) = weak.upgrade() {
                    Controller::dispatch(&controller, |controller| controller.return_to_deck());
                }
            });
        }
        Controller::refresh(controller);
    }

    fn install_close_handler(ui: &Rc<NotyWindow>) {
        ui.window().on_close_requested(|| {
            let _ = slint::quit_event_loop();
            CloseRequestResponse::HideWindow
        });
    }

    fn dispatch(controller: &Rc<RefCell<Self>>, action: impl FnOnce(&mut Self)) {
        action(&mut controller.borrow_mut());
        Controller::refresh(controller);
    }

    fn update_setting(&mut self, name: String, value: bool) {
        match name.as_str() {
            "deck-style" => {
                self.state.settings.deck_style = if value {
                    DeckStyle::LabelledTabs
                } else {
                    DeckStyle::ColourChips
                }
            }
            "deck-always-shown" => self.state.settings.deck_always_shown = value,
            "markdown-styling" => self.state.settings.markdown_styling = value,
            "left-edge" => self.state.settings.deck_on_left_edge = value,
            "pill-hidden" => self.state.settings.pill_hidden = value,
            "show-over-fullscreen" => self.state.settings.show_over_fullscreen = value,
            "launch-at-login" => match platform::set_launch_at_login(value) {
                Ok(()) => self.state.settings.launch_at_login = value,
                Err(error) => eprintln!("Noty: could not update launch-at-login: {error}"),
            },
            _ => return,
        }
        self.state.save_preferences();
    }

    fn update_display_target(&mut self, target: String) {
        let target = target.trim();
        if target != "all" && target != "main" && !target.starts_with("id:") {
            return;
        }
        self.state.settings.display_target = target.to_owned();
        self.state.save_preferences();
    }

    pub fn handle_hotkey(&mut self, action: HotkeyAction) {
        let pointer_display_id =
            platform::active_display(&self.state.displays).map(|display| display.id);
        self.state.active_display_id = preferred_display_id(
            &self.state.settings,
            &self.state.displays,
            pointer_display_id,
        )
        .or(self.state.active_display_id);
        match action {
            HotkeyAction::NewNote => self.create_note(true),
            HotkeyAction::AllNotes => self.open_library(false),
            HotkeyAction::Archive => self.open_library(true),
            HotkeyAction::QuickCapture => self.toggle_capture(),
        }
    }

    fn move_library_selection(&mut self, delta: i32) {
        let notes = self.state.library_notes();
        if notes.is_empty() {
            self.state.selected_id = None;
            return;
        }
        let current = self
            .state
            .selected_id
            .as_ref()
            .and_then(|id| notes.iter().position(|note| &note.id == id))
            .unwrap_or(0) as i32;
        let next = (current + delta).clamp(0, notes.len().saturating_sub(1) as i32) as usize;
        self.state.selected_id = Some(notes[next].id.clone());
    }

    fn move_library_selection_command(&mut self, command: &str) {
        let notes = self.state.library_notes();
        let Some(current) = self
            .state
            .selected_id
            .as_ref()
            .and_then(|id| notes.iter().position(|note| &note.id == id))
        else {
            if let Some(note) = notes.first() {
                self.state.selected_id = Some(note.id.clone());
            }
            return;
        };
        let next = match command {
            "home" => 0,
            "end" => notes.len().saturating_sub(1),
            "page-up" => current.saturating_sub(LIBRARY_PAGE_SIZE),
            "page-down" => (current + LIBRARY_PAGE_SIZE).min(notes.len() - 1),
            _ => return,
        };
        self.state.selected_id = Some(notes[next].id.clone());
    }

    fn handle_note_context_action(&mut self, id: String, action: String) {
        let in_library = self.state.view == View::Library;
        self.state.selected_id = Some(id);
        self.state.expanded_id = None;
        match action.as_str() {
            "pin" => self.toggle_selected_pin(),
            "archive" => self.archive_selected(),
            "restore" => self.restore_selected(),
            "delete" => self.delete_selected(),
            action if action.starts_with("colour-") => {
                if let Ok(colour) = action.trim_start_matches("colour-").parse::<i32>() {
                    self.set_selected_colour(colour);
                }
            }
            _ => return,
        }
        if in_library && self.state.view == View::Deck {
            self.state.view = View::Library;
            self.state.deck_state = DeckState::Fan;
            self.state.reconcile_selection();
        }
    }

    pub fn sync_displays(controller: &Rc<RefCell<Self>>) -> bool {
        let displays = platform::displays();
        let (missing, changed) = {
            let mut controller = controller.borrow_mut();
            let target_ids = target_display_ids(&controller.state.settings, &displays);
            let displays_unchanged = displays == controller.state.displays;
            let previous_active_display_id = controller.state.active_display_id;
            controller.state.displays = displays.clone();
            if controller
                .state
                .active_display_id
                .is_none_or(|id| !target_ids.contains(&id))
            {
                let pointer_display_id =
                    platform::active_display(&controller.state.displays).map(|display| display.id);
                controller.state.active_display_id = preferred_display_id(
                    &controller.state.settings,
                    &controller.state.displays,
                    pointer_display_id,
                );
            }
            let present_ids = target_ids
                .iter()
                .copied()
                .collect::<std::collections::HashSet<_>>();
            controller.windows.retain(|(window, display_id)| {
                if present_ids.contains(display_id) {
                    true
                } else {
                    let _ = window.hide();
                    false
                }
            });
            let existing = controller
                .windows
                .iter()
                .map(|(_, display_id)| *display_id)
                .collect::<std::collections::HashSet<_>>();
            let missing = controller
                .state
                .displays
                .iter()
                .filter(|display| {
                    present_ids.contains(&display.id) && !existing.contains(&display.id)
                })
                .map(|display| display.id)
                .collect::<Vec<_>>();
            let changed = !displays_unchanged
                || !missing.is_empty()
                || existing != present_ids
                || previous_active_display_id != controller.state.active_display_id;
            (missing, changed)
        };

        for display_id in missing {
            match NotyWindow::new() {
                Ok(window) => {
                    Controller::attach_window(controller, &Rc::new(window), display_id);
                }
                Err(error) => eprintln!("Noty: could not create display window: {error}"),
            }
        }
        changed
    }

    fn sync_fullscreen_state(&mut self) -> bool {
        let current = self
            .state
            .displays
            .iter()
            .map(|display| (display.id, platform::display_is_fullscreen(display.id)))
            .collect::<Vec<_>>();
        let changed = current != self.fullscreen_state;
        self.fullscreen_state = current;
        changed
    }

    fn handle_deck_hover(&mut self, inside: bool, display_id: Option<u64>) -> bool {
        self.deck_hovered = inside;
        self.handle_pointer_hover(self.deck_hovered, display_id)
    }

    fn pointer_inside_deck(&self) -> bool {
        self.deck_hovered
    }

    fn handle_pointer_hover(&mut self, inside: bool, display_id: Option<u64>) -> bool {
        if self.state.view != View::Deck {
            return false;
        }
        let previous_state = self.state.deck_state;
        let previous_active_display_id = self.state.active_display_id;
        let expanded = self.state.expanded_id.is_some();
        let expanded_pinned = self
            .state
            .expanded_id
            .as_deref()
            .and_then(|id| self.state.notes.iter().find(|note| note.id == id))
            .is_some_and(|note| note.pinned);
        if inside {
            if let Some(display_id) = display_id {
                if self.state.target_display_ids().contains(&display_id) {
                    self.state.active_display_id = Some(display_id);
                    self.hovered_display_id = Some(display_id);
                }
            }
            self.cancel_hover_collapse();
            if !expanded {
                self.state.deck_state = crate::deck::transition(
                    self.state.deck_state,
                    crate::deck::DeckInput::PointerEntered,
                    self.state.settings.deck_always_shown,
                );
            }
        } else {
            if let Some(display_id) = display_id {
                self.hovered_display_id = Some(display_id);
            }
            self.cancel_hover_collapse();
            if ((expanded && !expanded_pinned) || !self.state.settings.deck_always_shown)
                && (expanded || self.state.pending_deletes.is_empty())
            {
                self.schedule_hover_collapse(
                    if expanded {
                        Duration::from_secs(60)
                    } else {
                        FAN_COLLAPSE_DELAY
                    },
                    display_id.or(self.hovered_display_id),
                );
            }
        }
        previous_state != self.state.deck_state
            || previous_active_display_id != self.state.active_display_id
    }

    fn cancel_hover_collapse(&mut self) {
        self.hover_generation = self.hover_generation.wrapping_add(1);
        self.hover_timer.stop();
        self.idle_timer_armed = false;
    }

    fn schedule_hover_collapse(&mut self, delay: Duration, display_id: Option<u64>) {
        let weak = self.self_weak.clone();
        self.hover_generation = self.hover_generation.wrapping_add(1);
        let generation = self.hover_generation;
        self.idle_timer_armed = true;
        self.hover_timer
            .start(TimerMode::SingleShot, delay, move || {
                if let Some(controller) = weak.upgrade() {
                    let (should_collapse, should_close) = {
                        let controller = controller.borrow();
                        if controller.hover_generation != generation {
                            return;
                        }
                        let should_close =
                            controller.state.expanded_id.as_ref().is_some_and(|id| {
                                controller
                                    .state
                                    .notes
                                    .iter()
                                    .find(|note| note.id.as_str() == id.as_str())
                                    .is_some_and(|note| !note.pinned)
                            });
                        (
                            !controller.pointer_inside_deck()
                                && controller.state.view == View::Deck
                                && controller.state.active_display_id == display_id
                                && ((should_close
                                    && controller.state.deck_state == DeckState::Expanded)
                                    || (!should_close
                                        && !controller.state.settings.deck_always_shown
                                        && controller.state.pending_deletes.is_empty()
                                        && controller.state.deck_state == DeckState::Fan)),
                            should_close,
                        )
                    };
                    let mut state = controller.borrow_mut();
                    state.idle_timer_armed = false;
                    if should_collapse {
                        if should_close {
                            state.close_note();
                        } else {
                            state.state.fan_show_all = false;
                            state.state.deck_state = DeckState::Rest;
                        }
                        drop(state);
                        Controller::refresh(&controller);
                    }
                }
            });
    }

    fn schedule_fan_collapse_after_pending_delete(&mut self) {
        if self.state.view == View::Deck
            && self.state.deck_state == DeckState::Fan
            && !self.state.settings.deck_always_shown
            && !self.pointer_inside_deck()
            && self.state.pending_deletes.is_empty()
        {
            self.schedule_hover_collapse(
                FAN_COLLAPSE_DELAY,
                self.hovered_display_id.or(self.state.active_display_id),
            );
        }
    }

    fn handle_escape(&mut self) {
        match self.state.view {
            View::Capture | View::Library | View::Settings => self.return_to_deck(),
            View::Deck if self.state.find_visible => self.clear_find(),
            View::Deck if self.state.expanded_id.is_some() => self.close_note(),
            View::Deck
                if self.state.deck_state == DeckState::Fan
                    && self.state.pending_deletes.is_empty() =>
            {
                self.state.fan_show_all = false;
                self.state.deck_state = DeckState::Rest;
            }
            View::Deck => {}
        }
    }

    fn return_to_deck(&mut self) {
        self.cancel_hover_collapse();
        self.flush_pending();
        self.state.fan_show_all = false;
        let capture_origin = if self.state.view == View::Capture {
            self.state.capture_origin.take()
        } else {
            None
        };
        self.state.capture_body.clear();
        if let Some(origin) = capture_origin {
            self.state.view = origin.view;
            self.state.deck_state = origin.deck_state;
            self.state.expanded_id = origin.expanded_id;
            self.state.selected_id = origin.selected_id;
        } else {
            self.state.view = View::Deck;
            self.state.expanded_id = None;
            self.state.deck_state = if self.state.settings.deck_always_shown
                || !self.state.pending_deletes.is_empty()
            {
                DeckState::Fan
            } else {
                DeckState::Rest
            };
        }
        self.restore_previous_foreground();
    }

    fn open_library(&mut self, archive: bool) {
        self.cancel_hover_collapse();
        self.flush_pending();
        self.state.fan_show_all = false;
        self.state.expanded_id = None;
        self.state.library_archive = archive;
        self.state.view = View::Library;
        self.state.deck_state = DeckState::Fan;
        self.capture_previous_foreground();
        self.activation_requested = true;
    }

    fn open_settings(&mut self) {
        self.cancel_hover_collapse();
        self.flush_pending();
        self.state.fan_show_all = false;
        self.state.expanded_id = None;
        self.state.view = View::Settings;
        self.state.deck_state = DeckState::Rest;
        self.capture_previous_foreground();
        self.activation_requested = true;
    }

    fn open_capture(&mut self) {
        self.cancel_hover_collapse();
        self.flush_pending();
        self.state.fan_show_all = false;
        if self.state.view != View::Capture {
            self.state.capture_origin = Some(CaptureOrigin {
                view: self.state.view,
                deck_state: self.state.deck_state,
                expanded_id: self.state.expanded_id.clone(),
                selected_id: self.state.selected_id.clone(),
            });
        }
        self.state.capture_body.clear();
        self.state.view = View::Capture;
        self.state.deck_state = DeckState::Rest;
        self.capture_previous_foreground();
        self.activation_requested = true;
    }

    fn toggle_capture(&mut self) {
        if self.state.view == View::Capture {
            self.return_to_deck();
        } else {
            self.open_capture();
        }
    }

    fn reveal_more_notes(&mut self) {
        if self.state.view == View::Deck
            && self.state.deck_state == DeckState::Fan
            && self.state.active_notes().len() > MAX_VISIBLE_TABS
        {
            self.cancel_hover_collapse();
            self.state.fan_show_all = true;
        }
    }

    fn capture_previous_foreground(&mut self) {
        if self.previous_foreground.is_none() {
            self.previous_foreground = platform::capture_foreground();
        }
    }

    pub fn refresh(controller: &Rc<RefCell<Self>>) {
        let (
            windows,
            displays,
            active_display_id,
            view,
            deck_state,
            fan_show_all,
            settings,
            active_notes,
            library_notes,
            library_selection_index,
            library_archive,
            library_query,
            capture_body,
            capture_palette,
            markdown_preview,
            find_visible,
            find_query,
            find_match_count,
            find_match_index,
            find_selection_start,
            find_selection_end,
            selected,
            save_status,
            pending_delete,
            pending_delete_title,
            activation_requested,
        ) = {
            let mut controller = controller.borrow_mut();
            controller.state.reconcile_selection();
            let activation_requested = controller.activation_requested;
            controller.activation_requested = false;
            let state = &controller.state;
            let library_notes = state.library_notes();
            let library_selection_index = state
                .selected_id
                .as_ref()
                .and_then(|id| library_notes.iter().position(|note| &note.id == id))
                .unwrap_or(0) as i32;
            (
                controller
                    .windows
                    .iter()
                    .map(|(window, display_id)| (window.clone(), *display_id))
                    .collect::<Vec<_>>(),
                state.displays.clone(),
                state.active_display_id,
                state.view,
                state.deck_state,
                state.fan_show_all,
                state.settings.clone(),
                state.active_notes(),
                library_notes,
                library_selection_index,
                state.library_archive,
                state.library_query.clone(),
                state.capture_body.clone(),
                crate::model::palette(state.notes.len()),
                state.markdown_preview,
                state.find_visible,
                state.find_query.clone(),
                state.find_match_count,
                state.find_match_index,
                state.find_selection_start,
                state.find_selection_end,
                state.selected_note().cloned(),
                state.save_state.label().to_owned(),
                !state.pending_deletes.is_empty(),
                state
                    .pending_deletes
                    .last()
                    .map(|pending| pending.note.display_title().to_owned())
                    .unwrap_or_default(),
                activation_requested,
            )
        };

        for (ui, display_id) in windows {
            let Some(display) = displays.iter().find(|display| display.id == display_id) else {
                continue;
            };
            let is_active = active_display_id == Some(display_id);
            let local_view = if view == View::Deck || is_active {
                view
            } else {
                View::Deck
            };
            let local_deck_state = if local_view == View::Deck {
                if is_active {
                    deck_state
                } else if settings.deck_always_shown {
                    if deck_state == DeckState::Expanded {
                        DeckState::Fan
                    } else {
                        deck_state
                    }
                } else {
                    DeckState::Rest
                }
            } else {
                DeckState::Rest
            };
            let local_notes = if local_view == View::Library {
                library_notes.clone()
            } else {
                let limit = if local_deck_state == DeckState::Rest {
                    MAX_PILL_DASHES
                } else if fan_show_all && local_deck_state == DeckState::Fan {
                    active_notes.len()
                } else {
                    MAX_VISIBLE_TABS
                };
                active_notes.iter().take(limit).cloned().collect::<Vec<_>>()
            };
            let hidden_count = if local_view == View::Deck && local_deck_state == DeckState::Rest {
                active_notes.len().saturating_sub(MAX_PILL_DASHES)
            } else if local_view == View::Deck
                && local_deck_state == DeckState::Fan
                && !fan_show_all
            {
                active_notes.len().saturating_sub(MAX_VISIBLE_TABS)
            } else {
                0
            };
            let mut frame = if local_view == View::Deck {
                Some(crate::deck::geometry_with_activation_and_visibility(
                    display.work_area,
                    local_deck_state,
                    settings.deck_on_left_edge,
                    settings.deck_scale,
                    settings.deck_y_ratio,
                    settings.deck_style,
                    settings.note_width as u32,
                    settings.note_height as u32,
                    active_notes.len(),
                    settings.edge_activation,
                    fan_show_all && local_deck_state == DeckState::Fan,
                ))
            } else {
                None
            };
            if pending_delete && is_active && local_deck_state == DeckState::Fan {
                if let Some(frame) = frame.as_mut() {
                    let minimum_width =
                        (310.0 * settings.deck_scale * display.work_area.logical_scale()).round()
                            as u32;
                    if frame.width < minimum_width {
                        frame.width = minimum_width;
                        frame.x = if settings.deck_on_left_edge {
                            display.work_area.x
                        } else {
                            display.work_area.x + display.work_area.width as i32
                                - frame.width as i32
                        };
                    }
                }
            }
            let fullscreen_suppressed = local_view == View::Deck
                && local_deck_state != DeckState::Expanded
                && !settings.show_over_fullscreen
                && platform::display_is_fullscreen(display_id);
            let hit_test = deck_hit_test_mode(
                local_view,
                local_deck_state,
                frame,
                settings.deck_on_left_edge,
                settings.deck_style,
                settings.deck_scale,
                display.work_area.logical_scale(),
                local_notes.len(),
                hidden_count,
                pending_delete && is_active,
            );

            ui.set_display_id(display_id.to_string().into());
            ui.set_deck_state(state_string(local_deck_state));
            ui.set_view(local_view.as_str().into());
            ui.set_left_edge(settings.deck_on_left_edge);
            ui.set_hidden_count(hidden_count as i32);
            ui.set_fan_show_all(fan_show_all && local_deck_state == DeckState::Fan);
            ui.set_library_archive(library_archive);
            ui.set_library_selection_index(library_selection_index);
            ui.set_library_query(library_query.clone().into());
            ui.set_note_font_size(settings.note_font_size.into());
            ui.set_markdown_styling(settings.markdown_styling);
            ui.set_markdown_preview(markdown_preview && settings.markdown_styling);
            ui.set_find_visible(find_visible);
            ui.set_find_query(find_query.clone().into());
            ui.set_find_match_count(find_match_count as i32);
            ui.set_find_match_index(find_match_index);
            ui.set_find_selection_end(find_selection_end);
            ui.set_find_selection_start(find_selection_start);
            ui.set_save_status(save_status.clone().into());
            ui.set_deck_style(match settings.deck_style {
                DeckStyle::LabelledTabs => "tabs".into(),
                DeckStyle::ColourChips => "chips".into(),
            });
            ui.set_deck_always_shown(settings.deck_always_shown);
            ui.set_deck_scale(settings.deck_scale);
            ui.set_launch_at_login(settings.launch_at_login);
            ui.set_show_over_fullscreen(settings.show_over_fullscreen);
            ui.set_pill_hidden(settings.pill_hidden);
            ui.set_display_target(settings.display_target.clone().into());
            ui.set_capture_body(capture_body.clone().into());
            ui.set_capture_paper(slint_color(capture_palette.paper));
            ui.set_capture_dash(slint_color(capture_palette.dash));
            ui.set_capture_ink(slint_color(capture_palette.ink));
            ui.set_pending_delete(pending_delete && is_active);
            ui.set_pending_delete_title(if is_active {
                pending_delete_title.clone().into()
            } else {
                SharedString::default()
            });
            ui.set_notes(ModelRc::new(VecModel::from(
                local_notes
                    .iter()
                    .map(note_summary_data)
                    .collect::<Vec<_>>(),
            )));
            if fullscreen_suppressed {
                let _ = ui.hide();
                continue;
            }
            if let Some(note) = selected.as_ref() {
                ui.set_expanded_note(note_data(note));
                ui.set_editor_body(note.body.clone().into());
                ui.set_pinned(note.pinned);
                ui.set_selected_id(note.id.clone().into());
            } else {
                ui.set_selected_id(SharedString::default());
                ui.set_editor_body(SharedString::default());
                ui.set_pinned(false);
            }

            match local_view {
                View::Deck => {
                    if let Some(frame) = frame {
                        platform::configure_window(
                            &ui.window(),
                            frame,
                            settings.deck_on_left_edge,
                            is_active && local_deck_state == DeckState::Expanded,
                            settings.show_over_fullscreen,
                            hit_test.clone(),
                        );
                    }
                }
                View::Library if is_active => {
                    platform::centre_window(&ui.window(), 940, 580, display_id)
                }
                View::Settings if is_active => {
                    platform::centre_window(&ui.window(), 600, 580, display_id)
                }
                View::Capture if is_active => {
                    platform::position_capture_window(&ui.window(), 460, 150, display_id)
                }
                _ => {}
            }
            let _ = ui.show();
            let window_can_activate =
                is_active && (local_view != View::Deck || local_deck_state == DeckState::Expanded);
            platform::apply_window_style(
                &ui.window(),
                window_can_activate,
                local_view == View::Capture
                    || (local_view == View::Deck && settings.show_over_fullscreen),
                hit_test,
            );
            if activation_requested
                && is_active
                && (local_view != View::Deck || local_deck_state == DeckState::Expanded)
            {
                platform::activate_window(&ui.window());
            }
        }
    }

    fn open_note(&mut self, id: String) {
        if self.state.expanded_id.as_deref() == Some(id.as_str()) {
            self.close_note();
            return;
        }
        if self
            .state
            .notes
            .iter()
            .any(|note| note.id == id && !note.archived)
        {
            self.cancel_hover_collapse();
            self.clear_find();
            self.capture_previous_foreground();
            self.state.expanded_id = Some(id.clone());
            self.state.selected_id = Some(id);
            self.state.view = View::Deck;
            self.state.fan_show_all = false;
            self.state.deck_state = DeckState::Expanded;
            self.activation_requested = true;
        }
    }

    fn create_note(&mut self, open: bool) {
        self.cancel_hover_collapse();
        self.clear_find();
        let order = self
            .state
            .active_notes()
            .first()
            .map(|note| note.order - 1.0)
            .unwrap_or(0.0);
        let note = Note::new("", self.state.notes.len(), order);
        if !self.state.save_note(&note) {
            return;
        }
        let id = note.id.clone();
        self.state.notes.push(note);
        self.state.selected_id = Some(id.clone());
        self.state.view = View::Deck;
        self.state.fan_show_all = false;
        self.state.deck_state = if open {
            DeckState::Expanded
        } else {
            DeckState::Fan
        };
        self.state.expanded_id = open.then_some(id);
        if open {
            self.capture_previous_foreground();
        }
        self.activation_requested = open;
    }

    fn close_note(&mut self) {
        self.close_note_with_restore(true);
    }

    fn close_note_with_restore(&mut self, restore_foreground: bool) {
        self.cancel_hover_collapse();
        self.flush_pending();
        self.clear_find();
        if let Some(note) = self.state.selected_note() {
            let note = note.clone();
            self.state.save_note(&note);
        }
        self.state.expanded_id = None;
        self.state.fan_show_all = false;
        self.state.deck_state = DeckState::Fan;
        self.state.view = View::Deck;
        if restore_foreground {
            self.restore_previous_foreground();
        } else {
            self.previous_foreground.take();
        }
        self.schedule_fan_collapse_after_pending_delete();
    }

    fn expanded_note_is_pinned(&self) -> bool {
        self.state
            .expanded_id
            .as_deref()
            .and_then(|id| self.state.notes.iter().find(|note| note.id == id))
            .is_some_and(|note| note.pinned)
    }

    fn dismiss_expanded_note(&mut self) {
        if self.state.expanded_id.is_some() && !self.expanded_note_is_pinned() {
            self.close_note_with_restore(false);
        }
    }

    fn archive_selected(&mut self) {
        self.flush_pending();
        let in_library = self.state.view == View::Library;
        let Some(id) = self
            .state
            .expanded_id
            .clone()
            .or(self.state.selected_id.clone())
        else {
            return;
        };
        if let Some(index) = self.state.notes.iter().position(|note| note.id == id) {
            let previous = self.state.notes[index].clone();
            let saved = {
                let note = &mut self.state.notes[index];
                note.archived = true;
                note.modified_at = crate::model::now_unix_seconds();
                note.clone()
            };
            if !self.state.save_note(&saved) {
                self.state.notes[index] = previous;
                return;
            }
        }
        self.state.expanded_id = None;
        self.state.fan_show_all = false;
        self.state.deck_state = DeckState::Fan;
        if in_library {
            self.state.reconcile_selection();
        } else {
            self.state.view = View::Deck;
            self.restore_previous_foreground();
        }
    }

    fn delete_selected(&mut self) {
        self.flush_pending();
        let in_library = self.state.view == View::Library;
        let Some(id) = self
            .state
            .expanded_id
            .clone()
            .or(self.state.selected_id.clone())
        else {
            return;
        };
        let Some(position) = self.state.notes.iter().position(|note| note.id == id) else {
            return;
        };
        let note = self.state.notes.remove(position);
        if let Err(error) = self.state.store.delete(&id) {
            eprintln!("Noty: could not delete note: {error}");
            self.state.notes.insert(position, note);
            return;
        }
        self.state.pending_deletes.push(PendingDelete {
            note,
            expires_at: Instant::now() + Duration::from_secs(10),
        });
        self.schedule_delete_expiry();
        self.state.expanded_id = None;
        self.state.fan_show_all = false;
        self.state.deck_state = DeckState::Fan;
        if in_library {
            self.state.reconcile_selection();
        } else {
            self.state.view = View::Deck;
            self.restore_previous_foreground();
        }
    }

    fn restore_previous_foreground(&mut self) {
        platform::restore_foreground(self.previous_foreground.take());
    }

    fn undo_delete(&mut self) {
        self.expire_pending_deletes();
        let Some(pending) = self.state.pending_deletes.pop() else {
            return;
        };
        if pending.expires_at <= Instant::now() {
            self.schedule_delete_expiry();
            return;
        }
        if self.state.save_note(&pending.note) {
            self.state.notes.push(pending.note);
        } else {
            self.state.pending_deletes.push(pending);
        }
        self.schedule_delete_expiry();
        self.schedule_fan_collapse_after_pending_delete();
    }

    fn schedule_delete_expiry(&mut self) {
        let Some(earliest) = self
            .state
            .pending_deletes
            .iter()
            .map(|pending| pending.expires_at)
            .min()
        else {
            self.delete_timer.stop();
            return;
        };
        let delay = earliest.saturating_duration_since(Instant::now());
        let weak = self.self_weak.clone();
        self.delete_timer
            .start(TimerMode::SingleShot, delay, move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().expire_pending_deletes();
                    Controller::refresh(&controller);
                }
            });
    }

    fn expire_pending_deletes(&mut self) {
        let now = Instant::now();
        let mut expired = Vec::new();
        self.state.pending_deletes.retain(|pending| {
            if pending.expires_at > now {
                true
            } else {
                expired.push(pending.note.id.clone());
                false
            }
        });
        for id in expired {
            self.state.store.forget_unreadable_body(&id);
        }
        if !self.state.pending_deletes.is_empty() {
            self.schedule_delete_expiry();
        } else {
            self.schedule_fan_collapse_after_pending_delete();
        }
    }

    fn set_selected_colour(&mut self, colour: i32) {
        let (previous, saved) = {
            let Some(note) = self.state.selected_note_mut() else {
                return;
            };
            let previous = note.clone();
            note.colour = usize::try_from(colour)
                .unwrap_or_default()
                .min(crate::model::PALETTE.len().saturating_sub(1));
            note.modified_at = crate::model::now_unix_seconds();
            (previous, note.clone())
        };
        if !self.state.save_note(&saved) {
            if let Some(note) = self.state.selected_note_mut() {
                *note = previous;
            }
        }
    }

    fn restore_selected(&mut self) {
        let Some(id) = self.state.selected_id.clone() else {
            return;
        };
        if let Some(index) = self
            .state
            .notes
            .iter()
            .position(|note| note.id == id && note.archived)
        {
            let previous = self.state.notes[index].clone();
            let order = self
                .state
                .active_notes()
                .first()
                .map(|note| note.order - 1.0)
                .unwrap_or(0.0);
            let saved = {
                let note = &mut self.state.notes[index];
                note.archived = false;
                note.order = order;
                note.modified_at = crate::model::now_unix_seconds();
                note.clone()
            };
            if !self.state.save_note(&saved) {
                self.state.notes[index] = previous;
                return;
            }
        }
        self.state.reconcile_selection();
    }

    fn toggle_selected_pin(&mut self) {
        let (previous, saved) = {
            let Some(note) = self.state.selected_note_mut() else {
                return;
            };
            let previous = note.clone();
            note.pinned = !note.pinned;
            note.modified_at = crate::model::now_unix_seconds();
            (previous, note.clone())
        };
        if !self.state.save_note(&saved) {
            if let Some(note) = self.state.selected_note_mut() {
                *note = previous;
            }
        }
    }

    fn update_selected_body(&mut self, body: String) {
        let previous_body = self.state.selected_note().map(|note| note.body.clone());
        let previous_find_selection = self.active_find_selection();
        let id = self
            .state
            .expanded_id
            .as_deref()
            .or(self.state.selected_id.as_deref())
            .map(str::to_owned);
        if let Some(note) = self.state.selected_note_mut() {
            note.update_body(body);
        }
        self.state.save_state = SaveState::Saving;
        self.recount_find_after_edit(previous_body.as_deref(), previous_find_selection);
        self.reset_expanded_idle_close();
        let Some(id) = id else { return };
        if !self
            .pending_note_ids
            .iter()
            .any(|pending_id| pending_id == &id)
        {
            self.pending_note_ids.push(id);
        }
        let weak = self.self_weak.clone();
        self.persist_timer.start(
            TimerMode::SingleShot,
            Duration::from_millis(250),
            move || {
                if let Some(controller) = weak.upgrade() {
                    controller.borrow_mut().flush_selected();
                }
            },
        );
    }

    fn reset_expanded_idle_close(&mut self) {
        if self.state.view == View::Deck
            && self.state.deck_state == DeckState::Expanded
            && self.state.expanded_id.is_some()
            && !self.expanded_note_is_pinned()
            && !self.pointer_inside_deck()
        {
            self.schedule_hover_collapse(
                Duration::from_secs(60),
                self.hovered_display_id.or(self.state.active_display_id),
            );
        }
    }

    fn flush_selected(&mut self) {
        self.flush_pending();
    }

    pub fn flush_pending(&mut self) {
        let pending_ids = std::mem::take(&mut self.pending_note_ids);
        let mut failed_ids = Vec::new();
        for id in pending_ids {
            if let Some(note) = self.state.notes.iter().find(|note| note.id == id).cloned()
                && !self.state.save_note(&note)
            {
                failed_ids.push(id);
            }
        }
        self.pending_note_ids.extend(failed_ids);
        if !self.pending_note_ids.is_empty() {
            let weak = self.self_weak.clone();
            self.persist_timer.start(
                TimerMode::SingleShot,
                Duration::from_millis(250),
                move || {
                    if let Some(controller) = weak.upgrade() {
                        controller.borrow_mut().flush_pending();
                    }
                },
            );
        }
    }

    pub fn shutdown(&mut self) {
        self.persist_timer.stop();
        self.hover_timer.stop();
        self.delete_timer.stop();
        self.foreground_timer.stop();
        self.flush_pending();
    }

    fn toggle_task_at_cursor(&mut self, cursor_byte_offset: i32) {
        if self.state.view != View::Deck || self.state.expanded_id.is_none() {
            return;
        }
        if self.state.markdown_preview {
            self.state.markdown_preview = false;
            return;
        }
        let Some(body) = self.state.selected_note().map(|note| note.body.clone()) else {
            return;
        };
        let updated = toggle_task_at_cursor(&body, cursor_byte_offset);
        self.update_selected_body(updated);
        self.flush_pending();
    }

    fn set_find_visibility(&mut self, visible: bool) {
        if self.state.view != View::Deck || self.state.expanded_id.is_none() {
            return;
        }
        if visible {
            self.state.markdown_preview = false;
            self.state.find_visible = true;
            self.recount_find();
        } else {
            self.clear_find();
        }
    }

    fn set_find_query(&mut self, query: String) {
        if !self.state.find_visible {
            return;
        }
        self.state.find_query = query;
        self.state.find_match_index = -1;
        self.state.find_selection_start = -1;
        self.state.find_selection_end = -1;
        self.recount_find();
    }

    fn find_next(&mut self, forward: bool) {
        let Some(body) = self.state.selected_note().map(|note| note.body.clone()) else {
            return;
        };
        let matches = find_matches(&body, &self.state.find_query);
        self.state.find_match_count = matches.len();
        if matches.is_empty() {
            self.state.find_match_index = -1;
            self.state.find_selection_start = -1;
            self.state.find_selection_end = -1;
            return;
        }
        let current = usize::try_from(self.state.find_match_index)
            .ok()
            .filter(|index| *index < matches.len());
        let index = match (forward, current) {
            (true, Some(index)) => (index + 1) % matches.len(),
            (false, Some(index)) => index.checked_sub(1).unwrap_or(matches.len() - 1),
            (true, None) => 0,
            (false, None) => matches.len() - 1,
        };
        let (start, end) = matches[index];
        self.state.find_match_index = index as i32;
        self.state.find_selection_start = start as i32;
        self.state.find_selection_end = end as i32;
    }

    fn recount_find(&mut self) {
        self.recount_find_after_edit(None, None);
    }

    fn recount_find_after_edit(
        &mut self,
        previous_body: Option<&str>,
        previous_selection: Option<(usize, usize)>,
    ) {
        if !self.state.find_visible {
            self.state.find_match_count = 0;
            self.state.find_match_index = -1;
            self.state.find_selection_start = -1;
            self.state.find_selection_end = -1;
            return;
        }
        let body = self
            .state
            .selected_note()
            .map(|note| note.body.clone())
            .unwrap_or_default();
        let matches = find_matches(&body, &self.state.find_query);
        self.state.find_match_count = matches.len();
        if matches.is_empty() {
            self.state.find_match_index = -1;
            self.state.find_selection_start = -1;
            self.state.find_selection_end = -1;
            return;
        }

        let preserved_index =
            previous_body
                .zip(previous_selection)
                .and_then(|(previous_body, selection)| {
                    preserved_find_match_index(previous_body, &body, selection, &matches)
                });
        let current_index = usize::try_from(self.state.find_match_index)
            .ok()
            .filter(|index| *index < matches.len());
        if let Some(index) = preserved_index.or(current_index) {
            let (start, end) = matches[index];
            self.state.find_match_index = index as i32;
            self.state.find_selection_start = start as i32;
            self.state.find_selection_end = end as i32;
        } else {
            self.state.find_match_index = -1;
            self.state.find_selection_start = -1;
            self.state.find_selection_end = -1;
        }
    }

    fn active_find_selection(&self) -> Option<(usize, usize)> {
        if !self.state.find_visible {
            return None;
        }
        let start = usize::try_from(self.state.find_selection_start).ok()?;
        let end = usize::try_from(self.state.find_selection_end).ok()?;
        (start < end).then_some((start, end))
    }

    fn clear_find(&mut self) {
        self.state.find_visible = false;
        self.state.find_query.clear();
        self.state.find_match_count = 0;
        self.state.find_match_index = -1;
        self.state.find_selection_start = -1;
        self.state.find_selection_end = -1;
    }

    fn handle_task_link(&mut self, link: String) {
        let Some(line_index) = link
            .strip_prefix("noty-task:")
            .and_then(|index| index.parse::<usize>().ok())
        else {
            let _ = platform::open_url(&link);
            return;
        };
        let Some(body) = self.state.selected_note().map(|note| note.body.clone()) else {
            return;
        };
        self.update_selected_body(toggle_task_line_body(&body, line_index));
        self.flush_pending();
    }

    fn save_capture(&mut self, body: String) {
        if self.state.view != View::Capture {
            return;
        }
        let body = body.trim().to_owned();
        if body.is_empty() {
            self.return_to_deck();
            return;
        }
        let order = self
            .state
            .active_notes()
            .first()
            .map(|note| note.order - 1.0)
            .unwrap_or(0.0);
        let note = Note::new(body.clone(), self.state.notes.len(), order);
        if !self.state.save_note(&note) {
            self.state.capture_body = body;
            return;
        }
        self.state.notes.push(note);
        self.return_to_deck();
    }
}

fn state_string(deck_state: DeckState) -> SharedString {
    match deck_state {
        DeckState::Rest => "rest".into(),
        DeckState::Fan => "fan".into(),
        DeckState::Expanded => "expanded".into(),
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct FanItemLayout {
    x: i32,
    top: i32,
    width: u32,
    height: u32,
    pitch: u32,
    more_gap: i32,
    more_height: u32,
}

fn fan_item_layout(
    frame: PanelGeometry,
    left_edge: bool,
    style: DeckStyle,
    deck_scale: f32,
    display_scale: f32,
) -> FanItemLayout {
    let deck_scale = deck_scale.clamp(0.7, 1.8);
    let physical = |value: f32| value.round().max(1.0) as u32;
    let width = match style {
        DeckStyle::LabelledTabs => physical(30.0 * deck_scale * display_scale),
        DeckStyle::ColourChips => physical(24.0 * deck_scale * display_scale),
    };
    let height = match style {
        DeckStyle::LabelledTabs => physical(106.0 * deck_scale * display_scale),
        DeckStyle::ColourChips => physical(24.0 * deck_scale * display_scale),
    };
    let pitch = match style {
        DeckStyle::LabelledTabs => physical(56.0 * deck_scale * display_scale),
        DeckStyle::ColourChips => physical(36.0 * deck_scale * display_scale),
    };
    let edge_margin = physical(12.0 * display_scale) as i32;
    FanItemLayout {
        x: if left_edge {
            edge_margin
        } else {
            frame.width as i32 - edge_margin - width as i32
        },
        top: physical(24.0 * deck_scale * display_scale) as i32,
        width,
        height,
        pitch,
        more_gap: physical(18.0 * deck_scale * display_scale) as i32,
        more_height: physical(34.0 * deck_scale * display_scale),
    }
}

fn deck_hit_test_mode(
    view: View,
    deck_state: DeckState,
    frame: Option<PanelGeometry>,
    left_edge: bool,
    style: DeckStyle,
    deck_scale: f32,
    display_scale: f32,
    shown_count: usize,
    hidden_count: usize,
    pending_delete: bool,
) -> HitTestMode {
    if view != View::Deck || !matches!(deck_state, DeckState::Fan) {
        return HitTestMode::Full;
    }
    let Some(frame) = frame else {
        return HitTestMode::Full;
    };

    let deck_scale = deck_scale.clamp(0.7, 1.8);
    let physical = |value: f32| value.round().max(1.0) as u32;
    let item = fan_item_layout(frame, left_edge, style, deck_scale, display_scale);
    let show_all = shown_count > MAX_VISIBLE_TABS && hidden_count == 0;
    let hit_item_count = if show_all {
        shown_count.min(MAX_VISIBLE_TABS)
    } else {
        shown_count
    };
    let mut regions = Vec::with_capacity(hit_item_count + 5);
    let tab_hit_padding = if style == DeckStyle::LabelledTabs {
        physical(3.0 * deck_scale * display_scale) as i32
    } else {
        0
    };
    let tab_hit_vertical_padding = if style == DeckStyle::LabelledTabs {
        physical(1.0 * deck_scale * display_scale) as i32
    } else {
        0
    };
    // The tab cards are rotated by three degrees, so their painted bounds are
    // slightly wider than the untransformed layout rectangles.
    for index in 0..hit_item_count {
        regions.push(HitTestRect {
            x: item.x - tab_hit_padding,
            y: item.top + index as i32 * item.pitch as i32 - tab_hit_vertical_padding,
            width: item.width + tab_hit_padding as u32 * 2,
            height: item.height + tab_hit_vertical_padding as u32 * 2,
        });
    }

    if hidden_count > 0 {
        regions.push(HitTestRect {
            x: item.x,
            y: item.top + shown_count as i32 * item.pitch as i32 + item.more_gap,
            width: item.width,
            height: item.more_height,
        });
    }

    if show_all {
        let control_size = physical(28.0 * deck_scale * display_scale);
        let scroll_bottom =
            (frame.height as i32 - control_size as i32 - physical(74.0 * display_scale) as i32)
                .max(item.top);
        regions.push(HitTestRect {
            x: item.x,
            y: item.top,
            width: item.width,
            height: scroll_bottom.saturating_sub(item.top) as u32,
        });
    }

    let control_size = physical(28.0 * deck_scale * display_scale);
    let control_x = if left_edge {
        physical(12.0 * display_scale) as i32
    } else {
        frame.width as i32 - physical(12.0 * display_scale) as i32 - control_size as i32
    };
    for offset in [22.0, 62.0] {
        regions.push(HitTestRect {
            x: control_x,
            y: frame.height as i32 - control_size as i32 - physical(offset * display_scale) as i32,
            width: control_size,
            height: control_size,
        });
    }

    if pending_delete {
        let toast_width = physical(290.0 * display_scale);
        regions.push(HitTestRect {
            x: ((frame.width as i32 - toast_width as i32) / 2).max(0),
            y: frame.height as i32 - physical(52.0 * display_scale) as i32,
            width: toast_width,
            height: physical(36.0 * display_scale),
        });
    }

    // Overlap the tab edge so diagonal moves cannot fall through between tab rows.
    let bridge_overlap = physical(6.0 * deck_scale * display_scale).min(item.width) as i32;
    let bridge_x = if left_edge {
        0
    } else {
        item.x
            .saturating_add(item.width as i32)
            .saturating_sub(bridge_overlap)
    }
    .clamp(0, frame.width as i32);
    let bridge_width = if left_edge {
        item.x
            .saturating_add(bridge_overlap)
            .clamp(0, frame.width as i32) as u32
    } else {
        frame.width.saturating_sub(bridge_x as u32)
    };
    if bridge_width > 0 {
        regions.push(HitTestRect {
            x: bridge_x,
            y: 0,
            width: bridge_width,
            height: frame.height,
        });
    }

    HitTestMode::Regions(regions)
}

fn note_data(note: &Note) -> NoteData {
    note_data_with_style(note, true)
}

fn note_summary_data(note: &Note) -> NoteData {
    note_data_with_style(note, false)
}

fn note_data_with_style(note: &Note, include_style: bool) -> NoteData {
    let palette = note.palette();
    let progress = note.task_progress();
    NoteData {
        id: note.id.clone().into(),
        title: note.display_title().into(),
        tab_title: note.display_title().to_uppercase().into(),
        body: note.body.clone().into(),
        body_styled: if include_style {
            styled_note_body(&note.body)
        } else {
            slint::StyledText::from_plain_text("")
        },
        paper: slint_color(palette.paper),
        dash: slint_color(palette.dash),
        ink: slint_color(palette.ink),
        pinned: note.pinned,
        done: progress.done as i32,
        total: progress.total as i32,
    }
}

fn slint_color(argb: u32) -> slint::Color {
    slint::Color::from_argb_u8(
        (argb >> 24) as u8,
        (argb >> 16) as u8,
        (argb >> 8) as u8,
        argb as u8,
    )
}

fn styled_note_body(body: &str) -> slint::StyledText {
    let markdown = markdown_for_preview(body);
    slint::StyledText::from_markdown(&markdown)
        .unwrap_or_else(|_| slint::StyledText::from_plain_text(body))
}

fn markdown_for_preview(body: &str) -> String {
    let mut markdown = body
        .lines()
        .enumerate()
        .map(|(line_index, line)| {
            let indentation = &line[..line.len() - line.trim_start().len()];
            let content = line.trim_start();
            let task = content
                .strip_prefix("☐ ")
                .map(|rest| ("☐", rest))
                .or_else(|| content.strip_prefix("☑ ").map(|rest| ("☑", rest)));
            if let Some((marker, rest)) = task {
                let task = format!("[{marker}](noty-task:{line_index}) {rest}");
                if marker == "☑" {
                    format!("{indentation}~~{task}~~", indentation = indentation)
                } else {
                    format!("{indentation}{task}", indentation = indentation)
                }
            } else if content.starts_with('#') {
                let heading = content.trim_start_matches('#').trim_start();
                if heading.is_empty() {
                    return line.to_owned();
                }
                format!("{indentation}**{heading}**", indentation = indentation)
            } else {
                line.to_owned()
            }
        })
        .collect::<Vec<_>>()
        .join("\n");
    if body.ends_with('\n') {
        markdown.push('\n');
    }
    markdown
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct LowercaseSpan {
    folded_start: usize,
    folded_end: usize,
    original_start: usize,
    original_end: usize,
}

fn find_matches(body: &str, query: &str) -> Vec<(usize, usize)> {
    let query = query.to_lowercase();
    if query.is_empty() {
        return Vec::new();
    }

    let mut folded_body = String::with_capacity(body.len());
    let mut spans = Vec::with_capacity(body.chars().count());
    for (original_start, character) in body.char_indices() {
        let folded_start = folded_body.len();
        folded_body.extend(character.to_lowercase());
        spans.push(LowercaseSpan {
            folded_start,
            folded_end: folded_body.len(),
            original_start,
            original_end: original_start + character.len_utf8(),
        });
    }

    let mut matches = Vec::new();
    let mut offset = 0;
    while let Some(relative) = folded_body[offset..].find(&query) {
        let folded_start = offset + relative;
        let folded_end = folded_start + query.len();
        if let (Ok(first), Ok(last)) = (
            spans.binary_search_by_key(&folded_start, |span| span.folded_start),
            spans.binary_search_by_key(&folded_end, |span| span.folded_end),
        ) {
            matches.push((spans[first].original_start, spans[last].original_end));
        }
        offset = folded_end;
    }
    matches
}

fn preserved_find_match_index(
    previous_body: &str,
    updated_body: &str,
    previous_selection: (usize, usize),
    matches: &[(usize, usize)],
) -> Option<usize> {
    let (start, end) = previous_selection;
    if start >= end
        || end > previous_body.len()
        || !previous_body.is_char_boundary(start)
        || !previous_body.is_char_boundary(end)
    {
        return None;
    }

    let (unchanged_prefix, unchanged_suffix) = shared_text_edges(previous_body, updated_body);
    let previous_change_end = previous_body.len().saturating_sub(unchanged_suffix);
    let updated_change_end = updated_body.len().saturating_sub(unchanged_suffix);
    let expected_selection = (
        remap_find_offset_after_insertion(
            start,
            unchanged_prefix,
            previous_change_end,
            updated_change_end,
        ),
        remap_find_offset_before_insertion(
            end,
            unchanged_prefix,
            previous_change_end,
            updated_change_end,
        ),
    );

    matches
        .iter()
        .position(|selection| *selection == expected_selection)
        .or_else(|| {
            matches
                .iter()
                .enumerate()
                .min_by_key(|(_, selection)| {
                    selection
                        .0
                        .abs_diff(expected_selection.0)
                        .saturating_add(selection.1.abs_diff(expected_selection.1))
                })
                .map(|(index, _)| index)
        })
}

fn shared_text_edges(previous: &str, updated: &str) -> (usize, usize) {
    let mut prefix = 0;
    for (previous_character, updated_character) in previous.chars().zip(updated.chars()) {
        if previous_character != updated_character {
            break;
        }
        prefix += previous_character.len_utf8();
    }

    let mut suffix = 0;
    for (previous_character, updated_character) in previous[prefix..]
        .chars()
        .rev()
        .zip(updated[prefix..].chars().rev())
    {
        if previous_character != updated_character {
            break;
        }
        suffix += previous_character.len_utf8();
    }
    (prefix, suffix)
}

fn remap_find_offset_after_insertion(
    offset: usize,
    unchanged_prefix: usize,
    previous_change_end: usize,
    updated_change_end: usize,
) -> usize {
    if offset < unchanged_prefix {
        offset
    } else if offset >= previous_change_end {
        updated_change_end.saturating_add(offset - previous_change_end)
    } else {
        unchanged_prefix
    }
}

fn remap_find_offset_before_insertion(
    offset: usize,
    unchanged_prefix: usize,
    previous_change_end: usize,
    updated_change_end: usize,
) -> usize {
    if offset <= unchanged_prefix {
        offset
    } else if offset >= previous_change_end {
        updated_change_end.saturating_add(offset - previous_change_end)
    } else {
        unchanged_prefix
    }
}

fn target_display_ids(settings: &Settings, displays: &[DisplayInfo]) -> Vec<u64> {
    if displays.is_empty() {
        return Vec::new();
    }
    match settings.display_target.trim() {
        "" | "all" => displays.iter().map(|display| display.id).collect(),
        "main" => displays
            .iter()
            .find(|display| display.primary)
            .map(|display| vec![display.id])
            .unwrap_or_else(|| vec![displays[0].id]),
        target => target
            .strip_prefix("id:")
            .and_then(|id| id.parse::<u64>().ok())
            .and_then(|id| displays.iter().find(|display| display.id == id))
            .map(|display| vec![display.id])
            .unwrap_or_else(|| {
                displays
                    .iter()
                    .find(|display| display.primary)
                    .map(|display| vec![display.id])
                    .unwrap_or_else(|| vec![displays[0].id])
            }),
    }
}

fn preferred_display_id(
    settings: &Settings,
    displays: &[DisplayInfo],
    pointer_display_id: Option<u64>,
) -> Option<u64> {
    let target_ids = target_display_ids(settings, displays);
    if settings.display_target.trim().is_empty() || settings.display_target.trim() == "all" {
        pointer_display_id
            .filter(|id| target_ids.contains(id))
            .or_else(|| target_ids.first().copied())
    } else {
        target_ids.first().copied()
    }
}

const MAX_VISIBLE_TABS: usize = crate::model::MAX_VISIBLE_TABS;
const LIBRARY_PAGE_SIZE: usize = 8;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::deck::WorkArea;
    use crate::model::Note;
    use std::path::PathBuf;

    fn test_controller(notes: Vec<Note>) -> Rc<RefCell<Controller>> {
        test_controller_with_store(notes, Store::in_memory().expect("in-memory store"))
    }

    fn test_controller_with_store(notes: Vec<Note>, store: Store) -> Rc<RefCell<Controller>> {
        let displays = platform::displays();
        let pointer_display_id = platform::active_display(&displays).map(|display| display.id);
        let active_display_id =
            preferred_display_id(&Settings::default(), &displays, pointer_display_id);
        let state = AppState {
            notes,
            settings: Settings::default(),
            store,
            settings_path: PathBuf::from("test-settings.json"),
            deck_state: DeckState::Rest,
            view: View::Deck,
            expanded_id: None,
            selected_id: None,
            fan_show_all: false,
            markdown_preview: Settings::default().markdown_styling,
            save_state: SaveState::Saved,
            find_visible: false,
            find_query: String::new(),
            find_match_count: 0,
            find_match_index: -1,
            find_selection_start: -1,
            find_selection_end: -1,
            library_archive: false,
            library_query: String::new(),
            capture_body: String::new(),
            capture_origin: None,
            pending_deletes: Vec::new(),
            displays,
            active_display_id,
        };
        Rc::new_cyclic(|weak| {
            RefCell::new(Controller {
                state,
                persist_timer: Timer::default(),
                pending_note_ids: Vec::new(),
                hover_timer: Timer::default(),
                delete_timer: Timer::default(),
                foreground_timer: Timer::default(),
                deck_hovered: false,
                hovered_display_id: None,
                idle_timer_armed: false,
                hover_generation: 0,
                windows: Vec::new(),
                self_weak: weak.clone(),
                activation_requested: false,
                previous_foreground: None,
                fullscreen_state: Vec::new(),
            })
        })
    }

    fn notes(count: usize) -> Vec<Note> {
        (0..count)
            .map(|index| Note::new(format!("Note {index}"), index, index as f64))
            .collect()
    }

    #[test]
    fn library_selection_supports_page_and_boundary_commands() {
        let controller = test_controller(notes(12));
        let ids = controller
            .borrow()
            .state
            .notes
            .iter()
            .map(|note| note.id.clone())
            .collect::<Vec<_>>();
        {
            let mut controller = controller.borrow_mut();
            controller.state.view = View::Library;
            controller.state.selected_id = Some(ids[0].clone());
            controller.move_library_selection_command("page-down");
            assert_eq!(
                controller.state.selected_id.as_deref(),
                Some(ids[8].as_str())
            );
            controller.move_library_selection_command("end");
            assert_eq!(
                controller.state.selected_id.as_deref(),
                Some(ids[11].as_str())
            );
            controller.move_library_selection_command("page-up");
            assert_eq!(
                controller.state.selected_id.as_deref(),
                Some(ids[3].as_str())
            );
            controller.move_library_selection_command("home");
            assert_eq!(
                controller.state.selected_id.as_deref(),
                Some(ids[0].as_str())
            );
        }
    }

    #[test]
    fn context_actions_keep_library_open_and_undo_delete_restores_note() {
        let controller = test_controller(notes(2));
        let (first_id, second_id) = {
            let controller = controller.borrow();
            (
                controller.state.notes[0].id.clone(),
                controller.state.notes[1].id.clone(),
            )
        };
        {
            let mut controller = controller.borrow_mut();
            controller.state.view = View::Library;
            controller.state.selected_id = Some(first_id.clone());
            controller.handle_note_context_action(first_id.clone(), "archive".to_owned());
            assert_eq!(controller.state.view, View::Library);
            assert!(
                controller
                    .state
                    .notes
                    .iter()
                    .find(|note| note.id == first_id)
                    .is_some_and(|note| note.archived)
            );
            assert_eq!(
                controller.state.selected_id.as_deref(),
                Some(second_id.as_str())
            );

            controller.handle_note_context_action(second_id.clone(), "delete".to_owned());
            assert_eq!(controller.state.view, View::Library);
            assert!(
                !controller
                    .state
                    .notes
                    .iter()
                    .any(|note| note.id == second_id)
            );
            assert_eq!(controller.state.pending_deletes.len(), 1);

            controller.undo_delete();
            assert!(
                controller
                    .state
                    .notes
                    .iter()
                    .any(|note| note.id == second_id)
            );
            assert!(controller.state.pending_deletes.is_empty());
        }
    }

    #[test]
    fn restoring_an_archived_note_promotes_it_to_the_top_of_the_active_deck() {
        let active = Note::new("active", 0, 10.0);
        let mut archived = Note::new("archived", 1, 40.0);
        archived.archived = true;
        let archived_id = archived.id.clone();
        let controller = test_controller(vec![active, archived]);

        let mut controller = controller.borrow_mut();
        controller.state.view = View::Library;
        controller.state.library_archive = true;
        controller.state.selected_id = Some(archived_id.clone());
        controller.restore_selected();

        let active_notes = controller.state.active_notes();
        assert_eq!(
            active_notes.first().map(|note| note.id.as_str()),
            Some(archived_id.as_str())
        );
        assert!(!active_notes[0].archived);
        assert!(active_notes[0].order < active_notes[1].order);
    }

    #[test]
    fn shutdown_does_not_replay_a_stale_snapshot_over_a_newer_store_write() {
        let directory =
            std::env::temp_dir().join(format!("noty-two-store-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&directory).expect("test directory");

        let mut first_store = Store::open(&directory).expect("first store");
        let note = Note::new("initial", 0, 0.0);
        first_store.save_note(&note).expect("initial note");
        let mut second_store = Store::open(&directory).expect("second store");
        let first_notes = first_store.load_notes().expect("first snapshot");
        let second_notes = second_store.load_notes().expect("second snapshot");
        let first_controller = test_controller_with_store(first_notes, first_store);
        let second_controller = test_controller_with_store(second_notes, second_store);

        {
            let mut controller = first_controller.borrow_mut();
            controller.state.notes[0].update_body("newer");
            let saved = controller.state.notes[0].clone();
            assert!(controller.state.save_note(&saved));
        }
        second_controller.borrow_mut().shutdown();
        drop(second_controller);
        drop(first_controller);

        let mut verifier = Store::open(&directory).expect("verifier store");
        assert_eq!(verifier.load_notes().expect("verify note")[0].body, "newer");
        std::fs::remove_dir_all(directory).expect("remove test directory");
    }

    #[test]
    fn editing_a_searched_note_keeps_selection_after_it_stops_matching() {
        let matching = Note::new("alpha", 0, 0.0);
        let matching_id = matching.id.clone();
        let controller = test_controller(vec![matching, Note::new("other", 1, 1.0)]);
        {
            let mut controller = controller.borrow_mut();
            controller.state.view = View::Library;
            controller.state.library_query = "alpha".to_owned();
            controller.state.selected_id = Some(matching_id.clone());
            controller.state.reconcile_selection();

            controller.update_selected_body("beta".to_owned());

            assert_eq!(
                controller.state.selected_id.as_deref(),
                Some(matching_id.as_str())
            );
            assert_eq!(
                controller
                    .state
                    .selected_note()
                    .expect("selected note remains editable")
                    .body,
                "beta"
            );
            assert!(controller.state.library_notes().is_empty());

            controller.update_selected_body("gamma".to_owned());
            controller.flush_pending();
            let saved = controller
                .state
                .store
                .load_notes()
                .expect("load saved note");
            assert_eq!(
                saved
                    .iter()
                    .find(|note| note.id == matching_id)
                    .expect("saved selected note")
                    .body,
                "gamma"
            );
        }
    }

    #[test]
    fn expired_delete_rearms_fan_collapse_when_pointer_is_outside() {
        let note = Note::new("deleted", 0, 0.0);
        let controller = test_controller(Vec::new());
        {
            let mut controller = controller.borrow_mut();
            controller.state.view = View::Deck;
            controller.state.deck_state = DeckState::Fan;
            controller.state.pending_deletes.push(PendingDelete {
                note,
                expires_at: Instant::now() - Duration::from_secs(1),
            });
            controller.deck_hovered = false;
            let display_id = controller.state.active_display_id;
            controller.hovered_display_id = display_id;

            controller.expire_pending_deletes();

            assert!(controller.state.pending_deletes.is_empty());
            assert!(controller.idle_timer_armed);

            controller.handle_deck_hover(true, display_id);
            assert!(!controller.idle_timer_armed);
        }
    }

    #[test]
    fn quick_capture_escape_cancels_and_duplicate_save_is_ignored() {
        let controller = test_controller(Vec::new());
        {
            let mut controller = controller.borrow_mut();
            controller.state.view = View::Capture;
            controller.state.capture_body = "draft".to_owned();
            controller.handle_escape();
            assert_eq!(controller.state.view, View::Deck);
            assert!(controller.state.capture_body.is_empty());

            controller.state.view = View::Capture;
            controller.save_capture("first".to_owned());
            controller.save_capture("second".to_owned());
            assert_eq!(controller.state.notes.len(), 1);
            assert_eq!(controller.state.notes[0].body, "first");
            assert_eq!(controller.state.view, View::Deck);
        }
    }

    #[test]
    fn autosave_coalesces_body_edits_before_flush() {
        let note = Note::new("initial", 0, 0.0);
        let note_id = note.id.clone();
        let controller = test_controller(vec![note]);
        {
            let mut controller = controller.borrow_mut();
            controller.state.view = View::Deck;
            controller.state.expanded_id = Some(note_id.clone());
            controller.state.selected_id = Some(note_id.clone());
            controller.update_selected_body("first".to_owned());
            controller.update_selected_body("second".to_owned());
            assert_eq!(controller.state.save_state, SaveState::Saving);
            assert_eq!(controller.pending_note_ids, vec![note_id.clone()]);
            controller.flush_pending();
            assert!(controller.pending_note_ids.is_empty());
            assert_eq!(controller.state.save_state, SaveState::Saved);
            let saved = controller
                .state
                .store
                .load_notes()
                .expect("load saved note");
            assert_eq!(saved[0].body, "second");
        }
    }

    #[test]
    fn fan_reveal_exposes_hidden_notes_and_resets_on_escape() {
        let controller = test_controller(notes(MAX_VISIBLE_TABS + 1));
        {
            let mut controller = controller.borrow_mut();
            controller.state.deck_state = DeckState::Fan;
            controller.reveal_more_notes();
            assert!(controller.state.fan_show_all);

            controller.handle_escape();
            assert!(!controller.state.fan_show_all);
            assert_eq!(controller.state.deck_state, DeckState::Rest);
        }
    }

    #[test]
    fn revealed_fan_hit_testing_keeps_the_scroll_column_interactive() {
        let mode = deck_hit_test_mode(
            View::Deck,
            DeckState::Fan,
            Some(PanelGeometry {
                x: 0,
                y: 0,
                width: 120,
                height: 700,
            }),
            false,
            DeckStyle::LabelledTabs,
            1.0,
            1.0,
            MAX_VISIBLE_TABS + 4,
            0,
            false,
        );

        assert!(mode.accepts(100, 300));
        assert!(!mode.accepts(2, 300));
    }

    #[test]
    fn markdown_preview_normalizes_headings_and_links_task_markers() {
        assert_eq!(
            markdown_for_preview("## Plan\n  ☐ Ship it"),
            "**Plan**\n  [☐](noty-task:1) Ship it"
        );
        assert_eq!(
            markdown_for_preview("☑ Ship it"),
            "~~[☑](noty-task:0) Ship it~~"
        );
        assert_eq!(
            styled_note_body("**bold**"),
            slint::StyledText::from_markdown("**bold**").expect("valid markdown")
        );
    }

    #[test]
    fn find_matches_are_case_insensitive_and_non_overlapping() {
        assert_eq!(
            find_matches("Call Dana back; call Dana", "CALL"),
            vec![(0, 4), (16, 20)]
        );
        assert_eq!(find_matches("aaaa", "aa"), vec![(0, 2), (2, 4)]);
        assert!(find_matches("nothing here", "").is_empty());
    }

    #[test]
    fn find_matches_keep_original_utf8_offsets_after_case_expansion() {
        let first = "İSTANBUL";
        let body = format!("{first} {first}");

        assert_eq!(
            find_matches(&body, "İstanbul"),
            vec![(0, first.len()), (first.len() + 1, body.len())]
        );
    }

    #[test]
    fn find_navigation_wraps_and_selects_the_requested_match() {
        let note = Note::new("Alpha alpha ALPHA", 0, 0.0);
        let controller = test_controller(vec![note.clone()]);
        {
            let mut controller = controller.borrow_mut();
            controller.state.view = View::Deck;
            controller.state.expanded_id = Some(note.id.clone());
            controller.state.selected_id = Some(note.id);
            controller.set_find_visibility(true);
            controller.set_find_query("alpha".to_owned());
            controller.find_next(true);
            assert_eq!(controller.state.find_match_count, 3);
            assert_eq!(controller.state.find_match_index, 0);
            assert_eq!(
                (
                    controller.state.find_selection_start,
                    controller.state.find_selection_end
                ),
                (0, 5)
            );
            controller.find_next(false);
            assert_eq!(controller.state.find_match_index, 2);
            assert_eq!(
                (
                    controller.state.find_selection_start,
                    controller.state.find_selection_end
                ),
                (12, 17)
            );
        }
    }

    #[test]
    fn editing_before_the_active_find_match_keeps_it_selected() {
        let note = Note::new("alpha gap alpha", 0, 0.0);
        let controller = test_controller(vec![note.clone()]);
        let mut controller = controller.borrow_mut();
        controller.state.view = View::Deck;
        controller.state.deck_state = DeckState::Expanded;
        controller.state.expanded_id = Some(note.id.clone());
        controller.state.selected_id = Some(note.id);
        controller.set_find_visibility(true);
        controller.set_find_query("alpha".to_owned());
        controller.find_next(true);
        controller.find_next(true);

        controller.update_selected_body("prefix alpha gap alpha".to_owned());

        assert_eq!(controller.state.find_match_count, 2);
        assert_eq!(controller.state.find_match_index, 1);
        assert_eq!(
            (
                controller.state.find_selection_start,
                controller.state.find_selection_end
            ),
            (17, 22)
        );
    }

    #[test]
    fn find_and_task_actions_switch_preview_notes_to_editing() {
        let note = Note::new("task", 0, 0.0);
        let controller = test_controller(vec![note.clone()]);
        let mut controller = controller.borrow_mut();
        controller.state.view = View::Deck;
        controller.state.deck_state = DeckState::Expanded;
        controller.state.expanded_id = Some(note.id.clone());
        controller.state.selected_id = Some(note.id);
        controller.state.markdown_preview = true;

        controller.set_find_visibility(true);
        assert!(!controller.state.markdown_preview);
        assert!(controller.state.find_visible);

        controller.state.markdown_preview = true;
        controller.toggle_task_at_cursor(0);
        assert!(!controller.state.markdown_preview);
        assert_eq!(
            controller
                .state
                .selected_note()
                .map(|note| note.body.as_str()),
            Some("task")
        );
    }

    #[test]
    fn editing_an_expanded_note_restarts_its_idle_close_timer() {
        let note = Note::new("before", 0, 0.0);
        let controller = test_controller(vec![note.clone()]);
        let mut controller = controller.borrow_mut();
        controller.state.view = View::Deck;
        controller.state.deck_state = DeckState::Expanded;
        controller.state.expanded_id = Some(note.id.clone());
        controller.state.selected_id = Some(note.id);
        let active_display_id = controller.state.active_display_id;
        controller.handle_deck_hover(false, active_display_id);
        let previous_generation = controller.hover_generation;

        controller.update_selected_body("after".to_owned());

        assert!(controller.idle_timer_armed);
        assert_eq!(controller.hover_generation, previous_generation + 1);
    }

    #[test]
    fn persistent_deck_hover_survives_a_fan_tab_click() {
        let controller = test_controller(notes(1));
        let display_id = controller.borrow().state.active_display_id;
        {
            let mut controller = controller.borrow_mut();
            controller.state.deck_state = DeckState::Fan;
            controller.handle_deck_hover(true, display_id);
            controller.handle_deck_hover(true, display_id);

            assert!(controller.pointer_inside_deck());
            assert!(!controller.idle_timer_armed);

            controller.handle_deck_hover(false, display_id);
            assert!(!controller.pointer_inside_deck());
            assert!(controller.idle_timer_armed);
        }
    }

    #[test]
    fn fan_hit_testing_connects_edge_pill_to_tabs_and_passes_blank_area_through() {
        let mode = deck_hit_test_mode(
            View::Deck,
            DeckState::Fan,
            Some(PanelGeometry {
                x: 1870,
                y: 300,
                width: 50,
                height: 420,
            }),
            false,
            DeckStyle::LabelledTabs,
            1.0,
            1.0,
            1,
            0,
            false,
        );
        let HitTestMode::Regions(regions) = &mode else {
            panic!("fan decks should use regional hit testing");
        };
        assert_eq!(
            regions[0],
            HitTestRect {
                x: 5,
                y: 23,
                width: 36,
                height: 108,
            }
        );

        assert!(mode.accepts(10, 40));
        assert!(mode.accepts(10, 380));
        assert!(mode.accepts(45, 200));
        assert!(mode.accepts(30, 100));
        assert!(!mode.accepts(2, 200));

        let left_mode = deck_hit_test_mode(
            View::Deck,
            DeckState::Fan,
            Some(PanelGeometry {
                x: 0,
                y: 300,
                width: 50,
                height: 420,
            }),
            true,
            DeckStyle::LabelledTabs,
            1.0,
            1.0,
            1,
            0,
            false,
        );
        assert!(left_mode.accepts(5, 200));
        assert!(left_mode.accepts(15, 200));
        assert!(left_mode.accepts(20, 100));
        assert!(!left_mode.accepts(48, 200));
    }

    #[test]
    fn more_indicator_hit_region_uses_the_visible_chip_geometry() {
        let mode = deck_hit_test_mode(
            View::Deck,
            DeckState::Fan,
            Some(PanelGeometry {
                x: 0,
                y: 0,
                width: 120,
                height: 400,
            }),
            false,
            DeckStyle::ColourChips,
            1.5,
            1.0,
            2,
            1,
            false,
        );
        let HitTestMode::Regions(regions) = mode else {
            panic!("fan decks should use regional hit testing");
        };

        assert_eq!(
            regions[2],
            HitTestRect {
                x: 72,
                y: 171,
                width: 36,
                height: 51,
            }
        );
    }

    #[test]
    fn non_fan_views_keep_the_entire_window_interactive() {
        let mode = deck_hit_test_mode(
            View::Settings,
            DeckState::Rest,
            None,
            false,
            DeckStyle::LabelledTabs,
            1.0,
            1.0,
            0,
            0,
            false,
        );
        assert!(mode.accepts(0, 0));
        assert!(mode.accepts(1000, 1000));
    }

    fn test_displays() -> Vec<DisplayInfo> {
        vec![
            DisplayInfo {
                id: 10,
                work_area: WorkArea {
                    x: 0,
                    y: 0,
                    width: 1920,
                    height: 1080,
                    dpi: 96,
                },
                primary: true,
            },
            DisplayInfo {
                id: 20,
                work_area: WorkArea {
                    x: 1920,
                    y: 0,
                    width: 2560,
                    height: 1440,
                    dpi: 144,
                },
                primary: false,
            },
        ]
    }

    #[test]
    fn display_target_resolution_handles_all_main_and_specific_monitors() {
        let displays = test_displays();
        let mut settings = Settings::default();

        assert_eq!(target_display_ids(&settings, &displays), vec![10, 20]);
        assert_eq!(
            preferred_display_id(&settings, &displays, Some(20)),
            Some(20)
        );

        settings.display_target = "main".to_owned();
        assert_eq!(target_display_ids(&settings, &displays), vec![10]);
        assert_eq!(
            preferred_display_id(&settings, &displays, Some(20)),
            Some(10)
        );

        settings.display_target = "id:20".to_owned();
        assert_eq!(target_display_ids(&settings, &displays), vec![20]);
        assert_eq!(
            preferred_display_id(&settings, &displays, Some(10)),
            Some(20)
        );
    }

    #[test]
    fn missing_display_target_falls_back_to_the_primary_monitor() {
        let displays = test_displays();
        for target in ["id:999", "id:garbage", "garbage"] {
            let settings = Settings {
                display_target: target.to_owned(),
                ..Settings::default()
            };
            assert_eq!(target_display_ids(&settings, &displays), vec![10]);
        }
    }
}
