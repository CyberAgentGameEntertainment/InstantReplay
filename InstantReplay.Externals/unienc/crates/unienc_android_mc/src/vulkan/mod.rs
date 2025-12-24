pub mod hardware_buffer;
pub mod hardware_buffer_surface;
mod preprocess;
#[allow(dead_code)]
pub mod types;
mod utils;
mod format;

use ash::vk;
use std::fmt::Debug;
use std::future::Future;
use std::os::raw::c_int;
use std::sync::Arc;
use std::{
    os::raw::c_void,
    sync::{Mutex, OnceLock},
};
use crate::error::{AndroidError, Result, ResultExt};
use unity_native_plugin::graphics::{GfxDeviceEventType, UnityGraphics};
use unity_native_plugin::profiler::{
    BuiltinProfilerCategory, ProfilerCategoryId, ProfilerMarkerDesc, ProfilerMarkerEventType,
    ProfilerMarkerFlag, ProfilerMarkerFlags, UnityProfiler,
};
use unity_native_plugin_vulkan::vulkan::{
    UnityGraphicsVulkanV2, VulkanEventRenderPassPreCondition, VulkanGraphicsQueueAccess,
    VulkanPluginEventConfig,
};

use crate::vulkan::preprocess::PreprocessRenderPass;
use crate::vulkan::utils::FencePool;

static GRAPHICS: OnceLock<Mutex<UnityGraphics>> = OnceLock::new();
static CONTEXT: OnceLock<Mutex<GlobalContext>> = OnceLock::new();
pub static EVENT_ID: OnceLock<c_int> = OnceLock::new();
static MARKERS: OnceLock<Markers> = OnceLock::new();
static PROFILER: OnceLock<UnityProfiler> = OnceLock::new();

pub(crate) fn is_initialized() -> bool {
    CONTEXT.get().is_some()
}

pub(crate) struct GlobalContext {
    vulkan: UnityGraphicsVulkanV2,
    instance: ash::Instance,
    device: Arc<ash::Device>,
    render_pass: Arc<PreprocessRenderPass>,
    fence_pool: Arc<FencePool>,
}

#[derive(Debug)]
struct Markers {
    preprocess_blit: ProfilerMarkerDesc,
    preprocess_blit_resources: ProfilerMarkerDesc,
    preprocess_blit_commands: ProfilerMarkerDesc,
    preprocess_blit_submit: ProfilerMarkerDesc,
}

unsafe impl Sync for Markers {}

struct MarkerGuard<'a> {
    marker: &'a ProfilerMarkerDesc,
}

trait ProfilerMarkerDescExt {
    fn get(&'_ self) -> MarkerGuard<'_>;
}

impl ProfilerMarkerDescExt for ProfilerMarkerDesc {
    fn get(&'_ self) -> MarkerGuard<'_> {
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

pub(crate) fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
    println!("unienc: unity_plugin_load");
    let graphics = interfaces.interface::<UnityGraphics>().unwrap();
    let profiler = interfaces.interface::<UnityProfiler>().unwrap();
    if profiler.is_available() {
        _ = PROFILER.set(profiler);
        MARKERS
            .set(Markers {
                preprocess_blit: profiler
                    .create_marker(
                        c"unienc_android_mc::vulkan::preprocess::blit",
                        BuiltinProfilerCategory::Other as ProfilerCategoryId,
                        ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                        0,
                    )
                    .unwrap(),
                preprocess_blit_resources: profiler
                    .create_marker(
                        c"unienc_android_mc::vulkan::preprocess::blit::resources",
                        BuiltinProfilerCategory::Other as ProfilerCategoryId,
                        ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                        0,
                    )
                    .unwrap(),
                preprocess_blit_commands: profiler
                    .create_marker(
                        c"unienc_android_mc::vulkan::preprocess::blit::commands",
                        BuiltinProfilerCategory::Other as ProfilerCategoryId,
                        ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                        0,
                    )
                    .unwrap(),
                preprocess_blit_submit: profiler
                    .create_marker(
                        c"unienc_android_mc::vulkan::preprocess::blit::submit",
                        BuiltinProfilerCategory::Other as ProfilerCategoryId,
                        ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                        0,
                    )
                    .unwrap(),
            })
            .unwrap();
    }

    GRAPHICS
        .set(Mutex::new(graphics))
        .map_err(|_| AndroidError::GlobalStateSetFailed)
        .unwrap();

    graphics.register_device_event_callback(Some(on_device_event));
}

extern "system" fn on_device_event(ev_type: GfxDeviceEventType) {
    println!("unienc: on_device_event {ev_type:?}");
    match ev_type {
        GfxDeviceEventType::Initialize => {
            let graphics = GRAPHICS.get().unwrap().lock().unwrap();
            let renderer = graphics.renderer();
            println!("unienc: {renderer:?}");

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

            let queue_family_index = unity_instance.queue_family_index();

            let render_pass = preprocess::create_pass(device.clone(), queue_family_index)
                .context("Failed to create pipeline")
                .unwrap();

            CONTEXT
                .set(Mutex::new(GlobalContext {
                    vulkan,
                    device: device.clone(),
                    instance,
                    render_pass: Arc::new(render_pass),
                    fence_pool: Arc::new(FencePool::new(device)),
                }))
                .map_err(|_| AndroidError::GlobalStateSetFailed)
                .unwrap();
        }
        GfxDeviceEventType::Shutdown => {}
        GfxDeviceEventType::BeforeReset => {}
        GfxDeviceEventType::AfterReset => {}
    }
}

pub fn blit_to_hardware_buffer(
    src: &vk::Image,
    src_width: u32,
    src_height: u32,
    src_graphics_format: u32,
    flip_vertically: bool,
    is_gamma_workflow: bool,
    frame: &hardware_buffer_surface::HardwareBufferFrame,
) -> Result<impl Future<Output = Result<()>> + use<>> {
    let cx = crate::vulkan::CONTEXT
        .get()
        .ok_or(AndroidError::ContextNotInitialized)?
        .lock()
        .map_err(|_| AndroidError::MutexPoisoned)?;

    preprocess::blit_to_hardware_buffer(&cx, src, src_width, src_height, src_graphics_format, flip_vertically, is_gamma_workflow, frame)
}
