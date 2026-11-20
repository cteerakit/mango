//! Valve Game State Integration: payload parsing and normalization into [`MatchSnapshot`].
//!
//! [`MatchSnapshot`]: crate::model::MatchSnapshot

mod normalize;
mod payload;

pub use normalize::normalize;
pub use payload::*;

#[cfg(test)]
mod tests;
