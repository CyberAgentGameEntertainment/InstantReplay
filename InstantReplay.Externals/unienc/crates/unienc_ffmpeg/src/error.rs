use thiserror::Error;
use unienc_common::{CategorizedError, ErrorCategory};

#[derive(Error, Debug)]
pub enum FFmpegError {
    #[error("FFmpeg not found in PATH")]
    FFmpegNotFound,

    #[error("Failed to duplicate pipe file descriptor")]
    PipeDupFailed,

    #[error("Failed to get stdin from child process")]
    StdinNotAvailable,

    #[error("Failed to get inputs from FFmpeg process")]
    InputsNotAvailable,

    #[error("Input is not available")]
    InputNotAvailable,

    #[error("Failed to get output from FFmpeg process")]
    OutputNotAvailable,

    #[error("FFmpeg process failed with exit status")]
    ProcessFailed,

    #[error("No suitable H.264 encoder found")]
    NoSuitableEncoder,

    #[error("Unsupported video frame format: only Bgra32 is supported")]
    UnsupportedFrameFormat,

    #[error(transparent)]
    Io(#[from] std::io::Error),

    #[error(transparent)]
    Common(#[from] unienc_common::CommonError),

    #[error("{0}")]
    Other(String),
}

pub type Result<T> = std::result::Result<T, FFmpegError>;

impl CategorizedError for FFmpegError {
    fn category(&self) -> ErrorCategory {
        match self {
            // Initialization errors
            FFmpegError::FFmpegNotFound => ErrorCategory::Initialization,
            FFmpegError::NoSuitableEncoder => ErrorCategory::Initialization,

            // Resource allocation errors
            FFmpegError::PipeDupFailed => ErrorCategory::ResourceAllocation,
            FFmpegError::StdinNotAvailable => ErrorCategory::ResourceAllocation,
            FFmpegError::InputsNotAvailable => ErrorCategory::ResourceAllocation,
            FFmpegError::InputNotAvailable => ErrorCategory::ResourceAllocation,
            FFmpegError::OutputNotAvailable => ErrorCategory::ResourceAllocation,

            // Encoding errors
            FFmpegError::ProcessFailed => ErrorCategory::Encoding,

            // Invalid input errors
            FFmpegError::UnsupportedFrameFormat => ErrorCategory::InvalidInput,

            // IO errors (platform)
            FFmpegError::Io(_) => ErrorCategory::Platform,

            // Wrapped common errors - delegate to inner
            FFmpegError::Common(e) => e.category(),

            // Generic fallback
            FFmpegError::Other(_) => ErrorCategory::General,
        }
    }
}

impl From<FFmpegError> for unienc_common::CommonError {
    fn from(err: FFmpegError) -> Self {
        unienc_common::CommonError::Categorized {
            category: err.category(),
            message: err.to_string(),
        }
    }
}

pub trait ResultExt<T, E> {
    fn context<C: Into<String>>(self, context: C) -> Result<T>;
}

impl<T, E: std::fmt::Display> ResultExt<T, E> for std::result::Result<T, E> {
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.map_err(|e| FFmpegError::Other(format!("{}: {}", context.into(), e)))
    }
}

pub trait OptionExt<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T>;
}

impl<T> OptionExt<T> for Option<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.ok_or_else(|| FFmpegError::Other(context.into()))
    }
}
