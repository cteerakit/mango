//! Mango's game logic, ported from the C# `Mango.Core` project.
//!
//! Pure functions and data types only: no network, files or windows. The Tauri app wires
//! these to GSI, OpenDota, Steam and the UI.

pub mod assets;
pub mod gsi;
pub mod history;
pub mod match_build;
pub mod match_id;
pub mod model;
pub mod notes;
pub mod page_state;
pub mod rank_estimate;
pub mod ranks;
pub mod roster;
pub mod steam_id;
pub mod steam_local;
pub mod steam_top_live;
pub mod timers;

pub mod json;
pub mod text;
