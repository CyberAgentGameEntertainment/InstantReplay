use objc2::rc::Retained;
use objc2_foundation::NSError;
use thiserror::Error;
use unienc_common::{CategorizedError, ErrorCategory};

/// Error type for unienc_apple_vt
#[derive(Error, Debug)]
pub enum AppleError {
    // OSStatus errors
    #[error("OSStatus error: {0}")]
    OsStatus(i32),

    // Metal related errors
    #[error("Metal context is not initialized")]
    MetalNotInitialized,

    #[error("Failed to retain MTLTexture from raw pointer")]
    MetalTextureRetainFailed,

    #[error("Failed to set global state")]
    GlobalStateSetFailed,

    #[error("Failed to create CVMetalTextureCache")]
    MetalTextureCacheCreationFailed,

    #[error("CVMetalTextureCache is null")]
    MetalTextureCacheNull,

    #[error("Failed to get current command buffer")]
    CommandBufferNotAvailable,

    #[error("Failed to create render command encoder")]
    RenderCommandEncoderCreationFailed,

    #[error("Failed to create sampler state")]
    SamplerStateCreationFailed,

    #[error("Failed to create vertex uniforms buffer")]
    VertexUniformsBufferCreationFailed,

    // VideoToolbox related errors
    #[error("VTCompressionSession is null")]
    CompressionSessionNull,

    #[error("Failed to create NonNull pointer")]
    NonNullCreationFailed,

    #[error("CVPixelBuffer is null")]
    PixelBufferNull,

    #[error("Failed to send blit future")]
    BlitFutureSendFailed,

    #[error("Event ID is not reserved")]
    EventIdNotReserved,

    // AudioToolbox related errors
    #[error("Failed to create audio converter")]
    AudioConverterCreationFailed,

    // Muxer related errors
    #[error("Failed to start writing: {0}")]
    AssetWriterStartFailed(String),

    #[error("Failed to start writing")]
    AssetWriterStartFailedUnknown,

    // CVPixelBuffer/CVMetalTexture related errors
    #[error("CVMetalTexture is null")]
    MetalTextureNull,

    #[error("Failed to get MTLTexture from CVMetalTexture")]
    MetalTextureGetFailed,

    // Channel related errors
    #[error("Failed to send to channel")]
    ChannelSendFailed,

    // External error conversions
    #[error(transparent)]
    Common(#[from] unienc_common::CommonError),

    #[error(transparent)]
    OneshotRecv(#[from] tokio::sync::oneshot::error::RecvError),

    // Generic errors
    #[error("{0}")]
    Other(String),
}

impl<T> From<tokio::sync::mpsc::error::SendError<T>> for AppleError {
    fn from(_: tokio::sync::mpsc::error::SendError<T>) -> Self {
        AppleError::ChannelSendFailed
    }
}

impl From<Retained<NSError>> for AppleError {
    fn from(err: Retained<NSError>) -> Self {
        AppleError::Other(err.to_string())
    }
}

/// Result type alias for unienc_apple_vt
pub type Result<T> = std::result::Result<T, AppleError>;

impl CategorizedError for AppleError {
    fn category(&self) -> ErrorCategory {
        match self {
            // Platform errors (OSStatus)
            AppleError::OsStatus(_) => ErrorCategory::Platform,

            // Initialization errors
            AppleError::MetalNotInitialized => ErrorCategory::Initialization,
            AppleError::GlobalStateSetFailed => ErrorCategory::Initialization,
            AppleError::MetalTextureCacheCreationFailed => ErrorCategory::Initialization,
            AppleError::AudioConverterCreationFailed => ErrorCategory::Initialization,

            // Resource allocation errors
            AppleError::MetalTextureRetainFailed => ErrorCategory::ResourceAllocation,
            AppleError::MetalTextureCacheNull => ErrorCategory::ResourceAllocation,
            AppleError::CommandBufferNotAvailable => ErrorCategory::ResourceAllocation,
            AppleError::RenderCommandEncoderCreationFailed => ErrorCategory::ResourceAllocation,
            AppleError::SamplerStateCreationFailed => ErrorCategory::ResourceAllocation,
            AppleError::VertexUniformsBufferCreationFailed => ErrorCategory::ResourceAllocation,
            AppleError::CompressionSessionNull => ErrorCategory::ResourceAllocation,
            AppleError::NonNullCreationFailed => ErrorCategory::ResourceAllocation,
            AppleError::PixelBufferNull => ErrorCategory::ResourceAllocation,
            AppleError::MetalTextureNull => ErrorCategory::ResourceAllocation,
            AppleError::MetalTextureGetFailed => ErrorCategory::ResourceAllocation,

            // Communication errors
            AppleError::BlitFutureSendFailed => ErrorCategory::Communication,
            AppleError::EventIdNotReserved => ErrorCategory::Communication,
            AppleError::ChannelSendFailed => ErrorCategory::Communication,
            AppleError::OneshotRecv(_) => ErrorCategory::Communication,

            // Muxing errors
            AppleError::AssetWriterStartFailed(_) => ErrorCategory::Muxing,
            AppleError::AssetWriterStartFailedUnknown => ErrorCategory::Muxing,

            // Wrapped common errors - delegate to inner
            AppleError::Common(e) => e.category(),

            // Generic fallback
            AppleError::Other(_) => ErrorCategory::General,
        }
    }
}

impl From<AppleError> for unienc_common::CommonError {
    fn from(err: AppleError) -> Self {
        unienc_common::CommonError::Categorized {
            category: err.category(),
            message: err.to_string(),
        }
    }
}

/// Extension trait for adding context to Results
pub trait ResultExt<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T>;
}

impl<T, E: std::error::Error + Send + Sync + 'static> ResultExt<T> for std::result::Result<T, E> {
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.map_err(|e| AppleError::Other(format!("{}: {}", context.into(), e)))
    }
}

/// Extension trait for Option types
pub trait OptionExt<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T>;
}

impl<T> OptionExt<T> for Option<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.ok_or_else(|| AppleError::Other(context.into()))
    }
}

/// Trait for converting OSStatus to Result
pub trait OsStatusExt {
    fn to_result(&self) -> Result<()>;
}

impl OsStatusExt for i32 {
    fn to_result(&self) -> Result<()> {
        if *self == 0 {
            Ok(())
        } else {
            Err(AppleError::OsStatus(*self))
        }
    }
}
