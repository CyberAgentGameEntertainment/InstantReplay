use ash::vk;
use std::sync::Arc;

pub use handles::*;

pub struct VulkanImage {
    pub(crate) image: VulkanImageHandle,
    pub(crate) memory: Option<VulkanMemoryHandle>,
}

pub struct VulkanImageView {
    pub(crate) image: Arc<VulkanImage>,
    pub(crate) view: VulkanImageViewHandle,
}

pub struct VulkanCommandBuffer {
    pub(crate) command_pool: Arc<VulkanCommandPoolHandle>,
    pub(crate) command_buffer: vk::CommandBuffer,
    device: Arc<ash::Device>,
}

pub struct VulkanFramebuffer {
    pub(crate) framebuffer: VulkanFramebufferHandle,
    pub(crate) view: Arc<VulkanImageView>,
}

pub struct VulkanSwapchain {
    pub(crate) swapchain: VulkanSwapchainHandle,
    pub(crate) surface: Arc<VulkanSurfaceHandle>,
}

impl VulkanImage {
    pub fn new_externally_bound(image: VulkanImageHandle) -> Self {
        VulkanImage {
            image,
            memory: None,
        }
    }
}

impl VulkanCommandBuffer {
    pub fn new(
        command_pool: Arc<VulkanCommandPoolHandle>,
        command_buffer: vk::CommandBuffer,
        device: Arc<ash::Device>,
    ) -> Self {
        VulkanCommandBuffer {
            command_pool,
            command_buffer,
            device,
        }
    }
}

impl Drop for VulkanCommandBuffer {
    fn drop(&mut self) {
        unsafe {
            self.device.free_command_buffers(**self.command_pool, &[self.command_buffer]);
        }
    }
}

mod handles;
