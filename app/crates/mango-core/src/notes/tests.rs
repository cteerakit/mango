//! Ported from `PlayerNotesTransferTests.cs`, plus settings-format compatibility checks.

use super::*;

const ID: &str = "76561198000000000";

fn note(name: &str, content: &str) -> PlayerNote {
    PlayerNote { player_name: name.into(), content: content.into(), ..Default::default() }
}

#[test]
fn serialize_round_trips_notes() {
    let notes = NotesMap::from_iter([
        (
            ID,
            PlayerNote {
                player_id: ID.into(),
                updated_at: DateTime::parse_from_rfc3339("2026-08-24T01:00:00+07:00").ok(),
                ..note("Invoker", "Always mid")
            },
        ),
        ("Dire Carry", note("Dire Carry", "Rushes BKB")),
    ]);

    let json = serialize(&notes);
    assert!(json.contains("mango-player-notes"));
    assert!(json.contains("\"updatedAt\": \"2026-08-24T01:00:00+07:00\""), "{json}");

    let parsed = try_parse(&json).unwrap();
    assert_eq!(parsed.get(ID).unwrap().content, "Always mid");
    assert_eq!(parsed.get(ID).unwrap().player_name, "Invoker");
    assert_eq!(parsed.get(ID).unwrap().updated_at, notes.get(ID).unwrap().updated_at);
    assert_eq!(parsed.get("Dire Carry").unwrap().content, "Rushes BKB");
}

#[test]
fn try_parse_accepts_raw_string_map() {
    let notes = try_parse(r#"{ "Player One": "Ganks a lot" }"#).unwrap();
    assert_eq!(notes.get("Player One").unwrap().content, "Ganks a lot");
    assert_eq!(notes.get("Player One").unwrap().player_name, "Player One");
}

#[test]
fn try_parse_accepts_legacy_keyed_strings() {
    let notes = try_parse(r#"{ "format": "apem-player-notes", "version": 1, "notes": { "76561198000000000": "Always mid" } }"#)
        .unwrap();
    assert_eq!(notes.get(ID).unwrap().content, "Always mid");
    assert_eq!(notes.get(ID).unwrap().player_id, ID);
}

#[test]
fn try_parse_rejects_invalid_json() {
    assert_eq!(try_parse("{ not json").unwrap_err(), "That file is not valid JSON.");
    assert_eq!(try_parse("  ").unwrap_err(), "The file is empty.");
    assert_eq!(try_parse("[]").unwrap_err(), "Player notes must be a JSON object.");
    assert_eq!(try_parse(r#"{ "notes": 5 }"#).unwrap_err(), "The notes field must be an object or an array.");
    assert_eq!(try_parse(r#"{ "a": 5 }"#).unwrap_err(), "Each note must be a string or an object.");
}

#[test]
fn try_parse_rejects_unknown_format() {
    assert_eq!(
        try_parse(r#"{ "format": "other", "notes": { "a": "b" } }"#).unwrap_err(),
        "That JSON file is not a Mango player notes export."
    );
}

#[test]
fn apply_merges_and_updates() {
    let mut target = NotesMap::from_iter([("existing", note("existing", "old")), ("keep", note("keep", "same"))]);
    let imported = NotesMap::from_iter([
        ("existing", note("existing", "new")),
        ("keep", note("keep", "same")),
        ("fresh", note("fresh", "added")),
    ]);

    let result = apply(&mut target, &imported, false);

    assert_eq!(result, MergeResult { added: 1, updated: 1, skipped: 1, total: 3 });
    assert_eq!(target.get("existing").unwrap().content, "new");
    assert_eq!(target.get("keep").unwrap().content, "same");
    assert_eq!(target.get("fresh").unwrap().content, "added");
}

#[test]
fn apply_replace_clears_existing_notes() {
    let mut target = NotesMap::from_iter([("old", note("old", "gone"))]);

    let result = apply(&mut target, &NotesMap::from_iter([("new", note("new", "kept"))]), true);

    assert!(!target.contains_key("old"));
    assert_eq!(target.get("new").unwrap().content, "kept");
    assert_eq!((result.added, result.total), (1, 1));
}

#[test]
fn upsert_sets_name_id_content_and_timestamp() {
    let mut notes = NotesMap::new();
    upsert(&mut notes, "Sakamoto", ID, "Test");

    let n = notes.get(ID).unwrap();
    assert_eq!((n.player_name.as_str(), n.player_id.as_str(), n.content.as_str()), ("Sakamoto", ID, "Test"));
    assert!(n.updated_at.is_some());
}

#[test]
fn get_by_player_id_matches_id_and_ignores_name() {
    let notes = NotesMap::from_iter([
        ("Invoker", note("Invoker", "By name")),
        (ID, PlayerNote { player_id: ID.into(), ..note("Different nick", "By id") }),
    ]);

    assert_eq!(get_by_player_id(&notes, ID).unwrap().content, "By id");
    assert_eq!(find_content_by_player_id(&notes, "Invoker"), "");
    assert!(get_by_player_id(&notes, "Invoker").is_none());
    assert_eq!(find_content_by_player_id(&notes, ""), "");
}

#[test]
fn serialize_round_trips_like_and_dislike_counts() {
    let notes = NotesMap::from_iter([(
        ID,
        PlayerNote {
            player_id: ID.into(),
            like_count: 3,
            dislike_count: 1,
            last_vote_match_id: "8345".into(),
            last_vote: "like".into(),
            ..note("Invoker", "Always mid")
        },
    )]);

    let json = serialize(&notes);
    assert!(json.contains("likeCount") && json.contains("dislikeCount"));

    let parsed = try_parse(&json).unwrap();
    let n = parsed.get(ID).unwrap();
    assert_eq!((n.like_count, n.dislike_count), (3, 1));
    assert_eq!((n.last_vote_match_id.as_str(), n.last_vote.as_str()), ("8345", "like"));
}

#[test]
fn vote_toggles_same_vote_and_switches_opposite_vote() {
    let mut notes = NotesMap::new();

    assert!(vote(&mut notes, "Invoker", ID, "match-1", NoteVote::Like));
    assert_eq!(notes.get(ID).unwrap().like_count, 1);
    assert_eq!(notes.get(ID).unwrap().last_vote, "like");

    assert!(vote(&mut notes, "Invoker", ID, "match-1", NoteVote::Like));
    assert!(!notes.contains_key(ID));

    assert!(vote(&mut notes, "Invoker", ID, "match-1", NoteVote::Like));
    assert!(vote(&mut notes, "Invoker", ID, "match-1", NoteVote::Dislike));
    let n = notes.get(ID).unwrap();
    assert_eq!((n.like_count, n.dislike_count), (0, 1));
    assert_eq!((n.last_vote_match_id.as_str(), n.last_vote.as_str()), ("match-1", "dislike"));

    assert!(vote(&mut notes, "Invoker", ID, "match-2", NoteVote::Like));
    let n = notes.get(ID).unwrap();
    assert_eq!((n.like_count, n.dislike_count), (1, 1));
    assert_eq!((n.last_vote_match_id.as_str(), n.last_vote.as_str()), ("match-2", "like"));

    assert!(!vote(&mut notes, "Invoker", ID, "0", NoteVote::Like));
    assert!(!vote(&mut notes, "Invoker", " ", "match-3", NoteVote::Like));
}

#[test]
fn vote_keeps_vote_only_notes_without_content() {
    let mut notes = NotesMap::new();
    assert!(vote(&mut notes, "Invoker", ID, "match-1", NoteVote::Like));

    let parsed = try_parse(&serialize(&notes)).unwrap();
    let n = parsed.get(ID).unwrap();
    assert!(n.has_saved_data());
    assert_eq!(n.like_count, 1);
    assert_eq!(n.content, "");
}

#[test]
fn toxic_mark_round_trips_without_note_and_toggles_off() {
    let mut notes = NotesMap::new();
    assert!(toggle_toxic(&mut notes, "Invoker", ID));
    assert!(notes.get(ID).unwrap().is_toxic);

    upsert(&mut notes, "Invoker", ID, "");
    let mut parsed = try_parse(&serialize(&notes)).unwrap();
    assert!(parsed.get(ID).unwrap().is_toxic);

    assert!(toggle_toxic(&mut parsed, "Invoker", ID));
    assert!(!parsed.contains_key(ID));
}

#[test]
fn toxic_mark_is_independent_of_votes_and_preserved_when_editing_note() {
    let mut notes = NotesMap::new();
    toggle_toxic(&mut notes, "Invoker", ID);
    vote(&mut notes, "Invoker", ID, "match-1", NoteVote::Dislike);
    upsert(&mut notes, "Invoker", ID, "Always mid");
    assert!(notes.get(ID).unwrap().is_toxic);
    assert_eq!(notes.get(ID).unwrap().last_vote, "dislike");

    toggle_toxic(&mut notes, "Invoker", ID);
    let n = notes.get(ID).unwrap();
    assert!(!n.is_toxic);
    assert_eq!(n.last_vote, "dislike");
    assert_eq!(n.content, "Always mid");
    assert!(!toggle_toxic(&mut notes, "Unknown", ""));
}

#[test]
fn upsert_preserves_like_counts_when_editing_content() {
    let mut notes = NotesMap::new();
    vote(&mut notes, "Invoker", ID, "match-1", NoteVote::Like);
    upsert(&mut notes, "Invoker", ID, "Always mid");

    let n = notes.get(ID).unwrap();
    assert_eq!(n.content, "Always mid");
    assert_eq!(n.like_count, 1);
}

#[test]
fn keys_are_case_insensitive_and_keep_order() {
    let mut notes = NotesMap::from_iter([("Bob", note("Bob", "a")), ("alice", note("alice", "b"))]);
    notes.insert("BOB", note("Bob", "c"));
    assert_eq!(notes.len(), 2);
    assert_eq!(notes.get("bob").unwrap().content, "c");
    let keys: Vec<_> = notes.iter().map(|(k, _)| k).collect();
    assert_eq!(keys, ["Bob", "alice"]);
}

/// The WinUI app writes `settings.json` notes in PascalCase with .NET timestamps
/// (7 fractional digits); older files hold plain strings.
#[test]
fn reads_and_writes_winui_settings_notes() {
    let stored = r#"{
      "76561198000000000": {
        "PlayerName": "Invoker", "PlayerId": "76561198000000000", "Content": " Always mid ",
        "LikeCount": 2, "DislikeCount": 0, "IsToxic": true, "LastVoteMatchId": "8345", "LastVote": "like",
        "UpdatedAt": "2026-10-03T21:14:55.1234567+07:00"
      },
      "Old Nick": "legacy text",
      "Empty": { "PlayerName": "Empty", "Content": "" }
    }"#;

    let notes: NotesMap = serde_json::from_str(stored).unwrap();
    assert_eq!(notes.len(), 3);
    let n = notes.get(ID).unwrap();
    assert_eq!(n.content, " Always mid ", "settings read does not trim; normalize does");
    assert!(n.is_toxic);
    assert_eq!(n.like_count, 2);
    assert_eq!(n.updated_at.unwrap().to_rfc3339(), "2026-10-03T21:14:55.123456700+07:00");

    let cleaned = normalize_stored(&notes);
    assert_eq!(cleaned.len(), 2, "empty note dropped");
    assert_eq!(cleaned.get(ID).unwrap().content, "Always mid");
    assert_eq!(cleaned.get("Old Nick").unwrap().player_name, "Old Nick");

    let written = serde_json::to_value(&cleaned).unwrap();
    let entry = &written[ID];
    assert_eq!(entry["UpdatedAt"], "2026-10-03T21:14:55.1234567+07:00", ".NET timestamp format kept");
    assert_eq!(entry["PlayerName"], "Invoker");
    assert_eq!(entry["IsToxic"], true);
    assert_eq!(entry["LastVote"], "like");
    assert!(written["Old Nick"].get("LastVote").is_none(), "blank optional fields omitted");

    let reread: NotesMap = serde_json::from_value(written).unwrap();
    assert_eq!(reread, cleaned);
}
