use crate::vulkan::types::{
    VulkanFramebuffer, VulkanFramebufferHandle, VulkanImage, VulkanImageHandle, VulkanImageView,
    VulkanImageViewHandle, VulkanSurfaceHandle, VulkanSwapchain, VulkanSwapchainHandle,
};
use crate::vulkan::{GlobalContext, CONTEXT};
use anyhow::{anyhow, Context, Result};
use ash::prelude::VkResult;
use ash::vk;
use ash::vk::SurfaceFormatKHR;
use jni::JNIEnv;
use std::sync::{Arc, Mutex};

pub(crate) struct NativeWindow {
    inner: *mut ndk_sys::ANativeWindow,
}

unsafe impl Send for NativeWindow {}
unsafe impl Sync for NativeWindow {}

impl NativeWindow {
    pub unsafe fn from_ptr(ptr: *mut ndk_sys::ANativeWindow) -> anyhow::Result<Self> {
        if ptr.is_null() {
            return Err(anyhow!("ANativeWindow pointer is null"));
        }
        Ok(NativeWindow { inner: ptr })
    }
}

impl Drop for NativeWindow {
    fn drop(&mut self) {
        unsafe {
            ndk_sys::ANativeWindow_release(self.inner);
        }
    }
}

impl Clone for NativeWindow {
    fn clone(&self) -> Self {
        unsafe {
            ndk_sys::ANativeWindow_acquire(self.inner);
        }
        NativeWindow { inner: self.inner }
    }
}

#[allow(dead_code)]
pub(crate) struct VulkanSurface {
    jni_surface: crate::video::Surface,
    surface: Arc<VulkanSurfaceHandle>,
    native_window: NativeWindow,
    swapchain: Arc<VulkanSwapchain>,
    targets: Vec<Arc<VulkanSwapchainTaget>>,
    swapchain_device: Arc<ash::khr::swapchain::Device>,
    width: u32,
    height: u32,
    queue: vk::Queue,
    present_id: Mutex<u32>,
}

#[allow(dead_code)]
pub struct VulkanSwapchainTaget {
    pub swapchain: Arc<VulkanSwapchain>,
    pub framebuffer: VulkanFramebuffer,
    index: u32,
}

impl VulkanSurface {
    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }
    pub fn acquire_next_framebuffer(
        &self,
        semaphore: vk::Semaphore,
    ) -> Result<Option<Arc<VulkanSwapchainTaget>>> {
        match unsafe {
            self.swapchain_device.acquire_next_image(
                *self.swapchain.swapchain,
                u64::MAX,
                semaphore,
                vk::Fence::null(),
            )
        }
        .map(|(index, _is_swapchain_suboptimal)| self.targets[index as usize].clone())
        {
            Ok(ok) => Ok(Some(ok)),
            Err(err) => {
                if err == vk::Result::NOT_READY {
                    Ok(None)
                } else {
                    Err(anyhow!("Failed to acquire next image: {:?}", err))?
                }
            }
        }
    }

    pub fn present(
        &self,
        cx: &GlobalContext,
        target: Arc<VulkanSwapchainTaget>,
        semaphores: &[vk::Semaphore],
        timestamp_ns: u64,
    ) {
        let device = &cx.device;
        let swapchain_device = ash::khr::swapchain::Device::new(&cx.instance, device);

        let _lock = cx
            .present_queue_lock
            .lock()
            .map_err(|e| anyhow!("Failed to lock present queue"))
            .unwrap();
        let present_id = {
            let mut guard = self.present_id.lock().unwrap();
            let id = *guard;
            *guard += 1;
            id
        };

        unsafe {
            swapchain_device
                .queue_present(
                    self.queue,
                    &vk::PresentInfoKHR::default()
                        .wait_semaphores(semaphores)
                        .swapchains(&[*self.swapchain.swapchain])
                        .image_indices(&[target.index])
                        .push_next(
                            &mut vk::PresentTimesInfoGOOGLE::default().times(&[
                                vk::PresentTimeGOOGLE::default()
                                    .present_id(present_id)
                                    .desired_present_time(timestamp_ns),
                            ]),
                        ),
                )
                .unwrap();
        }
    }

    pub fn from_jni_surface(
        env: &mut JNIEnv<'_>,
        jni_surface: crate::video::Surface,
    ) -> Result<VulkanSurface> {
        let native_window = unsafe {
            ndk_sys::ANativeWindow_fromSurface(env.get_raw(), jni_surface.surface.as_obj().as_raw())
        };
        let native_window = unsafe { NativeWindow::from_ptr(native_window)? };
        let mut cx = CONTEXT
            .get()
            .unwrap()
            .lock()
            .map_err(|_e| anyhow!("Failed to get context"))?;

        let cx = &mut *cx;

        let instance = &cx.instance;
        let device = &cx.device;
        let android_surface_instance = &cx.android_surface_instance;
        let physical_device = cx.physical_device;
        let surface_instance = &cx.surface_instance;
        let swapchain_device = &cx.swapchain_device;

        let surface = Arc::new(VulkanSurfaceHandle::new(
            unsafe {
                android_surface_instance.create_android_surface(
                    &vk::AndroidSurfaceCreateInfoKHR::default()
                        .window(native_window.inner as *const _ as *mut _),
                    None,
                )
            }?,
            surface_instance.clone(),
        ));

        let queue = {
            // check queue family caps
            let present_queue = cx
                .present_queue_lock
                .lock()
                .map_err(|e| anyhow!("Failed to lock present queue"))?;
            let queue_family_props =
                unsafe { instance.get_physical_device_queue_family_properties(physical_device) };
            let family = queue_family_props
                .iter()
                .nth(present_queue.family_index as usize)
                .context("Invalid present queue family index")?;

            if !unsafe {
                surface_instance.get_physical_device_surface_support(
                    physical_device,
                    present_queue.family_index,
                    **surface,
                )
            }? {
                return Err(anyhow!(
                    "The specified present queue family does not support presentation"
                ));
            }
            unsafe {
                device.get_device_queue(present_queue.family_index, present_queue.queue_index)
            }
        };

        let surface_caps = unsafe {
            surface_instance.get_physical_device_surface_capabilities(physical_device, **surface)
        }
        .unwrap();

        // image count
        let desired_image_count = 5;
        let image_count = u32::min(
            surface_caps.max_image_count,
            u32::max(surface_caps.min_image_count, desired_image_count),
        );

        println!(
            "swapchain image count: {image_count} (min: {}, max: {})",
            surface_caps.min_image_count, surface_caps.max_image_count
        );

        // format
        let surface_formats = unsafe {
            surface_instance.get_physical_device_surface_formats(physical_device, **surface)
        }
        .unwrap();

        surface_formats
            .iter()
            .inspect(|f| {
                println!(
                    "surface format: {:?}, color space: {:?}",
                    f.format, f.color_space
                );
            })
            .count();

        let preferred_format = surface_formats.first();/*
            .iter()
            .fold(None, |state, current| match state {
                None => current.into(),
                Some(prev) => {
                    if prev.format != vk::Format::A2B10G10R10_UNORM_PACK32
                        && current.format == vk::Format::A2B10G10R10_UNORM_PACK32
                    {
                        current.into()
                    } else {
                        prev.into()
                    }
                }
            });
            */

        let preferred_format =
            preferred_format.context("Failed to find preferred surface format")?;

        println!(
            "selected surface format: {:?}, color space: {:?}",
            preferred_format.format, preferred_format.color_space
        );

        // pre transform
        let pre_transform = if surface_caps
            .supported_transforms
            .contains(vk::SurfaceTransformFlagsKHR::IDENTITY)
        {
            vk::SurfaceTransformFlagsKHR::IDENTITY
        } else {
            surface_caps.current_transform
        };

        let swapchain = VulkanSwapchainHandle::new(
            unsafe {
                swapchain_device.create_swapchain(
                    &vk::SwapchainCreateInfoKHR::default()
                        .surface(**surface)
                        .min_image_count(image_count)
                        .image_format(preferred_format.format)
                        .image_color_space(preferred_format.color_space)
                        .image_extent(surface_caps.current_extent)
                        .image_usage(vk::ImageUsageFlags::COLOR_ATTACHMENT |
                            vk::ImageUsageFlags::SAMPLED |
                            vk::ImageUsageFlags::TRANSFER_SRC |
                            vk::ImageUsageFlags::TRANSFER_DST |
                            vk::ImageUsageFlags::STORAGE |
                            vk::ImageUsageFlags::INPUT_ATTACHMENT,
                        )
                        .image_sharing_mode(vk::SharingMode::EXCLUSIVE)
                        .pre_transform(pre_transform)
                        .composite_alpha(vk::CompositeAlphaFlagsKHR::OPAQUE)
                        .present_mode(vk::PresentModeKHR::FIFO)
                        .clipped(true)
                        .image_array_layers(1)
                        .flags(vk::SwapchainCreateFlagsKHR::MUTABLE_FORMAT)
                    ,
                    None,
                )
            }?,
            swapchain_device.clone(),
        );

        // println!("unienc: swapchain created: {swapchain:?}");

        let swapchain = Arc::new(VulkanSwapchain {
            swapchain,
            surface: surface.clone(),
        });

        // setup images

        let width = unsafe { ndk_sys::ANativeWindow_getWidth(native_window.inner) } as u32;
        let height = unsafe { ndk_sys::ANativeWindow_getHeight(native_window.inner) } as u32;

        let targets = unsafe { swapchain_device.get_swapchain_images(*swapchain.swapchain) }?
            .iter()
            .enumerate()
            .map(|(index, image)| {
                let view = Arc::new(VulkanImageView {
                    image: Arc::new(VulkanImage::new_externally_bound(
                        VulkanImageHandle::new_external(*image),
                    )),
                    view: VulkanImageViewHandle::new(
                        unsafe {
                            device.create_image_view(
                                &vk::ImageViewCreateInfo::default()
                                    .image(*image)
                                    .view_type(vk::ImageViewType::TYPE_2D)
                                    .format(preferred_format.format)
                                    .components(
                                        vk::ComponentMapping::default()
                                            .r(vk::ComponentSwizzle::IDENTITY)
                                            .g(vk::ComponentSwizzle::IDENTITY)
                                            .b(vk::ComponentSwizzle::IDENTITY)
                                            .a(vk::ComponentSwizzle::IDENTITY),
                                    )
                                    .subresource_range(
                                        vk::ImageSubresourceRange::default()
                                            .aspect_mask(vk::ImageAspectFlags::COLOR)
                                            .base_mip_level(0)
                                            .level_count(1)
                                            .base_array_layer(0)
                                            .layer_count(1),
                                    ),
                                None,
                            )
                        }?,
                        device.clone(),
                    ),
                });

                Ok(Arc::new(VulkanSwapchainTaget {
                    framebuffer: VulkanFramebuffer {
                        framebuffer: VulkanFramebufferHandle::new(
                            unsafe {
                                device.create_framebuffer(
                                    &vk::FramebufferCreateInfo::default()
                                        .render_pass(*cx.render_pass.render_pass)
                                        .attachments(&[*view.view])
                                        .width(width)
                                        .height(height)
                                        .layers(1),
                                    None,
                                )
                            }?,
                            device.clone(),
                        ),
                        view,
                    },
                    index: index as u32,
                    swapchain: swapchain.clone(),
                }))
            })
            .collect::<Result<Vec<Arc<VulkanSwapchainTaget>>>>()?;

        Ok(VulkanSurface {
            jni_surface,
            surface,
            native_window: native_window.clone(),
            swapchain,
            targets,
            swapchain_device: swapchain_device.clone(),
            width,
            height,
            queue,
            present_id: Mutex::new(0),
        })
    }
}
