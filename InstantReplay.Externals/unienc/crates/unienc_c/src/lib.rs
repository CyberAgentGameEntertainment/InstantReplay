mod api;
mod buffer;
mod ffi;
mod platform;
mod runtime;
mod types;
#[cfg(feature = "unity")]
pub mod unity;

#[cfg(feature = "mimalloc")]
#[global_allocator]
static GLOBAL: mimalloc::MiMalloc = mimalloc::MiMalloc;

pub(crate) use crate::ffi::*;
pub(crate) use crate::platform::*;
pub(crate) use crate::runtime::*;
pub(crate) use crate::types::*;
