mod preprocess;
pub mod presentation;
#[allow(dead_code)]
pub mod types;
mod utils;

use anyhow::{anyhow, Context, Result};
use ash::vk::{Fence, PFN_vkCreateDevice, PFN_vkVoidFunction};
use ash::{vk, Entry};
use std::collections::{HashMap, VecDeque};
use std::ffi::{c_char, c_uint, CStr};
use std::fmt::Debug;
use std::future::Future;
use std::ops::DerefMut;
use std::os::raw::c_int;
use std::sync::{Arc, RwLock, Weak};
use std::{
    os::raw::c_void,
    sync::{Mutex, OnceLock},
};
use unity_native_plugin::enums::RenderingExtEventType;
use unity_native_plugin::graphics::{GfxDeviceEventType, UnityGraphics};
use unity_native_plugin::profiler::{BuiltinProfilerCategory, ProfilerCategoryId, ProfilerMarkerDesc, ProfilerMarkerEventType, ProfilerMarkerFlag, ProfilerMarkerFlags, UnityProfiler};
use unity_native_plugin_vulkan::vulkan::{
    UnityGraphicsVulkanV2, VulkanEventRenderPassPreCondition, VulkanGraphicsQueueAccess,
    VulkanPluginEventConfig,
};

use crate::vulkan::preprocess::PreprocessRenderPass;
use crate::vulkan::presentation::VulkanSurface;
use crate::vulkan::types::VulkanSemaphoreHandle;
use crate::vulkan::utils::{FencePool, SemaphorePool};

static GRAPHICS: OnceLock<Mutex<UnityGraphics>> = OnceLock::new();
static CONTEXT: OnceLock<Mutex<GlobalContext>> = OnceLock::new();
pub static EVENT_ID: OnceLock<c_int> = OnceLock::new();
static GET_INSTANCE_PROC_ADDR: OnceLock<vk::PFN_vkGetInstanceProcAddr> = OnceLock::new();
static PRESENT_QUEUE_INFO: OnceLock<QueueInfo> = OnceLock::new();
static CREATE_DEVICE: OnceLock<vk::PFN_vkCreateDevice> = OnceLock::new();
static MARKERS: OnceLock<Markers> = OnceLock::new();
static PROFILER: OnceLock<UnityProfiler> = OnceLock::new();

#[derive(Debug, Clone, Copy)]
struct QueueInfo {
    family_index: u32,
    queue_index: u32,
}

pub(crate) fn is_initialized() -> bool {
    CONTEXT.get().is_some()
}

struct GlobalContext {
    vulkan: UnityGraphicsVulkanV2,
    instance: ash::Instance,
    device: Arc<ash::Device>,
    android_surface_instance: ash::khr::android_surface::Instance,
    physical_device: ash::vk::PhysicalDevice,
    surface_instance: Arc<ash::khr::surface::Instance>,
    render_pass: Arc<PreprocessRenderPass>,
    queue_family_index: c_uint,
    swapchain_device: Arc<ash::khr::swapchain::Device>,
    present_queue_lock: Arc<Mutex<QueueInfo>>,
    semaphore_pool: Arc<SemaphorePool>,
    fence_pool: Arc<FencePool>,
}

#[derive(Debug)]
struct Markers {
    preprocess_blit: ProfilerMarkerDesc,
    preprocess_blit_acquire: ProfilerMarkerDesc,
    preprocess_blit_resources: ProfilerMarkerDesc,
    preprocess_blit_commands: ProfilerMarkerDesc,
    preprocess_blit_submit: ProfilerMarkerDesc,
    preprocess_blit_present: ProfilerMarkerDesc,
}

unsafe impl Sync for Markers {}

struct MarkerGuard<'a> {
    marker: &'a ProfilerMarkerDesc,
}

trait ProfilerMarkerDescExt {
    fn get(&self) -> MarkerGuard;
}

impl ProfilerMarkerDescExt for ProfilerMarkerDesc {
    fn get(&self) -> MarkerGuard {
        if let Some(profiler) = PROFILER.get() {
            profiler.emit_event(self, ProfilerMarkerEventType::Begin, &[]);
        }
        MarkerGuard { marker: self }
    }
}

impl<'a> Drop for MarkerGuard<'a> {
    fn drop(&mut self) {
        if let Some(profiler) = PROFILER.get() {
            profiler.emit_event(self.marker, ProfilerMarkerEventType::End, &[]);
        }
    }
}

mod entry_points {
    unity_native_plugin::unity_native_plugin_entry_point! {
        fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
            super::unity_plugin_load(interfaces);
        }
        fn unity_plugin_unload() {
            super::unity_plugin_unload();
        }
    }
}

fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
    println!("unienc: unity_plugin_load");
    let graphics = interfaces.interface::<UnityGraphics>().unwrap();
    let profiler = interfaces.interface::<UnityProfiler>().unwrap();
    if profiler.is_available() {
        _ = PROFILER.set(profiler);
        MARKERS.set(
            Markers {
                preprocess_blit: profiler.create_marker(c"unienc_android_mc::vulkan::preprocess::blit", BuiltinProfilerCategory::Other as ProfilerCategoryId, ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default), 0).unwrap(),
                preprocess_blit_acquire: profiler.create_marker(c"unienc_android_mc::vulkan::preprocess::blit::acquire", BuiltinProfilerCategory::Other as ProfilerCategoryId, ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default), 0).unwrap(),
                preprocess_blit_resources: profiler.create_marker(c"unienc_android_mc::vulkan::preprocess::blit::resources", BuiltinProfilerCategory::Other as ProfilerCategoryId, ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default), 0).unwrap(),
                preprocess_blit_commands: profiler.create_marker(c"unienc_android_mc::vulkan::preprocess::blit::commands", BuiltinProfilerCategory::Other as ProfilerCategoryId, ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default), 0).unwrap(),
                preprocess_blit_submit: profiler.create_marker(c"unienc_android_mc::vulkan::preprocess::blit::submit", BuiltinProfilerCategory::Other as ProfilerCategoryId, ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default), 0).unwrap(),
                preprocess_blit_present: profiler.create_marker(c"unienc_android_mc::vulkan::preprocess::blit::present", BuiltinProfilerCategory::Other as ProfilerCategoryId, ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default), 0).unwrap(),
            }
        ).unwrap();
    }

    GRAPHICS
        .set(Mutex::new(graphics))
        .map_err(|_e| anyhow!("Failed to set graphics"))
        .unwrap();

    graphics.register_device_event_callback(Some(on_device_event));
}
fn unity_plugin_unload() {}

extern "system" fn on_device_event(ev_type: GfxDeviceEventType) {
    println!("unienc: on_device_event {ev_type:?}");
    match ev_type {
        unity_native_plugin::graphics::GfxDeviceEventType::Initialize => {
            let graphics = GRAPHICS.get().unwrap().lock().unwrap();
            let renderer = graphics.renderer();
            println!("unienc: {renderer:?}");

            if renderer == unity_native_plugin::graphics::GfxRenderer::Null {
                // interceptor
                let interfaces = unity_native_plugin::interface::UnityInterfaces::get();
                let Some(vulkan) = interfaces.interface::<UnityGraphicsVulkanV2>() else {
                    return;
                };

                unsafe extern "system" fn create_device_custom(
                    physical_device: vk::PhysicalDevice,
                    p_create_info: *const vk::DeviceCreateInfo<'_>,
                    p_allocator: *const vk::AllocationCallbacks<'_>,
                    p_device: *mut vk::Device,
                ) -> vk::Result {
                    println!("unienc: create_device_custom called");

                    let mut create_info = *p_create_info;

                    assert!(create_info.queue_create_info_count >= 1);

                    let mut queue_create_infos = std::slice::from_raw_parts(
                        create_info.p_queue_create_infos,
                        create_info.queue_create_info_count as usize,
                    )
                    .to_vec();

                    let queue_family_index = queue_create_infos[0].queue_family_index;
                    let queue_index = queue_create_infos[0].queue_count;
                    queue_create_infos[0].queue_count += 1;

                    let res = CREATE_DEVICE.get().unwrap()(
                        physical_device,
                        &create_info.queue_create_infos(&queue_create_infos) as *const _,
                        p_allocator,
                        p_device,
                    );

                    if res == vk::Result::SUCCESS {
                        PRESENT_QUEUE_INFO
                            .set(QueueInfo {
                                family_index: queue_family_index,
                                queue_index,
                            })
                            .unwrap();
                    }

                    res
                }

                unsafe extern "system" fn get_instance_proc_addr_custom(
                    instance: vk::Instance,
                    p_name: *const c_char,
                ) -> PFN_vkVoidFunction {
                    let get_instance_proc_addr = GET_INSTANCE_PROC_ADDR.get().unwrap();

                    if CStr::from_ptr(p_name) == c"vkCreateDevice"
                    {
                        CREATE_DEVICE.get_or_init(move || {
                            std::mem::transmute(get_instance_proc_addr(instance, p_name))
                        });
                        let ptr: vk::PFN_vkCreateDevice = create_device_custom;
                        return Some(std::mem::transmute(ptr));
                    }

                    get_instance_proc_addr(instance, p_name)
                }

                unsafe extern "system" fn on_initialization(
                    get_instance_proc_addr: vk::PFN_vkGetInstanceProcAddr,
                    user_data: *mut c_void,
                ) -> vk::PFN_vkGetInstanceProcAddr {
                    GET_INSTANCE_PROC_ADDR.set(get_instance_proc_addr).unwrap();
                    get_instance_proc_addr_custom
                }

                unsafe {
                    vulkan.add_intercept_initialization(
                        Some(on_initialization),
                        std::ptr::null_mut(),
                        0,
                    )
                };
                return;
            }

            if renderer != unity_native_plugin::graphics::GfxRenderer::Vulkan {
                return;
            }

            let event_id = graphics.reserve_event_id_range(1);

            EVENT_ID.set(event_id).unwrap();
            println!("unienc: reserved event id {event_id}");

            let interfaces = unity_native_plugin::interface::UnityInterfaces::get();
            let vulkan = interfaces.interface::<UnityGraphicsVulkanV2>().unwrap();
            let unity_instance = vulkan.instance();
            let instance = unity_instance.instance();
            let device = unity_instance.device();
            let physical_device = unity_instance.physical_device();

            let present_queue = PRESENT_QUEUE_INFO.get().cloned().unwrap();
            println!("unienc: present queue: {present_queue:?}");

            vulkan.configure_event(
                event_id,
                &VulkanPluginEventConfig::new(
                    VulkanEventRenderPassPreCondition::EnsureOutside,
                    VulkanGraphicsQueueAccess::Allow,
                    8,
                ),
            );

            let instance = unsafe {
                ash::Instance::load(
                    &ash::StaticFn::load(|name| {
                        unity_instance
                            .get_instance_proc_addr(name.as_ptr())
                            .map(|p| p as *const c_void)
                            .unwrap_or_default()
                    }),
                    instance,
                )
            };
            let device = Arc::new(unsafe {
                ash::Device::load(
                    &ash::InstanceFnV1_0::load(|name| {
                        unity_instance
                            .get_instance_proc_addr(name.as_ptr())
                            .map(|p| p as *const c_void)
                            .unwrap_or_default()
                    }),
                    device,
                )
            });

            let entry = unsafe {
                Entry::from_static_fn(ash::StaticFn::load(|name| {
                    unity_instance
                        .get_instance_proc_addr(name.as_ptr())
                        .map(|p| p as *const c_void)
                        .unwrap_or_default()
                }))
            };
            let android_surface_instance =
                ash::khr::android_surface::Instance::new(&entry, &instance);

            let surface_instance = Arc::new(ash::khr::surface::Instance::new(&entry, &instance));
            let queue_family_index = unity_instance.queue_family_index();

            let render_pass = preprocess::create_pass(device.clone(), queue_family_index)
                .context("Failed to create pipeline")
                .unwrap();

            let swapchain_device = Arc::new(ash::khr::swapchain::Device::new(&instance, &device));

            CONTEXT
                .set(Mutex::new(GlobalContext {
                    vulkan,
                    device: device.clone(),
                    instance,
                    android_surface_instance,
                    physical_device,
                    surface_instance,
                    swapchain_device,
                    render_pass: Arc::new(render_pass),
                    queue_family_index,
                    present_queue_lock: Arc::new(Mutex::new(present_queue)),
                    semaphore_pool: Arc::new(SemaphorePool::new(device.clone())),
                    fence_pool: Arc::new(FencePool::new(device)),
                }))
                .map_err(|_e| anyhow!("Failed to set metal"))
                .unwrap();
        }
        unity_native_plugin::graphics::GfxDeviceEventType::Shutdown => {}
        unity_native_plugin::graphics::GfxDeviceEventType::BeforeReset => {}
        unity_native_plugin::graphics::GfxDeviceEventType::AfterReset => {}
    }
}

pub fn blit(
    src: &vk::Image,
    surface: &VulkanSurface,
    timestamp_ns: u64,
) -> Result<Option<impl Future<Output = Result<()>>>> {
    let cx = crate::vulkan::CONTEXT
        .get()
        .context("Failed to get context")?
        .lock()
        .map_err(|_| anyhow!("Failed to get context"))?;

    preprocess::blit(&cx, src, surface, timestamp_ns)
}
