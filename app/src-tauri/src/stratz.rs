//! STRATZ GraphQL: replay-analysed positions and purchase timelines (C# `StratzService`).
//! Needs a personal API token; finished-match results are cached forever.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;

use async_trait::async_trait;
use mango_core::history::MatchAnalysisOutcome;
use serde_json::{json, Value};

use crate::http::{Request, SharedHttp};
use crate::opendota::ReplaySource;

pub const GRAPHQL_URL: &str = "https://api.stratz.com/graphql";
const POSITIONS_QUERY: &str = "query($id: Long!){ match(id: $id){ players { steamAccountId position } } }";
const OUTCOME_QUERY: &str = "query($id: Long!){ match(id: $id){ analysisOutcome } }";
const PURCHASES_QUERY: &str =
    "query($id: Long!){ match(id: $id){ players { steamAccountId stats { itemPurchases { itemId time } } } } }";

pub struct Stratz {
    http: SharedHttp,
    cache_dir: PathBuf,
}

impl Stratz {
    pub fn new(http: SharedHttp, cache_dir: PathBuf) -> Self {
        let _ = std::fs::create_dir_all(&cache_dir);
        Self { http, cache_dir }
    }

    /// Position 1–5 by account id, or `None` when STRATZ has none for the match.
    pub async fn match_positions(&self, match_id: i64, token: &str) -> Option<HashMap<i64, i32>> {
        if match_id <= 0 || token.trim().is_empty() {
            return None;
        }
        let cache = self.cache_dir.join(format!("stratz_match_{match_id}_positions.json"));
        if let Some(cached) = read_cache::<HashMap<i64, i32>>(&cache).filter(|c| !c.is_empty()) {
            return Some(cached);
        }
        let root = self.query(POSITIONS_QUERY, match_id, token).await?;
        let positions = read_positions(&root);
        if positions.is_empty() {
            return None;
        }
        let _ = std::fs::write(&cache, serde_json::to_string(&positions).ok()?);
        Some(positions)
    }

    /// `(item_id, seconds)` for one player in time order, or `None` when STRATZ has none.
    pub async fn player_purchases(&self, match_id: i64, account_id: i64, token: &str) -> Option<Vec<(i32, i32)>> {
        if match_id <= 0 || account_id <= 0 || token.trim().is_empty() {
            return None;
        }
        let cache = self.cache_dir.join(format!("stratz_match_{match_id}_purchases_{account_id}.json"));
        if let Some(cached) = read_cache::<Vec<(i32, i32)>>(&cache).filter(|c| !c.is_empty()) {
            return Some(cached);
        }
        let root = self.query(PURCHASES_QUERY, match_id, token).await?;
        let purchases = read_purchases(&root, account_id);
        if purchases.is_empty() {
            return None;
        }
        let _ = std::fs::write(&cache, serde_json::to_string(&purchases).ok()?);
        Some(purchases)
    }

    async fn match_analysis_outcome(&self, match_id: i64, token: &str) -> Option<MatchAnalysisOutcome> {
        if match_id <= 0 || token.trim().is_empty() {
            return None;
        }
        let cache = self.cache_dir.join(format!("stratz_match_{match_id}_outcome.json"));
        if let Some(outcome) = read_cache(&cache) {
            return Some(outcome);
        }
        let root = self.query(OUTCOME_QUERY, match_id, token).await?;
        let outcome = match root.pointer("/data/match/analysisOutcome")?.as_str()? {
            "STOMPED" => MatchAnalysisOutcome::Stomp,
            "COMEBACK" => MatchAnalysisOutcome::Comeback,
            "CLOSE_GAME" => MatchAnalysisOutcome::CloseGame,
            _ => return None,
        };
        if let Ok(json) = serde_json::to_string(&outcome) {
            let _ = std::fs::write(cache, json);
        }
        Some(outcome)
    }

    async fn query(&self, query: &str, match_id: i64, token: &str) -> Option<Value> {
        let body = json!({ "query": query, "variables": { "id": match_id } }).to_string();
        let request = Request::post_json(GRAPHQL_URL, body)
            .header("Authorization", format!("Bearer {}", token.trim()))
            // STRATZ rejects API calls without this user agent.
            .header("User-Agent", "STRATZ_API")
            .timeout(std::time::Duration::from_secs(15));
        let response = self.http.send(request).await.ok()?;
        if !response.is_success() {
            return None;
        }
        serde_json::from_slice(&response.body).ok()
    }
}

fn read_cache<T: serde::de::DeserializeOwned>(path: &std::path::Path) -> Option<T> {
    serde_json::from_str(&std::fs::read_to_string(path).ok()?).ok()
}

fn match_players(root: &Value) -> impl Iterator<Item = &Value> {
    root.pointer("/data/match/players").and_then(Value::as_array).into_iter().flatten()
}

fn read_positions(root: &Value) -> HashMap<i64, i32> {
    match_players(root)
        .filter_map(|p| {
            let account = p.get("steamAccountId")?.as_i64().filter(|a| *a > 0)?;
            let position = p.get("position")?.as_str()?.strip_prefix("POSITION_")?.parse::<i32>().ok()?;
            (1..=5).contains(&position).then_some((account, position))
        })
        .collect()
}

fn read_purchases(root: &Value, account_id: i64) -> Vec<(i32, i32)> {
    let Some(player) = match_players(root).find(|p| p.get("steamAccountId").and_then(Value::as_i64) == Some(account_id))
    else {
        return Vec::new();
    };
    let mut purchases: Vec<(i32, i32)> = player
        .pointer("/stats/itemPurchases")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter_map(|p| {
            let item = i32::try_from(p.get("itemId")?.as_i64()?).ok()?;
            let time = i32::try_from(p.get("time")?.as_i64()?).ok()?;
            Some((item, time))
        })
        .collect();
    purchases.sort_by_key(|p| p.1);
    purchases
}

/// STRATZ as the OpenDota build's replay source, when a token is saved.
pub struct StratzReplay {
    pub stratz: Arc<Stratz>,
    pub token: String,
}

#[async_trait]
impl ReplaySource for StratzReplay {
    async fn analysis_outcome(&self, match_id: i64) -> Option<MatchAnalysisOutcome> {
        self.stratz.match_analysis_outcome(match_id, &self.token).await
    }

    async fn positions(&self, match_id: i64) -> Result<Option<HashMap<i64, i32>>, String> {
        Ok(self.stratz.match_positions(match_id, &self.token).await)
    }

    async fn purchases(&self, match_id: i64, account_id: i64) -> Result<Option<Vec<(i32, i32)>>, String> {
        Ok(self.stratz.player_purchases(match_id, account_id, &self.token).await)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::http::fake::FakeHttp;

    fn service(http: Arc<FakeHttp>) -> (Stratz, tempfile::TempDir) {
        let dir = tempfile::tempdir().unwrap();
        (Stratz::new(http, dir.path().to_path_buf()), dir)
    }

    #[tokio::test]
    async fn reads_positions_with_bearer_token_and_caches_them() {
        let http = FakeHttp::new(|r| {
            assert_eq!(r.method, "POST");
            assert!(r.headers.contains(&("Authorization".into(), "Bearer token".into())));
            assert!(r.headers.contains(&("User-Agent".into(), "STRATZ_API".into())));
            assert!(r.body.as_deref().unwrap().contains("\"id\":9050691699"));
            Some((200, r#"{"data":{"match":{"players":[
                {"steamAccountId":118993939,"position":"POSITION_2"},
                {"steamAccountId":1088652637,"position":"POSITION_5"},
                {"steamAccountId":0,"position":"POSITION_1"},
                {"steamAccountId":6304677,"position":"UNKNOWN"}]}}}"#.into()))
        });
        let (stratz, _dir) = service(http.clone());

        let positions = stratz.match_positions(9_050_691_699, " token ").await.unwrap();
        let cached = stratz.match_positions(9_050_691_699, "token").await.unwrap();

        assert_eq!(positions[&118_993_939], 2);
        assert_eq!(positions[&1_088_652_637], 5);
        assert_eq!(positions.len(), 2);
        assert_eq!(positions, cached);
        assert_eq!(http.count(), 1);
    }

    #[tokio::test]
    async fn reads_only_the_requested_players_purchases_in_time_order_and_caches_them() {
        let http = FakeHttp::new(|_| {
            Some((200, r#"{"data":{"match":{"players":[
                {"steamAccountId":1,"stats":{"itemPurchases":[{"itemId":9,"time":50}]}},
                {"steamAccountId":2,"stats":{"itemPurchases":[{"itemId":5,"time":900},{"itemId":7,"time":-30}]}}]}}}"#.into()))
        });
        let (stratz, _dir) = service(http.clone());

        let purchases = stratz.player_purchases(90, 2, " token ").await.unwrap();
        let cached = stratz.player_purchases(90, 2, "token").await.unwrap();

        assert_eq!(purchases, [(7, -30), (5, 900)]);
        assert_eq!(purchases, cached);
        assert_eq!(http.count(), 1);
        assert!(stratz.player_purchases(90, 3, "token").await.is_none());
    }

    #[tokio::test]
    async fn missing_match_or_rejected_token_returns_none_without_caching() {
        for answer in [
            Some((200, r#"{"data":{"match":null}}"#.to_string())),
            Some((200, r#"{"message":"A bearer token is required for a request."}"#.to_string())),
            Some((401, String::new())),
        ] {
            let http = FakeHttp::new(move |_| answer.clone());
            let (stratz, _dir) = service(http.clone());
            assert!(stratz.match_positions(1, "token").await.is_none());
            assert!(stratz.match_positions(1, "token").await.is_none());
            assert_eq!(http.count(), 2);
        }
    }

    #[tokio::test]
    async fn match_outcome_maps_stratz_classifications_and_caches_classified_matches() {
        for (value, expected) in [
            ("STOMPED", Some(MatchAnalysisOutcome::Stomp)),
            ("COMEBACK", Some(MatchAnalysisOutcome::Comeback)),
            ("CLOSE_GAME", Some(MatchAnalysisOutcome::CloseGame)),
            ("NONE", None),
        ] {
            let http = FakeHttp::new(move |_| {
                Some((200, json!({"data": {"match": {"analysisOutcome": value}}}).to_string()))
            });
            let (stratz, _dir) = service(http.clone());
            assert_eq!(stratz.match_analysis_outcome(90, "token").await, expected);
            assert_eq!(stratz.match_analysis_outcome(90, "token").await, expected);
            assert_eq!(http.count(), if expected.is_some() { 1 } else { 2 });
        }
    }

    #[tokio::test]
    async fn no_token_skips_the_request() {
        let http = FakeHttp::new(|_| panic!("no request expected"));
        let (stratz, _dir) = service(http);
        assert!(stratz.match_positions(1, " ").await.is_none());
    }
}
