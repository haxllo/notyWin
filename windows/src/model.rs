use serde::{Deserialize, Serialize};
use std::time::{SystemTime, UNIX_EPOCH};
use uuid::Uuid;

pub const MAX_VISIBLE_TABS: usize = 5;
pub const MAX_PILL_DASHES: usize = 14;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct Palette {
    pub name: &'static str,
    pub paper: u32,
    pub dash: u32,
    pub ink: u32,
}

pub const PALETTE: [Palette; 8] = [
    Palette {
        name: "Lemon",
        paper: 0xFFFCE795,
        dash: 0xFFE0AD08,
        ink: 0xFF3A3008,
    },
    Palette {
        name: "Peach",
        paper: 0xFFFBCFA6,
        dash: 0xFFE2762A,
        ink: 0xFF422413,
    },
    Palette {
        name: "Rose",
        paper: 0xFFFAC4D1,
        dash: 0xFFDC4570,
        ink: 0xFF40161F,
    },
    Palette {
        name: "Lilac",
        paper: 0xFFD9C7FA,
        dash: 0xFF7C4DEE,
        ink: 0xFF2A1B44,
    },
    Palette {
        name: "Sky",
        paper: 0xFFBEDDFA,
        dash: 0xFF2280D6,
        ink: 0xFF13293A,
    },
    Palette {
        name: "Mint",
        paper: 0xFFB4E8D0,
        dash: 0xFF0E9B6E,
        ink: 0xFF0F2E23,
    },
    Palette {
        name: "Sand",
        paper: 0xFFE3D3B4,
        dash: 0xFFA37B3C,
        ink: 0xFF372C18,
    },
    Palette {
        name: "Slate",
        paper: 0xFFCBD6E2,
        dash: 0xFF4E6579,
        ink: 0xFF1A242E,
    },
];

pub fn palette(index: usize) -> Palette {
    PALETTE[index % PALETTE.len()]
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum TextDirection {
    #[default]
    Automatic,
    LeftToRight,
    RightToLeft,
}

#[derive(Clone, Copy, Debug, Default, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum DeckStyle {
    #[default]
    LabelledTabs,
    ColourChips,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct Note {
    pub id: String,
    pub title: String,
    pub body: String,
    /// The ciphertext could not be opened; never persist the placeholder body.
    #[serde(default)]
    pub body_unreadable: bool,
    pub colour: usize,
    pub created_at: i64,
    pub modified_at: i64,
    pub archived: bool,
    pub pinned: bool,
    pub order: f64,
    pub direction: TextDirection,
}

impl Note {
    pub fn new(body: impl Into<String>, colour: usize, order: f64) -> Self {
        let body = body.into();
        Self {
            id: Uuid::new_v4().to_string(),
            title: derived_title(&body),
            body,
            body_unreadable: false,
            colour,
            created_at: now_unix_seconds(),
            modified_at: now_unix_seconds(),
            archived: false,
            pinned: false,
            order,
            direction: TextDirection::Automatic,
        }
    }

    pub fn palette(&self) -> Palette {
        palette(self.colour)
    }

    pub fn display_title(&self) -> &str {
        if self.title.trim().is_empty() {
            "Untitled"
        } else {
            &self.title
        }
    }

    pub fn update_body(&mut self, body: impl Into<String>) {
        self.body = body.into().replace("\r\n", "\n");
        self.body_unreadable = false;
        self.title = derived_title(&self.body);
        self.modified_at = now_unix_seconds();
    }

    pub fn task_progress(&self) -> TaskProgress {
        task_progress(&self.body)
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct TaskProgress {
    pub done: u32,
    pub total: u32,
}

pub fn derived_title(body: &str) -> String {
    body.lines()
        .map(str::trim)
        .find(|line| !line.is_empty())
        .map(|line| {
            line.trim_start_matches(['#', '☐', '☑', '-', '*', ' '])
                .trim()
        })
        .unwrap_or_default()
        .chars()
        .take(80)
        .collect()
}

pub fn task_progress(body: &str) -> TaskProgress {
    body.lines()
        .fold(TaskProgress::default(), |mut progress, line| {
            let trimmed = line.trim_start();
            if trimmed.starts_with("☐ ") || trimmed.starts_with("☑ ") {
                progress.total += 1;
                if trimmed.starts_with("☑ ") {
                    progress.done += 1;
                }
            }
            progress
        })
}

pub fn toggle_task_line(body: &str, line_index: usize) -> String {
    if body.is_empty() && line_index == 0 {
        return "☐ ".to_owned();
    }

    let mut result = body
        .lines()
        .enumerate()
        .map(|(index, line)| {
            if index != line_index {
                return line.to_owned();
            }
            let indent = &line[..line.len() - line.trim_start().len()];
            let content = line.trim_start();
            match content.strip_prefix("☐ ") {
                Some(rest) => format!("{indent}☑ {rest}"),
                None => match content.strip_prefix("☑ ") {
                    Some(rest) => format!("{indent}☐ {rest}"),
                    None => format!("{indent}☐ {content}"),
                },
            }
        })
        .collect::<Vec<_>>()
        .join("\n");

    if body.ends_with('\n') {
        result.push('\n');
    }
    result
}

pub fn toggle_task_at_cursor(body: &str, cursor_byte_offset: i32) -> String {
    let mut cursor = usize::try_from(cursor_byte_offset)
        .unwrap_or(body.len())
        .min(body.len());
    while cursor > 0 && !body.is_char_boundary(cursor) {
        cursor -= 1;
    }
    let line_index = body[..cursor].bytes().filter(|byte| *byte == b'\n').count();
    if line_index >= body.lines().count() && body.ends_with('\n') {
        return format!("{body}☐ ");
    }

    let mut result = body
        .lines()
        .enumerate()
        .map(|(index, line)| {
            if index != line_index {
                return line.to_owned();
            }
            let indentation_length = line.len() - line.trim_start().len();
            let (indentation, content) = line.split_at(indentation_length);
            let unmarked_content = content
                .strip_prefix("☐")
                .or_else(|| content.strip_prefix("☑"))
                .map(|rest| rest.strip_prefix(' ').unwrap_or(rest));
            match unmarked_content {
                Some(content) => format!("{indentation}{content}"),
                None => format!("{indentation}☐ {content}"),
            }
        })
        .collect::<Vec<_>>()
        .join("\n");
    if body.ends_with('\n') {
        result.push('\n');
    }
    result
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(default)]
pub struct Settings {
    pub deck_style: DeckStyle,
    pub deck_scale: f32,
    pub deck_on_left_edge: bool,
    pub deck_y_ratio: f32,
    pub display_target: String,
    pub edge_activation: f32,
    pub deck_always_shown: bool,
    pub pill_hidden: bool,
    pub tab_preview: bool,
    pub open_on_hover: bool,
    pub show_over_fullscreen: bool,
    pub note_font_size: f32,
    pub note_width: f32,
    pub note_height: f32,
    pub markdown_styling: bool,
    pub launch_at_login: bool,
    pub welcome_shown: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            deck_style: DeckStyle::LabelledTabs,
            deck_scale: 1.0,
            deck_on_left_edge: false,
            deck_y_ratio: 0.5,
            display_target: "all".to_owned(),
            edge_activation: 20.0,
            deck_always_shown: false,
            pill_hidden: false,
            tab_preview: true,
            open_on_hover: false,
            show_over_fullscreen: false,
            note_font_size: 13.5,
            note_width: 720.0,
            note_height: 580.0,
            markdown_styling: true,
            launch_at_login: false,
            welcome_shown: false,
        }
    }
}

impl Settings {
    pub fn normalize(&mut self) {
        self.deck_scale = self.deck_scale.clamp(0.7, 1.8);
        self.deck_y_ratio = self.deck_y_ratio.clamp(0.0, 1.0);
        self.edge_activation = self.edge_activation.clamp(8.0, 64.0);
        self.note_font_size = self.note_font_size.clamp(10.0, 30.0);
        self.note_width = self.note_width.clamp(360.0, 1200.0);
        self.note_height = self.note_height.clamp(280.0, 900.0);
        if self.display_target.is_empty() {
            self.display_target = "all".to_owned();
        }
    }
}

#[derive(Clone, Debug)]
pub struct PendingDelete {
    pub note: Note,
    pub expires_at: std::time::Instant,
}

pub fn now_unix_seconds() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs() as i64)
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn title_uses_first_non_empty_line_and_strips_markers() {
        assert_eq!(derived_title("\n##  Buy milk\nsecond"), "Buy milk");
        assert_eq!(derived_title("☐ Send invoice"), "Send invoice");
    }

    #[test]
    fn task_progress_counts_only_inline_task_markers() {
        assert_eq!(
            task_progress("☐ one\n☑ two\nplain"),
            TaskProgress { done: 1, total: 2 }
        );
    }

    #[test]
    fn toggling_a_line_preserves_other_lines() {
        assert_eq!(toggle_task_line("one\ntwo", 0), "☐ one\ntwo");
        assert_eq!(toggle_task_line("☐ one\ntwo", 0), "☑ one\ntwo");
        assert_eq!(toggle_task_line("☐ one\n", 0), "☑ one\n");
    }

    #[test]
    fn toggling_at_the_caret_targets_the_current_line() {
        assert_eq!(toggle_task_at_cursor("one\n☐ two", 7), "one\ntwo");
        assert_eq!(toggle_task_at_cursor("one\ntwo", 6), "one\n☐ two");
        assert_eq!(toggle_task_at_cursor("☐ one\n", 6), "one\n");
        assert_eq!(toggle_task_at_cursor("☐ one\n", 8), "☐ one\n☐ ");
    }

    #[test]
    fn settings_are_clamped_before_use() {
        let mut settings = Settings {
            deck_scale: 4.0,
            note_width: 2.0,
            ..Settings::default()
        };
        settings.normalize();
        assert_eq!(settings.deck_scale, 1.8);
        assert_eq!(settings.note_width, 360.0);
    }
}