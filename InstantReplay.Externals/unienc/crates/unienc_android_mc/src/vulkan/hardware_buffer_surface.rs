use crate::common::{ImageWriter, ImageWriterImage};
use crate::error::{AndroidError, Result};
use crate::java::SafeGlobalRef;
use crate::vulkan::hardware_buffer::HardwareBufferImage;
use crate::vulkan::types::VulkanFramebufferHandle;
use crate::vulkan::CONTEXT;
use ash::vk;

/// A surface backed by ImageWriter and HardwareBuffer
/// This provides explicit control over HardwareBuffer usage flags (VIDEO_ENCODE)
pub struct HardwareBufferSurface {
    image_writer: ImageWriter,
    width: u32,
    height: u32,
}

impl HardwareBufferSurface {
    /// Create a new HardwareBufferSurface from MediaCodec's input surface
    pub fn new(
        input_surface: &SafeGlobalRef,
        width: u32,
        height: u32,
        max_images: i32,
    ) -> Result<Self> {
        let image_writer =
            ImageWriter::new(input_surface, max_images, width as i32, height as i32)?;

        Ok(Self {
            image_writer,
            width,
            height,
        })
    }

    /// Dequeue an available frame for rendering
    /// Returns None if no frame is available (non-blocking)
    pub fn dequeue_frame(&self) -> Result<HardwareBufferFrame> {
        let image = self.image_writer.dequeue_input_image()?;

        // Get the hardware buffer
        let ahb = image.get_hardware_buffer()?;

        // Get the Vulkan context
        let cx = CONTEXT
            .get()
            .ok_or(AndroidError::ContextNotInitialized)?
            .lock()
            .map_err(|_| AndroidError::MutexPoisoned)?;

        // Import the hardware buffer as a Vulkan image
        let vk_image = HardwareBufferImage::from_hardware_buffer(
            &cx.device,
            &cx.instance,
            ahb,
        )?;

        // Create framebuffer for the image
        let framebuffer = VulkanFramebufferHandle::new(
            unsafe {
                cx.device.create_framebuffer(
                    &vk::FramebufferCreateInfo::default()
                        .render_pass(*cx.render_pass.render_pass)
                        .attachments(&[vk_image.vk_image_view()])
                        .width(self.width)
                        .height(self.height)
                        .layers(1),
                    None,
                )
            }
            .map_err(AndroidError::FramebufferCreationFailed)?,
            cx.device.clone(),
        );

        Ok(HardwareBufferFrame {
            image,
            vk_image,
            framebuffer,
            width: self.width,
            height: self.height,
        })
    }

    /// Queue a frame back to the encoder with timestamp
    pub fn queue_frame(&self, frame: HardwareBufferFrame, timestamp_ns: i64) -> Result<()> {
        // Drop Vulkan resources first (framebuffer, vk_image)
        drop(frame.framebuffer);
        drop(frame.vk_image);

        // Then queue the image to ImageWriter
        self.image_writer
            .queue_input_image(frame.image, timestamp_ns)?;

        Ok(())
    }
}

/// A frame dequeued from HardwareBufferSurface
/// Contains both the ImageWriter image and the imported Vulkan resources
pub struct HardwareBufferFrame {
    image: ImageWriterImage,
    pub vk_image: HardwareBufferImage,
    pub framebuffer: VulkanFramebufferHandle,
    pub width: u32,
    pub height: u32,
}

impl HardwareBufferFrame {
    pub fn vk_image_handle(&self) -> vk::Image {
        self.vk_image.vk_image()
    }

    pub fn vk_framebuffer(&self) -> vk::Framebuffer {
        *self.framebuffer
    }
}
