use std::{
    cell::{Cell, RefCell},
    ffi::c_void,
    ptr::NonNull,
    sync::{Arc, Mutex, OnceLock},
};

use block2::RcBlock;
use objc2::{rc::Retained, runtime::ProtocolObject};
use objc2_core_foundation::{kCFAllocatorDefault, kCFBooleanTrue, CFDictionary};
use objc2_core_video::{
    kCVPixelBufferMetalCompatibilityKey, kCVPixelFormatType_32BGRA, CVMetalTexture,
    CVMetalTextureCache, CVMetalTextureGetTexture, CVPixelBuffer, CVPixelBufferCreate,
};
use objc2_foundation::NSString;
use objc2_metal::{
    MTLBuffer, MTLCommandBuffer, MTLCommandEncoder, MTLCullMode, MTLDevice, MTLIndexType, MTLLibrary, MTLPixelFormat, MTLPrimitiveType, MTLRenderCommandEncoder,
    MTLRenderPassColorAttachmentDescriptor, MTLRenderPipelineColorAttachmentDescriptor,
    MTLRenderPipelineDescriptor, MTLRenderPipelineState, MTLResourceOptions, MTLSamplerAddressMode,
    MTLSamplerDescriptor, MTLSamplerMinMagFilter, MTLSamplerMipFilter, MTLTexture,
    MTLVertexAttributeDescriptor, MTLVertexBufferLayoutDescriptor, MTLVertexDescriptor,
    MTLVertexFormat, MTLVertexStepFunction,
};
use unity_native_plugin::{
    enums::RenderingExtEventType,
    graphics::{GfxDeviceEventType, UnityGraphics},
    metal::objc2::{UnityGraphicsMetalV1, UnityGraphicsMetalV1Interface},
};

use anyhow::{anyhow, Context, Result};

use crate::{common::UnsafeSendRetained, OsStatus};

static GRAPHICS: OnceLock<Mutex<UnityGraphics>> = OnceLock::new();
static CONTEXT: OnceLock<Mutex<GlobalContext>> = OnceLock::new();

struct GlobalContext {
    metal: UnityGraphicsMetalV1,
    device: Retained<ProtocolObject<dyn MTLDevice>>,
    library: Retained<ProtocolObject<dyn MTLLibrary>>,
    pipeline_state: Retained<ProtocolObject<dyn MTLRenderPipelineState>>,
    vertices: UnsafeSendRetained<ProtocolObject<dyn MTLBuffer>>,
    indices: UnsafeSendRetained<ProtocolObject<dyn MTLBuffer>>,
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
    let graphics = interfaces.interface::<UnityGraphics>().unwrap();

    GRAPHICS
        .set(Mutex::new(graphics))
        .map_err(|e| anyhow!("Failed to set graphics"))
        .unwrap();
    graphics.register_device_event_callback(Some(on_device_event));
}
fn unity_plugin_unload() {}

#[repr(C)]
struct VertexUniforms {
    scale_and_tiling: [f32; 4],
}

extern "system" fn on_device_event(ev_type: GfxDeviceEventType) {
    match ev_type {
        unity_native_plugin::graphics::GfxDeviceEventType::Initialize => {
            let renderer = GRAPHICS.get().unwrap().lock().unwrap().renderer();

            if renderer == unity_native_plugin::graphics::GfxRenderer::Metal {
                let interfaces = unity_native_plugin::interface::UnityInterfaces::get();
                let metal = interfaces.interface::<UnityGraphicsMetalV1>().unwrap();
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
                color_desc.setPixelFormat(MTLPixelFormat::BGRA8Unorm_sRGB);

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

                CONTEXT
                    .set(Mutex::new(GlobalContext {
                        metal,
                        device,
                        library,
                        pipeline_state,
                        vertices: vertices.into(),
                        indices: indices.into(),
                    }))
                    .map_err(|e| anyhow!("Failed to set metal"))
                    .unwrap();
            }
        }
        unity_native_plugin::graphics::GfxDeviceEventType::Shutdown => {}
        unity_native_plugin::graphics::GfxDeviceEventType::BeforeReset => {}
        unity_native_plugin::graphics::GfxDeviceEventType::AfterReset => {}
    }
}

extern "C" fn unienc_get_custom_blit(event: RenderingExtEventType, data: *mut c_void) {
    if event == RenderingExtEventType::UserEventsStart {
        // custom blit;
        let Some(source) =
            (unsafe { Retained::<ProtocolObject<dyn MTLTexture>>::retain(data as *mut _) })
        else {
            return;
        };

        let context = CONTEXT.get().unwrap().lock().unwrap();
        let metal = &context.metal;
        let device = &context.device;

        let mut cache: *mut CVMetalTextureCache = std::ptr::null_mut();
        unsafe {
            CVMetalTextureCache::create(
                kCFAllocatorDefault,
                None,
                device,
                None,
                NonNull::new(&mut cache).unwrap(),
            )
            .to_result()
            .unwrap()
        };

        let cache = unsafe { Retained::from_raw(cache).unwrap() };

        let width = source.width();
        let height = source.height();

        let shared_texture = SharedTexture::new(&cache, width, height).unwrap();

        let command_buffer = metal.current_command_buffer().unwrap();
        metal.end_current_command_encoder();

        let color_attachment_desc = MTLRenderPassColorAttachmentDescriptor::new();
        color_attachment_desc.setTexture(Some(&shared_texture.metal_texture()));
        color_attachment_desc.setLoadAction(objc2_metal::MTLLoadAction::DontCare);
        color_attachment_desc.setStoreAction(objc2_metal::MTLStoreAction::Store);

        let render_pass_descriptor = objc2_metal::MTLRenderPassDescriptor::new();
        unsafe {
            render_pass_descriptor
                .colorAttachments()
                .setObject_atIndexedSubscript(Some(&color_attachment_desc), 0)
        };

        let encoder = command_buffer
            .renderCommandEncoderWithDescriptor(&render_pass_descriptor)
            .unwrap();

        let color_desc = MTLRenderPipelineColorAttachmentDescriptor::new();
        color_desc.setPixelFormat(MTLPixelFormat::BGRA8Unorm);

        let sampler_desc = MTLSamplerDescriptor::new();
        sampler_desc.setSAddressMode(MTLSamplerAddressMode::ClampToEdge);
        sampler_desc.setTAddressMode(MTLSamplerAddressMode::ClampToEdge);
        sampler_desc.setMinFilter(MTLSamplerMinMagFilter::Linear);
        sampler_desc.setMagFilter(MTLSamplerMinMagFilter::Linear);
        sampler_desc.setMipFilter(MTLSamplerMipFilter::NotMipmapped);

        let sampler_state = device.newSamplerStateWithDescriptor(&sampler_desc).unwrap();

        encoder.setRenderPipelineState(&context.pipeline_state);
        encoder.setCullMode(MTLCullMode::None);

        // vertex
        unsafe { encoder.setVertexBuffer_offset_atIndex(Some(&*context.vertices), 0, 0) };

        let mut vert_uniforms = VertexUniforms {
            scale_and_tiling: [1.0, 1.0, 0.0, 0.0],
        };
        let vert_uniforms = unsafe {
            device.newBufferWithBytes_length_options(
                NonNull::new(&mut vert_uniforms as *mut VertexUniforms as *mut _).unwrap(),
                std::mem::size_of::<VertexUniforms>(),
                MTLResourceOptions::CPUCacheModeWriteCombined,
            )
        }
        .unwrap();

        unsafe { encoder.setVertexBuffer_offset_atIndex(Some(&vert_uniforms), 0, 1) };

        // fragment

        unsafe { encoder.setFragmentTexture_atIndex(Some(&source), 0) };
        unsafe { encoder.setFragmentSamplerState_atIndex(Some(&sampler_state), 0) };

        unsafe {
            encoder.drawIndexedPrimitives_indexCount_indexType_indexBuffer_indexBufferOffset(
                MTLPrimitiveType::Triangle,
                3,
                MTLIndexType::UInt16,
                &context.indices,
                0,
            )
        };

        encoder.endEncoding();

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
                drop(shared_texture);

                drop(cell_clone.borrow_mut().take()); // drop self
            },
        ));

        cell.borrow_mut().replace(block.clone());
        let block_ptr = RcBlock::into_raw(block);

        unsafe { command_buffer.addCompletedHandler(block_ptr) };
    }
}

#[no_mangle]
pub extern "C" fn unienc_get_custom_blit_ptr() -> usize {
    unienc_get_custom_blit as usize
}

struct SharedTexture {
    texture: Retained<CVMetalTexture>,
    pixel_buffer: Retained<CVPixelBuffer>,
}

impl SharedTexture {
    pub fn new(cache: &CVMetalTextureCache, width: usize, height: usize) -> Result<Self> {
        let pixel_format = kCVPixelFormatType_32BGRA;

        let pixel_buffer_attrs = unsafe {
            CFDictionary::from_slices(
                &[kCVPixelBufferMetalCompatibilityKey],
                &[kCFBooleanTrue.unwrap()],
            )
        };

        let mut buffer: *mut CVPixelBuffer = std::ptr::null_mut();
        unsafe {
            CVPixelBufferCreate(
                kCFAllocatorDefault,
                width,
                height,
                pixel_format,
                Some(pixel_buffer_attrs.as_opaque()),
                NonNull::new(&mut buffer).context("Failed to create CVPixelBuffer")?,
            )
        }
        .to_result()?;

        let buffer = unsafe { Retained::from_raw(buffer) }.context("CVPixelBuffer is null")?;

        let mut texture: *mut CVMetalTexture = std::ptr::null_mut();
        unsafe {
            CVMetalTextureCache::create_texture_from_image(
                kCFAllocatorDefault,
                cache,
                &buffer,
                None,
                MTLPixelFormat::BGRA8Unorm_sRGB,
                width,
                height,
                0,
                NonNull::new(&mut texture).context("Failed to create CVMetalTexture")?,
            )
        }
        .to_result()
        .context("Failed to create CVMetalTexture")?;
        let texture = unsafe { Retained::from_raw(texture) }
            .context("Failed to get MTLTexture from CVMetalTexture")?;

        Ok(Self {
            texture,
            pixel_buffer: buffer,
        })
    }

    pub fn metal_texture(&self) -> Retained<ProtocolObject<dyn MTLTexture>> {
        unsafe { CVMetalTextureGetTexture(&self.texture).unwrap() }
    }
}
