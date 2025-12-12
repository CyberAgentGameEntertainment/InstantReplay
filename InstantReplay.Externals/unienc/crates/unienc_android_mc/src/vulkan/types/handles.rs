use ash::vk;
use super::*;

// Macro to define Vulkan handle wrapper structs with automatic resource cleanup
macro_rules! define_handle {
    ($name:ident, $handle_type:ty,  $destroy_fn:ident, $device_type:ty) => {
        pub struct $name($handle_type, Option<Arc<$device_type>>);
        impl Drop for $name {
            fn drop(&mut self) {
                unsafe {
                    if let Some(device) = &self.1 {device.$destroy_fn(self.0, None); }
                }
            }
        }

        impl $name {
            pub fn new(handle: $handle_type, device: Arc<$device_type>) -> Self {
                Self(handle, Some(device))
            }

            pub fn new_external(handle: $handle_type) -> Self {
                Self(handle, None)
            }
        }

        impl std::ops::Deref for $name {
            type Target = $handle_type;
            fn deref(&self) -> &Self::Target {
                &self.0
            }
        }
    };
    () => {};
}

define_handle!(VulkanImageHandle, vk::Image, destroy_image, ash::Device);
define_handle!(VulkanMemoryHandle, vk::DeviceMemory, free_memory, ash::Device);
define_handle!(VulkanPipelineLayoutHandle, vk::PipelineLayout, destroy_pipeline_layout, ash::Device);
define_handle!(VulkanPipelineHandle, vk::Pipeline, destroy_pipeline, ash::Device);
define_handle!(VulkanShaderModuleHandle, vk::ShaderModule, destroy_shader_module, ash::Device);
define_handle!(VulkanDescriptorSetLayoutHandle, vk::DescriptorSetLayout, destroy_descriptor_set_layout, ash::Device);
define_handle!(VulkanRenderPassHandle, vk::RenderPass, destroy_render_pass, ash::Device);
define_handle!(VulkanBufferHandle, vk::Buffer, destroy_buffer, ash::Device);
define_handle!(VulkanImageViewHandle, vk::ImageView, destroy_image_view, ash::Device);
define_handle!(VulkanFramebufferHandle, vk::Framebuffer, destroy_framebuffer, ash::Device);
define_handle!(VulkanSamplerHandle, vk::Sampler, destroy_sampler, ash::Device);
define_handle!(VulkanCommandPoolHandle, vk::CommandPool, destroy_command_pool, ash::Device);
define_handle!(VulkanFenceHandle, vk::Fence, destroy_fence, ash::Device);
define_handle!(VulkanSemaphoreHandle, vk::Semaphore, destroy_semaphore, ash::Device);
define_handle!(VulkanSurfaceHandle, vk::SurfaceKHR, destroy_surface, ash::khr::surface::Instance);
define_handle!(VulkanSwapchainHandle, vk::SwapchainKHR, destroy_swapchain, ash::khr::swapchain::Device);
define_handle!(VulkanDescriptorPoolHandle, vk::DescriptorPool, destroy_descriptor_pool, ash::Device);
