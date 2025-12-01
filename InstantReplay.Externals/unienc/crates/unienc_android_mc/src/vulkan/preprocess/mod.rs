use crate::vulkan::presentation::{VulkanSurface, VulkanSwapchainTaget};
use crate::vulkan::types::{VulkanCommandBuffer, VulkanCommandPoolHandle, VulkanDescriptorPoolHandle, VulkanDescriptorSet, VulkanDescriptorSetLayoutHandle, VulkanImageViewHandle, VulkanPipelineHandle, VulkanPipelineLayoutHandle, VulkanRenderPassHandle, VulkanSamplerHandle, VulkanShaderModuleHandle};
use crate::vulkan::utils::{create_shader_module, FenceGuard, SemaphoreGuard};
use crate::vulkan::{GlobalContext, ProfilerMarkerDescExt, MARKERS};
use anyhow::{anyhow, Context, Result};
use ash::vk;
use std::ffi::c_str;
use std::future::Future;
use std::sync::{Arc, Mutex};
use unity_native_plugin_vulkan::vulkan::VulkanGraphicsQueueAccess;

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
    command_pool: Arc<VulkanCommandPoolHandle>,
    // src_view_cache: Mutex<>
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
        if let Some(desc_set) = sets.pop() {
            Some(DescriptorSetGuard {
                desc_set: Some(desc_set),
                pool: self.clone(),
            })
        } else {
            None
        }
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

#[repr(C)]
struct VertPushConstants {
    scale_and_tiling: [f32; 4],
}
#[repr(C)]
struct FragPushConstants {
    rechannel: [[f32; 4]; 4],
}

pub fn create_pass(
    device: Arc<ash::Device>,
    queue_family_index: u32,
) -> anyhow::Result<PreprocessRenderPass> {
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
                    .final_layout(vk::ImageLayout::PRESENT_SRC_KHR)])
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

    // cx.deref_mut().
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
                            .size(std::mem::size_of::<VertPushConstants>() as u32)
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
                        .name(c_str::CStr::from_bytes_with_nul(b"main\0")?),
                    vk::PipelineShaderStageCreateInfo::default()
                        .stage(vk::ShaderStageFlags::FRAGMENT)
                        .module(*shader_frag)
                        .name(c_str::CStr::from_bytes_with_nul(b"main\0")?),
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
            return Err(anyhow!("Failed to create graphics pipeline: {:?}", result));
        }
    };

    const MAX_FRAMES_IN_FLIGHT: u32 = 4;

    // create desc pool
    let desc_pool = Arc::new(VulkanDescriptorPoolHandle::new(unsafe {
        device.create_descriptor_pool(
            &vk::DescriptorPoolCreateInfo::default()
                .pool_sizes(&[vk::DescriptorPoolSize::default()
                    .ty(vk::DescriptorType::COMBINED_IMAGE_SAMPLER)
                    .descriptor_count(MAX_FRAMES_IN_FLIGHT)])
                .max_sets(MAX_FRAMES_IN_FLIGHT),
            None,
        )
    }?, device.clone()));

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

    let desc_sets = Arc::new(DescriptorSetPool::new(unsafe {
        device.allocate_descriptor_sets(
            &vk::DescriptorSetAllocateInfo::default()
                .descriptor_pool(**desc_pool)
                .set_layouts(&[*set_layout; MAX_FRAMES_IN_FLIGHT as usize]),
        )
    }?.iter().map(|s| {
        VulkanDescriptorSet::new(*s, desc_pool.clone(), device.clone())
    }).collect()));

    let command_pool = VulkanCommandPoolHandle::new(
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

    Ok(PreprocessRenderPass {
        pipelines,
        pipeline_layout,
        shader_mod_vert: shader_vert,
        shader_mod_frag: shader_frag,
        desc_set_layout: set_layout,
        desc_sets,
        sampler,
        render_pass: VulkanRenderPassHandle::new(render_pass, device.clone()),
        command_pool: Arc::new(command_pool),
    })
}

#[allow(dead_code)]
// resources need to be retained until blit is finished
struct BlitResources {
    command_buffer: VulkanCommandBuffer,
    semaphore_acquire: SemaphoreGuard,
    semaphore: SemaphoreGuard,
    pass: Arc<PreprocessRenderPass>,
    target: Arc<VulkanSwapchainTaget>,
    src_view: VulkanImageViewHandle,
    fence: FenceGuard,
    desc_set: DescriptorSetGuard,
}

struct DescriptorSetLock {
    desc_set: vk::DescriptorSet,
    device: Arc<ash::Device>,
}

pub fn blit(
    cx: &GlobalContext,
    src: &vk::Image,
    surface: &VulkanSurface,
    timestamp_ns: u64,
) -> Result<Option<impl Future<Output = Result<()>>>> {
    let markers = MARKERS.get().unwrap();
    let _guard = markers.preprocess_blit.get();
    let vulkan = &cx.vulkan;
    let device = &cx.device;
    let pass = &cx.render_pass;

    let Some(desc_set) = pass.desc_sets.pop() else {
        return Err(anyhow!("No available descriptor sets in preprocess blit"));
    };

    let rec_state = vulkan
        .command_recording_state(VulkanGraphicsQueueAccess::Allow)
        .context("Failed to get command recording state")?;

    let frame = rec_state.current_frame_number();

    let semaphore_acquire = cx.semaphore_pool.pop()?;

    let Some(target) = ({
        let _guard = markers.preprocess_blit_acquire.get();
        surface.acquire_next_framebuffer(**semaphore_acquire.get())?
    }) else {
        return Ok(None);
    };

    let framebuffer = &target.framebuffer;

    /*
    let src_accessed = unsafe {
        vulkan.access_texture(
            src as *const vk::Image as *mut c_void,
            None, // whole image
            vk::ImageLayout::SHADER_READ_ONLY_OPTIMAL,
            vk::PipelineStageFlags::FRAGMENT_SHADER,
            vk::AccessFlags::SHADER_READ,
            VulkanResourceAccessMode::PipelineBarrier,
        )
    }
        .context(format!("Failed to access source texture: {src:?}"))?;
     */

    let (src_view, queue, mut command_buffers, fence, semaphore) = {
        let _guard = markers.preprocess_blit_resources.get();

        let src_view = VulkanImageViewHandle::new(
            unsafe {
                device.create_image_view(
                    &vk::ImageViewCreateInfo::default()
                        .image(*src)
                        .view_type(vk::ImageViewType::TYPE_2D)
                        .format(vk::Format::R8G8B8A8_UNORM)
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

        // println!("({frame}) accessing queue");

        let queue = vulkan.instance().graphics_queue();

        // println!("({frame}) allocate_command_buffers");
        let mut command_buffers = unsafe {
            device.allocate_command_buffers(
                &vk::CommandBufferAllocateInfo::default()
                    .command_pool(**pass.command_pool)
                    .level(vk::CommandBufferLevel::PRIMARY)
                    .command_buffer_count(1),
            )
        }
            .map(|v| {
                v.iter()
                    .map(|c| VulkanCommandBuffer::new(pass.command_pool.clone(), *c, device.clone()))
                    .collect::<Vec<VulkanCommandBuffer>>()
            })?;
        // println!("({frame}) create_fence");

        let fence = cx.fence_pool.pop()?;

        // println!("({frame}) create_semaphore");
        let semaphore = cx.semaphore_pool.pop()?;
        (src_view, queue, command_buffers, fence, semaphore)
    };

    let command_buffer = command_buffers.swap_remove(0);
    {
        let _guard = markers.preprocess_blit_commands.get();
        let command_buffer = &command_buffer.command_buffer;
        /*
        let command_buffer = &vulkan
            .command_recording_state(VulkanGraphicsQueueAccess::DontCare)
            .context("Failed to get command recording state")?
            .command_buffer();
        */

        // println!("({frame}) begin_command_buffer");
        unsafe {
            device.begin_command_buffer(*command_buffer, &vk::CommandBufferBeginInfo::default())
        }?;

        let width = surface.width();
        let height = surface.height();

        // println!("({frame}) cmd_begin_render_pass");
        unsafe {
            device.cmd_begin_render_pass(
                *command_buffer,
                &vk::RenderPassBeginInfo::default()
                    .render_pass(*pass.render_pass)
                    .framebuffer(*framebuffer.framebuffer)
                    .render_area(vk::Rect2D {
                        offset: vk::Offset2D { x: 0, y: 0 },
                        extent: vk::Extent2D { width, height },
                    }),
                vk::SubpassContents::INLINE,
            )
        };

        let push_constants_vert = VertPushConstants {
            scale_and_tiling: [1.0, 1.0, 0.0, 0.0],
        };

        // println!("({frame}) cmd_bind_pipeline");
        unsafe {
            device.cmd_bind_pipeline(
                *command_buffer,
                vk::PipelineBindPoint::GRAPHICS,
                *pass.pipelines[0],
            )
        };

        // println!("({frame}) cmd_push_constants (vert)");
        unsafe {
            device.cmd_push_constants(
                *command_buffer,
                *pass.pipeline_layout,
                vk::ShaderStageFlags::VERTEX,
                0,
                std::slice::from_ref(&push_constants_vert)
                    .align_to::<u8>()
                    .1,
            )
        };

        // println!("({frame}) cmd_bind_descriptor_sets");
        unsafe {
            device.cmd_bind_descriptor_sets(
                *command_buffer,
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

        // println!("({frame}) cmd_set_viewport");
        unsafe {
            device.cmd_set_viewport(*command_buffer, 0, &[viewport]);
        }

        let scissor = vk::Rect2D {
            offset: vk::Offset2D { x: 0, y: 0 },
            extent: vk::Extent2D { width, height },
        };

        // println!("({frame}) cmd_set_scissor");
        unsafe {
            device.cmd_set_scissor(*command_buffer, 0, &[scissor]);
        }

        // println!("({frame}) cmd_draw");
        unsafe {
            device.cmd_draw(*command_buffer, 3, 1, 0, 0);
        }

        // println!("({frame}) cmd_end_render_pass");
        unsafe {
            device.cmd_end_render_pass(*command_buffer);
        }

        // println!("({frame}) end_command_buffer");
        unsafe { device.end_command_buffer(*command_buffer) }?;

        {
            let _guard = markers.preprocess_blit_submit.get();
            unsafe {
                device.queue_submit(
                    queue,
                    &[vk::SubmitInfo::default()
                        .command_buffers(&[*command_buffer])
                        .wait_semaphores(&[**semaphore_acquire.get()])
                        .wait_dst_stage_mask(&[vk::PipelineStageFlags::FRAGMENT_SHADER])
                        .signal_semaphores(&[**semaphore.get()])],
                    **fence.get(),
                )
            }
                .context("queue_submit failed")?;
        }
    }

    {
        let _guard = markers.preprocess_blit_present.get();
        // present
        surface.present(&cx, target.clone(), &[**semaphore.get()], timestamp_ns);
    }

    let device = device.clone();
    let resources = BlitResources {
        command_buffer,
        semaphore_acquire,
        semaphore,
        pass: pass.clone(),
        target,
        src_view,
        fence,
        desc_set,
    };

    let now = std::time::Instant::now();


    tokio::task::spawn_blocking(move || {
        unsafe { device.wait_for_fences(&[**resources.fence.get()], true, u64::MAX) };
        drop(resources);
        let elapsed = now.elapsed();
        println!("Fence signaled in {:?}", elapsed);
    });

    Ok(async move {
        Ok(())
    }.into())
}
