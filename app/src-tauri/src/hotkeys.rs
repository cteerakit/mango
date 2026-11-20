//! Global hotkeys from settings (C# `HotkeyService`, `HotkeyFormatting`).
//!
//! Bindings are stored as Windows virtual-key codes, as the WinUI app saved them.

use tauri_plugin_global_shortcut::{Code, Modifiers, Shortcut};

use crate::settings::HotkeyBinding;

/// The plugin key for a Windows virtual-key code.
pub fn code_for(virtual_key: u32) -> Option<Code> {
    use Code::*;
    Some(match virtual_key {
        0x08 => Backspace,
        0x09 => Tab,
        0x0D => Enter,
        0x1B => Escape,
        0x20 => Space,
        0x21 => PageUp,
        0x22 => PageDown,
        0x23 => End,
        0x24 => Home,
        0x25 => ArrowLeft,
        0x26 => ArrowUp,
        0x27 => ArrowRight,
        0x28 => ArrowDown,
        0x2D => Insert,
        0x2E => Delete,
        0x30 => Digit0,
        0x31 => Digit1,
        0x32 => Digit2,
        0x33 => Digit3,
        0x34 => Digit4,
        0x35 => Digit5,
        0x36 => Digit6,
        0x37 => Digit7,
        0x38 => Digit8,
        0x39 => Digit9,
        0x41 => KeyA,
        0x42 => KeyB,
        0x43 => KeyC,
        0x44 => KeyD,
        0x45 => KeyE,
        0x46 => KeyF,
        0x47 => KeyG,
        0x48 => KeyH,
        0x49 => KeyI,
        0x4A => KeyJ,
        0x4B => KeyK,
        0x4C => KeyL,
        0x4D => KeyM,
        0x4E => KeyN,
        0x4F => KeyO,
        0x50 => KeyP,
        0x51 => KeyQ,
        0x52 => KeyR,
        0x53 => KeyS,
        0x54 => KeyT,
        0x55 => KeyU,
        0x56 => KeyV,
        0x57 => KeyW,
        0x58 => KeyX,
        0x59 => KeyY,
        0x5A => KeyZ,
        0x60 => Numpad0,
        0x61 => Numpad1,
        0x62 => Numpad2,
        0x63 => Numpad3,
        0x64 => Numpad4,
        0x65 => Numpad5,
        0x66 => Numpad6,
        0x67 => Numpad7,
        0x68 => Numpad8,
        0x69 => Numpad9,
        0x70 => F1,
        0x71 => F2,
        0x72 => F3,
        0x73 => F4,
        0x74 => F5,
        0x75 => F6,
        0x76 => F7,
        0x77 => F8,
        0x78 => F9,
        0x79 => F10,
        0x7A => F11,
        0x7B => F12,
        0xBA => Semicolon,
        0xBB => Equal,
        0xBC => Comma,
        0xBD => Minus,
        0xBE => Period,
        0xBF => Slash,
        0xC0 => Backquote,
        0xDB => BracketLeft,
        0xDC => Backslash,
        0xDD => BracketRight,
        0xDE => Quote,
        _ => return None,
    })
}

/// A registrable shortcut; `None` without a modifier (global hotkeys must have one) or for unknown keys.
pub fn shortcut_for(binding: &HotkeyBinding) -> Option<Shortcut> {
    if !binding.has_modifier() {
        return None;
    }
    let mut modifiers = Modifiers::empty();
    modifiers.set(Modifiers::CONTROL, binding.ctrl);
    modifiers.set(Modifiers::ALT, binding.alt);
    modifiers.set(Modifiers::SHIFT, binding.shift);
    modifiers.set(Modifiers::SUPER, binding.win);
    Some(Shortcut::new(Some(modifiers), code_for(binding.virtual_key)?))
}

/// The Windows virtual-key code for a DOM `KeyboardEvent.code`, for recording shortcuts.
pub fn virtual_key_for_dom_code(code: &str) -> Option<u32> {
    (0x08..=0xDE).find(|vk| code_for(*vk).is_some_and(|c| c.to_string() == code))
}

pub fn display(binding: &HotkeyBinding) -> String {
    if binding.virtual_key == 0 {
        return "Not set".into();
    }
    let mut parts: Vec<String> = Vec::new();
    for (on, name) in [(binding.ctrl, "Ctrl"), (binding.alt, "Alt"), (binding.shift, "Shift"), (binding.win, "Win")] {
        if on {
            parts.push(name.into());
        }
    }
    parts.push(key_name(binding.virtual_key));
    parts.join("+")
}

pub fn key_name(vk: u32) -> String {
    match vk {
        0x08 => "Backspace".into(),
        0x09 => "Tab".into(),
        0x0D => "Enter".into(),
        0x1B => "Esc".into(),
        0x20 => "Space".into(),
        0x21 => "PageUp".into(),
        0x22 => "PageDown".into(),
        0x23 => "End".into(),
        0x24 => "Home".into(),
        0x25 => "Left".into(),
        0x26 => "Up".into(),
        0x27 => "Right".into(),
        0x28 => "Down".into(),
        0x2D => "Insert".into(),
        0x2E => "Delete".into(),
        0x30..=0x39 | 0x41..=0x5A => char::from_u32(vk).map(String::from).unwrap_or_default(),
        0x60..=0x69 => format!("Num{}", vk - 0x60),
        0x70..=0x7B => format!("F{}", vk - 0x6F),
        0xBA => ";".into(),
        0xBB => "=".into(),
        0xBC => ",".into(),
        0xBD => "-".into(),
        0xBE => ".".into(),
        0xBF => "/".into(),
        0xC0 => "`".into(),
        0xDB => "[".into(),
        0xDC => "\\".into(),
        0xDD => "]".into(),
        0xDE => "'".into(),
        _ => format!("VK_{vk:02X}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_map_to_alt_f9_and_alt_backquote() {
        let overlay = shortcut_for(&HotkeyBinding::default_toggle_overlay()).unwrap();
        assert_eq!(overlay, Shortcut::new(Some(Modifiers::ALT), Code::F9));
        let interactive = shortcut_for(&HotkeyBinding::default_toggle_interactive()).unwrap();
        assert_eq!(interactive, Shortcut::new(Some(Modifiers::ALT), Code::Backquote));
        assert_eq!(display(&HotkeyBinding::default_toggle_interactive()), "Alt+`");
    }

    #[test]
    fn rejects_unmodified_and_unknown_keys() {
        assert!(shortcut_for(&HotkeyBinding { virtual_key: 0x78, ..Default::default() }).is_none());
        assert!(shortcut_for(&HotkeyBinding { alt: true, virtual_key: 0xFF, ..Default::default() }).is_none());
        assert_eq!(display(&HotkeyBinding::default()), "Not set");
    }

    #[test]
    fn dom_codes_round_trip_to_virtual_keys() {
        assert_eq!(virtual_key_for_dom_code("F9"), Some(0x78));
        assert_eq!(virtual_key_for_dom_code("Backquote"), Some(0xC0));
        assert_eq!(virtual_key_for_dom_code("KeyQ"), Some(0x51));
        assert_eq!(virtual_key_for_dom_code("Nope"), None);
        let b = HotkeyBinding { ctrl: true, shift: true, virtual_key: 0x51, ..Default::default() };
        assert_eq!(display(&b), "Ctrl+Shift+Q");
    }
}
