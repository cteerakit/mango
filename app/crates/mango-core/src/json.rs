//! Lenient readers over `serde_json::Value`.
//!
//! GSI and the public APIs send numbers as strings, ids larger than 2^53, and fields that
//! change shape (local player vs spectator maps). Reading through `Value` lets one bad field
//! fall back to its default instead of rejecting the whole payload.

use serde_json::{Map, Value};

pub type Object = Map<String, Value>;

/// Exact-name property lookup (System.Text.Json default).
pub fn get<'a>(obj: &'a Object, key: &str) -> Option<&'a Value> {
    obj.get(key).filter(|value| !value.is_null())
}

/// Case-insensitive property lookup (`PropertyNameCaseInsensitive = true`).
pub fn get_ci<'a>(obj: &'a Object, key: &str) -> Option<&'a Value> {
    get(obj, key).or_else(|| {
        obj.iter()
            .find(|(name, value)| name.eq_ignore_ascii_case(key) && !value.is_null())
            .map(|(_, value)| value)
    })
}

pub fn as_i64(value: &Value) -> Option<i64> {
    match value {
        Value::Number(n) => n
            .as_i64()
            .or_else(|| n.as_f64().filter(|f| f.fract() == 0.0 && f.is_finite()).map(|f| f as i64)),
        Value::String(s) => s.trim().parse().ok(),
        _ => None,
    }
}

pub fn as_u64(value: &Value) -> Option<u64> {
    match value {
        Value::Number(n) => n.as_u64(),
        Value::String(s) => s.trim().parse().ok(),
        _ => None,
    }
}

pub fn as_i32(value: &Value) -> Option<i32> {
    as_i64(value).and_then(|n| i32::try_from(n).ok())
}

pub fn as_f64(value: &Value) -> Option<f64> {
    match value {
        Value::Number(n) => n.as_f64(),
        Value::String(s) => s.trim().parse().ok(),
        _ => None,
    }
}

pub fn as_bool(value: &Value) -> Option<bool> {
    match value {
        Value::Bool(b) => Some(*b),
        Value::Number(n) => n.as_i64().map(|n| n != 0),
        Value::String(s) => match s.trim().to_ascii_lowercase().as_str() {
            "true" | "1" => Some(true),
            "false" | "0" => Some(false),
            _ => None,
        },
        _ => None,
    }
}

/// A string property; numbers are not coerced (use [`as_flexible_string`] for ids).
pub fn as_string(value: &Value) -> Option<String> {
    value.as_str().map(str::to_string)
}

/// Ids that arrive as either a JSON string or a (possibly > 2^53) number.
pub fn as_flexible_string(value: &Value) -> Option<String> {
    match value {
        Value::String(s) => Some(s.clone()),
        Value::Number(n) => Some(n.to_string()),
        _ => None,
    }
}

pub fn i32_of(obj: &Object, key: &str) -> i32 {
    get(obj, key).and_then(as_i32).unwrap_or_default()
}

pub fn f64_of(obj: &Object, key: &str) -> f64 {
    get(obj, key).and_then(as_f64).unwrap_or_default()
}

pub fn bool_of(obj: &Object, key: &str) -> bool {
    get(obj, key).and_then(as_bool).unwrap_or_default()
}

pub fn string_of(obj: &Object, key: &str) -> Option<String> {
    get(obj, key).and_then(as_string)
}

pub fn flexible_string_of(obj: &Object, key: &str) -> Option<String> {
    get(obj, key).and_then(as_flexible_string)
}
