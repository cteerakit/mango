//! Receives Dota's GSI POSTs on `127.0.0.1:<port>` (C# `GsiListenerService`).

use std::net::SocketAddr;
use std::sync::Arc;

use axum::extract::State;
use axum::http::StatusCode;
use axum::routing::any;
use axum::Router;
use mango_core::gsi::GsiPayload;

/// Called with each authorised payload.
pub type OnPayload = Arc<dyn Fn(GsiPayload) + Send + Sync>;

#[derive(Clone)]
struct Listener {
    token: Arc<str>,
    on_payload: OnPayload,
}

/// Parses a body and checks its auth token. Empty expected token accepts all.
pub fn accept(body: &str, expected_token: &str) -> Result<Option<GsiPayload>, ()> {
    if body.trim().is_empty() {
        return Ok(None);
    }
    let payload = GsiPayload::parse(body).map_err(|_| ())?;
    let authorised = expected_token.is_empty()
        || payload.auth.as_ref().and_then(|a| a.token.as_deref()) == Some(expected_token);
    Ok(authorised.then_some(payload))
}

async fn receive(State(listener): State<Listener>, body: String) -> (StatusCode, &'static str) {
    match accept(&body, &listener.token) {
        Ok(Some(payload)) => {
            (listener.on_payload)(payload);
            (StatusCode::OK, "ok")
        }
        Ok(None) => (StatusCode::OK, "ok"),
        Err(()) => (StatusCode::BAD_REQUEST, "bad request"),
    }
}

/// Binds the listener; `Err` holds a message for the Status page.
pub async fn start(port: u16, token: String, on_payload: OnPayload) -> Result<String, String> {
    let address = SocketAddr::from(([127, 0, 0, 1], port));
    let socket = tokio::net::TcpListener::bind(address).await.map_err(|error| {
        if error.kind() == std::io::ErrorKind::AddrInUse {
            format!("Port {port} is in use — close the other Mango (or app using it) and restart Mango.")
        } else {
            format!("Could not listen on 127.0.0.1:{port}: {error}")
        }
    })?;
    let app = Router::new()
        .fallback(any(receive))
        .with_state(Listener { token: token.into(), on_payload });
    tauri::async_runtime::spawn(async move {
        let _ = axum::serve(socket, app).await;
    });
    Ok(format!("Listening on 127.0.0.1:{port}"))
}

#[cfg(test)]
mod tests {
    use std::sync::Mutex;

    use super::*;

    #[test]
    fn checks_the_auth_token() {
        let body = r#"{"auth":{"token":"secret"},"map":{"matchid":"1"}}"#;
        assert!(accept(body, "secret").unwrap().is_some());
        assert!(accept(body, "other").unwrap().is_none());
        assert!(accept(body, "").unwrap().is_some());
        assert!(accept("", "secret").unwrap().is_none());
        assert!(accept("{ nope", "secret").is_err());
    }

    #[tokio::test]
    async fn serves_gsi_posts() {
        let received = Arc::new(Mutex::new(Vec::new()));
        let sink = received.clone();
        let port = {
            let probe = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
            probe.local_addr().unwrap().port()
        };
        let status = start(port, "t".into(), Arc::new(move |p: GsiPayload| sink.lock().unwrap().push(p))).await.unwrap();
        assert!(status.contains(&port.to_string()));

        let client = reqwest::Client::new();
        let response = client
            .post(format!("http://127.0.0.1:{port}/"))
            .body(r#"{"auth":{"token":"t"},"map":{"matchid":"42"}}"#)
            .send()
            .await
            .unwrap();
        assert_eq!(response.status(), 200);
        assert_eq!(received.lock().unwrap()[0].map.as_ref().unwrap().match_id.as_deref(), Some("42"));

        let busy = start(port, "t".into(), Arc::new(|_| {})).await.unwrap_err();
        assert!(busy.contains("in use"), "{busy}");
    }
}
