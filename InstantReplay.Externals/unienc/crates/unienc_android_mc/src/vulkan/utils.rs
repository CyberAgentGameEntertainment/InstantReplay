use std::collections::VecDeque;
use crate::vulkan::types::{VulkanFenceHandle, VulkanSemaphoreHandle, VulkanShaderModuleHandle};
use std::sync::{Arc, Mutex};
use anyhow::anyhow;

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


pub(crate) struct SemaphorePool {
    device: Arc<ash::Device>,
    pool: Mutex<VecDeque<VulkanSemaphoreHandle>>,
}

impl SemaphorePool {
    pub fn new(device: Arc<ash::Device>) -> Self {
        Self {
            device,
            pool: Mutex::new(VecDeque::new()),
        }
    }
}

pub(crate) struct SemaphoreGuard {
    semaphore: Option<VulkanSemaphoreHandle>,
    pool: Arc<SemaphorePool>,
}

impl SemaphoreGuard {
    pub fn new(
        semaphore: VulkanSemaphoreHandle,
        pool: Arc<SemaphorePool>,
    ) -> Self {
        Self {
            semaphore: Some(semaphore),
            pool,
        }
    }

    pub fn get(&self) -> &VulkanSemaphoreHandle {
        self.semaphore.as_ref().unwrap()
    }
}

impl Drop for SemaphoreGuard {
    fn drop(&mut self) {
        if let Some(semaphore) = self.semaphore.take() {
            let _ = self.pool.push(semaphore);
        }
    }
}

impl SemaphorePool {
    pub fn pop(self: &Arc<Self>) -> anyhow::Result<SemaphoreGuard> {
        let mut pool = self.pool.lock().map_err(|e| anyhow!(e.to_string()))?;
        if let Some(semaphore) = pool.pop_front() {
            Ok(SemaphoreGuard::new(semaphore, self.clone()))
        } else {
            println!("Creating new semaphore");
            let semaphore_info = ash::vk::SemaphoreCreateInfo::default();
            Ok(SemaphoreGuard::new(VulkanSemaphoreHandle::new(
                unsafe { self.device.create_semaphore(&semaphore_info, None) }?,
                self.device.clone(),
            ), self.clone()))
        }
    }

    pub fn push(&self, semaphore: VulkanSemaphoreHandle) -> anyhow::Result<()> {
        let mut pool = self.pool.lock().map_err(|e| anyhow!(e.to_string()))?;
        pool.push_back(semaphore);
        Ok(())
    }
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
            Ok(FenceGuard{fence: Some(fence), pool: self.clone()})
        } else {
            println!("Creating new fence");
            let fence_info = ash::vk::FenceCreateInfo::default();
            Ok(FenceGuard{fence: VulkanFenceHandle::new(unsafe { self.device.create_fence(&fence_info, None) }?, self.device.clone()).into(), pool: self.clone()} )
        }
    }

    pub fn push(&self, fence: VulkanFenceHandle) -> anyhow::Result<()> {
        let mut pool = self.pool.lock().map_err(|e| anyhow!(e.to_string()))?;
        unsafe { self.device.reset_fences(&[*fence])? };
        pool.push_back(fence);
        Ok(())
    }
}