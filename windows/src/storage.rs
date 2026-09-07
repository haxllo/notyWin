use crate::model::{Note, Settings, TextDirection};
use aes_gcm::{
    Aes256Gcm,
    aead::{Aead, AeadCore, KeyInit, OsRng},
};
use rusqlite::{Connection, TransactionBehavior, params};
use std::{
    collections::{HashMap, HashSet},
    fs, io,
    path::{Path, PathBuf},
};

#[cfg(windows)]
use std::os::windows::ffi::OsStrExt;

#[cfg(windows)]
use windows_sys::Win32::{
    Foundation::{GetLastError, HLOCAL, LocalFree},
    Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
    },
    Storage::FileSystem::{MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW},
};

pub type StoreResult<T> = Result<T, Box<dyn std::error::Error + Send + Sync>>;

const CURRENT_SCHEMA_VERSION: i64 = 1;

pub fn data_directory() -> StoreResult<PathBuf> {
    let base = dirs::data_local_dir()
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "local application data directory unavailable",
            )
        })?
        .join("NotyWin");
    fs::create_dir_all(&base)?;
    Ok(base)
}

/// Imports the data directory used by the deleted Windows prototype without
/// moving or mutating the source files. The copy is validated with the legacy
/// key before the new directory becomes visible to the application.
pub fn migrate_legacy_data(directory: &Path) -> StoreResult<()> {
    #[cfg(windows)]
    {
        migrate_legacy_windows_data(directory)
    }
    #[cfg(not(windows))]
    {
        let _ = directory;
        Ok(())
    }
}

pub struct Store {
    connection: Connection,
    cipher: Aes256Gcm,
    unreadable_bodies: HashMap<String, Vec<u8>>,
    accepts_legacy_empty_bodies: bool,
}

impl Store {
    pub fn open(directory: &Path) -> StoreResult<Self> {
        fs::create_dir_all(directory)?;
        let database = directory.join("notes.db");
        let key = load_or_create_key(&directory.join("note.key"))?;
        let (connection, accepts_legacy_empty_bodies) = match open_database(&database) {
            Ok(database) => database,
            Err(error) => {
                if database.try_exists()? && is_database_corruption(error.as_ref()) {
                    quarantine_database(directory, error.as_ref())?;
                    open_database(&database)?
                } else {
                    return Err(error);
                }
            }
        };
        Ok(Self {
            connection,
            cipher: Aes256Gcm::new(aes_gcm::Key::<Aes256Gcm>::from_slice(&key)),
            unreadable_bodies: HashMap::new(),
            accepts_legacy_empty_bodies,
        })
    }

    pub fn in_memory() -> StoreResult<Self> {
        let connection = Connection::open_in_memory()?;
        connection.execute_batch(
            "CREATE TABLE notes (
                id TEXT PRIMARY KEY NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                body BLOB NOT NULL,
                colour INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL,
                modified_at INTEGER NOT NULL,
                archived INTEGER NOT NULL DEFAULT 0,
                pinned INTEGER NOT NULL DEFAULT 0,
                sort_order REAL NOT NULL DEFAULT 0,
                direction TEXT NOT NULL DEFAULT 'automatic'
            );
            PRAGMA user_version = 1;",
        )?;
        let key = [7_u8; 32];
        Ok(Self {
            connection,
            cipher: Aes256Gcm::new(aes_gcm::Key::<Aes256Gcm>::from_slice(&key)),
            unreadable_bodies: HashMap::new(),
            accepts_legacy_empty_bodies: false,
        })
    }

    pub fn load_notes(&mut self) -> StoreResult<Vec<Note>> {
        let mut notes = Vec::new();
        let mut unreadable = Vec::new();
        {
            let mut statement = self.connection.prepare(
                "SELECT id, title, body, colour, created_at, modified_at,
                        archived, pinned, sort_order, direction
                 FROM notes ORDER BY archived ASC, sort_order ASC, modified_at DESC",
            )?;
            let mut rows = statement.query([])?;
            while let Some(row) = rows.next()? {
                let decoded: StoreResult<(Note, Option<Vec<u8>>)> = (|| {
                    let id: String = row.get(0)?;
                    let sealed: Vec<u8> = row.get(2)?;
                    let direction = match row.get::<_, String>(9)?.as_str() {
                        "left-to-right" => TextDirection::LeftToRight,
                        "right-to-left" => TextDirection::RightToLeft,
                        _ => TextDirection::Automatic,
                    };
                    let (body, body_unreadable) = match self.open_body(&sealed) {
                        Ok(body) => (body, false),
                        Err(error) => {
                            eprintln!("Noty: could not decrypt note {id}: {error}");
                            (String::new(), true)
                        }
                    };
                    Ok((
                        Note {
                            id,
                            title: row.get(1)?,
                            body,
                            body_unreadable,
                            colour: row.get::<_, i64>(3)?.max(0) as usize,
                            created_at: row.get(4)?,
                            modified_at: row.get(5)?,
                            archived: row.get::<_, i64>(6)? != 0,
                            pinned: row.get::<_, i64>(7)? != 0,
                            order: row.get(8)?,
                            direction,
                        },
                        Some(sealed),
                    ))
                })();
                match decoded {
                    Ok((note, sealed)) => {
                        if note.body_unreadable {
                            if let Some(sealed) = sealed {
                                unreadable.push((note.id.clone(), sealed));
                            }
                        }
                        notes.push(note);
                    }
                    Err(error) => {
                        eprintln!("Noty: skipping malformed note row: {error}");
                    }
                }
            }
        }
        self.unreadable_bodies.clear();
        self.unreadable_bodies.extend(unreadable);
        Ok(notes)
    }

    pub fn save_note(&mut self, note: &Note) -> StoreResult<()> {
        let sealed = self.body_blob(note)?;
        let transaction = self.connection.transaction()?;
        let updated = transaction.execute(
            "UPDATE notes SET
                title = ?1, body = ?2, colour = ?3, created_at = ?4,
                modified_at = ?5, archived = ?6, pinned = ?7, sort_order = ?8,
                direction = ?9
             WHERE id = ?10",
            params![
                note.title,
                sealed,
                note.colour as i64,
                note.created_at,
                note.modified_at,
                note.archived as i64,
                note.pinned as i64,
                note.order,
                direction_name(note.direction),
                note.id,
            ],
        )?;
        if updated == 0 {
            transaction.execute(
                "INSERT INTO notes
                    (id, title, body, colour, created_at, modified_at, archived,
                     pinned, sort_order, direction)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
                params![
                    note.id,
                    note.title,
                    sealed,
                    note.colour as i64,
                    note.created_at,
                    note.modified_at,
                    note.archived as i64,
                    note.pinned as i64,
                    note.order,
                    direction_name(note.direction),
                ],
            )?;
        }
        transaction.commit()?;
        if !note.body_unreadable {
            self.unreadable_bodies.remove(&note.id);
        }
        Ok(())
    }

    pub fn delete(&mut self, id: &str) -> StoreResult<()> {
        self.connection
            .execute("DELETE FROM notes WHERE id = ?1", [id])?;
        Ok(())
    }

    pub fn forget_unreadable_body(&mut self, id: &str) {
        self.unreadable_bodies.remove(id);
    }

    fn body_blob(&self, note: &Note) -> StoreResult<Vec<u8>> {
        if note.body_unreadable {
            return self
                .unreadable_bodies
                .get(&note.id)
                .cloned()
                .ok_or_else(|| {
                    io::Error::new(
                        io::ErrorKind::InvalidData,
                        "unreadable note body is no longer available",
                    )
                    .into()
                });
        }
        self.seal_body(&note.body)
    }

    fn seal_body(&self, text: &str) -> StoreResult<Vec<u8>> {
        let nonce = Aes256Gcm::generate_nonce(&mut OsRng);
        let mut sealed = nonce.to_vec();
        let ciphertext = self.cipher.encrypt(&nonce, text.as_bytes()).map_err(|_| {
            io::Error::new(io::ErrorKind::InvalidData, "could not encrypt note body")
        })?;
        sealed.extend(ciphertext);
        Ok(sealed)
    }

    fn open_body(&self, sealed: &[u8]) -> StoreResult<String> {
        // The retired Windows writer represented an empty body as a zero-byte BLOB.
        if sealed.is_empty() && self.accepts_legacy_empty_bodies {
            return Ok(String::new());
        }
        if sealed.len() < 12 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "encrypted note body is truncated",
            )
            .into());
        }
        let (nonce, ciphertext) = sealed.split_at(12);
        let nonce = aes_gcm::aead::generic_array::GenericArray::from_slice(nonce);
        self.cipher
            .decrypt(&nonce, ciphertext)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "could not decrypt note body"))
            .and_then(|bytes| {
                String::from_utf8(bytes).map_err(|_| {
                    io::Error::new(io::ErrorKind::InvalidData, "note body is not valid UTF-8")
                })
            })
            .map_err(Into::into)
    }
}

pub fn load_settings(path: &Path) -> StoreResult<Settings> {
    let mut settings = if path.exists() {
        let contents = fs::read_to_string(path)?;
        match serde_json::from_str(&contents) {
            Ok(settings) => settings,
            Err(error) => {
                quarantine_file(path, "settings", &error)?;
                Settings::default()
            }
        }
    } else {
        Settings::default()
    };
    settings.normalize();
    Ok(settings)
}

pub fn save_settings(path: &Path, settings: &Settings) -> StoreResult<()> {
    let temporary = path.with_extension("json.tmp");
    let encoded = serde_json::to_vec_pretty(settings)?;
    fs::write(&temporary, encoded)?;
    replace_file(&temporary, path)?;
    Ok(())
}

fn ensure_schema(connection: &mut Connection) -> StoreResult<bool> {
    // Prevent simultaneous launches from both inspecting and backfilling a stale schema.
    let transaction = connection.transaction_with_behavior(TransactionBehavior::Immediate)?;
    let schema_version: i64 = transaction.query_row("PRAGMA user_version", [], |row| row.get(0))?;
    if schema_version > CURRENT_SCHEMA_VERSION {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            format!(
                "notes database schema version {schema_version} is newer than supported version {CURRENT_SCHEMA_VERSION}"
            ),
        )
        .into());
    }
    transaction.execute_batch(
        "CREATE TABLE IF NOT EXISTS notes (
            id TEXT PRIMARY KEY NOT NULL,
            title TEXT NOT NULL DEFAULT '',
            body BLOB NOT NULL,
            colour INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            modified_at INTEGER NOT NULL,
            archived INTEGER NOT NULL DEFAULT 0,
            pinned INTEGER NOT NULL DEFAULT 0,
            sort_order REAL NOT NULL DEFAULT 0,
            direction TEXT NOT NULL DEFAULT 'automatic'
        );",
    )?;

    let columns = table_columns(&transaction)?;
    if !columns.contains("body") {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "notes database has no body column",
        )
        .into());
    }
    if !columns.contains("id") {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "notes database has no id column",
        )
        .into());
    }

    let accepts_legacy_empty_bodies = ["color", "created", "modified"]
        .iter()
        .all(|column| columns.contains(*column));
    if !columns.contains("colour") {
        transaction.execute(
            "ALTER TABLE notes ADD COLUMN colour INTEGER NOT NULL DEFAULT 0",
            [],
        )?;
        if columns.contains("color") {
            transaction.execute("UPDATE notes SET colour = color", [])?;
        }
    }
    if !columns.contains("created_at") {
        transaction.execute(
            "ALTER TABLE notes ADD COLUMN created_at INTEGER NOT NULL DEFAULT 0",
            [],
        )?;
        if columns.contains("created") {
            transaction.execute("UPDATE notes SET created_at = CAST(created AS INTEGER)", [])?;
        }
    }
    if !columns.contains("modified_at") {
        transaction.execute(
            "ALTER TABLE notes ADD COLUMN modified_at INTEGER NOT NULL DEFAULT 0",
            [],
        )?;
        if columns.contains("modified") {
            transaction.execute(
                "UPDATE notes SET modified_at = CAST(modified AS INTEGER)",
                [],
            )?;
        }
    }
    if !columns.contains("direction") {
        transaction.execute(
            "ALTER TABLE notes ADD COLUMN direction TEXT NOT NULL DEFAULT 'automatic'",
            [],
        )?;
        if columns.contains("text_direction") {
            transaction.execute("UPDATE notes SET direction = text_direction", [])?;
        }
    }
    if !columns.contains("title") {
        transaction.execute(
            "ALTER TABLE notes ADD COLUMN title TEXT NOT NULL DEFAULT ''",
            [],
        )?;
    }
    if !columns.contains("archived") {
        transaction.execute(
            "ALTER TABLE notes ADD COLUMN archived INTEGER NOT NULL DEFAULT 0",
            [],
        )?;
    }
    if !columns.contains("pinned") {
        transaction.execute(
            "ALTER TABLE notes ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0",
            [],
        )?;
    }
    if !columns.contains("sort_order") {
        transaction.execute(
            "ALTER TABLE notes ADD COLUMN sort_order REAL NOT NULL DEFAULT 0",
            [],
        )?;
    }

    transaction.execute(
        "CREATE INDEX IF NOT EXISTS notes_active_order
         ON notes (archived, sort_order)",
        [],
    )?;
    transaction.execute_batch(&format!("PRAGMA user_version = {CURRENT_SCHEMA_VERSION};"))?;
    transaction.commit()?;
    Ok(accepts_legacy_empty_bodies)
}

fn open_database(path: &Path) -> StoreResult<(Connection, bool)> {
    let mut connection = Connection::open(path)?;
    connection.pragma_update(None, "journal_mode", "WAL")?;
    connection.pragma_update(None, "synchronous", "NORMAL")?;
    let accepts_legacy_empty_bodies = ensure_schema(&mut connection)?;
    Ok((connection, accepts_legacy_empty_bodies))
}

fn is_database_corruption(error: &(dyn std::error::Error + 'static)) -> bool {
    let Some(sqlite_error) = error.downcast_ref::<rusqlite::Error>() else {
        return false;
    };
    matches!(
        sqlite_error.sqlite_error_code(),
        Some(rusqlite::ErrorCode::DatabaseCorrupt | rusqlite::ErrorCode::NotADatabase)
    )
}

fn database_artifact_paths(directory: &Path) -> [PathBuf; 3] {
    ["notes.db", "notes.db-wal", "notes.db-shm"].map(|name| directory.join(name))
}

fn database_artifacts_exist(directory: &Path) -> io::Result<bool> {
    for path in database_artifact_paths(directory) {
        if path.try_exists()? {
            return Ok(true);
        }
    }
    Ok(false)
}

fn quarantine_database(directory: &Path, error: &dyn std::error::Error) -> StoreResult<()> {
    eprintln!("Noty: recovering an unreadable database: {error}");
    for path in database_artifact_paths(directory) {
        if path.try_exists()? {
            quarantine_file(&path, "database", error)?;
        }
    }
    Ok(())
}

fn quarantine_file(path: &Path, kind: &str, error: &dyn std::error::Error) -> StoreResult<()> {
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("data");
    let quarantined =
        path.with_file_name(format!("{name}.corrupt-{kind}-{}", uuid::Uuid::new_v4()));
    eprintln!(
        "Noty: preserving invalid {name} at {}: {error}",
        quarantined.display()
    );
    fs::rename(path, quarantined)?;
    Ok(())
}

#[cfg(windows)]
fn migrate_legacy_windows_data(directory: &Path) -> StoreResult<()> {
    let Some(local_app_data) = dirs::data_local_dir() else {
        return Ok(());
    };
    let legacy = local_app_data.join("Noty");
    let legacy_db = legacy.join("notes.db");
    let legacy_settings = legacy.join("settings.json");
    let current_db = directory.join("notes.db");
    let current_settings = directory.join("settings.json");

    if current_db.exists() || !legacy_db.exists() {
        if !current_settings.exists() && legacy_settings.exists() {
            let mut settings = import_legacy_settings(&legacy_settings)?;
            settings.welcome_shown = current_db.exists();
            save_settings(&current_settings, &settings)?;
        }
        return Ok(());
    }

    let legacy_key = ["note.key.dpapi", "note.key"]
        .iter()
        .map(|name| legacy.join(name))
        .find(|path| path.exists())
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "legacy notes database has no encryption key",
            )
        })?;
    fs::create_dir_all(directory)?;
    let staging = directory.join(format!(".migration-{}", uuid::Uuid::new_v4()));
    fs::create_dir(&staging)?;
    let result = (|| {
        for suffix in ["notes.db", "notes.db-wal", "notes.db-shm"] {
            let source = legacy.join(suffix);
            if source.exists() {
                fs::copy(&source, staging.join(suffix))?;
            }
        }
        fs::copy(&legacy_key, staging.join("note.key"))?;

        let mut imported = Store::open(&staging)?;
        let notes = imported.load_notes()?;
        drop(imported);

        if !current_settings.exists() {
            let mut settings = import_legacy_settings(&legacy_settings)?;
            settings.welcome_shown = !notes.is_empty();
            save_settings(&staging.join("settings.json"), &settings)?;
        }

        // Publish the key before the database. If interrupted, the next launch
        // can safely repeat the copy because the destination database is absent.
        replace_file(&staging.join("note.key"), &directory.join("note.key"))?;
        for suffix in ["notes.db-wal", "notes.db-shm"] {
            let source = staging.join(suffix);
            if source.exists() {
                replace_file(&source, &directory.join(suffix))?;
            }
        }
        replace_file(&staging.join("notes.db"), &current_db)?;
        if !current_settings.exists() {
            replace_file(&staging.join("settings.json"), &current_settings)?;
        }
        Ok(())
    })();
    let _ = fs::remove_dir_all(&staging);
    result
}

#[cfg(windows)]
fn import_legacy_settings(path: &Path) -> StoreResult<Settings> {
    let mut settings = Settings::default();
    let contents = match fs::read_to_string(path) {
        Ok(contents) => contents,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(settings),
        Err(error) => return Err(error.into()),
    };
    let value: serde_json::Value = match serde_json::from_str(&contents) {
        Ok(value) => value,
        Err(_) => return Ok(settings),
    };
    settings.show_over_fullscreen =
        legacy_bool(&value, "ShowOverFullScreen", "show_over_fullscreen");
    settings.deck_on_left_edge = legacy_bool(&value, "DeckOnLeftEdge", "deck_on_left_edge");
    settings.deck_y_ratio =
        legacy_number(&value, "DeckYRatio", "deck_y_ratio", settings.deck_y_ratio);
    settings.display_target = legacy_string(
        &value,
        "DisplayTarget",
        "display_target",
        &settings.display_target,
    );
    settings.note_font_size = legacy_number(
        &value,
        "NoteFontSize",
        "note_font_size",
        settings.note_font_size,
    );
    settings.edge_activation = legacy_number(
        &value,
        "EdgeWidth",
        "edge_activation",
        settings.edge_activation,
    );
    settings.open_on_hover = legacy_bool(&value, "OpenOnHover", "open_on_hover");
    settings.tab_preview =
        legacy_bool_or_default(&value, "TabPreview", "tab_preview", settings.tab_preview);
    settings.markdown_styling = legacy_bool_or_default(
        &value,
        "MarkdownStyling",
        "markdown_styling",
        settings.markdown_styling,
    );
    settings.deck_always_shown = legacy_bool(&value, "DeckAlwaysShown", "deck_always_shown");
    settings.pill_hidden = legacy_bool(&value, "DeckPillHidden", "pill_hidden");
    settings.deck_scale = legacy_number(&value, "DeckScale", "deck_scale", settings.deck_scale);
    if let Some(style) = legacy_value(&value, "DeckStyle", "deck_style") {
        settings.deck_style = match style {
            serde_json::Value::Number(number) if number.as_u64() == Some(1) => {
                crate::model::DeckStyle::ColourChips
            }
            serde_json::Value::String(style)
                if style.eq_ignore_ascii_case("chips")
                    || style.eq_ignore_ascii_case("colour-chips") =>
            {
                crate::model::DeckStyle::ColourChips
            }
            _ => crate::model::DeckStyle::LabelledTabs,
        };
    }
    settings.note_width = legacy_number(
        &value,
        "FloatingNoteWidth",
        "note_width",
        settings.note_width,
    );
    settings.note_height = legacy_number(
        &value,
        "FloatingNoteHeight",
        "note_height",
        settings.note_height,
    );
    settings.launch_at_login = legacy_bool(&value, "LaunchAtLogin", "launch_at_login");
    settings.normalize();
    Ok(settings)
}

#[cfg(windows)]
fn legacy_value<'a>(
    value: &'a serde_json::Value,
    pascal: &str,
    snake: &str,
) -> Option<&'a serde_json::Value> {
    value.get(pascal).or_else(|| value.get(snake))
}

#[cfg(windows)]
fn legacy_bool(value: &serde_json::Value, pascal: &str, snake: &str) -> bool {
    legacy_value(value, pascal, snake)
        .and_then(serde_json::Value::as_bool)
        .unwrap_or(false)
}

#[cfg(windows)]
fn legacy_bool_or_default(
    value: &serde_json::Value,
    pascal: &str,
    snake: &str,
    default: bool,
) -> bool {
    legacy_value(value, pascal, snake)
        .and_then(serde_json::Value::as_bool)
        .unwrap_or(default)
}

#[cfg(windows)]
fn legacy_number(value: &serde_json::Value, pascal: &str, snake: &str, default: f32) -> f32 {
    legacy_value(value, pascal, snake)
        .and_then(serde_json::Value::as_f64)
        .map(|value| value as f32)
        .unwrap_or(default)
}

#[cfg(windows)]
fn legacy_string<'a>(
    value: &'a serde_json::Value,
    pascal: &str,
    snake: &str,
    default: &'a str,
) -> String {
    legacy_value(value, pascal, snake)
        .and_then(serde_json::Value::as_str)
        .unwrap_or(default)
        .to_owned()
}

fn table_columns(connection: &Connection) -> StoreResult<HashSet<String>> {
    let mut statement = connection.prepare("PRAGMA table_info(notes)")?;
    let mut rows = statement.query([])?;
    let mut columns = HashSet::new();
    while let Some(row) = rows.next()? {
        columns.insert(row.get::<_, String>(1)?);
    }
    Ok(columns)
}

fn write_file_atomically(path: &Path, contents: &[u8]) -> io::Result<()> {
    use std::io::Write;

    let file_name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("data");
    let temporary = path.with_file_name(format!(".{file_name}.{}.tmp", uuid::Uuid::new_v4()));
    let result = (|| {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        file.write_all(contents)?;
        file.sync_all()?;
        drop(file);
        replace_file(&temporary, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}

fn replace_file(from: &Path, to: &Path) -> io::Result<()> {
    #[cfg(windows)]
    {
        let from = wide_path(from);
        let to = wide_path(to);
        let moved = unsafe {
            MoveFileExW(
                from.as_ptr(),
                to.as_ptr(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        };
        if moved == 0 {
            return Err(io::Error::last_os_error());
        }
        return Ok(());
    }

    #[cfg(not(windows))]
    {
        fs::rename(from, to)
    }
}

#[cfg(windows)]
fn wide_path(path: &Path) -> Vec<u16> {
    path.as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}

fn direction_name(direction: TextDirection) -> &'static str {
    match direction {
        TextDirection::Automatic => "automatic",
        TextDirection::LeftToRight => "left-to-right",
        TextDirection::RightToLeft => "right-to-left",
    }
}

fn load_or_create_key(path: &Path) -> StoreResult<[u8; 32]> {
    if path.try_exists()? {
        return read_key_file(path);
    }
    let directory = path.parent().unwrap_or_else(|| Path::new("."));
    if database_artifacts_exist(directory)? {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "note encryption key is missing while note database files exist",
        )
        .into());
    }
    let generated = Aes256Gcm::generate_key(&mut OsRng);
    let mut key = [0_u8; 32];
    key.copy_from_slice(&generated);
    let stored = encode_key(&key)?;
    if let Err(error) = write_file_atomically(path, &stored) {
        if path.try_exists()? {
            return read_key_file(path);
        }
        return Err(error.into());
    }
    read_key_file(path)
}

fn read_key_file(path: &Path) -> StoreResult<[u8; 32]> {
    let bytes = fs::read(path)?;
    let decoded = decode_key(&bytes)?;
    if decoded.len() != 32 {
        return Err(
            io::Error::new(io::ErrorKind::InvalidData, "note encryption key is invalid").into(),
        );
    }
    let mut key = [0_u8; 32];
    key.copy_from_slice(&decoded);
    #[cfg(windows)]
    if bytes.len() == 32 {
        let protected = encode_key(&key)?;
        write_file_atomically(path, &protected)?;
    }
    #[cfg(unix)]
    fs::set_permissions(path, std::os::unix::fs::PermissionsExt::from_mode(0o600))?;
    Ok(key)
}

#[cfg(not(windows))]
fn encode_key(key: &[u8; 32]) -> StoreResult<Vec<u8>> {
    Ok(key.to_vec())
}

#[cfg(not(windows))]
fn decode_key(bytes: &[u8]) -> StoreResult<Vec<u8>> {
    Ok(bytes.to_vec())
}

#[cfg(windows)]
fn encode_key(key: &[u8; 32]) -> StoreResult<Vec<u8>> {
    let input = CRYPT_INTEGER_BLOB {
        cbData: key.len() as u32,
        pbData: key.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    let protected = unsafe {
        CryptProtectData(
            &input,
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    if protected == 0 || output.pbData.is_null() {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            format!("could not protect note encryption key: {}", unsafe {
                GetLastError()
            }),
        )
        .into());
    }
    let bytes =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        LocalFree(output.pbData as HLOCAL);
    }
    Ok(bytes)
}

#[cfg(windows)]
fn decode_key(bytes: &[u8]) -> StoreResult<Vec<u8>> {
    if bytes.len() == 32 {
        return Ok(bytes.to_vec());
    }
    let input = CRYPT_INTEGER_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_ptr() as *mut u8,
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    let unprotected = unsafe {
        CryptUnprotectData(
            &input,
            std::ptr::null_mut(),
            std::ptr::null(),
            std::ptr::null(),
            std::ptr::null(),
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut output,
        )
    };
    if unprotected == 0 || output.pbData.is_null() {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            format!("could not unprotect note encryption key: {}", unsafe {
                GetLastError()
            }),
        )
        .into());
    }
    let bytes =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        LocalFree(output.pbData as HLOCAL);
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::Note;

    #[test]
    fn encrypted_body_round_trips_through_sqlite() {
        let mut store = Store::in_memory().unwrap();
        let note = Note::new("private body", 2, 0.0);
        store.save_note(&note).unwrap();
        let loaded = store.load_notes().unwrap();
        assert_eq!(loaded[0].body, "private body");
        assert_ne!(loaded[0].body.as_bytes(), b"");
    }

    #[test]
    fn legacy_table_without_id_constraint_still_supports_edits() {
        let directory =
            std::env::temp_dir().join(format!("noty-legacy-save-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("note.key"), [7_u8; 32]).unwrap();
        Connection::open(directory.join("notes.db"))
            .unwrap()
            .execute_batch(
                "CREATE TABLE notes (
                    id TEXT NOT NULL,
                    title TEXT NOT NULL DEFAULT '',
                    body BLOB NOT NULL,
                    colour INTEGER NOT NULL DEFAULT 0,
                    created_at INTEGER NOT NULL,
                    modified_at INTEGER NOT NULL,
                    archived INTEGER NOT NULL DEFAULT 0,
                    pinned INTEGER NOT NULL DEFAULT 0,
                    sort_order REAL NOT NULL DEFAULT 0,
                    direction TEXT NOT NULL DEFAULT 'automatic'
                );",
            )
            .unwrap();

        let mut store = Store::open(&directory).unwrap();
        let mut note = Note::new("first", 0, 0.0);
        store.save_note(&note).unwrap();
        note.update_body("second");
        store.save_note(&note).unwrap();

        let loaded = store.load_notes().unwrap();
        assert_eq!(loaded.len(), 1);
        assert_eq!(loaded[0].body, "second");
        drop(store);
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn missing_key_beside_database_artifact_is_not_generated() {
        for artifact in ["notes.db", "notes.db-wal", "notes.db-shm"] {
            let directory =
                std::env::temp_dir().join(format!("noty-missing-key-{}", uuid::Uuid::new_v4()));
            fs::create_dir_all(&directory).unwrap();
            let database_artifact = directory.join(artifact);
            fs::write(&database_artifact, b"existing note data").unwrap();

            let error = Store::open(&directory)
                .err()
                .expect("database artifacts must prevent key generation");

            assert!(error.to_string().contains("encryption key is missing"));
            assert!(database_artifact.exists());
            assert!(!directory.join("note.key").exists());
            let _ = fs::remove_dir_all(directory);
        }
    }

    #[test]
    fn schema_version_is_written_and_migrations_are_idempotent() {
        let directory =
            std::env::temp_dir().join(format!("noty-schema-version-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("note.key"), [7_u8; 32]).unwrap();

        let first = Store::open(&directory).unwrap();
        let version: i64 = first
            .connection
            .query_row("PRAGMA user_version", [], |row| row.get(0))
            .unwrap();
        assert_eq!(version, CURRENT_SCHEMA_VERSION);
        drop(first);

        let second = Store::open(&directory).unwrap();
        let version: i64 = second
            .connection
            .query_row("PRAGMA user_version", [], |row| row.get(0))
            .unwrap();
        assert_eq!(version, CURRENT_SCHEMA_VERSION);
        drop(second);
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn unsupported_schema_version_is_not_quarantined() {
        let directory =
            std::env::temp_dir().join(format!("noty-future-schema-{}", uuid::Uuid::new_v4()));
        let database = directory.join("notes.db");
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("note.key"), [7_u8; 32]).unwrap();
        let connection = Connection::open(&database).unwrap();
        connection
            .execute_batch("CREATE TABLE notes (id TEXT PRIMARY KEY NOT NULL, body BLOB NOT NULL); PRAGMA user_version = 99;")
            .unwrap();
        drop(connection);

        let error = Store::open(&directory)
            .err()
            .expect("newer schema versions must fail without replacement");
        assert!(error.to_string().contains("newer than supported version"));
        assert!(database.exists());
        assert!(fs::read_dir(&directory).unwrap().flatten().all(|entry| {
            !entry
                .file_name()
                .to_string_lossy()
                .contains(".corrupt-database-")
        }));
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn corrupt_database_is_quarantined() {
        let directory = std::env::temp_dir().join(format!("noty-corrupt-{}", uuid::Uuid::new_v4()));
        let database = directory.join("notes.db");
        let corrupted: &[u8] = b"not a sqlite database";
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("note.key"), [7_u8; 32]).unwrap();
        fs::write(&database, corrupted).unwrap();

        let mut recovered = Store::open(&directory).expect("corrupt database should recover");
        assert!(recovered.load_notes().unwrap().is_empty());
        drop(recovered);

        assert!(database.exists());
        let quarantined = fs::read_dir(&directory)
            .unwrap()
            .flatten()
            .find(|entry| {
                entry
                    .file_name()
                    .to_string_lossy()
                    .starts_with("notes.db.corrupt-database-")
            })
            .map(|entry| entry.path())
            .expect("corrupt database should be preserved");
        assert_eq!(fs::read(quarantined).unwrap(), corrupted);
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn ordinary_database_open_failure_is_not_quarantined() {
        let directory =
            std::env::temp_dir().join(format!("noty-schema-error-{}", uuid::Uuid::new_v4()));
        let database = directory.join("notes.db");
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("note.key"), [7_u8; 32]).unwrap();
        let connection = Connection::open(&database).unwrap();
        connection
            .execute_batch("CREATE TABLE notes (id TEXT PRIMARY KEY NOT NULL)")
            .unwrap();
        drop(connection);

        let error = Store::open(&directory)
            .err()
            .expect("invalid schema should not be replaced");

        assert!(error.to_string().contains("no body column"));
        assert!(database.exists());
        assert!(fs::read_dir(&directory).unwrap().flatten().all(|entry| {
            !entry
                .file_name()
                .to_string_lossy()
                .contains(".corrupt-database-")
        }));
        let connection = Connection::open(&database).unwrap();
        let columns = table_columns(&connection).unwrap();
        assert_eq!(columns.len(), 1, "failed migrations must roll back");
        assert!(columns.contains("id"));
        drop(connection);
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn database_recovery_requires_verified_sqlite_corruption() {
        for code in [rusqlite::ffi::SQLITE_CORRUPT, rusqlite::ffi::SQLITE_NOTADB] {
            let error = rusqlite::Error::SqliteFailure(rusqlite::ffi::Error::new(code), None);
            assert!(is_database_corruption(&error));
        }

        for code in [
            rusqlite::ffi::SQLITE_BUSY,
            rusqlite::ffi::SQLITE_LOCKED,
            rusqlite::ffi::SQLITE_PERM,
            rusqlite::ffi::SQLITE_FULL,
        ] {
            let error = rusqlite::Error::SqliteFailure(rusqlite::ffi::Error::new(code), None);
            assert!(!is_database_corruption(&error));
        }

        let migration_error = io::Error::new(io::ErrorKind::InvalidData, "no body column");
        assert!(!is_database_corruption(&migration_error));
    }

    #[test]
    fn unreadable_ciphertext_survives_metadata_saves_until_replaced() {
        let mut store = Store::in_memory().unwrap();
        let note = Note::new("private body", 2, 0.0);
        store.save_note(&note).unwrap();
        store
            .connection
            .execute("UPDATE notes SET body = x'00'", [])
            .unwrap();

        let loaded = store.load_notes().unwrap();
        assert_eq!(loaded.len(), 1);
        assert!(loaded[0].body.is_empty());
        assert!(loaded[0].body_unreadable);

        let mut metadata_only = loaded[0].clone();
        metadata_only.pinned = true;
        store.save_note(&metadata_only).unwrap();
        let preserved: Vec<u8> = store
            .connection
            .query_row("SELECT body FROM notes", [], |row| row.get(0))
            .unwrap();
        assert_eq!(preserved, vec![0]);
        assert!(store.load_notes().unwrap()[0].body_unreadable);

        metadata_only.update_body("recovered body");
        store.save_note(&metadata_only).unwrap();
        let recovered = store.load_notes().unwrap();
        assert_eq!(recovered[0].body, "recovered body");
        assert!(!recovered[0].body_unreadable);
    }

    #[test]
    fn unreadable_ciphertext_can_be_restored_after_delete() {
        let mut store = Store::in_memory().unwrap();
        let note = Note::new("private body", 2, 0.0);
        store.save_note(&note).unwrap();
        store
            .connection
            .execute("UPDATE notes SET body = x'00'", [])
            .unwrap();
        let unreadable = store.load_notes().unwrap().remove(0);

        store.delete(&unreadable.id).unwrap();
        store.save_note(&unreadable).unwrap();

        let restored = store.load_notes().unwrap();
        assert_eq!(restored.len(), 1);
        assert!(restored[0].body_unreadable);
    }

    #[test]
    fn malformed_settings_are_quarantined_and_defaults_are_used() {
        let path =
            std::env::temp_dir().join(format!("noty-settings-{}.json", uuid::Uuid::new_v4()));
        fs::write(&path, b"{ definitely not json").unwrap();

        let settings = load_settings(&path).unwrap();

        assert_eq!(settings.deck_scale, Settings::default().deck_scale);
        assert!(!path.exists());
        assert!(
            fs::read_dir(path.parent().unwrap())
                .unwrap()
                .flatten()
                .any(|entry| entry
                    .file_name()
                    .to_string_lossy()
                    .starts_with("noty-settings-"))
        );
        for entry in fs::read_dir(path.parent().unwrap()).unwrap().flatten() {
            if entry
                .file_name()
                .to_string_lossy()
                .starts_with(path.file_name().unwrap().to_string_lossy().as_ref())
            {
                let _ = fs::remove_file(entry.path());
            }
        }
    }

    #[test]
    fn legacy_windows_schema_is_migrated_without_losing_notes() {
        let directory = std::env::temp_dir().join(format!("noty-schema-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("note.key"), [7_u8; 32]).unwrap();
        let source = Store::in_memory().unwrap();
        let sealed = source.seal_body("legacy body").unwrap();
        {
            let connection = Connection::open(directory.join("notes.db")).unwrap();
            connection
                .execute_batch(
                    "CREATE TABLE notes (
                        id TEXT PRIMARY KEY NOT NULL,
                        title TEXT NOT NULL DEFAULT '',
                        body BLOB NOT NULL,
                        color INTEGER NOT NULL DEFAULT 0,
                        created REAL NOT NULL,
                        modified REAL NOT NULL,
                        archived INTEGER NOT NULL DEFAULT 0,
                        sort_order REAL NOT NULL DEFAULT 0,
                        pinned INTEGER NOT NULL DEFAULT 0,
                        text_direction TEXT NOT NULL DEFAULT 'automatic'
                    )",
                )
                .unwrap();
            connection
                .execute(
                    "INSERT INTO notes
                     (id, title, body, color, created, modified, archived, sort_order,
                      pinned, text_direction)
                     VALUES ('legacy', 'Legacy', ?1, 3, 10.0, 20.0, 0, -1.0, 1,
                             'right-to-left')",
                    [&sealed],
                )
                .unwrap();
        }

        let mut migrated = Store::open(&directory).unwrap();
        let notes = migrated.load_notes().unwrap();
        assert_eq!(notes.len(), 1);
        assert_eq!(notes[0].id, "legacy");
        assert_eq!(notes[0].body, "legacy body");
        assert_eq!(notes[0].colour, 3);
        assert!(notes[0].pinned);
        assert_eq!(notes[0].direction, TextDirection::RightToLeft);
        drop(migrated);

        let mut reopened = Store::open(&directory).unwrap();
        let reopened_notes = reopened.load_notes().unwrap();
        assert_eq!(reopened_notes.len(), 1);
        assert_eq!(reopened_notes[0].body, "legacy body");
        assert_eq!(reopened_notes[0].created_at, 10);
        assert_eq!(reopened_notes[0].modified_at, 20);
        drop(reopened);
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn zero_byte_legacy_body_loads_as_empty_plaintext() {
        let directory =
            std::env::temp_dir().join(format!("noty-empty-legacy-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("note.key"), [7_u8; 32]).unwrap();
        {
            let connection = Connection::open(directory.join("notes.db")).unwrap();
            connection
                .execute_batch(
                    "CREATE TABLE notes (
                        id TEXT PRIMARY KEY NOT NULL,
                        title TEXT NOT NULL DEFAULT '',
                        body BLOB NOT NULL,
                        color INTEGER NOT NULL DEFAULT 0,
                        created REAL NOT NULL,
                        modified REAL NOT NULL,
                        archived INTEGER NOT NULL DEFAULT 0,
                        sort_order REAL NOT NULL DEFAULT 0,
                        pinned INTEGER NOT NULL DEFAULT 0,
                        text_direction TEXT NOT NULL DEFAULT 'automatic'
                    );
                    INSERT INTO notes
                    (id, title, body, color, created, modified, archived, sort_order,
                     pinned, text_direction)
                    VALUES ('legacy-empty', '', x'', 0, 0.0, 0.0, 0, 0.0, 0,
                            'automatic');",
                )
                .unwrap();
        }

        let mut migrated = Store::open(&directory).unwrap();
        let notes = migrated.load_notes().unwrap();
        assert_eq!(notes.len(), 1);
        assert!(notes[0].body.is_empty());
        assert!(!notes[0].body_unreadable);
        drop(migrated);
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn invalid_existing_key_is_not_replaced() {
        let directory = std::env::temp_dir().join(format!("noty-key-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join("note.key"), [1_u8, 2, 3]).unwrap();

        assert!(Store::open(&directory).is_err());
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn settings_save_is_atomic_at_the_file_boundary() {
        let path =
            std::env::temp_dir().join(format!("noty-settings-{}.json", uuid::Uuid::new_v4()));
        let settings = Settings::default();
        save_settings(&path, &settings).unwrap();
        assert_eq!(load_settings(&path).unwrap().deck_scale, 1.0);
        let _ = fs::remove_file(path);
    }
}
