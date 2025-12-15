use thiserror::Error;
use unienc_common::{CategorizedError, ErrorCategory};

/// Error type for unienc_android_mc
#[derive(Error, Debug)]
pub enum AndroidError {
    // JNI related errors
    #[error("JavaVM not initialized")]
    JavaVmNotInitialized,

    #[error("Failed to attach current thread to JVM: {0}")]
    JvmAttachFailed(String),

    #[error("JNI exception occurred")]
    JniException,

    #[error("Failed to create global reference")]
    JniGlobalRefFailed,

    #[error("Failed to call method '{0}'")]
    JniMethodCallFailed(String),

    #[error("Expected {expected} return value")]
    JniUnexpectedReturnValue { expected: &'static str },

    #[error("Failed to get field '{0}'")]
    JniFieldGetFailed(String),

    #[error("Failed to create Java string")]
    JniStringCreationFailed,

    #[error("Buffer is not a direct buffer")]
    NotDirectBuffer,

    // MediaCodec related errors
    #[error("Image is null")]
    ImageNull,

    #[error("No input buffer available")]
    NoInputBuffer,

    #[error("Unsupported number of planes: {0}")]
    UnsupportedPlaneCount(usize),

    #[error("Failed to create byte buffer")]
    ByteBufferCreationFailed,

    // ImageWriter related errors
    #[error("dequeueInputImage returned null")]
    DequeueImageNull,

    #[error("getHardwareBuffer returned null")]
    HardwareBufferNull,

    #[error("AHardwareBuffer_fromHardwareBuffer returned null")]
    AHardwareBufferNull,

    // Vulkan related errors
    #[error("Null Vulkan texture pointer")]
    NullVulkanTexture,

    #[error("Vulkan context is not initialized")]
    ContextNotInitialized,

    #[error("Failed to lock mutex")]
    MutexPoisoned,

    #[error("Failed to set global state")]
    GlobalStateSetFailed,

    #[error("Vulkan error: {0}")]
    Vulkan(ash::vk::Result),

    #[error("Failed to create graphics pipeline: {0}")]
    GraphicsPipelineCreationFailed(ash::vk::Result),

    #[error("Failed to create framebuffer: {0}")]
    FramebufferCreationFailed(ash::vk::Result),

    #[error("Failed to create image view: {0}")]
    ImageViewCreationFailed(ash::vk::Result),

    #[error("Failed to create image from AHardwareBuffer: {0}")]
    HardwareBufferImageCreationFailed(ash::vk::Result),

    #[error("Failed to allocate memory for AHardwareBuffer: {0}")]
    HardwareBufferMemoryAllocationFailed(ash::vk::Result),

    #[error("Failed to bind image memory: {0}")]
    ImageMemoryBindFailed(ash::vk::Result),

    #[error("Failed to wait for fence: {0}")]
    FenceWaitFailed(ash::vk::Result),

    #[error("No available descriptor sets")]
    NoAvailableDescriptorSets,

    #[error("No suitable memory type found")]
    NoSuitableMemoryType,

    #[error("AHardwareBuffer properties query failed: {0}")]
    HardwareBufferPropertiesFailed(ash::vk::Result),

    #[error("Unsupported graphics format: {0}")]
    UnsupportedGraphicsFormat(u32),

    // Muxer related errors
    #[error("Muxer already started")]
    MuxerAlreadyStarted,

    #[error("Track does not have metadata")]
    MissingTrackMetadata,

    #[error("Failed to send {0} signal")]
    ChannelSendFailed(&'static str),

    #[error("Invalid output path")]
    InvalidOutputPath,

    // Encoder related errors
    #[error("This encoder is initialized for other input")]
    EncoderInputMismatch,

    #[error("Event ID is not reserved")]
    EventIdNotReserved,

    #[error("Failed to send from render thread")]
    RenderThreadSendFailed,

    // External error conversions
    #[error(transparent)]
    Jni(#[from] jni::errors::Error),

    #[error(transparent)]
    Common(#[from] unienc_common::CommonError),

    #[error(transparent)]
    OneshotRecv(#[from] tokio::sync::oneshot::error::RecvError),

    #[error("Vulkan operation failed: {0:?}")]
    VulkanResult(#[from] ash::vk::Result),

    #[error("UTF-8 encoding error: {0}")]
    Utf8(#[from] std::str::Utf8Error),

    // Generic errors
    #[error("{0}")]
    Other(String),
}

/// Result type alias for unienc_android_mc
pub type Result<T> = std::result::Result<T, AndroidError>;

impl CategorizedError for AndroidError {
    fn category(&self) -> ErrorCategory {
        match self {
            // Initialization errors
            AndroidError::JavaVmNotInitialized => ErrorCategory::Initialization,
            AndroidError::JvmAttachFailed(_) => ErrorCategory::Initialization,
            AndroidError::ContextNotInitialized => ErrorCategory::Initialization,

            // Platform/JNI errors
            AndroidError::JniException => ErrorCategory::Platform,
            AndroidError::JniGlobalRefFailed => ErrorCategory::Platform,
            AndroidError::JniMethodCallFailed(_) => ErrorCategory::Platform,
            AndroidError::JniUnexpectedReturnValue { .. } => ErrorCategory::Platform,
            AndroidError::JniFieldGetFailed(_) => ErrorCategory::Platform,
            AndroidError::JniStringCreationFailed => ErrorCategory::Platform,
            AndroidError::Jni(_) => ErrorCategory::Platform,

            // Resource allocation errors
            AndroidError::NotDirectBuffer => ErrorCategory::ResourceAllocation,
            AndroidError::ImageNull => ErrorCategory::ResourceAllocation,
            AndroidError::NoInputBuffer => ErrorCategory::ResourceAllocation,
            AndroidError::ByteBufferCreationFailed => ErrorCategory::ResourceAllocation,
            AndroidError::DequeueImageNull => ErrorCategory::ResourceAllocation,
            AndroidError::HardwareBufferNull => ErrorCategory::ResourceAllocation,
            AndroidError::AHardwareBufferNull => ErrorCategory::ResourceAllocation,
            AndroidError::NullVulkanTexture => ErrorCategory::ResourceAllocation,
            AndroidError::NoAvailableDescriptorSets => ErrorCategory::ResourceAllocation,
            AndroidError::NoSuitableMemoryType => ErrorCategory::ResourceAllocation,
            AndroidError::HardwareBufferMemoryAllocationFailed(_) => ErrorCategory::ResourceAllocation,

            // Encoding errors (Vulkan pipeline errors)
            AndroidError::MutexPoisoned => ErrorCategory::Encoding,
            AndroidError::GlobalStateSetFailed => ErrorCategory::Encoding,
            AndroidError::Vulkan(_) => ErrorCategory::Encoding,
            AndroidError::GraphicsPipelineCreationFailed(_) => ErrorCategory::Encoding,
            AndroidError::FramebufferCreationFailed(_) => ErrorCategory::Encoding,
            AndroidError::ImageViewCreationFailed(_) => ErrorCategory::Encoding,
            AndroidError::HardwareBufferImageCreationFailed(_) => ErrorCategory::Encoding,
            AndroidError::ImageMemoryBindFailed(_) => ErrorCategory::Encoding,
            AndroidError::FenceWaitFailed(_) => ErrorCategory::Encoding,
            AndroidError::HardwareBufferPropertiesFailed(_) => ErrorCategory::Encoding,
            AndroidError::VulkanResult(_) => ErrorCategory::Encoding,
            AndroidError::EncoderInputMismatch => ErrorCategory::Encoding,

            // Muxing errors
            AndroidError::MuxerAlreadyStarted => ErrorCategory::Muxing,
            AndroidError::MissingTrackMetadata => ErrorCategory::Muxing,
            AndroidError::InvalidOutputPath => ErrorCategory::Muxing,

            // Communication errors
            AndroidError::ChannelSendFailed(_) => ErrorCategory::Communication,
            AndroidError::OneshotRecv(_) => ErrorCategory::Communication,
            AndroidError::RenderThreadSendFailed => ErrorCategory::Communication,
            AndroidError::EventIdNotReserved => ErrorCategory::Communication,

            // Invalid input errors
            AndroidError::UnsupportedPlaneCount(_) => ErrorCategory::InvalidInput,
            AndroidError::UnsupportedGraphicsFormat(_) => ErrorCategory::InvalidInput,
            AndroidError::Utf8(_) => ErrorCategory::InvalidInput,

            // Wrapped common errors - delegate to inner
            AndroidError::Common(e) => e.category(),

            // Generic fallback
            AndroidError::Other(_) => ErrorCategory::General,
        }
    }
}

impl From<AndroidError> for unienc_common::CommonError {
    fn from(err: AndroidError) -> Self {
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
        self.map_err(|e| AndroidError::Other(format!("{}: {}", context.into(), e)))
    }
}

/// Extension trait for Option types
pub trait OptionExt<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T>;
}

impl<T> OptionExt<T> for Option<T> {
    fn context<C: Into<String>>(self, context: C) -> Result<T> {
        self.ok_or_else(|| AndroidError::Other(context.into()))
    }
}
