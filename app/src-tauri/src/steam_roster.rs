//! Lobby roster from the local Steam client (C# `LocalSteamRosterService` + `Read-SteamRoster.ps1`).
//!
//! Dota's console log names the game server; Steam's Friends API lists who is on it. Calling
//! Steam means initialising the Steam API as Dota (app 570), so it runs in a short-lived copy
//! of Mango (`Mango.exe --steam-roster <dll> <server>`) that exits right after — never in the
//! app itself. This replaces the WinUI app's PowerShell script.

use std::collections::HashMap;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::process::Stdio;
use std::sync::Mutex;
use std::time::Duration;

use mango_core::model::{MatchPlayer, MatchSnapshot};
use mango_core::{match_id, steam_id, steam_local};
use serde_json::json;

use crate::dota;

pub const HELPER_FLAG: &str = "--steam-roster";
const RESULT_PREFIX: &str = "MANGO_STEAM_ROSTER:";
const LOG_TAIL_BYTES: u64 = 1024 * 1024;

pub struct LocalSteamRoster {
    dota_paths: Vec<PathBuf>,
    /// match id → (server steam id, steam_api64.dll)
    servers: Mutex<HashMap<String, (String, PathBuf)>>,
}

impl LocalSteamRoster {
    pub fn new(dota_paths: Vec<PathBuf>) -> Self {
        Self { dota_paths, servers: Mutex::new(HashMap::new()) }
    }

    /// `(players, server_steam_id)`; players may be empty when only the server is known.
    pub async fn roster(&self, snapshot: &MatchSnapshot) -> Option<(Vec<MatchPlayer>, String)> {
        if !match_id::is_usable(&snapshot.match_id) || steam_id::to_account_id(&snapshot.steam_id).is_none() {
            return None;
        }
        let connection = self.server_for(&snapshot.match_id)?;
        let (server, library) = connection;
        let output = run_helper(&library, &server).await?;
        let line = output.lines().rev().find(|l| l.starts_with(RESULT_PREFIX))?;
        let players = steam_local::read_players(&line[RESULT_PREFIX.len()..], &server, &snapshot.steam_id);
        Some((players, server))
    }

    pub(crate) fn server_for(&self, match_id: &str) -> Option<(String, PathBuf)> {
        if let Some(found) = self.servers.lock().unwrap().get(match_id) {
            return Some(found.clone());
        }
        for dota in &self.dota_paths {
            let log = dota::console_log(dota);
            let library = dota::steam_api_library(dota);
            if !log.exists() || !library.exists() {
                continue;
            }
            let Some(text) = read_tail(&log, LOG_TAIL_BYTES) else { continue };
            if let Some(server) = steam_local::find_server(&text, match_id) {
                let found = (server, library);
                self.servers.lock().unwrap().insert(match_id.to_string(), found.clone());
                return Some(found);
            }
        }
        None
    }
}

/// The last `bytes` of a file Dota keeps open for writing.
fn read_tail(path: &Path, bytes: u64) -> Option<String> {
    let mut file = std::fs::File::open(path).ok()?;
    let length = file.metadata().ok()?.len();
    file.seek(SeekFrom::Start(length.saturating_sub(bytes))).ok()?;
    let mut buffer = Vec::new();
    file.read_to_end(&mut buffer).ok()?;
    Some(String::from_utf8_lossy(&buffer).into_owned())
}

/// Confirmed Steam friends in the local Dota party, including the local user.
pub async fn party_members(library: &Path) -> Vec<MatchPlayer> {
    let Some(output) = run_helper(library, "party").await else { return Vec::new() };
    let Some(json) = output.lines().find_map(|line| line.strip_prefix(RESULT_PREFIX)) else { return Vec::new() };
    let local = serde_json::from_str::<serde_json::Value>(json).ok()
        .and_then(|v| v.pointer("/players/0/steamId")?.as_str().map(str::to_owned));
    local.map(|id| steam_local::read_players(json, "party", &id)).unwrap_or_default()
}

async fn run_helper(library: &Path, server: &str) -> Option<String> {
    let exe = std::env::current_exe().ok()?;
    let mut id = [0u8; 8];
    let _ = getrandom::fill(&mut id);
    let scratch = std::env::temp_dir().join(format!("Mango-SteamRoster-{}", hex::encode(id)));
    std::fs::create_dir_all(&scratch).ok()?;
    // SteamAPI_Init reads the app id from the working directory.
    std::fs::write(scratch.join("steam_appid.txt"), "570").ok()?;

    let mut command = tokio::process::Command::new(exe);
    command
        .arg(HELPER_FLAG)
        .arg(library)
        .arg(server)
        .current_dir(&scratch)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .kill_on_drop(true);
    #[cfg(windows)]
    command.creation_flags(0x0800_0000); // CREATE_NO_WINDOW

    let result = match command.spawn() {
        Ok(child) => match tokio::time::timeout(Duration::from_secs(8), child.wait_with_output()).await {
            Ok(Ok(output)) if output.status.success() => Some(String::from_utf8_lossy(&output.stdout).into_owned()),
            _ => None,
        },
        Err(_) => None,
    };
    let _ = std::fs::remove_dir_all(&scratch);
    result
}

/// Entry point of the helper process. Prints `MANGO_STEAM_ROSTER:{json}` and returns the exit code.
pub fn helper_main(args: &[String]) -> i32 {
    let (Some(library), Some(server)) = (args.first(), args.get(1)) else { return 2 };
    let server_id = if server == "party" { None } else if let Ok(id) = server.parse::<u64>() { Some(id) } else { return 2 };
    match read_server_members(Path::new(library), server_id) {
        Ok(players) => {
            let players: Vec<_> = players.into_iter().map(|(id, name)| json!({"steamId": id.to_string(), "name": name})).collect();
            println!("{RESULT_PREFIX}{}", json!({"serverSteamId": server, "players": players}));
            0
        }
        Err(error) => {
            eprintln!("{error}");
            1
        }
    }
}

#[cfg(windows)]
fn read_server_members(library: &Path, server: Option<u64>) -> Result<Vec<(u64, String)>, String> {
    use std::ffi::{c_char, c_void, CStr};

    type InitFlat = unsafe extern "C" fn(*mut c_char) -> i32;
    type Shutdown = unsafe extern "C" fn();
    type FriendsAccessor = unsafe extern "C" fn() -> *mut c_void;
    type CountFromSource = unsafe extern "C" fn(*mut c_void, u64) -> i32;
    type FriendFromSource = unsafe extern "C" fn(*mut c_void, u64, i32) -> u64;
    type PersonaName = unsafe extern "C" fn(*mut c_void, u64) -> *const c_char;

    // SAFETY: Valve's flat C API from Dota's own steam_api64.dll, called with the documented
    // signatures. The library stays loaded until this function returns.
    unsafe {
        let lib = libloading::Library::new(library).map_err(|e| format!("Steam API library unavailable: {e}"))?;
        let init: libloading::Symbol<InitFlat> = lib.get(b"SteamAPI_InitFlat").map_err(|e| e.to_string())?;
        let shutdown: libloading::Symbol<Shutdown> = lib.get(b"SteamAPI_Shutdown").map_err(|e| e.to_string())?;
        let mut error = [0 as c_char; 1024];
        let status = init(error.as_mut_ptr());
        if status != 0 {
            return Err(format!("Steam API initialization failed: {status}"));
        }
        let result = (|| {
            let accessor: libloading::Symbol<FriendsAccessor> = lib
                .get(b"SteamAPI_SteamFriends_v018")
                .or_else(|_| lib.get(b"SteamAPI_SteamFriends_v017"))
                .map_err(|_| "Steam Friends interface unavailable.".to_string())?;
            let friends = accessor();
            if friends.is_null() {
                return Err("Steam Friends interface unavailable.".to_string());
            }
            let count_fn: libloading::Symbol<CountFromSource> =
                lib.get(b"SteamAPI_ISteamFriends_GetFriendCountFromSource").map_err(|e| e.to_string())?;
            let friend_fn: libloading::Symbol<FriendFromSource> =
                lib.get(b"SteamAPI_ISteamFriends_GetFriendFromSourceByIndex").map_err(|e| e.to_string())?;
            let name_fn: libloading::Symbol<PersonaName> =
                lib.get(b"SteamAPI_ISteamFriends_GetFriendPersonaName").map_err(|e| e.to_string())?;

            let ids: Vec<u64> = if let Some(server) = server {
                let count = count_fn(friends, server);
                if !(1..=10).contains(&count) { return Ok(Vec::new()) }
                (0..count).map(|index| friend_fn(friends, server, index)).collect()
            } else {
                let user = lib.get::<FriendsAccessor>(b"SteamAPI_SteamUser_v023").map_err(|e| e.to_string())?();
                if user.is_null() { return Ok(Vec::new()) }
                let me = lib.get::<unsafe extern "C" fn(*mut c_void) -> u64>(b"SteamAPI_ISteamUser_GetSteamID").map_err(|e| e.to_string())?(user);
                let presence = lib.get::<unsafe extern "C" fn(*mut c_void, u64, *const c_char) -> *const c_char>(b"SteamAPI_ISteamFriends_GetFriendRichPresence").map_err(|e| e.to_string())?;
                let group = presence(friends, me, c"steam_player_group".as_ptr());
                if group.is_null() { return Ok(Vec::new()) }
                let group = CStr::from_ptr(group).to_bytes().to_vec();
                if group.is_empty() { return Ok(Vec::new()) }
                let count = lib.get::<unsafe extern "C" fn(*mut c_void, i32) -> i32>(b"SteamAPI_ISteamFriends_GetFriendCount").map_err(|e| e.to_string())?(friends, 4);
                let by_index = lib.get::<unsafe extern "C" fn(*mut c_void, i32, i32) -> u64>(b"SteamAPI_ISteamFriends_GetFriendByIndex").map_err(|e| e.to_string())?;
                let mut party = vec![me];
                for index in 0..count {
                    let id = by_index(friends, index, 4);
                    let other = presence(friends, id, c"steam_player_group".as_ptr());
                    if !other.is_null() && CStr::from_ptr(other).to_bytes() == group { party.push(id); }
                }
                party
            };
            Ok(ids.into_iter().map(|id| {
                    let name = name_fn(friends, id);
                    let name = if name.is_null() { String::new() } else { CStr::from_ptr(name).to_string_lossy().into_owned() };
                    (id, name)
                })
                .collect())
        })();
        shutdown();
        result
    }
}

#[cfg(not(windows))]
fn read_server_members(_library: &Path, _server: Option<u64>) -> Result<Vec<(u64, String)>, String> {
    Err("The local Steam roster is only available on Windows.".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_the_tail_of_a_log() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("console.log");
        std::fs::write(&path, "0123456789").unwrap();
        assert_eq!(read_tail(&path, 4).unwrap(), "6789");
        assert_eq!(read_tail(&path, 100).unwrap(), "0123456789");
    }

    #[test]
    fn finds_and_remembers_the_server_for_a_match() {
        let dota = tempfile::tempdir().unwrap();
        let log = dota::console_log(dota.path());
        std::fs::create_dir_all(log.parent().unwrap()).unwrap();
        std::fs::write(&log, "Received Steam datagram ticket for server steamid:901 vport 0. match_id=123 mm=1\n").unwrap();
        let library = dota::steam_api_library(dota.path());
        std::fs::create_dir_all(library.parent().unwrap()).unwrap();
        std::fs::write(&library, "").unwrap();

        let roster = LocalSteamRoster::new(vec![dota.path().to_path_buf()]);
        assert_eq!(roster.server_for("123").map(|(s, _)| s).as_deref(), Some("901"));
        std::fs::remove_file(&log).unwrap();
        assert_eq!(roster.server_for("123").map(|(s, _)| s).as_deref(), Some("901"), "cached");
        assert!(roster.server_for("456").is_none());
    }

    #[test]
    fn helper_rejects_bad_arguments() {
        assert_eq!(helper_main(&[]), 2);
        assert_eq!(helper_main(&["lib.dll".into(), "not-a-number".into()]), 2);
    }
}
