mod preprocess;
pub mod presentation;
pub mod types;
mod utils;

use anyhow::{anyhow, Context, Result};
use ash::{vk, Entry};
use std::collections::HashMap;
use std::ffi::c_uint;
use std::future::Future;
use std::os::raw::c_int;
use std::sync::{Arc, Weak};
use std::{
    os::raw::c_void,
    sync::{Mutex, OnceLock},
};
use unity_native_plugin::enums::RenderingExtEventType;
use unity_native_plugin::graphics::{GfxDeviceEventType, UnityGraphics};
use unity_native_plugin_vulkan::vulkan::{
    UnityGraphicsVulkanV2, VulkanEventRenderPassPreCondition, VulkanGraphicsQueueAccess,
    VulkanPluginEventConfig,
};

use utils::*;

use crate::vulkan::preprocess::PreprocessRenderPass;
use crate::vulkan::presentation::VulkanSurface;

static GRAPHICS: OnceLock<Mutex<UnityGraphics>> = OnceLock::new();
static CONTEXT: OnceLock<Mutex<GlobalContext>> = OnceLock::new();
pub static EVENT_ID: OnceLock<c_int> = OnceLock::new();

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
    surface_context: SurfaceContext,
    swapchain_device: Arc<ash::khr::swapchain::Device>,
}

struct SurfaceContext {
    queues: HashMap<u32 /* family_index */, Weak<Mutex<vk::Queue>>>,
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

            let event_id = graphics.reserve_event_id_range(1);

            EVENT_ID.set(event_id).unwrap();
            println!("unienc: reserved event id {event_id}");

            if renderer != unity_native_plugin::graphics::GfxRenderer::Vulkan {
                return;
            }
            let interfaces = unity_native_plugin::interface::UnityInterfaces::get();
            let vulkan = interfaces.interface::<UnityGraphicsVulkanV2>().unwrap();
            let unity_instance = vulkan.instance();
            let instance = unity_instance.instance();
            let device = unity_instance.device();
            let physical_device = unity_instance.physical_device();

            vulkan.configure_event(
                RenderingExtEventType::UserEventsStart as i32,
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

            let surface_context = SurfaceContext {
                queues: HashMap::new(),
            };

            CONTEXT
                .set(Mutex::new(GlobalContext {
                    vulkan,
                    device,
                    instance,
                    android_surface_instance,
                    physical_device,
                    surface_instance,
                    swapchain_device,
                    render_pass: Arc::new(render_pass),
                    queue_family_index,
                    surface_context,
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
) -> Result<impl Future<Output = Result<()>>> {
    let cx = crate::vulkan::CONTEXT
        .get()
        .context("Failed to get context")?
        .lock()
        .map_err(|_| anyhow!("Failed to get context"))?;

    preprocess::blit(&cx, src, surface, timestamp_ns)
}
