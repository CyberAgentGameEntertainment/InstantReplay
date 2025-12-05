use crate::vulkan::types::{VulkanFramebuffer, VulkanFramebufferHandle, VulkanImage, VulkanImageHandle, VulkanImageView, VulkanImageViewHandle, VulkanSurfaceHandle, VulkanSwapchain, VulkanSwapchainHandle};
use crate::vulkan::CONTEXT;
use anyhow::{anyhow, Context, Result};
use ash::vk;
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
        Ok(NativeWindow {
            inner: ptr as *mut ndk_sys::ANativeWindow,
        })
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

pub(crate) struct VulkanSurface {
    jni_surface: crate::video::Surface,
    surface: Arc<VulkanSurfaceHandle>,
    native_window: NativeWindow,
    swapchain: Arc<VulkanSwapchain>,
    queue: Arc<Mutex<vk::Queue>>,
    targets: Vec<Arc<VulkanSwapchainTaget>>,
    swapchain_device: Arc<ash::khr::swapchain::Device>,
    width: u32,
    height: u32,
}

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
    ) -> Result<Arc<VulkanSwapchainTaget>> {
        let (index, _is_swapchain_suboptimal) = unsafe {
            self.swapchain_device.acquire_next_image(
                *self.swapchain.swapchain,
                u64::MAX,
                semaphore,
                vk::Fence::null(),
            )
        }?;

        Ok(self.targets[index as usize].clone())
    }

    pub fn present(
        &self,
        target: Arc<VulkanSwapchainTaget>,
        semaphores: &[vk::Semaphore],
        timestamp_ns: u64,
    ) {
        let cx = CONTEXT.get().unwrap().lock().unwrap();
        let device = &cx.device;
        let swapchain_device = ash::khr::swapchain::Device::new(&cx.instance, device);

        let queue = self.queue.lock().unwrap();

        unsafe {
            swapchain_device
                .queue_present(*queue, &vk::PresentInfoKHR::default()
                    .wait_semaphores(semaphores)
                    .swapchains(&[*self.swapchain.swapchain])
                    .image_indices(&[target.index])
                    .push_next(
                        &mut vk::PresentTimesInfoGOOGLE::default()
                            .times(&[vk::PresentTimeGOOGLE::default().desired_present_time(timestamp_ns)]),
                    ))
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
        let mut cx = CONTEXT.get().unwrap().lock().map_err(|_e| anyhow!("Failed to get context"))?;

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
            }
            .unwrap(),
            surface_instance.clone(),
        ));

        // create presentation queue
        let queue_family_props =
            unsafe { instance.get_physical_device_queue_family_properties(physical_device) };

        let mut presentation_queue = None;
        for (i, _queue_family_prop) in queue_family_props.iter().enumerate() {
            let queue_family_index = i as u32;
            if let Some(weak) = cx.surface_context.queues.get(&queue_family_index) {
                if let Some(queue) = weak.upgrade() {
                    if unsafe {
                        surface_instance.get_physical_device_surface_support(
                            physical_device,
                            i as u32,
                            **surface,
                        )
                    }? {
                        presentation_queue = Some(queue);
                    }
                }
            }
        }

        for (i, _queue_family_prop) in queue_family_props.iter().enumerate() {
            let queue_family_index = i as u32;
            if let Some(weak) = cx.surface_context.queues.get(&queue_family_index) {
                if let Some(_queue) = weak.upgrade() {
                    continue;
                }
            }

            if unsafe {
                surface_instance.get_physical_device_surface_support(
                    physical_device,
                    i as u32,
                    **surface,
                )
            }? {
                // NOTE: Assumes that unity uses queue #0 for both graphics and presentation. Can this be made more safe using vulkan interceptors?
                let queue_index = if queue_family_index == cx.queue_family_index {
                    // get queue count
                    let props = unsafe {
                        instance.get_physical_device_queue_family_properties(physical_device)
                    }[i];

                    if props.queue_count < 2 {
                        continue;
                    }
                    1
                } else {
                    0
                };
                let queue = Arc::new(Mutex::new(unsafe {
                    device.get_device_queue(queue_family_index, queue_index)
                }));
                cx.surface_context
                    .queues
                    .insert(queue_family_index, Arc::downgrade(&queue));
                presentation_queue = queue.into();
            }
        }

        let presentation_queue =
            presentation_queue.context(anyhow!("Failed to find presentation queue"))?;

        let surface_caps = unsafe {
            surface_instance.get_physical_device_surface_capabilities(physical_device, **surface)
        }
        .unwrap();

        // image count
        let desired_image_count = 3;
        let image_count = u32::min(
            surface_caps.max_image_count,
            u32::max(surface_caps.min_image_count, desired_image_count),
        );

        // format
        let surface_format = unsafe {
            surface_instance.get_physical_device_surface_formats(physical_device, **surface)
        }
        .unwrap()
        .first()
        .copied()
        .unwrap();

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
                        .image_format(surface_format.format)
                        .image_color_space(surface_format.color_space)
                        .image_extent(surface_caps.current_extent)
                        .image_usage(vk::ImageUsageFlags::COLOR_ATTACHMENT)
                        .image_sharing_mode(vk::SharingMode::EXCLUSIVE)
                        .pre_transform(pre_transform)
                        .composite_alpha(vk::CompositeAlphaFlagsKHR::OPAQUE)
                        .present_mode(vk::PresentModeKHR::FIFO)
                        .clipped(true)
                        .image_array_layers(1),
                    None,
                )
            }
            .unwrap(),
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
                                    .format(surface_format.format)
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
                    swapchain: swapchain.clone()
                }))
            })
            .collect::<Result<Vec<Arc<VulkanSwapchainTaget>>>>()?;

        Ok(VulkanSurface {
            jni_surface,
            surface,
            native_window: native_window.clone(),
            swapchain,
            queue: presentation_queue,
            targets,
            swapchain_device: swapchain_device.clone(),
            width,
            height,
        })
    }
}
