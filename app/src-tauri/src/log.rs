//! Tiny diagnostics log at `%LOCALAPPDATA%\Mango\mango.log`, trimmed when it grows past 1 MB.

use std::io::Write;
use std::path::PathBuf;
use std::sync::{Mutex, OnceLock};

const MAX_BYTES: u64 = 1024 * 1024;

static PATH: OnceLock<PathBuf> = OnceLock::new();
static LOCK: Mutex<()> = Mutex::new(());

pub fn init(directory: &std::path::Path) {
    let _ = std::fs::create_dir_all(directory);
    let _ = PATH.set(directory.join("mango.log"));
}

pub fn write(message: impl AsRef<str>) {
    let message = message.as_ref();
    eprintln!("{message}");
    let Some(path) = PATH.get() else { return };
    let _guard = LOCK.lock();
    if std::fs::metadata(path).is_ok_and(|m| m.len() > MAX_BYTES) {
        let _ = std::fs::rename(path, path.with_extension("log.old"));
    }
    if let Ok(mut file) = std::fs::OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(file, "{} {message}", chrono::Local::now().format("%Y-%m-%d %H:%M:%S"));
    }
}

#[macro_export]
macro_rules! log {
    ($($arg:tt)*) => { $crate::log::write(format!($($arg)*)) };
}
