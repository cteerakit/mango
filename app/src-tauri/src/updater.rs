//! Update check and verified installer download from GitHub releases (C# `AppUpdateService`).
//!
//! Releases keep the WinUI app's asset names and installer, so either app can update to the other.

use std::path::{Path, PathBuf};

use serde::Serialize;
use serde_json::Value;
use sha2::{Digest, Sha256};

use crate::http::{Request, SharedHttp};

pub const INSTALLER_NAME: &str = "MangoSetup-x64.exe";
pub const CHECKSUM_NAME: &str = "SHA256SUMS.txt";
const LATEST_RELEASE: &str = "https://api.github.com/repos/cteerakit/mango/releases/latest";

/// Four-part version: `MANGO_VERSION` from the release build, else the crate version.
pub fn installed_version() -> String {
    let version = option_env!("MANGO_VERSION").unwrap_or(env!("CARGO_PKG_VERSION"));
    let mut parts: Vec<&str> = version.split('.').collect();
    while parts.len() < 4 {
        parts.push("0");
    }
    parts.join(".")
}

#[derive(Debug, Clone, Default, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateCheck {
    pub status: String,
    pub download_url: Option<String>,
    pub checksum_url: Option<String>,
}

impl UpdateCheck {
    fn status(status: &str) -> Self {
        Self { status: status.into(), ..Default::default() }
    }
}

fn parse_version(text: &str) -> Option<[u64; 4]> {
    let parts: Vec<u64> = text.split('.').map(|p| p.parse().ok()).collect::<Option<_>>()?;
    if parts.is_empty() || parts.len() > 4 {
        return None;
    }
    let mut version = [0; 4];
    version[..parts.len()].copy_from_slice(&parts);
    Some(version)
}

fn asset_url(tag: &str, name: &str) -> String {
    format!("https://github.com/cteerakit/mango/releases/download/{tag}/{name}")
}

pub async fn check(http: &SharedHttp, installed: &str) -> UpdateCheck {
    let failed = UpdateCheck::status("Could not check for updates. Check your internet connection and try again.");
    let request = Request::get(LATEST_RELEASE)
        .header("User-Agent", format!("Mango/{installed}"))
        .header("Accept", "application/vnd.github+json")
        .timeout(std::time::Duration::from_secs(15));
    let Ok(response) = http.send(request).await else { return failed };
    if !response.is_success() {
        return failed;
    }
    let Ok(release) = serde_json::from_slice::<Value>(&response.body) else { return failed };
    let flag = |k: &str| release.get(k).and_then(Value::as_bool) == Some(true);
    let Some(tag) = release.get("tag_name").and_then(Value::as_str) else { return failed };
    let latest = tag.strip_prefix('v').and_then(parse_version);
    let (Some(latest), Some(current)) = (latest, parse_version(installed)) else { return failed };
    if flag("draft") || flag("prerelease") {
        return failed;
    }
    if latest <= current {
        return UpdateCheck::status("Mango is up to date.");
    }

    let download = asset_url(tag, INSTALLER_NAME);
    let checksum = asset_url(tag, CHECKSUM_NAME);
    let has = |name: &str, url: &str| {
        release.get("assets").and_then(Value::as_array).is_some_and(|assets| {
            assets.iter().any(|a| {
                a.get("name").and_then(Value::as_str) == Some(name)
                    && a.get("browser_download_url").and_then(Value::as_str) == Some(url)
            })
        })
    };
    if !has(INSTALLER_NAME, &download) || !has(CHECKSUM_NAME, &checksum) {
        return UpdateCheck::status(
            "A newer release exists, but its x64 setup installer is not available yet. Try again later.",
        );
    }
    UpdateCheck {
        status: format!(
            "Mango {} is available. Install it now, or download the installer to run yourself.",
            &tag[1..]
        ),
        download_url: Some(download),
        checksum_url: Some(checksum),
    }
}

/// SHA-256 for `file_name` in `sha256sum`-style text (`hash  name` or `hash *name`).
pub fn parse_checksum(text: &str, file_name: &str) -> Option<String> {
    text.lines().map(str::trim).find_map(|line| {
        let split = line.find([' ', '\t'])?;
        if split != 64 {
            return None;
        }
        let (hash, rest) = line.split_at(split);
        let name = rest.trim_start_matches([' ', '\t', '*']);
        (name == file_name && hash.chars().all(|c| c.is_ascii_hexdigit())).then(|| hash.to_ascii_lowercase())
    })
}

/// Downloads the installer into a fresh temp folder and checks it against SHA256SUMS.txt.
/// The folder is removed when anything fails.
pub async fn download(
    http: &SharedHttp,
    update: &UpdateCheck,
    temp_dir: &Path,
    progress: impl Fn(f64),
) -> Result<PathBuf, String> {
    let (Some(download_url), Some(checksum_url)) = (&update.download_url, &update.checksum_url) else {
        return Err("No update is available to install.".into());
    };
    let mut id = [0u8; 8];
    let _ = getrandom::fill(&mut id);
    let folder = temp_dir.join(format!("Mango-update-{}", hex::encode(id)));
    let result = async {
        let failed = || "Could not download the update. Check your internet connection and try again.".to_string();
        let sums = http.send(Request::get(checksum_url.clone())).await.map_err(|_| failed())?;
        if !sums.is_success() {
            return Err(failed());
        }
        let expected = parse_checksum(&sums.text(), INSTALLER_NAME).ok_or_else(failed)?;

        progress(0.0);
        let installer = http
            .send(Request::get(download_url.clone()).timeout(std::time::Duration::from_secs(600)))
            .await
            .map_err(|_| failed())?;
        if !installer.is_success() {
            return Err(failed());
        }
        progress(1.0);

        let actual = hex::encode(Sha256::digest(&installer.body));
        if actual != expected {
            return Err(
                "The downloaded installer did not match its published checksum, so it was not run. Try again later."
                    .into(),
            );
        }
        std::fs::create_dir_all(&folder).map_err(|_| failed())?;
        let path = folder.join(INSTALLER_NAME);
        std::fs::write(&path, &installer.body).map_err(|_| failed())?;
        Ok(path)
    }
    .await;
    if result.is_err() {
        let _ = std::fs::remove_dir_all(&folder);
    }
    result
}

pub fn installer_arguments(log_path: &Path) -> Vec<String> {
    ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1"]
        .iter()
        .map(|s| s.to_string())
        .chain([format!("/LOG={}", log_path.display())])
        .collect()
}

/// Starts the verified installer; the caller exits so files can be replaced. The installer relaunches Mango.
pub fn start_installer(installer: &Path) -> bool {
    let Some(folder) = installer.parent() else { return false };
    std::process::Command::new(installer)
        .args(installer_arguments(&folder.join("install.log")))
        .current_dir(folder)
        .spawn()
        .is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::http::fake::FakeHttp;

    fn release(version: &str) -> String {
        let tag = format!("v{version}");
        serde_json::json!({
            "tag_name": tag,
            "assets": [
                {"name": INSTALLER_NAME, "browser_download_url": asset_url(&tag, INSTALLER_NAME)},
                {"name": CHECKSUM_NAME, "browser_download_url": asset_url(&tag, CHECKSUM_NAME)}
            ]
        })
        .to_string()
    }

    fn api(body: String) -> SharedHttp {
        FakeHttp::ok_or_404(move |_| Some(body.clone()))
    }

    #[tokio::test]
    async fn newer_release_offers_its_x64_installer() {
        let result = check(&api(release("1.0.10.0")), "1.0.3.0").await;
        assert_eq!(result.status, "Mango 1.0.10.0 is available. Install it now, or download the installer to run yourself.");
        assert_eq!(result.download_url.as_deref(), Some("https://github.com/cteerakit/mango/releases/download/v1.0.10.0/MangoSetup-x64.exe"));
        assert_eq!(result.checksum_url.as_deref(), Some("https://github.com/cteerakit/mango/releases/download/v1.0.10.0/SHA256SUMS.txt"));
    }

    #[tokio::test]
    async fn equal_or_older_release_does_not_offer_an_update() {
        for version in ["1.0.3.0", "1.0.2.0"] {
            let result = check(&api(release(version)), "1.0.3.0").await;
            assert_eq!(result.status, "Mango is up to date.");
            assert!(result.download_url.is_none());
        }
    }

    #[tokio::test]
    async fn missing_or_unexpected_installer_is_not_offered() {
        for json in [
            r#"{"tag_name":"v1.0.4.0","assets":[]}"#,
            r#"{"tag_name":"v1.0.4.0","assets":[null]}"#,
            r#"{"tag_name":"v1.0.4.0","assets":[{"name":"MangoSetup-x64.exe","browser_download_url":"https://example.com/malware.exe"}]}"#,
            r#"{"tag_name":"v1.0.4.0","assets":[{"name":"MangoSetup-x64.exe","browser_download_url":"https://github.com/cteerakit/mango/releases/download/v1.0.4.0/MangoSetup-x64.exe"}]}"#,
        ] {
            let result = check(&api(json.into()), "1.0.3.0").await;
            assert!(result.status.contains("installer is not available yet"), "{json}");
            assert!(result.download_url.is_none());
        }
    }

    #[tokio::test]
    async fn invalid_release_or_failures_can_be_retried() {
        for json in ["not json", "null", "{}", r#"{"tag_name":"v1.0.4.0","prerelease":true}"#, r#"{"tag_name":"v1.0.4.0","draft":true}"#] {
            assert!(check(&api(json.into()), "1.0.3.0").await.status.contains("Could not check for updates"));
        }
        for status in [403, 404, 500] {
            let http: SharedHttp = FakeHttp::new(move |_| Some((status, "{}".into())));
            assert!(check(&http, "1.0.3.0").await.status.contains("Could not check for updates"));
        }
        let offline: SharedHttp = FakeHttp::new(|_| None);
        assert!(check(&offline, "1.0.3.0").await.status.contains("Could not check for updates"));
    }

    #[tokio::test]
    async fn check_uses_github_api_and_identifies_mango() {
        let http = FakeHttp::ok_or_404(|r| {
            assert_eq!(r.url, LATEST_RELEASE);
            assert!(r.headers.contains(&("User-Agent".into(), "Mango/1.0.3.0".into())));
            Some(release("1.0.3.0"))
        });
        let shared: SharedHttp = http.clone();
        check(&shared, "1.0.3.0").await;
        assert_eq!(http.count(), 1);
    }

    fn download_api(installer: &'static [u8], hash: String, fail_download: bool) -> SharedHttp {
        let release = release("1.0.10.0");
        FakeHttp::new(move |r| {
            if r.url == LATEST_RELEASE {
                Some((200, release.clone()))
            } else if r.url.ends_with(CHECKSUM_NAME) {
                Some((200, format!("{hash}  {INSTALLER_NAME}\n")))
            } else if fail_download {
                None
            } else {
                Some((200, String::from_utf8(installer.to_vec()).unwrap()))
            }
        })
    }

    #[tokio::test]
    async fn verified_installer_is_downloaded() {
        let bytes = b"installer bytes";
        let http = download_api(bytes, hex::encode(Sha256::digest(bytes)), false);
        let temp = tempfile::tempdir().unwrap();
        let update = check(&http, "1.0.3.0").await;
        let path = download(&http, &update, temp.path(), |_| {}).await.unwrap();
        assert_eq!(std::fs::read(path).unwrap(), bytes);
    }

    #[tokio::test]
    async fn installer_with_wrong_checksum_or_failed_download_leaves_nothing_behind() {
        for (fail, expected) in [(false, "did not match"), (true, "Could not download the update")] {
            let http = download_api(b"installer bytes", "0".repeat(64), fail);
            let temp = tempfile::tempdir().unwrap();
            let update = check(&http, "1.0.3.0").await;
            let error = download(&http, &update, temp.path(), |_| {}).await.unwrap_err();
            assert!(error.contains(expected), "{error}");
            assert_eq!(std::fs::read_dir(temp.path()).unwrap().count(), 0);
        }
        let none = download(&download_api(b"", String::new(), false), &UpdateCheck::default(), Path::new("."), |_| {}).await;
        assert!(none.is_err());
    }

    #[test]
    fn checksum_file_is_parsed() {
        let hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        for (text, expected) in [
            ("abc  MangoSetup-x64.exe".to_string(), None),
            (String::new(), None),
            (format!("{}  other.exe", hash.to_uppercase()), None),
            (format!("zzzz{}  MangoSetup-x64.exe", &hash[4..]), None),
            (format!("{hash}  MangoSetup-x64.exe"), Some(hash)),
            (format!("other\r\n{} *MangoSetup-x64.exe\r\n", hash.to_uppercase()), Some(hash)),
        ] {
            assert_eq!(parse_checksum(&text, INSTALLER_NAME).as_deref(), expected, "{text}");
        }
    }

    #[test]
    fn installer_runs_silently_and_relaunches_mango() {
        let args = installer_arguments(Path::new(r"C:\temp\install.log"));
        assert!(args.contains(&"/VERYSILENT".to_string()));
        assert!(args.contains(&"/RELAUNCH=1".to_string()));
        assert!(args.contains(&r"/LOG=C:\temp\install.log".to_string()));
    }

    #[test]
    fn versions_compare_with_four_parts() {
        assert!(parse_version("1.1.0").unwrap() > parse_version("1.0.6.0").unwrap());
        assert_eq!(parse_version("1.0.6").unwrap(), parse_version("1.0.6.0").unwrap());
        assert!(parse_version("x").is_none());
        assert_eq!(installed_version().split('.').count(), 4);
    }
}
