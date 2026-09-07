use crate::model::{DeckStyle, MAX_PILL_DASHES, MAX_VISIBLE_TABS};

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct WorkArea {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
    pub dpi: u32,
}

impl WorkArea {
    pub fn logical_scale(self) -> f32 {
        (self.dpi.max(96) as f32) / 96.0
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct PanelGeometry {
    pub x: i32,
    pub y: i32,
    pub width: u32,
    pub height: u32,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum DeckState {
    #[default]
    Rest,
    Fan,
    Expanded,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum DeckInput {
    PointerEntered,
    PointerExited,
    Expand(String),
    Collapse,
    Dismiss,
    Create,
    Escape,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Metrics {
    pub pill_width: u32,
    pub tab_width: u32,
    pub tab_height: u32,
    pub tab_pitch: u32,
    pub chip_width: u32,
    pub chip_height: u32,
    pub plus_size: u32,
}

impl Metrics {
    pub fn scaled(scale: f32) -> Self {
        let s = |value: f32| (value * scale).round().max(1.0) as u32;
        Self {
            pill_width: s(12.0),
            tab_width: s(30.0),
            tab_height: s(106.0),
            tab_pitch: s(56.0),
            chip_width: s(30.0),
            chip_height: s(24.0),
            plus_size: s(28.0),
        }
    }

    pub fn pill_height(self, note_count: usize, scale: f32) -> u32 {
        let dash_count =
            note_count.min(MAX_PILL_DASHES).max(1) + usize::from(note_count > MAX_PILL_DASHES);
        let dash_height = (14.0 * scale).round() as u32;
        let gap = (5.0 * scale).round() as u32;
        let padding = (7.0 * scale).round() as u32;
        padding * 2 + dash_count as u32 * dash_height + dash_count.saturating_sub(1) as u32 * gap
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
struct FanLayout {
    width: u32,
    height: u32,
}

fn physical_pixels(value: f32, scale: f32) -> u32 {
    (value * scale).round().max(1.0) as u32
}

fn fan_layout(
    style: DeckStyle,
    note_count: usize,
    deck_scale: f32,
    display_scale: f32,
    show_all: bool,
    preview_visible: bool,
) -> FanLayout {
    let deck_pixels = |value| physical_pixels(value, deck_scale * display_scale);
    let display_pixels = |value| physical_pixels(value, display_scale);
    let shown = if show_all {
        note_count.max(1)
    } else {
        note_count.min(MAX_VISIBLE_TABS).max(1)
    } as u32;
    let edge_margin = display_pixels(12.0);
    let tab_width = deck_pixels(30.0);
    let item_width = match style {
        DeckStyle::LabelledTabs => tab_width,
        DeckStyle::ColourChips => deck_pixels(24.0),
    };
    let control_size = deck_pixels(28.0);
    let base_width = tab_width
        .max(item_width)
        .max(control_size)
        .saturating_add(edge_margin.saturating_mul(2))
        .max(display_pixels(50.0));
    let preview_space = if preview_visible {
        deck_pixels(220.0)
    } else {
        0
    };
    let width = base_width.saturating_add(preview_space);

    let top = deck_pixels(24.0);
    let (pitch, item_height) = match style {
        DeckStyle::LabelledTabs => (deck_pixels(56.0), deck_pixels(106.0)),
        DeckStyle::ColourChips => (deck_pixels(36.0), deck_pixels(24.0)),
    };
    let item_bottom = top
        .saturating_add(shown.saturating_sub(1).saturating_mul(pitch))
        .saturating_add(item_height);
    let more_bottom = if !show_all && note_count > MAX_VISIBLE_TABS {
        let more_pitch = match style {
            DeckStyle::LabelledTabs => pitch,
            DeckStyle::ColourChips => display_pixels(36.0),
        };
        display_pixels(24.0)
            .saturating_add(shown.saturating_mul(more_pitch))
            .saturating_add(display_pixels(18.0))
            .saturating_add(display_pixels(34.0))
    } else {
        0
    };
    let content_bottom = item_bottom.max(more_bottom);
    // The controls are anchored to the frame's bottom in Slint, so reserve
    // their size and their unscaled bottom offset after the fan content.
    let height = content_bottom
        .saturating_add(control_size)
        .saturating_add(display_pixels(62.0))
        .saturating_add(display_pixels(12.0))
        .max(deck_pixels(180.0));

    FanLayout { width, height }
}

pub fn geometry(
    work: WorkArea,
    state: DeckState,
    on_left_edge: bool,
    scale: f32,
    deck_y_ratio: f32,
    style: DeckStyle,
    note_width: u32,
    note_height: u32,
    note_count: usize,
) -> PanelGeometry {
    geometry_with_activation(
        work,
        state,
        on_left_edge,
        scale,
        deck_y_ratio,
        style,
        note_width,
        note_height,
        note_count,
        20.0,
    )
}

pub fn geometry_with_activation_and_visibility(
    work: WorkArea,
    state: DeckState,
    on_left_edge: bool,
    scale: f32,
    deck_y_ratio: f32,
    style: DeckStyle,
    note_width: u32,
    note_height: u32,
    note_count: usize,
    edge_activation: f32,
    show_all: bool,
) -> PanelGeometry {
    geometry_with_activation_and_visibility_and_preview(
        work,
        state,
        on_left_edge,
        scale,
        deck_y_ratio,
        style,
        note_width,
        note_height,
        note_count,
        edge_activation,
        show_all,
        false,
    )
}

pub fn geometry_with_activation_and_visibility_and_preview(
    work: WorkArea,
    state: DeckState,
    on_left_edge: bool,
    scale: f32,
    deck_y_ratio: f32,
    style: DeckStyle,
    note_width: u32,
    note_height: u32,
    note_count: usize,
    edge_activation: f32,
    show_all: bool,
    preview_visible: bool,
) -> PanelGeometry {
    geometry_with_activation_inner(
        work,
        state,
        on_left_edge,
        scale,
        deck_y_ratio,
        style,
        note_width,
        note_height,
        note_count,
        edge_activation,
        show_all,
        preview_visible,
    )
}

pub fn geometry_with_activation(
    work: WorkArea,
    state: DeckState,
    on_left_edge: bool,
    scale: f32,
    deck_y_ratio: f32,
    style: DeckStyle,
    note_width: u32,
    note_height: u32,
    note_count: usize,
    edge_activation: f32,
) -> PanelGeometry {
    geometry_with_activation_inner(
        work,
        state,
        on_left_edge,
        scale,
        deck_y_ratio,
        style,
        note_width,
        note_height,
        note_count,
        edge_activation,
        false,
        false,
    )
}

fn geometry_with_activation_inner(
    work: WorkArea,
    state: DeckState,
    on_left_edge: bool,
    scale: f32,
    deck_y_ratio: f32,
    style: DeckStyle,
    note_width: u32,
    note_height: u32,
    note_count: usize,
    edge_activation: f32,
    show_all: bool,
    preview_visible: bool,
) -> PanelGeometry {
    let deck_scale = scale.clamp(0.7, 1.8);
    let display_scale = work.logical_scale();
    let scale = deck_scale * display_scale;
    let metrics = Metrics::scaled(scale);
    let safe_ratio = deck_y_ratio.clamp(0.0, 1.0);
    let (width, height) = match state {
        DeckState::Rest => (
            metrics
                .pill_width
                .max((edge_activation.max(18.0) * scale).round() as u32),
            metrics.pill_height(note_count, scale),
        ),
        DeckState::Fan => {
            let layout = fan_layout(
                style,
                note_count,
                deck_scale,
                display_scale,
                show_all,
                preview_visible,
            );
            (layout.width, layout.height.min(work.height.max(1)))
        }
        DeckState::Expanded => {
            let gutter = metrics.tab_width;
            (
                ((note_width as f32 * scale).round() as u32)
                    .saturating_add(gutter)
                    .max((420.0 * work.logical_scale()).round() as u32)
                    .min(work.width.max(1)),
                ((note_height as f32 * scale).round() as u32)
                    .max((280.0 * work.logical_scale()).round() as u32)
                    .min(work.height.max(1)),
            )
        }
    };
    let y = if state == DeckState::Rest {
        let available = work.height.saturating_sub(height);
        work.y + (available as f32 * (1.0 - safe_ratio)).round() as i32
    } else {
        let available = work.height.saturating_sub(height);
        work.y + (available as f32 * 0.5).round() as i32
    };
    let x = if on_left_edge {
        work.x
    } else {
        work.x + work.width as i32 - width as i32
    };
    PanelGeometry {
        x,
        y,
        width,
        height,
    }
}

pub fn transition(state: DeckState, input: DeckInput, keep_open: bool) -> DeckState {
    match (state, input) {
        (DeckState::Rest, DeckInput::PointerEntered) => DeckState::Fan,
        (DeckState::Rest, DeckInput::Create) => DeckState::Expanded,
        (DeckState::Fan, DeckInput::Expand(_)) => DeckState::Expanded,
        (DeckState::Fan, DeckInput::Create) => DeckState::Expanded,
        (DeckState::Fan, DeckInput::PointerExited | DeckInput::Dismiss) if !keep_open => {
            DeckState::Rest
        }
        (DeckState::Fan, DeckInput::Escape) => DeckState::Rest,
        (DeckState::Expanded, DeckInput::Collapse | DeckInput::Dismiss | DeckInput::Escape) => {
            DeckState::Fan
        }
        (DeckState::Expanded, DeckInput::PointerExited) => DeckState::Fan,
        (next, _) => next,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn area() -> WorkArea {
        WorkArea {
            x: -1920,
            y: 0,
            width: 1920,
            height: 1080,
            dpi: 144,
        }
    }

    #[test]
    fn rest_is_attached_to_the_requested_edge() {
        let right = geometry(
            area(),
            DeckState::Rest,
            false,
            1.0,
            0.5,
            DeckStyle::LabelledTabs,
            720,
            580,
            3,
        );
        let left = geometry(
            area(),
            DeckState::Rest,
            true,
            1.0,
            0.5,
            DeckStyle::LabelledTabs,
            720,
            580,
            3,
        );
        assert_eq!(right.x + right.width as i32, 0);
        assert_eq!(left.x, -1920);
    }

    #[test]
    fn expanded_panel_stays_inside_the_work_area() {
        let panel = geometry(
            area(),
            DeckState::Expanded,
            false,
            1.0,
            0.5,
            DeckStyle::LabelledTabs,
            720,
            580,
            3,
        );
        assert!(panel.x >= area().x);
        assert!(panel.x + panel.width as i32 <= area().x + area().width as i32);
    }

    #[test]
    fn tabs_shrink_the_deck_before_they_leave_the_screen() {
        let panel = geometry(
            area(),
            DeckState::Fan,
            false,
            1.0,
            0.5,
            DeckStyle::LabelledTabs,
            720,
            580,
            5,
        );
        assert!(panel.height <= 1080);
        assert!(panel.width >= 50);
    }

    #[test]
    fn high_dpi_tabs_fit_the_hidden_indicator_and_controls() {
        let work = WorkArea {
            x: 0,
            y: 0,
            width: 1920,
            height: 1080,
            dpi: 192,
        };
        let panel = geometry(
            work,
            DeckState::Fan,
            false,
            1.0,
            0.5,
            DeckStyle::LabelledTabs,
            720,
            580,
            6,
        );

        assert_eq!(panel.width, 108);
        assert_eq!(panel.height, 916);
        assert!(panel.y >= work.y);
        assert!(panel.y + panel.height as i32 <= work.y + work.height as i32);
    }

    #[test]
    fn high_dpi_chips_fit_the_hidden_indicator_and_controls() {
        let work = WorkArea {
            x: 0,
            y: 0,
            width: 1366,
            height: 768,
            dpi: 192,
        };
        let panel = geometry(
            work,
            DeckState::Fan,
            false,
            1.0,
            0.5,
            DeckStyle::ColourChips,
            720,
            580,
            6,
        );

        assert_eq!(panel.width, 108);
        assert_eq!(panel.height, 716);
        assert!(panel.y >= work.y);
        assert!(panel.y + panel.height as i32 <= work.y + work.height as i32);
    }

    #[test]
    fn revealed_fan_caps_to_the_work_area_for_many_notes() {
        let work = area();
        let panel = geometry_with_activation_and_visibility(
            work,
            DeckState::Fan,
            false,
            1.0,
            0.5,
            DeckStyle::LabelledTabs,
            720,
            580,
            24,
            20.0,
            true,
        );

        assert_eq!(panel.height, work.height);
        assert!(panel.width >= 50);
        assert!(panel.y >= work.y);
    }

    #[test]
    fn fan_preview_reserves_card_and_gap_width_only_when_visible() {
        let work = area();
        let hidden = geometry_with_activation_and_visibility_and_preview(
            work,
            DeckState::Fan,
            false,
            1.0,
            0.5,
            DeckStyle::LabelledTabs,
            720,
            580,
            2,
            20.0,
            false,
            false,
        );
        let visible = geometry_with_activation_and_visibility_and_preview(
            work,
            DeckState::Fan,
            false,
            1.0,
            0.5,
            DeckStyle::LabelledTabs,
            720,
            580,
            2,
            20.0,
            false,
            true,
        );

        let preview_space = (220.0 * work.logical_scale()).round() as u32;
        assert_eq!(visible.width - hidden.width, preview_space);
        assert_eq!(hidden.x - visible.x, preview_space as i32);
        assert_eq!(visible.x + visible.width as i32, work.x + work.width as i32);
    }

    #[test]
    fn state_machine_preserves_fan_when_keep_open_is_enabled() {
        assert_eq!(
            transition(DeckState::Fan, DeckInput::PointerExited, true),
            DeckState::Fan
        );
        assert_eq!(
            transition(DeckState::Fan, DeckInput::PointerExited, false),
            DeckState::Rest
        );
    }
}
