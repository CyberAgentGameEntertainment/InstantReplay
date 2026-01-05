use thiserror::Error;
use unienc_common::{CategorizedError, ErrorCategory};

/// Error type for unienc_windows_mf
#[derive(Error, Debug, Clone)]
pub enum WindowsError {
    // MFT (Media Foundation Transform) related errors
    #[error("No suitable MFT found")]
    NoSuitableMft,

    #[error("Expected 1 input and 1 output stream for encoder")]
    InvalidStreamCount,

    #[error("Input type is None")]
    InputTypeNone,

    #[error("Output type is None")]
    OutputTypeNone,

    #[error("Failed to get output")]
    OutputGetFailed,

    // Media event related errors
    #[error("Failed to receive media event")]
    MediaEventReceiveFailed,

    #[error("Failed to send video sample: {0}")]
    SampleSendFailed(String),

    // Stream related errors
    #[error("Stream is not initialized")]
    StreamNotInitialized,

    #[error("Failed to get stream")]
    StreamGetFailed,

    #[error("Failed to send media type")]
    MediaTypeSendFailed,

    #[error("Failed to send stream")]
    StreamSendFailed,

    #[error("Failed to send finish signal")]
    FinishSignalSendFailed,

    // Video encoder related errors
    #[error("MediaFoundationVideoEncoder only supports Bgra32 frames")]
    UnsupportedVideoFrameFormat,

    // Muxer related errors
    #[error("Failed to send video data to muxer: {0}")]
    MuxerSendFailed(String),

    #[error("Failed to wait for muxer completion: {0}")]
    MuxerCompletionWaitFailed(String),

    // Channel related errors
    #[error("Failed to send to channel")]
    ChannelSendFailed,

    // External error conversions
    #[error(transparent)]
    Windows(#[from] windows_core::Error),

    #[error(transparent)]
    Common(#[from] unienc_common::CommonError),

    #[error(transparent)]
    OneshotRecv(#[from] tokio::sync::oneshot::error::RecvError),
    
    #[error("Failed to convert UTF-16 into String")]
    Utf16ToStringConversionFailed,

    // Generic errors
    #[error("{0}")]
    Other(String),
}

/// Result type alias for unienc_windows_mf
pub type Result<T> = std::result::Result<T, WindowsError>;

impl CategorizedError for WindowsError {
    fn category(&self) -> ErrorCategory {
        match self {
            // Initialization/Configuration errors
            WindowsError::NoSuitableMft => ErrorCategory::Initialization,
            WindowsError::InvalidStreamCount => ErrorCategory::Configuration,
            WindowsError::InputTypeNone => ErrorCategory::Configuration,
            WindowsError::OutputTypeNone => ErrorCategory::Configuration,
            WindowsError::StreamNotInitialized => ErrorCategory::Initialization,

            // Encoding errors
            WindowsError::OutputGetFailed => ErrorCategory::Encoding,
            WindowsError::UnsupportedVideoFrameFormat => ErrorCategory::InvalidInput,

            // Communication errors
            WindowsError::MediaEventReceiveFailed => ErrorCategory::Communication,
            WindowsError::SampleSendFailed(_) => ErrorCategory::Communication,
            WindowsError::StreamGetFailed => ErrorCategory::Communication,
            WindowsError::MediaTypeSendFailed => ErrorCategory::Communication,
            WindowsError::StreamSendFailed => ErrorCategory::Communication,
            WindowsError::FinishSignalSendFailed => ErrorCategory::Communication,
            WindowsError::ChannelSendFailed => ErrorCategory::Communication,
            WindowsError::OneshotRecv(_) => ErrorCategory::Communication,

            // Muxing errors
            WindowsError::MuxerSendFailed(_) => ErrorCategory::Muxing,
            WindowsError::MuxerCompletionWaitFailed(_) => ErrorCategory::Muxing,

            // Platform errors
            WindowsError::Windows(_) => ErrorCategory::Platform,

            // Wrapped common errors - delegate to inner
            WindowsError::Common(e) => e.category(),

            // Generic fallback
            WindowsError::Other(_) => ErrorCategory::General,
            WindowsError::Utf16ToStringConversionFailed => ErrorCategory::General,
        }
    }
}

impl From<WindowsError> for unienc_common::CommonError {
    fn from(err: WindowsError) -> Self {
        unienc_common::CommonError::Categorized {
            category: err.category(),
            message: err.to_string(),
        }
    }
}

impl<T> From<tokio::sync::mpsc::error::SendError<T>> for WindowsError {
    fn from(_: tokio::sync::mpsc::error::SendError<T>) -> Self {
        WindowsError::ChannelSendFailed
    }
}

/// Extension trait for adding context to Results
pub trait ResultExt<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T>;
}

impl<T, E: std::error::Error + Send + Sync + 'static> ResultExt<T> for std::result::Result<T, E> {
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.map_err(|e| WindowsError::Other(format!("{}: {}", context.into(), e)))
    }
}

/// Extension trait for Option types
pub trait OptionExt<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T>;
}

impl<T> OptionExt<T> for Option<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.ok_or_else(|| WindowsError::Other(context.into()))
    }
}
