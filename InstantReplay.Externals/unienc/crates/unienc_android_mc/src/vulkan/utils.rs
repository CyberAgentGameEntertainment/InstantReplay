use crate::vulkan::types::{
    VulkanMemoryHandle, VulkanShaderModuleHandle,
};
use ash::vk::MemoryPropertyFlags;
use std::sync::Arc;

pub fn create_shader_module(
    device: &Arc<ash::Device>,
    code: &'static [u8],
) -> anyhow::Result<VulkanShaderModuleHandle> {
    let mut info = ash::vk::ShaderModuleCreateInfo::default();
    info.code_size = code.len();
    info.p_code = code.as_ptr() as *const u32;
    Ok(VulkanShaderModuleHandle::new(
        unsafe { device.create_shader_module(&info, None) }?,
        device.clone(),
    ))
}