//! Per-player notes, votes and toxic marks (C# `PlayerNote`, `PlayerNotesTransfer`).
//!
//! Notes live in `settings.json` under `PlayerNotes` in PascalCase, and export/import as a
//! camelCase `mango-player-notes` document. Both readers accept either casing, plain strings
//! (the oldest format) and the legacy `apem-player-notes` id.

use chrono::{DateTime, FixedOffset, Local};
use indexmap::IndexMap;
use serde::de::Error as _;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::{Map, Value};

use crate::json::Object;
use crate::match_id;
use crate::text::{eq_ignore_case, first_non_empty, fold, is_blank};

pub const FORMAT_ID: &str = "mango-player-notes";
pub const LEGACY_FORMAT_ID: &str = "apem-player-notes";
pub const CURRENT_VERSION: i32 = 3;

#[derive(Debug, Clone, Default, PartialEq)]
pub struct PlayerNote {
    pub player_name: String,
    pub player_id: String,
    pub content: String,
    pub like_count: i32,
    pub dislike_count: i32,
    pub is_toxic: bool,
    pub last_vote_match_id: String,
    /// `like`, `dislike` or empty.
    pub last_vote: String,
    pub updated_at: Option<DateTime<FixedOffset>>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NoteVote {
    Like,
    Dislike,
}

impl NoteVote {
    fn token(self) -> &'static str {
        match self {
            Self::Like => "like",
            Self::Dislike => "dislike",
        }
    }
}

/// Property naming for [`PlayerNote::to_json`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Casing {
    /// `PlayerName` — `settings.json`.
    Pascal,
    /// `playerName` — exported files.
    Camel,
}

impl PlayerNote {
    /// The player id when known, otherwise the name.
    pub fn storage_key(&self) -> String {
        lookup_key(&self.player_id, &self.player_name)
    }

    pub fn has_saved_data(&self) -> bool {
        !is_blank(&self.content) || self.like_count > 0 || self.dislike_count > 0 || self.is_toxic
    }

    pub fn has_voted_in_match(&self, match_id: &str) -> bool {
        let id = match_id.trim();
        match_id::is_usable(id) && eq_ignore_case(&self.last_vote_match_id, id)
    }

    /// Old notes were keyed by id or name with no identity inside; recover it from the key.
    pub fn fill_identity_from_key(&mut self, key: &str) {
        if is_blank(key) {
            return;
        }
        if looks_like_steam_id(key) {
            if is_blank(&self.player_id) {
                self.player_id = key.trim().into();
            }
        } else if is_blank(&self.player_name) {
            self.player_name = key.trim().into();
        }
    }

    /// Reads one note: a plain string (content only), `null`, or an object with any casing.
    pub fn from_json(value: &Value) -> Result<Self, String> {
        match value {
            Value::String(content) => Ok(Self { content: content.clone(), ..Default::default() }),
            Value::Null => Ok(Self::default()),
            Value::Object(o) => Ok(Self::from_object(o)),
            _ => Err("Each note must be a string or an object.".into()),
        }
    }

    fn from_object(o: &Object) -> Self {
        let find = |name: &'static str| {
            o.iter().filter(move |(key, _)| key.eq_ignore_ascii_case(name)).map(|(_, v)| v)
        };
        let string = |name: &'static str| find(name).find_map(Value::as_str).unwrap_or_default().to_string();
        let count = |name: &'static str| {
            find(name)
                .find_map(|v| match v {
                    Value::Number(n) => n.as_i64().and_then(|n| i32::try_from(n).ok()),
                    Value::String(s) => s.trim().parse().ok(),
                    _ => None,
                })
                .unwrap_or_default()
                .max(0)
        };
        Self {
            player_name: string("playerName"),
            player_id: string("playerId"),
            content: string("content"),
            like_count: count("likeCount"),
            dislike_count: count("dislikeCount"),
            is_toxic: find("isToxic").any(|v| v.as_bool() == Some(true)),
            last_vote_match_id: string("lastVoteMatchId"),
            last_vote: string("lastVote"),
            updated_at: find("updatedAt").filter_map(Value::as_str).find_map(parse_time),
        }
    }

    pub fn to_json(&self, casing: Casing) -> Value {
        let name = |pascal: &str| match casing {
            Casing::Pascal => pascal.to_string(),
            Casing::Camel => pascal[..1].to_ascii_lowercase() + &pascal[1..],
        };
        let mut o = Map::new();
        o.insert(name("PlayerName"), self.player_name.clone().into());
        o.insert(name("PlayerId"), self.player_id.clone().into());
        o.insert(name("Content"), self.content.clone().into());
        o.insert(name("LikeCount"), self.like_count.into());
        o.insert(name("DislikeCount"), self.dislike_count.into());
        o.insert(name("IsToxic"), self.is_toxic.into());
        if !is_blank(&self.last_vote_match_id) {
            o.insert(name("LastVoteMatchId"), self.last_vote_match_id.clone().into());
        }
        if !is_blank(&self.last_vote) {
            o.insert(name("LastVote"), self.last_vote.clone().into());
        }
        if let Some(updated) = self.updated_at {
            o.insert(name("UpdatedAt"), format_time(updated).into());
        }
        Value::Object(o)
    }

    /// Trimmed copy (C# `Clone`).
    fn trimmed(&self) -> Self {
        Self {
            player_name: self.player_name.trim().into(),
            player_id: self.player_id.trim().into(),
            content: self.content.trim().into(),
            last_vote_match_id: self.last_vote_match_id.trim().into(),
            last_vote: self.last_vote.trim().into(),
            ..self.clone()
        }
    }
}

/// `.NET` `DateTimeOffset.TryParse` is lenient; accept RFC 3339 and a few common variants.
fn parse_time(text: &str) -> Option<DateTime<FixedOffset>> {
    let text = text.trim();
    DateTime::parse_from_rfc3339(text)
        .or_else(|_| DateTime::parse_from_str(text, "%Y-%m-%dT%H:%M:%S%.f%:z"))
        .or_else(|_| DateTime::parse_from_str(text, "%Y-%m-%d %H:%M:%S%.f%:z"))
        .ok()
        .or_else(|| {
            chrono::NaiveDateTime::parse_from_str(text, "%Y-%m-%dT%H:%M:%S%.f")
                .ok()
                .and_then(|naive| naive.and_local_timezone(Local).single())
                .map(|local| local.fixed_offset())
        })
}

/// System.Text.Json's `DateTimeOffset` form: up to 7 fractional digits (100 ns ticks),
/// trailing zeros trimmed, e.g. `2026-10-03T21:14:55.1234567+07:00`.
fn format_time(time: DateTime<FixedOffset>) -> String {
    let base = time.format("%Y-%m-%dT%H:%M:%S").to_string();
    let ticks = format!("{:07}", time.timestamp_subsec_nanos() / 100);
    let fraction = ticks.trim_end_matches('0');
    let offset = time.format("%:z");
    if fraction.is_empty() {
        format!("{base}{offset}")
    } else {
        format!("{base}.{fraction}{offset}")
    }
}

fn now() -> DateTime<FixedOffset> {
    Local::now().fixed_offset()
}

/// The id when present, otherwise the name; trimmed.
pub fn lookup_key(player_id: &str, player_name: &str) -> String {
    let id = player_id.trim();
    if !id.is_empty() {
        id.into()
    } else {
        player_name.trim().into()
    }
}

pub fn looks_like_steam_id(value: &str) -> bool {
    value.chars().count() >= 15 && value.chars().all(|c| c.is_ascii_digit())
}

/// Notes keyed by player id or name, case-insensitively, in insertion order.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct NotesMap {
    entries: IndexMap<String, (String, PlayerNote)>,
}

impl NotesMap {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn get(&self, key: &str) -> Option<&PlayerNote> {
        self.entries.get(&fold(key)).map(|(_, note)| note)
    }

    pub fn contains_key(&self, key: &str) -> bool {
        self.entries.contains_key(&fold(key))
    }

    /// Replaces in place (keeping the original key's spelling and position) or appends.
    pub fn insert(&mut self, key: &str, note: PlayerNote) {
        match self.entries.get_mut(&fold(key)) {
            Some(entry) => entry.1 = note,
            None => {
                self.entries.insert(fold(key), (key.to_string(), note));
            }
        }
    }

    pub fn remove(&mut self, key: &str) -> Option<PlayerNote> {
        self.entries.shift_remove(&fold(key)).map(|(_, note)| note)
    }

    pub fn clear(&mut self) {
        self.entries.clear();
    }

    pub fn iter(&self) -> impl Iterator<Item = (&str, &PlayerNote)> {
        self.entries.values().map(|(key, note)| (key.as_str(), note))
    }

    pub fn values(&self) -> impl Iterator<Item = &PlayerNote> {
        self.entries.values().map(|(_, note)| note)
    }

    /// `settings.json` form: `{ key: { PlayerName, ... } }`.
    pub fn to_json(&self, casing: Casing) -> Value {
        Value::Object(self.iter().map(|(key, note)| (key.to_string(), note.to_json(casing))).collect())
    }

    /// Reads a `{ key: note }` object (no trimming; see [`normalize_stored`] for that).
    pub fn from_json(value: &Value) -> Result<Self, String> {
        let Value::Object(o) = value else {
            return Err("Player notes must be a JSON object.".into());
        };
        let mut notes = Self::new();
        for (key, value) in o {
            notes.insert(key, PlayerNote::from_json(value)?);
        }
        Ok(notes)
    }
}

impl<'a> FromIterator<(&'a str, PlayerNote)> for NotesMap {
    fn from_iter<I: IntoIterator<Item = (&'a str, PlayerNote)>>(iter: I) -> Self {
        let mut notes = Self::new();
        for (key, note) in iter {
            notes.insert(key, note);
        }
        notes
    }
}

impl Serialize for NotesMap {
    fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
        self.to_json(Casing::Pascal).serialize(serializer)
    }
}

impl<'de> Deserialize<'de> for NotesMap {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        let value = Value::deserialize(deserializer)?;
        if value.is_null() {
            return Ok(Self::new());
        }
        Self::from_json(&value).map_err(D::Error::custom)
    }
}

/// Settings-load cleanup (C# `AppSettings.NormalizeNotes` via `Normalize`): trims, recovers
/// identity from keys, re-keys by id, and drops notes with nothing saved.
pub fn normalize_stored(notes: &NotesMap) -> NotesMap {
    let mut normalized = NotesMap::new();
    for (key, value) in notes.iter() {
        let mut note = value.trimmed();
        note.fill_identity_from_key(key);
        if !note.has_saved_data() {
            continue;
        }
        let storage_key = match note.storage_key() {
            k if is_blank(&k) => key.trim().to_string(),
            k => k,
        };
        if !is_blank(&storage_key) {
            normalized.insert(&storage_key, note);
        }
    }
    normalized
}

/// Export document: `{ format, version, notes: [...] }`, camelCase, indented.
pub fn serialize(notes: &NotesMap) -> String {
    let list: Vec<Value> = normalize_stored(notes).values().map(|n| n.to_json(Casing::Camel)).collect();
    let mut document = Map::new();
    document.insert("format".into(), FORMAT_ID.into());
    document.insert("version".into(), CURRENT_VERSION.into());
    document.insert("notes".into(), Value::Array(list));
    serde_json::to_string_pretty(&Value::Object(document)).expect("JSON values serialize")
}

/// Reads an export document, a legacy keyed map, or a raw `{ name: "text" }` map.
pub fn try_parse(json: &str) -> Result<NotesMap, String> {
    if is_blank(json) {
        return Err("The file is empty.".into());
    }
    let root: Value = serde_json::from_str(json).map_err(|_| "That file is not valid JSON.".to_string())?;
    let Value::Object(o) = &root else {
        return Err("Player notes must be a JSON object.".into());
    };

    if let Some(notes) = o.get("notes") {
        if let Some(format) = o.get("format").and_then(Value::as_str) {
            if !eq_ignore_case(format, FORMAT_ID) && !eq_ignore_case(format, LEGACY_FORMAT_ID) {
                return Err("That JSON file is not a Mango player notes export.".into());
            }
        }
        return read_notes(notes);
    }
    read_notes(&root)
}

fn read_notes(element: &Value) -> Result<NotesMap, String> {
    let mut notes = NotesMap::new();
    match element {
        Value::Array(items) => {
            for item in items {
                add_note(&mut notes, read_note(item, "")?);
            }
        }
        Value::Object(o) => {
            for (key, value) in o {
                add_note(&mut notes, read_note(value, key)?);
            }
        }
        _ => return Err("The notes field must be an object or an array.".into()),
    }
    Ok(notes)
}

fn read_note(element: &Value, key_hint: &str) -> Result<PlayerNote, String> {
    let mut note = match element {
        Value::Null => return Ok(PlayerNote::default()),
        Value::String(_) | Value::Object(_) => PlayerNote::from_json(element)?.trimmed(),
        _ => return Err("Each note must be a string or an object.".into()),
    };
    note.like_count = note.like_count.max(0);
    note.dislike_count = note.dislike_count.max(0);
    note.fill_identity_from_key(key_hint);
    Ok(note)
}

fn add_note(notes: &mut NotesMap, note: PlayerNote) {
    let key = note.storage_key();
    if note.has_saved_data() && !is_blank(&key) {
        notes.insert(&key, note);
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MergeResult {
    pub added: usize,
    pub updated: usize,
    pub skipped: usize,
    pub total: usize,
}

/// Imports into `target`; `replace` clears it first. Imported values win field by field.
pub fn apply(target: &mut NotesMap, imported: &NotesMap, replace: bool) -> MergeResult {
    if replace {
        target.clear();
    }
    let mut result = MergeResult::default();
    for (key, incoming) in normalize_stored(imported).iter() {
        if !incoming.has_saved_data() {
            result.skipped += 1;
            continue;
        }
        match target.get(key) {
            Some(existing) if notes_equal(existing, incoming) => result.skipped += 1,
            Some(existing) => {
                let merged = merge_note(existing, incoming);
                target.insert(key, merged);
                result.updated += 1;
            }
            None => {
                target.insert(key, incoming.clone());
                result.added += 1;
            }
        }
    }
    result.total = target.len();
    result
}

/// The note for a player id. A name-keyed note never matches, so renamed players keep notes.
pub fn get_by_player_id<'a>(notes: &'a NotesMap, player_id: &str) -> Option<&'a PlayerNote> {
    let id = player_id.trim();
    if id.is_empty() {
        return None;
    }
    if let Some(note) = notes.get(id) {
        if has_player_id(note, id) || (is_blank(&note.player_id) && looks_like_steam_id(id)) {
            return Some(note);
        }
    }
    notes.values().find(|note| has_player_id(note, id))
}

pub fn find_content_by_player_id(notes: &NotesMap, player_id: &str) -> String {
    get_by_player_id(notes, player_id).map(|n| n.content.clone()).unwrap_or_default()
}

/// Saves note text; blank text deletes the note unless it still holds votes or a toxic mark.
pub fn upsert(notes: &mut NotesMap, player_name: &str, player_id: &str, content: &str) {
    let key = lookup_key(player_id, player_name);
    if is_blank(&key) {
        return;
    }
    let existing = notes.get(&key);
    let trimmed = content.trim();
    let keeps_data = existing.is_some_and(|n| n.like_count > 0 || n.dislike_count > 0 || n.is_toxic);
    if trimmed.is_empty() && !keeps_data {
        notes.remove(&key);
        return;
    }
    let note = with_identity(existing, player_name, player_id, trimmed);
    notes.insert(&key, note);
}

/// Toggles the toxic mark. False when the player has no id.
pub fn toggle_toxic(notes: &mut NotesMap, player_name: &str, player_id: &str) -> bool {
    if is_blank(player_id) {
        return false;
    }
    let id = player_id.trim();
    let existing = get_by_player_id(notes, id);
    let content = existing.map(|n| n.content.clone()).unwrap_or_default();
    let mut note = with_identity(existing, player_name, id, &content);
    note.is_toxic = !note.is_toxic;
    store_or_remove(notes, note);
    true
}

/// One like or dislike per match: the same vote again removes it, the other vote switches it.
/// False when the player has no id or the match id is unusable.
pub fn vote(notes: &mut NotesMap, player_name: &str, player_id: &str, match_id: &str, vote: NoteVote) -> bool {
    if is_blank(player_id) || !match_id::is_usable(match_id) {
        return false;
    }
    let id = player_id.trim();
    let match_id = match_id.trim();
    let existing = get_by_player_id(notes, id);
    let content = existing.map(|n| n.content.clone()).unwrap_or_default();
    let mut note = with_identity(existing, player_name, id, &content);

    if note.has_voted_in_match(match_id) {
        let current = note.last_vote.trim().to_string();
        if eq_ignore_case(&current, vote.token()) {
            adjust(&mut note, vote, -1);
            note.last_vote_match_id.clear();
            note.last_vote.clear();
        } else {
            if eq_ignore_case(&current, "like") {
                adjust(&mut note, NoteVote::Like, -1);
            } else if eq_ignore_case(&current, "dislike") {
                adjust(&mut note, NoteVote::Dislike, -1);
            }
            apply_vote(&mut note, vote, match_id);
        }
    } else {
        apply_vote(&mut note, vote, match_id);
    }

    store_or_remove(notes, note);
    true
}

fn store_or_remove(notes: &mut NotesMap, note: PlayerNote) {
    let key = note.storage_key();
    if note.has_saved_data() {
        notes.insert(&key, note);
    } else {
        notes.remove(&key);
    }
}

fn apply_vote(note: &mut PlayerNote, vote: NoteVote, match_id: &str) {
    note.last_vote_match_id = match_id.into();
    note.last_vote = vote.token().into();
    adjust(note, vote, 1);
}

fn adjust(note: &mut PlayerNote, vote: NoteVote, delta: i32) {
    let count = match vote {
        NoteVote::Like => &mut note.like_count,
        NoteVote::Dislike => &mut note.dislike_count,
    };
    *count = (*count + delta).max(0);
}

fn with_identity(existing: Option<&PlayerNote>, player_name: &str, player_id: &str, content: &str) -> PlayerNote {
    let mut note = existing.map(PlayerNote::trimmed).unwrap_or_default();
    note.player_name = first_non_empty(&[player_name, &note.player_name]);
    note.player_id = first_non_empty(&[player_id, &note.player_id]);
    note.content = content.into();
    note.updated_at = Some(now());
    note
}

fn merge_note(existing: &PlayerNote, incoming: &PlayerNote) -> PlayerNote {
    PlayerNote {
        player_name: first_non_empty(&[&incoming.player_name, &existing.player_name]),
        player_id: first_non_empty(&[&incoming.player_id, &existing.player_id]),
        content: incoming.content.trim().into(),
        like_count: incoming.like_count,
        dislike_count: incoming.dislike_count,
        is_toxic: incoming.is_toxic,
        last_vote_match_id: first_non_empty(&[&incoming.last_vote_match_id, &existing.last_vote_match_id]),
        last_vote: first_non_empty(&[&incoming.last_vote, &existing.last_vote]),
        updated_at: incoming.updated_at.or(existing.updated_at).or_else(|| Some(now())),
    }
}

fn notes_equal(a: &PlayerNote, b: &PlayerNote) -> bool {
    a.content == b.content
        && a.player_name == b.player_name
        && a.player_id == b.player_id
        && a.like_count == b.like_count
        && a.dislike_count == b.dislike_count
        && a.is_toxic == b.is_toxic
        && eq_ignore_case(&a.last_vote_match_id, &b.last_vote_match_id)
        && eq_ignore_case(&a.last_vote, &b.last_vote)
}

fn has_player_id(note: &PlayerNote, id: &str) -> bool {
    eq_ignore_case(note.player_id.trim(), id)
}

#[cfg(test)]
mod tests;
