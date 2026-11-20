//! Minimal HTTP abstraction so services can be tested with canned responses.

use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;

#[derive(Debug, Clone, PartialEq)]
pub struct Request {
    pub method: &'static str,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<String>,
    pub timeout: Duration,
}

impl Request {
    pub fn get(url: impl Into<String>) -> Self {
        Self { method: "GET", url: url.into(), headers: Vec::new(), body: None, timeout: Duration::from_secs(20) }
    }

    pub fn post_json(url: impl Into<String>, body: String) -> Self {
        Self {
            method: "POST",
            url: url.into(),
            headers: vec![("Content-Type".into(), "application/json".into())],
            body: Some(body),
            timeout: Duration::from_secs(20),
        }
    }

    pub fn header(mut self, name: &str, value: impl Into<String>) -> Self {
        self.headers.push((name.into(), value.into()));
        self
    }

    pub fn timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }
}

#[derive(Debug, Clone)]
pub struct Response {
    pub status: u16,
    pub body: Vec<u8>,
}

impl Response {
    pub fn is_success(&self) -> bool {
        (200..300).contains(&self.status)
    }

    pub fn text(&self) -> String {
        String::from_utf8_lossy(&self.body).into_owned()
    }
}

/// Network or timeout failure (not an HTTP error status).
#[derive(Debug, Clone)]
pub struct HttpError(pub String);

impl std::fmt::Display for HttpError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for HttpError {}

#[async_trait]
pub trait Http: Send + Sync {
    async fn send(&self, request: Request) -> Result<Response, HttpError>;
}

pub type SharedHttp = Arc<dyn Http>;

/// The real client.
pub struct ReqwestHttp {
    client: reqwest::Client,
}

impl ReqwestHttp {
    pub fn new() -> Self {
        let client = reqwest::Client::builder()
            .user_agent(concat!("Mango/", env!("CARGO_PKG_VERSION")))
            .connect_timeout(Duration::from_secs(10))
            .build()
            .expect("HTTP client");
        Self { client }
    }
}

#[async_trait]
impl Http for ReqwestHttp {
    async fn send(&self, request: Request) -> Result<Response, HttpError> {
        let method = reqwest::Method::from_bytes(request.method.as_bytes()).map_err(|e| HttpError(e.to_string()))?;
        let mut builder = self.client.request(method, &request.url).timeout(request.timeout);
        for (name, value) in &request.headers {
            builder = builder.header(name, value);
        }
        if let Some(body) = request.body {
            builder = builder.body(body);
        }
        let response = builder.send().await.map_err(|e| HttpError(e.to_string()))?;
        let status = response.status().as_u16();
        let body = response.bytes().await.map_err(|e| HttpError(e.to_string()))?.to_vec();
        Ok(Response { status, body })
    }
}

#[cfg(test)]
pub mod fake {
    //! Test double: answers by path with a closure.

    use std::sync::Mutex;

    use super::*;

    type Responder = dyn Fn(&Request) -> Option<(u16, String)> + Send + Sync;

    pub struct FakeHttp {
        respond: Box<Responder>,
        pub requests: Mutex<Vec<Request>>,
    }

    impl FakeHttp {
        /// `respond` returns `None` for a network failure.
        pub fn new(respond: impl Fn(&Request) -> Option<(u16, String)> + Send + Sync + 'static) -> Arc<Self> {
            Arc::new(Self { respond: Box::new(respond), requests: Mutex::new(Vec::new()) })
        }

        /// 200 with the body, or 404 when `respond` returns `None`.
        pub fn ok_or_404(respond: impl Fn(&Request) -> Option<String> + Send + Sync + 'static) -> Arc<Self> {
            Self::new(move |r| Some(respond(r).map_or((404, String::new()), |b| (200, b))))
        }

        pub fn count(&self) -> usize {
            self.requests.lock().unwrap().len()
        }
    }

    #[async_trait]
    impl Http for FakeHttp {
        async fn send(&self, request: Request) -> Result<Response, HttpError> {
            let answer = (self.respond)(&request);
            self.requests.lock().unwrap().push(request);
            match answer {
                Some((status, body)) => Ok(Response { status, body: body.into_bytes() }),
                None => Err(HttpError("offline".into())),
            }
        }
    }

    /// Path without the query, e.g. `/api/players/1`.
    pub fn path(request: &Request) -> String {
        let after_scheme = request.url.split("://").nth(1).unwrap_or(&request.url);
        let path = after_scheme.find('/').map_or("/", |i| &after_scheme[i..]);
        path.split('?').next().unwrap_or(path).to_string()
    }

    pub fn query(request: &Request) -> String {
        request.url.split_once('?').map(|(_, q)| q.to_string()).unwrap_or_default()
    }
}
