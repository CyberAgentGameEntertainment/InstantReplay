use crate::error::{AndroidError, Result, ResultExt};
use crate::vulkan::hardware_buffer_surface::HardwareBufferFrame;
use crate::vulkan::types::{
    VulkanCommandPoolHandle, VulkanDescriptorPoolHandle, VulkanDescriptorSet,
    VulkanDescriptorSetLayoutHandle, VulkanImageViewHandle, VulkanPipelineHandle,
    VulkanPipelineLayoutHandle, VulkanRenderPassHandle, VulkanSamplerHandle,
    VulkanShaderModuleHandle,
};
use crate::vulkan::utils::{FenceGuard, create_shader_module};
use crate::vulkan::{GlobalContext, MARKERS, ProfilerMarkerDescExt};
use ash::vk;
use std::ffi::c_void;
use std::future::Future;
use std::os::raw::c_int;
use std::sync::{Arc, Mutex, MutexGuard};
use unity_native_plugin::vulkan::{IUnityGraphicsVulkan, VulkanResourceAccessMode};

const VERT: &[u8] = include_bytes!("preprocess.vert.glsl.spv");
const FRAG: &[u8] = include_bytes!("preprocess.frag.glsl.spv");

#[allow(dead_code)]
pub struct PreprocessRenderPass {
    pipelines: Vec<VulkanPipelineHandle>,
    pipeline_layout: VulkanPipelineLayoutHandle,
    shader_mod_vert: VulkanShaderModuleHandle,
    shader_mod_frag: VulkanShaderModuleHandle,
    desc_set_layout: VulkanDescriptorSetLayoutHandle,
    desc_sets: Arc<DescriptorSetPool>,
    sampler: VulkanSamplerHandle,
    pub(crate) render_pass: VulkanRenderPassHandle,
    command_buffers: Arc<CommandBufferPool>,
    /// Queue family of Unity's graphics queue; the queue family that owns the
    /// HardwareBuffer image until it is released to the encoder.
    queue_family_index: u32,
}

struct DescriptorSetPool {
    sets: Mutex<Vec<VulkanDescriptorSet>>,
}

struct DescriptorSetGuard {
    desc_set: Option<VulkanDescriptorSet>,
    pool: Arc<DescriptorSetPool>,
}

impl DescriptorSetPool {
    pub fn new(sets: Vec<VulkanDescriptorSet>) -> Self {
        Self {
            sets: Mutex::new(sets),
        }
    }
    pub fn pop(self: &Arc<Self>) -> Option<DescriptorSetGuard> {
        let mut sets = self.sets.lock().unwrap();
        sets.pop().map(|desc_set| DescriptorSetGuard {
            desc_set: Some(desc_set),
            pool: self.clone(),
        })
    }

    pub fn push(&self, desc_set: VulkanDescriptorSet) {
        let mut sets = self.sets.lock().unwrap();
        sets.push(desc_set);
    }
}

impl Drop for DescriptorSetGuard {
    fn drop(&mut self) {
        if let Some(desc_set) = self.desc_set.take() {
            self.pool.push(desc_set);
        }
    }
}

impl DescriptorSetGuard {
    pub fn get(&self) -> &VulkanDescriptorSet {
        self.desc_set.as_ref().unwrap()
    }
}

/// Command buffers for the blit.
///
/// A `VkCommandPool` and the command buffers allocated from it are externally synchronized
/// objects: allocation, recording and reuse must not overlap on the host. Recording happens on
/// Unity's render thread while completed blits are torn down on a blocking-pool thread, so every
/// host access goes through `inner`. Command buffers are never freed; a completed one is returned
/// to `free` (see `PooledCommandBuffer`) and reused by a later blit, which avoids issuing Vulkan
/// calls against the pool from the teardown thread at all.
struct CommandBufferPool {
    device: Arc<ash::Device>,
    inner: Mutex<CommandBufferPoolInner>,
}

struct CommandBufferPoolInner {
    pool: VulkanCommandPoolHandle,
    free: Vec<vk::CommandBuffer>,
}

/// A command buffer in the recording state. Keeps the pool locked until `end` is called; if it is
/// dropped without `end` (an error while recording) the command buffer goes back to the free
/// list, and the next `vkBeginCommandBuffer` implicitly resets it.
struct RecordingCommandBuffer<'a> {
    inner: MutexGuard<'a, CommandBufferPoolInner>,
    pool: &'a Arc<CommandBufferPool>,
    command_buffer: vk::CommandBuffer,
    ended: bool,
}

/// A recorded command buffer that is owned by a pending blit. Returned to the pool on drop, which
/// must happen only after the GPU has finished executing it.
struct PooledCommandBuffer {
    command_buffer: vk::CommandBuffer,
    pool: Arc<CommandBufferPool>,
}

impl CommandBufferPool {
    fn new(device: Arc<ash::Device>, queue_family_index: u32) -> Result<Self> {
        let pool = VulkanCommandPoolHandle::new(
            unsafe {
                device.create_command_pool(
                    &vk::CommandPoolCreateInfo::default()
                        .queue_family_index(queue_family_index)
                        .flags(vk::CommandPoolCreateFlags::RESET_COMMAND_BUFFER),
                    None,
                )
            }?,
            device.clone(),
        );
        Ok(Self {
            device,
            inner: Mutex::new(CommandBufferPoolInner {
                pool,
                free: Vec::new(),
            }),
        })
    }

    fn begin(self: &Arc<Self>) -> Result<RecordingCommandBuffer<'_>> {
        let mut inner = self.inner.lock().map_err(|_| AndroidError::MutexPoisoned)?;
        let command_buffer = match inner.free.pop() {
            Some(command_buffer) => command_buffer,
            None => unsafe {
                self.device.allocate_command_buffers(
                    &vk::CommandBufferAllocateInfo::default()
                        .command_pool(*inner.pool)
                        .level(vk::CommandBufferLevel::PRIMARY)
                        .command_buffer_count(1),
                )
            }?[0],
        };
        let recording = RecordingCommandBuffer {
            inner,
            pool: self,
            command_buffer,
            ended: false,
        };
        // Implicitly resets a previously recorded command buffer (RESET_COMMAND_BUFFER pool).
        unsafe {
            self.device
                .begin_command_buffer(command_buffer, &vk::CommandBufferBeginInfo::default())
        }?;
        Ok(recording)
    }
}

impl RecordingCommandBuffer<'_> {
    fn handle(&self) -> vk::CommandBuffer {
        self.command_buffer
    }

    fn end(mut self) -> Result<PooledCommandBuffer> {
        unsafe { self.pool.device.end_command_buffer(self.command_buffer) }?;
        self.ended = true;
        Ok(PooledCommandBuffer {
            command_buffer: self.command_buffer,
            pool: self.pool.clone(),
        })
    }
}

impl Drop for RecordingCommandBuffer<'_> {
    fn drop(&mut self) {
        if !self.ended {
            self.inner.free.push(self.command_buffer);
        }
    }
}

impl Drop for PooledCommandBuffer {
    fn drop(&mut self) {
        if let Ok(mut inner) = self.pool.inner.lock() {
            inner.free.push(self.command_buffer);
        }
    }
}

#[repr(C)]
struct VertPushConstants {
    scale_and_tiling: [f32; 4],
}

pub fn create_pass(
    device: Arc<ash::Device>,
    queue_family_index: u32,
) -> Result<PreprocessRenderPass> {
    // create render pass
    let render_pass = unsafe {
        device.create_render_pass(
            &vk::RenderPassCreateInfo::default()
                .attachments(&[vk::AttachmentDescription::default()
                    .format(vk::Format::R8G8B8A8_UNORM)
                    .samples(vk::SampleCountFlags::TYPE_1)
                    .load_op(vk::AttachmentLoadOp::DONT_CARE)
                    .store_op(vk::AttachmentStoreOp::STORE)
                    .stencil_load_op(vk::AttachmentLoadOp::DONT_CARE)
                    .stencil_store_op(vk::AttachmentStoreOp::DONT_CARE)
                    .initial_layout(vk::ImageLayout::UNDEFINED)
                    // The barrier recorded after the render pass releases the image to the
                    // encoder and expects it in this layout.
                    .final_layout(vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL)])
                .subpasses(&[vk::SubpassDescription::default()
                    .pipeline_bind_point(vk::PipelineBindPoint::GRAPHICS)
                    .color_attachments(&[vk::AttachmentReference::default()
                        .attachment(0)
                        .layout(vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL)])])
                .dependencies(&[vk::SubpassDependency::default()
                    .src_subpass(vk::SUBPASS_EXTERNAL)
                    .dst_subpass(0)
                    .src_stage_mask(vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT)
                    .src_access_mask(vk::AccessFlags::empty())
                    .dst_stage_mask(vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT)
                    .dst_access_mask(vk::AccessFlags::COLOR_ATTACHMENT_WRITE)]),
            None,
        )
    }?;

    // create pipeline
    let shader_vert = create_shader_module(&device, VERT)?;
    let shader_frag = create_shader_module(&device, FRAG)?;

    let set_layout = VulkanDescriptorSetLayoutHandle::new(
        unsafe {
            device.create_descriptor_set_layout(
                &vk::DescriptorSetLayoutCreateInfo::default().bindings(&[
                    vk::DescriptorSetLayoutBinding::default()
                        .binding(0)
                        .descriptor_type(vk::DescriptorType::COMBINED_IMAGE_SAMPLER)
                        .descriptor_count(1)
                        .stage_flags(vk::ShaderStageFlags::FRAGMENT),
                ]),
                None,
            )
        }?,
        device.clone(),
    );

    let pipeline_layout = VulkanPipelineLayoutHandle::new(
        unsafe {
            device.create_pipeline_layout(
                &vk::PipelineLayoutCreateInfo::default()
                    .set_layouts(&[*set_layout])
                    .push_constant_ranges(&[
                        // vert
                        vk::PushConstantRange::default()
                            .stage_flags(vk::ShaderStageFlags::VERTEX)
                            .offset(0)
                            .size(std::mem::size_of::<VertPushConstants>() as u32),
                    ]),
                None,
            )
        }?,
        device.clone(),
    );

    let pipelines = match unsafe {
        device.create_graphics_pipelines(
            vk::PipelineCache::null(),
            &[vk::GraphicsPipelineCreateInfo::default()
                .stages(&[
                    vk::PipelineShaderStageCreateInfo::default()
                        .stage(vk::ShaderStageFlags::VERTEX)
                        .module(*shader_vert)
                        .name(c"main"),
                    vk::PipelineShaderStageCreateInfo::default()
                        .stage(vk::ShaderStageFlags::FRAGMENT)
                        .module(*shader_frag)
                        .name(c"main"),
                ])
                .vertex_input_state(&vk::PipelineVertexInputStateCreateInfo::default())
                .input_assembly_state(
                    &vk::PipelineInputAssemblyStateCreateInfo::default()
                        .topology(vk::PrimitiveTopology::TRIANGLE_LIST)
                        .primitive_restart_enable(false),
                )
                .viewport_state(
                    &vk::PipelineViewportStateCreateInfo::default()
                        .viewport_count(1)
                        .scissor_count(1),
                )
                .rasterization_state(
                    &vk::PipelineRasterizationStateCreateInfo::default()
                        .depth_clamp_enable(false)
                        .rasterizer_discard_enable(false)
                        .polygon_mode(vk::PolygonMode::FILL)
                        .line_width(1.0f32)
                        .cull_mode(vk::CullModeFlags::NONE)
                        .front_face(vk::FrontFace::COUNTER_CLOCKWISE)
                        .depth_bias_enable(false)
                        .depth_bias_constant_factor(0.0f32)
                        .depth_bias_clamp(0.0f32)
                        .depth_bias_slope_factor(0.0f32),
                )
                .multisample_state(
                    &vk::PipelineMultisampleStateCreateInfo::default()
                        .sample_shading_enable(false)
                        .rasterization_samples(vk::SampleCountFlags::TYPE_1)
                        .min_sample_shading(1.0f32)
                        .alpha_to_coverage_enable(false)
                        .alpha_to_one_enable(false),
                )
                .color_blend_state(
                    &vk::PipelineColorBlendStateCreateInfo::default()
                        .logic_op_enable(false)
                        .logic_op(vk::LogicOp::COPY)
                        .attachments(&[vk::PipelineColorBlendAttachmentState::default()
                            .color_write_mask(
                                vk::ColorComponentFlags::R
                                    | vk::ColorComponentFlags::G
                                    | vk::ColorComponentFlags::B
                                    | vk::ColorComponentFlags::A,
                            )
                            .blend_enable(false)])
                        .blend_constants([0.0, 0.0, 0.0, 0.0]),
                )
                .dynamic_state(
                    &vk::PipelineDynamicStateCreateInfo::default()
                        .dynamic_states(&[vk::DynamicState::VIEWPORT, vk::DynamicState::SCISSOR]),
                )
                .layout(*pipeline_layout)
                .render_pass(render_pass)
                .subpass(0)
                .base_pipeline_handle(vk::Pipeline::null())
                .base_pipeline_index(0)],
            None,
        )
    } {
        Ok(pipelines) => pipelines
            .iter()
            .map(|p| VulkanPipelineHandle::new(*p, device.clone()))
            .collect::<Vec<_>>(),
        Err((pipelines, result)) => {
            for pipeline in pipelines {
                unsafe { device.destroy_pipeline(pipeline, None) };
            }
            return Err(AndroidError::GraphicsPipelineCreationFailed(result));
        }
    };

    const MAX_FRAMES_IN_FLIGHT: u32 = 4;

    // create desc pool
    let desc_pool = Arc::new(VulkanDescriptorPoolHandle::new(
        unsafe {
            device.create_descriptor_pool(
                &vk::DescriptorPoolCreateInfo::default()
                    .pool_sizes(&[vk::DescriptorPoolSize::default()
                        .ty(vk::DescriptorType::COMBINED_IMAGE_SAMPLER)
                        .descriptor_count(MAX_FRAMES_IN_FLIGHT)])
                    .max_sets(MAX_FRAMES_IN_FLIGHT),
                None,
            )
        }?,
        device.clone(),
    ));

    let sampler = VulkanSamplerHandle::new(
        unsafe {
            device.create_sampler(
                &vk::SamplerCreateInfo::default()
                    .mag_filter(vk::Filter::LINEAR)
                    .min_filter(vk::Filter::LINEAR)
                    .address_mode_u(vk::SamplerAddressMode::CLAMP_TO_EDGE)
                    .address_mode_v(vk::SamplerAddressMode::CLAMP_TO_EDGE)
                    .address_mode_w(vk::SamplerAddressMode::CLAMP_TO_EDGE)
                    .anisotropy_enable(false)
                    .max_anisotropy(16.0)
                    .border_color(vk::BorderColor::INT_OPAQUE_BLACK)
                    .unnormalized_coordinates(false)
                    .compare_enable(false)
                    .compare_op(vk::CompareOp::ALWAYS)
                    .mipmap_mode(vk::SamplerMipmapMode::LINEAR)
                    .mip_lod_bias(0.0)
                    .min_lod(0.0)
                    .max_lod(vk::LOD_CLAMP_NONE),
                None,
            )
        }?,
        device.clone(),
    );

    let desc_sets = Arc::new(DescriptorSetPool::new(
        unsafe {
            device.allocate_descriptor_sets(
                &vk::DescriptorSetAllocateInfo::default()
                    .descriptor_pool(**desc_pool)
                    .set_layouts(&[*set_layout; MAX_FRAMES_IN_FLIGHT as usize]),
            )
        }?
        .iter()
        .map(|s| VulkanDescriptorSet::new(*s, desc_pool.clone(), device.clone()))
        .collect(),
    ));

    let command_buffers = Arc::new(CommandBufferPool::new(device.clone(), queue_family_index)?);

    Ok(PreprocessRenderPass {
        pipelines,
        pipeline_layout,
        shader_mod_vert: shader_vert,
        shader_mod_frag: shader_frag,
        desc_set_layout: set_layout,
        desc_sets,
        sampler,
        render_pass: VulkanRenderPassHandle::new(render_pass, device.clone()),
        command_buffers,
        queue_family_index,
    })
}

/// Resources for HardwareBuffer blit that need to be kept alive until GPU completes
#[allow(dead_code)]
struct HardwareBufferBlitResources {
    command_buffer: PooledCommandBuffer,
    pass: Arc<PreprocessRenderPass>,
    src_view: VulkanImageViewHandle,
    fence: FenceGuard,
    desc_set: DescriptorSetGuard,
}

/// A recorded blit waiting for Unity to grant access to the graphics queue.
///
/// Unity hands this back in the `AccessQueue` callback, which may run synchronously or later on
/// Unity's submission thread. The callback only submits; waiting for the GPU happens on the
/// receiving side of `tx` so that no runtime work is done on Unity's thread.
struct QueueSubmission {
    device: Arc<ash::Device>,
    queue: vk::Queue,
    resources: HardwareBufferBlitResources,
    tx: tokio::sync::oneshot::Sender<Result<HardwareBufferBlitResources>>,
}

impl QueueSubmission {
    fn submit(self) {
        let markers = MARKERS.get();
        let _guard = markers.map(|m| m.preprocess_blit_submit.get());

        let result = unsafe {
            self.device.queue_submit(
                self.queue,
                &[vk::SubmitInfo::default()
                    .command_buffers(&[self.resources.command_buffer.command_buffer])],
                **self.resources.fence.get(),
            )
        }
        .context("queue_submit failed");

        // The receiver has gone away only if the encoder was torn down; the resources are then
        // dropped here, which is safe because nothing was submitted or the fence is unused.
        let _ = self.tx.send(result.map(|()| self.resources));
    }
}

unsafe extern "system" fn queue_access_callback(_event_id: c_int, user_data: *mut c_void) {
    let submission = unsafe { Box::from_raw(user_data as *mut QueueSubmission) };
    submission.submit();
}

/// Blit source image to a HardwareBuffer-backed frame
/// Returns a Future that completes when GPU work is done
///
/// Must be called from a plugin event callback configured with
/// `VulkanGraphicsQueueAccess::DontCare`: the source texture is acquired through Unity's
/// `AccessTexture`, which records the layout transition into Unity's command buffer and
/// forbids queue access in the same callback. Submission is deferred to `AccessQueue` with
/// `flush = true`, so Unity's pending command buffers (including that transition) reach the queue
/// before the blit does.
#[allow(clippy::too_many_arguments)]
pub fn blit_to_hardware_buffer<R: unienc_common::Runtime + 'static>(
    cx: &GlobalContext,
    native_texture: *mut c_void,
    src_width: u32,
    src_height: u32,
    flip_vertically: bool,
    is_gamma_workflow: bool,
    frame: &HardwareBufferFrame,
    event_id: c_int,
    runtime: R,
) -> Result<impl Future<Output = Result<()>> + use<R>> {
    let markers = MARKERS.get();
    let _guard = markers.map(|m| m.preprocess_blit.get());
    let vulkan = &cx.vulkan;
    let device = &cx.device;
    let pass = &cx.render_pass;

    let Some(desc_set) = pass.desc_sets.pop() else {
        return Err(AndroidError::NoAvailableDescriptorSets);
    };

    let (src_view, queue, fence) = {
        let _guard = markers.map(|m| m.preprocess_blit_resources.get());

        // Let Unity transition the source to the layout the fragment shader samples it in and
        // make the preceding writes visible. Unity keeps tracking the layout afterwards, so the
        // next write to the texture is fenced against this read as well.
        let src = unsafe {
            vulkan.access_texture(
                native_texture,
                None,
                vk::ImageLayout::SHADER_READ_ONLY_OPTIMAL,
                vk::PipelineStageFlags::FRAGMENT_SHADER,
                vk::AccessFlags::SHADER_READ,
                VulkanResourceAccessMode::PipelineBarrier,
            )
        }
        .ok_or(AndroidError::AccessTextureFailed)?;

        let format = src.format();

        // A format of AHardwareBuffer doesn't seem to be mapped to SRGB formats directly while MediaCodec accepts sRGB pixels.
        // (mapping table: https://docs.vulkan.org/spec/latest/chapters/memory.html#memory-external-android-hardware-buffer-formats)

        let view_format = if is_gamma_workflow {
            // With gamma workflow we don't need to do anything special here because input image is always sRGB stored as UNORM
            format
        } else {
            // With linear workflow we need to create vkImage with sRGB format
            // because input image is sRGB stored as SRGB or linear stored as UNORM
            match format {
                vk::Format::R8G8B8A8_SRGB => vk::Format::R8G8B8A8_UNORM,
                vk::Format::R8G8B8_SRGB => vk::Format::R8G8B8_UNORM,
                _ => format,
            }
        };

        let src_view = VulkanImageViewHandle::new(
            unsafe {
                device.create_image_view(
                    &vk::ImageViewCreateInfo::default()
                        .image(src.image())
                        .view_type(vk::ImageViewType::TYPE_2D)
                        .format(view_format)
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
        );

        unsafe {
            device.update_descriptor_sets(
                &[vk::WriteDescriptorSet::default()
                    .dst_set(**desc_set.get())
                    .dst_binding(0)
                    .dst_array_element(0)
                    .descriptor_type(vk::DescriptorType::COMBINED_IMAGE_SAMPLER)
                    .image_info(&[vk::DescriptorImageInfo::default()
                        .sampler(*pass.sampler)
                        .image_view(*src_view)
                        .image_layout(vk::ImageLayout::SHADER_READ_ONLY_OPTIMAL)])],
                &[],
            )
        };

        let queue = vulkan.instance().graphics_queue();

        let fence = cx.fence_pool.pop()?;

        (src_view, queue, fence)
    };

    let command_buffer = {
        let _guard = markers.map(|m| m.preprocess_blit_commands.get());
        let recording = pass.command_buffers.begin()?;
        let cb = recording.handle();

        let width = frame.width;
        let height = frame.height;

        // Transition HardwareBuffer image to COLOR_ATTACHMENT_OPTIMAL
        unsafe {
            device.cmd_pipeline_barrier(
                cb,
                vk::PipelineStageFlags::TOP_OF_PIPE,
                vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT,
                vk::DependencyFlags::empty(),
                &[],
                &[],
                &[vk::ImageMemoryBarrier::default()
                    .src_access_mask(vk::AccessFlags::empty())
                    .dst_access_mask(vk::AccessFlags::COLOR_ATTACHMENT_WRITE)
                    .old_layout(vk::ImageLayout::UNDEFINED)
                    .new_layout(vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL)
                    .src_queue_family_index(vk::QUEUE_FAMILY_IGNORED)
                    .dst_queue_family_index(vk::QUEUE_FAMILY_IGNORED)
                    .image(frame.vk_image_handle())
                    .subresource_range(vk::ImageSubresourceRange {
                        aspect_mask: vk::ImageAspectFlags::COLOR,
                        base_mip_level: 0,
                        level_count: 1,
                        base_array_layer: 0,
                        layer_count: 1,
                    })],
            );
        }

        unsafe {
            device.cmd_begin_render_pass(
                cb,
                &vk::RenderPassBeginInfo::default()
                    .render_pass(*pass.render_pass)
                    .framebuffer(frame.vk_framebuffer())
                    .render_area(vk::Rect2D {
                        offset: vk::Offset2D { x: 0, y: 0 },
                        extent: vk::Extent2D { width, height },
                    }),
                vk::SubpassContents::INLINE,
            )
        };

        // scale to fit
        let pixel_scale = f32::min(
            width as f32 / src_width as f32,
            height as f32 / src_height as f32,
        );
        let render_scale_x = pixel_scale * src_width as f32 / width as f32;
        let render_scale_y = pixel_scale * src_height as f32 / height as f32;

        let push_constants_vert = if flip_vertically {
            VertPushConstants {
                scale_and_tiling: [1f32 / render_scale_x, -1f32 / render_scale_y, 0.0, 1.0],
            }
        } else {
            VertPushConstants {
                scale_and_tiling: [1f32 / render_scale_x, 1f32 / render_scale_y, 0.0, 0.0],
            }
        };

        unsafe {
            device.cmd_bind_pipeline(cb, vk::PipelineBindPoint::GRAPHICS, *pass.pipelines[0])
        };

        unsafe {
            device.cmd_push_constants(
                cb,
                *pass.pipeline_layout,
                vk::ShaderStageFlags::VERTEX,
                0,
                std::slice::from_ref(&push_constants_vert)
                    .align_to::<u8>()
                    .1,
            )
        };

        unsafe {
            device.cmd_bind_descriptor_sets(
                cb,
                vk::PipelineBindPoint::GRAPHICS,
                *pass.pipeline_layout,
                0,
                &[**desc_set.get()],
                &[],
            )
        };

        let viewport = vk::Viewport {
            x: 0.0,
            y: 0.0,
            width: width as f32,
            height: height as f32,
            min_depth: 0.0,
            max_depth: 1.0,
        };

        unsafe {
            device.cmd_set_viewport(cb, 0, &[viewport]);
        }

        let scissor = vk::Rect2D {
            offset: vk::Offset2D { x: 0, y: 0 },
            extent: vk::Extent2D { width, height },
        };

        unsafe {
            device.cmd_set_scissor(cb, 0, &[scissor]);
        }

        unsafe {
            device.cmd_draw(cb, 3, 1, 0, 0);
        }

        unsafe {
            device.cmd_end_render_pass(cb);
        }

        // Release the HardwareBuffer image to the encoder: a queue family ownership transfer
        // from Unity's graphics queue family to VK_QUEUE_FAMILY_EXTERNAL, leaving the image in
        // GENERAL. The old layout is the render pass' final layout.
        unsafe {
            device.cmd_pipeline_barrier(
                cb,
                vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT,
                vk::PipelineStageFlags::BOTTOM_OF_PIPE,
                vk::DependencyFlags::empty(),
                &[],
                &[],
                &[vk::ImageMemoryBarrier::default()
                    .src_access_mask(vk::AccessFlags::COLOR_ATTACHMENT_WRITE)
                    .dst_access_mask(vk::AccessFlags::empty())
                    .old_layout(vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL)
                    .new_layout(vk::ImageLayout::GENERAL)
                    .src_queue_family_index(pass.queue_family_index)
                    .dst_queue_family_index(vk::QUEUE_FAMILY_EXTERNAL)
                    .image(frame.vk_image_handle())
                    .subresource_range(vk::ImageSubresourceRange {
                        aspect_mask: vk::ImageAspectFlags::COLOR,
                        base_mip_level: 0,
                        level_count: 1,
                        base_array_layer: 0,
                        layer_count: 1,
                    })],
            );
        }

        recording.end()?
    };

    let resources = HardwareBufferBlitResources {
        command_buffer,
        pass: pass.clone(),
        src_view,
        fence,
        desc_set,
    };

    let (tx, rx) = tokio::sync::oneshot::channel();
    let submission = Box::new(QueueSubmission {
        device: device.clone(),
        queue,
        resources,
        tx,
    });

    // `flush = true`: Unity submits its own pending command buffers first, so the blit is
    // ordered after the layout transition recorded by `access_texture` above.
    unsafe {
        vulkan.access_queue(
            Some(queue_access_callback),
            event_id,
            Box::into_raw(submission) as *mut c_void,
            true,
        )
    };

    let device = device.clone();
    Ok(async move {
        let resources = rx
            .await
            .map_err(|_| AndroidError::QueueAccessCallbackDropped)??;
        runtime
            .spawn_blocking(move || {
                let result =
                    unsafe { device.wait_for_fences(&[**resources.fence.get()], true, u64::MAX) }
                        .map_err(AndroidError::Vulkan);
                drop(resources);
                result
            })
            .await
    })
}
