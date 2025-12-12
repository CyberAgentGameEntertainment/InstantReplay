use crate::vulkan::types::{VulkanFenceHandle, VulkanShaderModuleHandle};
use anyhow::anyhow;
use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

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

pub(crate) struct FencePool {
    device: Arc<ash::Device>,
    pool: Mutex<VecDeque<VulkanFenceHandle>>,
}

pub(crate) struct FenceGuard {
    fence: Option<VulkanFenceHandle>,
    pool: Arc<FencePool>,
}

impl FenceGuard {
    pub fn get(&self) -> &VulkanFenceHandle {
        self.fence.as_ref().unwrap()
    }
}

impl Drop for FenceGuard {
    fn drop(&mut self) {
        if let Some(fence) = self.fence.take() {
            let _ = self.pool.push(fence);
        }
    }
}

impl FencePool {
    pub fn new(device: Arc<ash::Device>) -> Self {
        Self {
            device,
            pool: Mutex::new(VecDeque::new()),
        }
    }
}

impl FencePool {
    pub fn pop(self: &Arc<Self>) -> anyhow::Result<FenceGuard> {
        let mut pool = self.pool.lock().map_err(|e| anyhow!(e.to_string()))?;
        if let Some(fence) = pool.pop_front() {
            Ok(FenceGuard {
                fence: Some(fence),
                pool: self.clone(),
            })
        } else {
            println!("Creating new fence");
            let fence_info = ash::vk::FenceCreateInfo::default();
            Ok(FenceGuard {
                fence: VulkanFenceHandle::new(
                    unsafe { self.device.create_fence(&fence_info, None) }?,
                    self.device.clone(),
                )
                .into(),
                pool: self.clone(),
            })
        }
    }

    pub fn push(&self, fence: VulkanFenceHandle) -> anyhow::Result<()> {
        let mut pool = self.pool.lock().map_err(|e| anyhow!(e.to_string()))?;
        unsafe { self.device.reset_fences(&[*fence])? };
        pool.push_back(fence);
        Ok(())
    }
}
