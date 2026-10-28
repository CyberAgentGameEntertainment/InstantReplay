use crate::common::{ImageWriter, ImageWriterImage};
use crate::error::{AndroidError, Result};
use crate::java::SafeGlobalRef;
use crate::vulkan::CONTEXT;
use crate::vulkan::hardware_buffer::HardwareBufferImage;
use crate::vulkan::types::VulkanFramebufferHandle;
use ash::vk;
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

/// A surface backed by ImageWriter and HardwareBuffer
/// This provides explicit control over HardwareBuffer usage flags (VIDEO_ENCODE)
pub struct HardwareBufferSurface {
    image_writer: ImageWriter,
    width: u32,
    height: u32,
    /// Vulkan resources of the hardware buffers seen so far, most recently imported last.
    targets: Mutex<VecDeque<Arc<HardwareBufferTarget>>>,
    max_targets: usize,
}

/// A hardware buffer imported into Vulkan, together with the framebuffer that renders into it.
///
/// The ImageWriter cycles through a fixed set of hardware buffers, so the import, which allocates
/// device memory and creates an image, a view and a framebuffer, is done once per buffer rather
/// than once per frame. The imported image holds a reference to its hardware buffer, so the buffer
/// cannot be freed, and its address cannot be reused by another buffer, while it is cached.
struct HardwareBufferTarget {
    // Dropped before the image it is attached to.
    framebuffer: VulkanFramebufferHandle,
    vk_image: HardwareBufferImage,
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
            targets: Mutex::new(VecDeque::new()),
            // The ImageWriter is expected to keep `max_images` buffers, but it may replace them.
            // Buffers that are no longer in use are evicted once more than this many are cached.
            max_targets: max_images.max(1) as usize * 2,
        })
    }

    /// Dequeue an available frame for rendering
    /// Returns None if no frame is available (non-blocking)
    pub fn dequeue_frame(&self) -> Result<HardwareBufferFrame> {
        let image = self.image_writer.dequeue_input_image()?;

        // Get the hardware buffer
        let ahb = image.get_hardware_buffer()?;
        let target = self.get_or_import_target(ahb)?;

        Ok(HardwareBufferFrame {
            image,
            target,
            width: self.width,
            height: self.height,
        })
    }

    fn get_or_import_target(
        &self,
        ahb: *mut ndk_sys::AHardwareBuffer,
    ) -> Result<Arc<HardwareBufferTarget>> {
        let mut targets = self.targets.lock().map_err(|_| AndroidError::MutexPoisoned)?;
        if let Some(target) = targets.iter().find(|target| target.vk_image.ahb() == ahb) {
            return Ok(target.clone());
        }

        // Get the Vulkan context
        let cx = CONTEXT
            .get()
            .ok_or(AndroidError::ContextNotInitialized)?
            .lock()
            .map_err(|_| AndroidError::MutexPoisoned)?;

        // Import the hardware buffer as a Vulkan image
        let vk_image = HardwareBufferImage::from_hardware_buffer(&cx.device, &cx.instance, ahb)?;

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

        let target = Arc::new(HardwareBufferTarget {
            framebuffer,
            vk_image,
        });

        // A frame in flight holds its own reference, so an evicted target is destroyed only after
        // the frame has been queued.
        if targets.len() >= self.max_targets {
            targets.pop_front();
        }
        targets.push_back(target.clone());
        Ok(target)
    }

    /// Queue a frame back to the encoder with timestamp
    pub fn queue_frame(&self, frame: HardwareBufferFrame, timestamp_ns: i64) -> Result<()> {
        // Release this frame's reference to the Vulkan resources first. They stay cached for the
        // next time the ImageWriter hands out the same hardware buffer.
        drop(frame.target);

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
    target: Arc<HardwareBufferTarget>,
    pub width: u32,
    pub height: u32,
}

impl HardwareBufferFrame {
    pub fn vk_image_handle(&self) -> vk::Image {
        self.target.vk_image.vk_image()
    }

    pub fn vk_framebuffer(&self) -> vk::Framebuffer {
        *self.target.framebuffer
    }
}
