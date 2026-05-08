use crate::allocator;
use block2::RcBlock;
use objc2::{rc::Retained, runtime::ProtocolObject};
use objc2_core_foundation::{CFDictionary, CFNumber, CFString, CFType, kCFBooleanTrue};
use objc2_core_video::{
    CVMetalTexture, CVMetalTextureCache, CVMetalTextureGetTexture, CVPixelBuffer,
    CVPixelBufferPool, kCVPixelBufferHeightKey, kCVPixelBufferMetalCompatibilityKey,
    kCVPixelBufferPixelFormatTypeKey, kCVPixelBufferPoolAllocationThresholdKey,
    kCVPixelBufferPoolMaximumBufferAgeKey, kCVPixelBufferPoolMinimumBufferCountKey,
    kCVPixelBufferWidthKey, kCVPixelFormatType_32BGRA,
};
use objc2_foundation::NSString;
use objc2_metal::{
    MTLBuffer, MTLCommandBuffer, MTLCommandEncoder, MTLCommandQueue, MTLCullMode, MTLDevice,
    MTLIndexType, MTLLibrary, MTLPixelFormat, MTLPrimitiveType, MTLRenderCommandEncoder,
    MTLRenderPassColorAttachmentDescriptor, MTLRenderPassDescriptor,
    MTLRenderPipelineColorAttachmentDescriptor, MTLRenderPipelineDescriptor,
    MTLRenderPipelineState, MTLResourceOptions, MTLSamplerAddressMode, MTLSamplerDescriptor,
    MTLSamplerMinMagFilter, MTLSamplerMipFilter, MTLSamplerState, MTLTexture,
    MTLVertexAttributeDescriptor, MTLVertexBufferLayoutDescriptor, MTLVertexDescriptor,
    MTLVertexFormat, MTLVertexStepFunction,
};
use std::os::raw::c_int;
use std::{
    cell::{Cell, RefCell},
    future::Future,
    ptr::NonNull,
    sync::{Arc, Mutex, OnceLock},
};
use tokio::sync::oneshot;
use unity_native_plugin::{
    graphics::{GfxDeviceEventType, UnityGraphics, IUnityGraphics},
    metal::{
        UnityGraphicsMetalV1Interface, UnityGraphicsMetalV2, UnityGraphicsMetalV2Interface,
    },
    profiler::{
        BuiltinProfilerCategory, ProfilerCategoryId, ProfilerMarkerDesc, ProfilerMarkerEventType,
        ProfilerMarkerFlag, ProfilerMarkerFlags, UnityProfiler,
    },
};
use unity_native_plugin::profiler::IUnityProfiler;
use crate::error::{AppleError, OsStatusExt, Result};

use crate::common::UnsafeSendRetained;

static GRAPHICS: OnceLock<Mutex<UnityGraphics>> = OnceLock::new();
static CONTEXT: OnceLock<Mutex<GlobalContext>> = OnceLock::new();
pub static EVENT_ID: OnceLock<c_int> = OnceLock::new();
static MARKERS: OnceLock<Markers> = OnceLock::new();
static PROFILER: OnceLock<UnityProfiler> = OnceLock::new();

#[derive(Debug)]
struct Markers {
    custom_blit: ProfilerMarkerDesc,
    custom_blit_resources: ProfilerMarkerDesc,
    custom_blit_resources_shared_texture: ProfilerMarkerDesc,
    custom_blit_resources_pixel_buffer_create: ProfilerMarkerDesc,
    custom_blit_resources_metal_texture_create: ProfilerMarkerDesc,
    custom_blit_resources_commit_unity: ProfilerMarkerDesc,
    custom_blit_resources_command_buffer: ProfilerMarkerDesc,
    custom_blit_commands: ProfilerMarkerDesc,
    custom_blit_commands_encoder_create: ProfilerMarkerDesc,
    custom_blit_commands_vert_uniforms: ProfilerMarkerDesc,
    custom_blit_commands_record: ProfilerMarkerDesc,
    custom_blit_commands_end_encoding: ProfilerMarkerDesc,
    custom_blit_commands_completion_handler: ProfilerMarkerDesc,
    custom_blit_submit: ProfilerMarkerDesc,
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

pub(crate) fn is_initialized() -> bool {
    CONTEXT.get().is_some()
}

struct GlobalContext {
    metal: UnityGraphicsMetalV2,
    pipeline_state: Retained<ProtocolObject<dyn MTLRenderPipelineState>>,
    pipeline_state_srgb: Retained<ProtocolObject<dyn MTLRenderPipelineState>>,
    vertices: UnsafeSendRetained<ProtocolObject<dyn MTLBuffer>>,
    indices: UnsafeSendRetained<ProtocolObject<dyn MTLBuffer>>,
    // The blit's sampler is invariant across frames; build once and reuse.
    sampler_state: Retained<ProtocolObject<dyn MTLSamplerState>>,
    // The render pass descriptor is reused across frames; per-frame we only
    // mutate its colorAttachment[0]'s texture binding via objectAtIndexedSubscript.
    // (Note: setObject:atIndexedSubscript: copies the input, so we must mutate
    // the descriptor's own colorAttachment in place rather than caching a
    // standalone MTLRenderPassColorAttachmentDescriptor.)
    render_pass_descriptor: UnsafeSendRetained<MTLRenderPassDescriptor>,
    // CVMetalTextureCache is tied to the MTLDevice and expensive to create,
    // so reuse one per device for the entire plugin lifetime. Pooling
    // CVPixelBuffers (below) keeps the cache contents bounded, so explicit
    // flushes are unnecessary.
    texture_cache: UnsafeSendRetained<CVMetalTextureCache>,
    // CVPixelBuffer + CVMetalTexture creation per-frame is the dominant
    // cost of custom_blit. A CVPixelBufferPool lets us recycle buffers,
    // which in turn lets CVMetalTextureCache return pre-mapped textures
    // for cache hits. Lazy-initialized; recreated when dst dimensions change.
    pixel_buffer_pool: Option<PixelBufferPoolEntry>,
}

struct PixelBufferPoolEntry {
    width: u32,
    height: u32,
    pool: UnsafeSendRetained<CVPixelBufferPool>,
}

pub(crate) fn unity_plugin_load(interfaces: &unity_native_plugin::interface::UnityInterfaces) {
    println!("unienc: unity_plugin_load");
    let graphics = interfaces.interface::<UnityGraphics>().unwrap();

    if let Some(profiler) = interfaces.interface::<UnityProfiler>()
        && profiler.is_available()
    {
        _ = PROFILER.set(profiler);
        _ = MARKERS.set(Markers {
            custom_blit: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_resources: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::resources",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_resources_shared_texture: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::resources::shared_texture",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_resources_pixel_buffer_create: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::resources::pixel_buffer_create",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_resources_metal_texture_create: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::resources::metal_texture_create",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_resources_commit_unity: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::resources::commit_unity",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_resources_command_buffer: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::resources::command_buffer",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_commands: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::commands",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_commands_encoder_create: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::commands::encoder_create",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_commands_vert_uniforms: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::commands::vert_uniforms",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_commands_record: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::commands::record",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_commands_end_encoding: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::commands::end_encoding",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_commands_completion_handler: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::commands::completion_handler",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
            custom_blit_submit: profiler
                .create_marker(
                    c"unienc_apple_vt::metal::custom_blit::submit",
                    BuiltinProfilerCategory::Other as ProfilerCategoryId,
                    ProfilerMarkerFlags::new(ProfilerMarkerFlag::Default),
                    0,
                )
                .unwrap(),
        });
    }

    GRAPHICS
        .set(Mutex::new(graphics))
        .map_err(|_e| AppleError::GlobalStateSetFailed)
        .unwrap();

    graphics.register_device_event_callback(Some(on_device_event));

    // "load on startup" is unreliable
    on_device_event(GfxDeviceEventType::Initialize);
}

#[repr(C)]
struct VertexUniforms {
    scale_and_tiling: [f32; 4],
}

extern "system" fn on_device_event(ev_type: GfxDeviceEventType) {
    println!("unienc: on_device_event {ev_type:?}");
    match ev_type {
        unity_native_plugin::graphics::GfxDeviceEventType::Initialize => {
            let graphics = GRAPHICS.get().unwrap().lock().unwrap();
            let renderer = graphics.renderer();

            if renderer == unity_native_plugin::graphics::GfxRenderer::Metal {
                if CONTEXT.get().is_some() {
                    // already initialized
                    return;
                }

                let event_id = graphics.reserve_event_id_range(1);

                EVENT_ID.set(event_id).unwrap();
                println!("unienc: reserved event id {event_id}");

                let interfaces = unity_native_plugin::interface::UnityInterfaces::get();
                let metal = interfaces.interface::<UnityGraphicsMetalV2>().unwrap();
                let device = metal.metal_device().unwrap();
                let library = device
                    .newLibraryWithSource_options_error(
                        &NSString::from_str(
                            "
#include <metal_stdlib>
using namespace metal;

struct VertexUniforms {
    float4 scaleAndTiling;
};

struct VertexIn {
    float4 position [[attribute(0)]];
    float2 uv [[attribute(1)]];
};

struct VertexOut {
    float4 position [[position]];
    float2 uv;
};
struct FShaderOutput
{
    half4 frag_data [[color(0)]];
};

vertex VertexOut vertex_main(const VertexIn in [[stage_in]],
                             constant VertexUniforms &uniforms [[buffer(1)]])
{
    VertexOut out;
    out.position = in.position;
    out.uv = in.uv * uniforms.scaleAndTiling.xy + uniforms.scaleAndTiling.zw;

    return out;
}

fragment FShaderOutput fragment_main(VertexOut in [[stage_in]],
                             texture2d<half> mainTex [[texture(0)]],
                             sampler mainSampler [[sampler(0)]])
{
    bool isInside = all(in.uv >= 0.0h) && all(in.uv <= 1.0h);
    FShaderOutput out = { isInside ? mainTex.sample(mainSampler, in.uv) : half4(0.0h) };
    return out;
}

                                ",
                        ),
                        None,
                    )
                    .unwrap();

                // single triangle for full screen blit
                // pos.x pos.y pox.z pos.w uv.x uv.y
                let vertices = &[
                    -1.0f32, -1.0, 0.0, 1.0, 0.0, 1.0, // bottom left
                    3.0, -1.0, 0.0, 1.0, 2.0, 1.0, // bottom right
                    -1.0, 3.0, 0.0, 1.0, 0.0, -1.0, // top left
                ];
                let indices = &[0u16, 1, 2];
                let vertices = unsafe {
                    device.newBufferWithBytes_length_options(
                        NonNull::new(vertices.as_ptr() as *mut _).unwrap(),
                        vertices.len() * std::mem::size_of::<f32>(),
                        MTLResourceOptions::CPUCacheModeWriteCombined,
                    )
                }
                .unwrap();
                let indices = unsafe {
                    device.newBufferWithBytes_length_options(
                        NonNull::new(indices.as_ptr() as *mut _).unwrap(),
                        indices.len() * std::mem::size_of::<u16>(),
                        MTLResourceOptions::CPUCacheModeWriteCombined,
                    )
                }
                .unwrap();

                let pos_desc = MTLVertexAttributeDescriptor::new();
                pos_desc.setFormat(MTLVertexFormat::Float4);
                unsafe { pos_desc.setOffset(0) };

                let uv_desc = MTLVertexAttributeDescriptor::new();
                uv_desc.setFormat(MTLVertexFormat::Float2);
                unsafe { uv_desc.setOffset(16) };

                let vert_desc = MTLVertexDescriptor::new();
                unsafe {
                    vert_desc
                        .attributes()
                        .setObject_atIndexedSubscript(Some(&pos_desc), 0)
                };
                unsafe {
                    vert_desc
                        .attributes()
                        .setObject_atIndexedSubscript(Some(&uv_desc), 1)
                };

                let layout_desc = MTLVertexBufferLayoutDescriptor::new();
                unsafe { layout_desc.setStride(24) };
                layout_desc.setStepFunction(MTLVertexStepFunction::PerVertex);
                unsafe { layout_desc.setStepRate(1) };
                unsafe {
                    vert_desc
                        .layouts()
                        .setObject_atIndexedSubscript(Some(&layout_desc), 0)
                };

                let color_desc = MTLRenderPipelineColorAttachmentDescriptor::new();
                color_desc.setPixelFormat(MTLPixelFormat::BGRA8Unorm);

                let color_desc_srgb = MTLRenderPipelineColorAttachmentDescriptor::new();
                color_desc_srgb.setPixelFormat(MTLPixelFormat::BGRA8Unorm_sRGB);

                let pipeline_state_desc = MTLRenderPipelineDescriptor::new();
                pipeline_state_desc.setLabel(Some(&NSString::from_str("unienc blit")));
                pipeline_state_desc.setVertexFunction(Some(
                    &library
                        .newFunctionWithName(&NSString::from_str("vertex_main"))
                        .unwrap(),
                ));
                pipeline_state_desc.setFragmentFunction(Some(
                    &library
                        .newFunctionWithName(&NSString::from_str("fragment_main"))
                        .unwrap(),
                ));
                unsafe {
                    pipeline_state_desc
                        .colorAttachments()
                        .setObject_atIndexedSubscript(Some(&color_desc), 0)
                };
                pipeline_state_desc.setVertexDescriptor(Some(&vert_desc));

                let pipeline_state = device
                    .newRenderPipelineStateWithDescriptor_error(&pipeline_state_desc)
                    .unwrap();

                unsafe {
                    pipeline_state_desc
                        .colorAttachments()
                        .setObject_atIndexedSubscript(Some(&color_desc_srgb), 0)
                };

                let pipeline_state_srgb = device
                    .newRenderPipelineStateWithDescriptor_error(&pipeline_state_desc)
                    .unwrap();

                let mut cache: *mut CVMetalTextureCache = std::ptr::null_mut();
                unsafe {
                    CVMetalTextureCache::create(
                        allocator::default(),
                        None,
                        &device,
                        None,
                        NonNull::new(&mut cache).unwrap(),
                    )
                    .to_result()
                    .unwrap()
                };
                let texture_cache = unsafe { Retained::from_raw(cache) }
                    .expect("CVMetalTextureCache::create returned null");

                let sampler_desc = MTLSamplerDescriptor::new();
                sampler_desc.setSAddressMode(MTLSamplerAddressMode::ClampToEdge);
                sampler_desc.setTAddressMode(MTLSamplerAddressMode::ClampToEdge);
                sampler_desc.setMinFilter(MTLSamplerMinMagFilter::Linear);
                sampler_desc.setMagFilter(MTLSamplerMinMagFilter::Linear);
                sampler_desc.setMipFilter(MTLSamplerMipFilter::NotMipmapped);
                let sampler_state = device
                    .newSamplerStateWithDescriptor(&sampler_desc)
                    .expect("MTLSamplerState creation failed");

                let render_pass_descriptor = MTLRenderPassDescriptor::new();
                {
                    // Accessing colorAttachments[0] lazily allocates the
                    // attachment owned by the descriptor; configure it once.
                    let color_attachment = unsafe {
                        render_pass_descriptor
                            .colorAttachments()
                            .objectAtIndexedSubscript(0)
                    };
                    color_attachment.setLoadAction(objc2_metal::MTLLoadAction::DontCare);
                    color_attachment.setStoreAction(objc2_metal::MTLStoreAction::Store);
                }

                CONTEXT
                    .set(Mutex::new(GlobalContext {
                        metal,
                        pipeline_state,
                        pipeline_state_srgb,
                        vertices: vertices.into(),
                        indices: indices.into(),
                        sampler_state,
                        render_pass_descriptor: render_pass_descriptor.into(),
                        texture_cache: texture_cache.into(),
                        pixel_buffer_pool: None,
                    }))
                    .map_err(|_e| AppleError::GlobalStateSetFailed)
                    .unwrap();
            }
        }
        unity_native_plugin::graphics::GfxDeviceEventType::Shutdown => {}
        unity_native_plugin::graphics::GfxDeviceEventType::BeforeReset => {}
        unity_native_plugin::graphics::GfxDeviceEventType::AfterReset => {}
    }
}

pub(crate) fn custom_blit(
    source: &ProtocolObject<dyn MTLTexture>,
    dst_width: u32,
    dst_height: u32,
    flip_vertically: bool,
    is_gamma_workflow: bool,
) -> Result<impl Future<Output = Result<SharedTexture>> + Send + use<>> {
    let markers = MARKERS.get();
    let _blit_guard = markers.map(|m| m.custom_blit.get());

    let mut context = CONTEXT
        .get()
        .ok_or(AppleError::MetalNotInitialized)?
        .lock()
        .map_err(|e| AppleError::Other(e.to_string()))?;

    let (shared_texture, command_buffer) = {
        let _guard = markers.map(|m| m.custom_blit_resources.get());

        // (Re)create the pixel buffer pool if dimensions changed.
        let needs_new_pool = match &context.pixel_buffer_pool {
            Some(entry) => entry.width != dst_width || entry.height != dst_height,
            None => true,
        };
        if needs_new_pool {
            let new_pool = create_pixel_buffer_pool(dst_width, dst_height)?;
            context.pixel_buffer_pool = Some(PixelBufferPoolEntry {
                width: dst_width,
                height: dst_height,
                pool: new_pool.into(),
            });
        }

        let cache = &context.texture_cache;
        let pool = &context
            .pixel_buffer_pool
            .as_ref()
            .expect("pixel_buffer_pool must be initialized")
            .pool;

        let shared_texture = {
            let _guard = markers.map(|m| m.custom_blit_resources_shared_texture.get());
            SharedTexture::new(
                cache,
                pool,
                dst_width as usize,
                dst_height as usize,
                !is_gamma_workflow,
            )?
            // with gamma workflow, input is unorm with gamma color space
        };

        // Commit Unity's current command buffer to ensure all prior GPU work
        // (including writes to the source texture) is submitted, and any active
        // encoder is ended. Then create our own command buffer on Unity's queue
        // so GPU execution order is guaranteed and resource tracking works.
        let metal = context.metal;
        {
            let _guard = markers.map(|m| m.custom_blit_resources_commit_unity.get());
            metal.commit_current_command_buffer();
        }
        let command_buffer = {
            let _guard = markers.map(|m| m.custom_blit_resources_command_buffer.get());
            let command_queue = metal
                .command_queue()
                .ok_or(AppleError::CommandBufferNotAvailable)?;
            command_queue
                .commandBuffer()
                .ok_or(AppleError::CommandBufferNotAvailable)?
        };

        (shared_texture, command_buffer)
    };

    let (block_ptr, rx) = {
        let _guard = markers.map(|m| m.custom_blit_commands.get());

        let encoder = {
            let _guard = markers.map(|m| m.custom_blit_commands_encoder_create.get());

            // Reuse the cached MTLRenderPassDescriptor; mutate its
            // colorAttachment[0] in place via objectAtIndexedSubscript.
            // (setObject:atIndexedSubscript: copies the input, so we must
            // not cache a standalone MTLRenderPassColorAttachmentDescriptor.)
            // custom_blit is render-thread serial, so in-place mutation
            // across frames is safe.
            unsafe {
                context
                    .render_pass_descriptor
                    .colorAttachments()
                    .objectAtIndexedSubscript(0)
            }
            .setTexture(Some(&shared_texture.metal_texture()));

            command_buffer
                .renderCommandEncoderWithDescriptor(&context.render_pass_descriptor)
                .ok_or(AppleError::RenderCommandEncoderCreationFailed)?
        };

        let vert_uniforms = {
            let _guard = markers.map(|m| m.custom_blit_commands_vert_uniforms.get());

            // scale to fit
            let pixel_scale = f32::min(
                dst_width as f32 / source.width() as f32,
                dst_height as f32 / source.height() as f32,
            );
            let render_scale_x = pixel_scale * source.width() as f32 / dst_width as f32;
            let render_scale_y = pixel_scale * source.height() as f32 / dst_height as f32;

            if flip_vertically {
                VertexUniforms {
                    scale_and_tiling: [1f32 / render_scale_x, -1f32 / render_scale_y, 0.0, 1.0],
                }
            } else {
                VertexUniforms {
                    scale_and_tiling: [1f32 / render_scale_x, 1f32 / render_scale_y, 0.0, 0.0],
                }
            }
        };

        {
            let _guard = markers.map(|m| m.custom_blit_commands_record.get());

            if is_gamma_workflow {
                encoder.setRenderPipelineState(&context.pipeline_state);
            } else {
                encoder.setRenderPipelineState(&context.pipeline_state_srgb);
            }

            encoder.setCullMode(MTLCullMode::None);

            // vertex
            unsafe { encoder.setVertexBuffer_offset_atIndex(Some(&*context.vertices), 0, 0) };
            // setVertexBytes copies into Metal's per-frame scratch and avoids
            // allocating a transient MTLBuffer for the 16-byte uniforms.
            unsafe {
                encoder.setVertexBytes_length_atIndex(
                    NonNull::new(&vert_uniforms as *const VertexUniforms as *mut _)
                        .ok_or(AppleError::NonNullCreationFailed)?,
                    std::mem::size_of::<VertexUniforms>(),
                    1,
                )
            };

            // fragment
            unsafe { encoder.setFragmentTexture_atIndex(Some(source), 0) };
            unsafe { encoder.setFragmentSamplerState_atIndex(Some(&context.sampler_state), 0) };

            unsafe {
                encoder.drawIndexedPrimitives_indexCount_indexType_indexBuffer_indexBufferOffset(
                    MTLPrimitiveType::Triangle,
                    3,
                    MTLIndexType::UInt16,
                    &context.indices,
                    0,
                )
            };
        }

        {
            let _guard = markers.map(|m| m.custom_blit_commands_end_encoding.get());
            encoder.endEncoding();
        }

        let (block_ptr, rx) = {
            let _guard = markers.map(|m| m.custom_blit_commands_completion_handler.get());

            let (tx, rx) = oneshot::channel();

            let cell = Arc::new(RefCell::new(None));
            let cell_clone = cell.clone();

            fn fnonce_to_fn<Args>(closure: impl FnOnce(Args)) -> impl Fn(Args) {
                let cell = Cell::new(Some(closure));
                move |args| {
                    let closure = cell.take().expect("called twice");
                    closure(args)
                }
            }

            let block = RcBlock::new(fnonce_to_fn(
                move |_command_buffer: NonNull<ProtocolObject<dyn MTLCommandBuffer>>| {
                    tx.send(shared_texture).unwrap();

                    drop(cell_clone.borrow_mut().take()); // drop self
                },
            ));

            cell.borrow_mut().replace(block.clone());
            let block_ptr = RcBlock::into_raw(block);

            (block_ptr, rx)
        };

        (block_ptr, rx)
    };

    {
        let _guard = markers.map(|m| m.custom_blit_submit.get());
        unsafe { command_buffer.addCompletedHandler(block_ptr) };
        command_buffer.commit();
    }
    Ok(async move { rx.await.map_err(AppleError::from) })
}

#[derive(Debug)]
pub struct SharedTexture {
    inner: Arc<Mutex<SharedTextureInner>>,
}

#[derive(Debug)]
struct SharedTextureInner {
    texture: UnsafeSendRetained<CVMetalTexture>,
    pixel_buffer: UnsafeSendRetained<CVPixelBuffer>,
}

// Steady-state buffer count: the current frame on the render thread plus
// one in flight in VideoToolbox is typical, so 2 is a reasonable floor.
const POOL_MIN_BUFFER_COUNT: i32 = 2;
// Excess buffers beyond MinimumBufferCount are released after this many
// seconds of disuse.
const POOL_MAX_BUFFER_AGE_SECS: f64 = 1.0;
// Hard cap. Buffer allocation beyond this threshold fails with
// kCVReturnWouldExceedAllocationThreshold; the calling code drops the frame.
const POOL_ALLOCATION_THRESHOLD: i32 = 4;

fn create_pixel_buffer_pool(width: u32, height: u32) -> Result<Retained<CVPixelBufferPool>> {
    let width_num = CFNumber::new_i32(width as i32);
    let height_num = CFNumber::new_i32(height as i32);
    let format_num = CFNumber::new_i32(kCVPixelFormatType_32BGRA as i32);
    let true_val = unsafe { kCFBooleanTrue }.expect("kCFBooleanTrue is null");

    let pixel_buffer_keys: [&CFString; 4] = unsafe {
        [
            kCVPixelBufferWidthKey,
            kCVPixelBufferHeightKey,
            kCVPixelBufferPixelFormatTypeKey,
            kCVPixelBufferMetalCompatibilityKey,
        ]
    };
    let pixel_buffer_values: [&CFType; 4] = [&width_num, &height_num, &format_num, true_val];
    let pixel_buffer_attrs = CFDictionary::from_slices(&pixel_buffer_keys, &pixel_buffer_values);

    let min_count_num = CFNumber::new_i32(POOL_MIN_BUFFER_COUNT);
    let max_age_num = CFNumber::new_f64(POOL_MAX_BUFFER_AGE_SECS);
    let pool_keys: [&CFString; 2] = unsafe {
        [
            kCVPixelBufferPoolMinimumBufferCountKey,
            kCVPixelBufferPoolMaximumBufferAgeKey,
        ]
    };
    let pool_values: [&CFType; 2] = [&min_count_num, &max_age_num];
    let pool_attrs = CFDictionary::from_slices(&pool_keys, &pool_values);

    let mut pool: *mut CVPixelBufferPool = std::ptr::null_mut();
    unsafe {
        CVPixelBufferPool::create(
            allocator::default(),
            Some(pool_attrs.as_opaque()),
            Some(pixel_buffer_attrs.as_opaque()),
            NonNull::new(&mut pool).ok_or(AppleError::NonNullCreationFailed)?,
        )
    }
    .to_result()?;

    unsafe { Retained::from_raw(pool) }.ok_or(AppleError::PixelBufferNull)
}

impl SharedTexture {
    pub fn new(
        cache: &CVMetalTextureCache,
        pool: &CVPixelBufferPool,
        width: usize,
        height: usize,
        srgb: bool,
    ) -> Result<Self> {
        let markers = MARKERS.get();

        let buffer = {
            let _guard = markers.map(|m| m.custom_blit_resources_pixel_buffer_create.get());

            let threshold_num = CFNumber::new_i32(POOL_ALLOCATION_THRESHOLD);
            let aux_keys: [&CFString; 1] = unsafe { [kCVPixelBufferPoolAllocationThresholdKey] };
            let aux_values: [&CFType; 1] = [&threshold_num];
            let aux_attrs = CFDictionary::from_slices(&aux_keys, &aux_values);

            let mut buffer: *mut CVPixelBuffer = std::ptr::null_mut();
            unsafe {
                CVPixelBufferPool::create_pixel_buffer_with_aux_attributes(
                    allocator::default(),
                    pool,
                    Some(aux_attrs.as_opaque()),
                    NonNull::new(&mut buffer).ok_or(AppleError::NonNullCreationFailed)?,
                )
            }
            .to_result()?;

            unsafe { Retained::from_raw(buffer) }.ok_or(AppleError::PixelBufferNull)?
        };

        let texture = {
            let _guard = markers.map(|m| m.custom_blit_resources_metal_texture_create.get());

            let mut texture: *mut CVMetalTexture = std::ptr::null_mut();
            unsafe {
                CVMetalTextureCache::create_texture_from_image(
                    allocator::default(),
                    cache,
                    &buffer,
                    None,
                    if srgb {
                        MTLPixelFormat::BGRA8Unorm_sRGB
                    } else {
                        MTLPixelFormat::BGRA8Unorm
                    },
                    width,
                    height,
                    0,
                    NonNull::new(&mut texture).ok_or(AppleError::NonNullCreationFailed)?,
                )
            }
            .to_result()?;
            unsafe { Retained::from_raw(texture) }.ok_or(AppleError::MetalTextureGetFailed)?
        };

        Ok(Self {
            inner: Arc::new(Mutex::new(SharedTextureInner {
                texture: texture.into(),
                pixel_buffer: buffer.into(),
            })),
        })
    }

    pub fn metal_texture(&self) -> Retained<ProtocolObject<dyn MTLTexture>> {
        CVMetalTextureGetTexture(&self.inner.lock().unwrap().texture).unwrap()
    }

    pub fn pixel_buffer(&self) -> Retained<CVPixelBuffer> {
        self.inner.lock().unwrap().pixel_buffer.inner.clone()
    }
}
