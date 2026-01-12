use thiserror::Error;

/// Error category for FFI communication
/// This enum is used to categorize errors at the FFI boundary
#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ErrorCategory {
    /// General/unspecified error
    General = 1,
    /// Failed to initialize a component
    Initialization = 2,
    /// Configuration error
    Configuration = 3,
    /// Resource allocation failure (memory, buffers, etc.)
    ResourceAllocation = 4,
    /// Encoding error
    Encoding = 5,
    /// Muxing error
    Muxing = 6,
    /// Communication/channel error
    Communication = 7,
    /// Timeout error
    Timeout = 8,
    /// Invalid input
    InvalidInput = 9,
    /// Platform-specific error
    Platform = 10,
}

/// Trait for errors that can provide an error category
pub trait CategorizedError: std::error::Error {
    /// Returns the error category for this error
    fn category(&self) -> ErrorCategory;
}

/// Common error type for unienc_common
#[derive(Error, Debug, Clone)]
pub enum CommonError {
    #[error("Buffer pool limit exceeded")]
    BufferPoolExceeded,

    #[error("Blit not supported in this encoding system")]
    BlitNotSupported,

    /// Error with explicit category from platform code
    #[error("{message}")]
    Categorized {
        category: ErrorCategory,
        message: String,
    },

    #[error("{0}")]
    Other(String),
}

impl CategorizedError for CommonError {
    fn category(&self) -> ErrorCategory {
        match self {
            CommonError::BufferPoolExceeded => ErrorCategory::ResourceAllocation,
            CommonError::BlitNotSupported => ErrorCategory::Configuration,
            CommonError::Categorized { category, .. } => *category,
            CommonError::Other(_) => ErrorCategory::General,
        }
    }
}

/// Result type alias for unienc_common
pub type Result<T> = std::result::Result<T, CommonError>;

/// Extension trait for adding context to Results (similar to anyhow::Context)
pub trait ResultExt<T> {
    /// Wrap the error with additional context
    fn context<C: Into<String>>(self, context: C) -> Result<T>;

    /// Wrap the error with lazily-evaluated context
    fn with_context<C, F>(self, f: F) -> Result<T>
    where
        C: Into<String>,
        F: FnOnce() -> C;
}

impl<T, E: std::error::Error + Send + Sync + 'static> ResultExt<T>
    for std::result::Result<T, E>
{
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.map_err(|e| CommonError::Other(format!("{}: {}", context.into(), e)))
    }

    fn with_context<C, F>(self, f: F) -> Result<T>
    where
        C: Into<String>,
        F: FnOnce() -> C,
    {
        self.map_err(|e| CommonError::Other(format!("{}: {}", f().into(), e)))
    }
}

/// Extension trait for Option types
pub trait OptionExt<T> {
    /// Convert Option to Result with context message
    fn context<C: Into<String>>(self, context: C) -> Result<T>;

    /// Convert Option to Result with lazily-evaluated context
    fn with_context<C, F>(self, f: F) -> Result<T>
    where
        C: Into<String>,
        F: FnOnce() -> C;
}

impl<T> OptionExt<T> for Option<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.ok_or_else(|| CommonError::Other(context.into()))
    }

    fn with_context<C, F>(self, f: F) -> Result<T>
    where
        C: Into<String>,
        F: FnOnce() -> C,
    {
        self.ok_or_else(|| CommonError::Other(f().into()))
    }
}
