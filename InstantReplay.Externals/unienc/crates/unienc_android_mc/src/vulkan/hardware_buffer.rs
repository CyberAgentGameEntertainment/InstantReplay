use crate::error::{AndroidError, Result};
use crate::vulkan::types::{VulkanImageHandle, VulkanImageViewHandle, VulkanMemoryHandle};
use ash::vk;
use std::sync::Arc;

/// A Vulkan image backed by an Android HardwareBuffer
#[allow(dead_code)]
pub struct HardwareBufferImage {
    pub image: VulkanImageHandle,
    pub _memory: VulkanMemoryHandle,
    pub view: VulkanImageViewHandle,
    pub width: u32,
    pub height: u32,
    ahb: *mut ndk_sys::AHardwareBuffer,
}

unsafe impl Send for HardwareBufferImage {}
unsafe impl Sync for HardwareBufferImage {}

impl HardwareBufferImage {
    /// Import an AHardwareBuffer as a Vulkan image
    pub fn from_hardware_buffer(
        device: &Arc<ash::Device>,
        instance: &ash::Instance,
        ahb: *mut ndk_sys::AHardwareBuffer,
    ) -> Result<Self> {
        // Acquire the hardware buffer to ensure it stays valid
        unsafe { ndk_sys::AHardwareBuffer_acquire(ahb) };

        // Get hardware buffer description
        let mut desc: ndk_sys::AHardwareBuffer_Desc = unsafe { std::mem::zeroed() };
        unsafe { ndk_sys::AHardwareBuffer_describe(ahb, &mut desc) };

        let width = desc.width;
        let height = desc.height;

        // Load the external memory android hardware buffer extension
        let external_memory_ahb =
            ash::android::external_memory_android_hardware_buffer::Device::new(instance, device);

        // Get hardware buffer properties
        let mut format_properties = vk::AndroidHardwareBufferFormatPropertiesANDROID::default();
        let mut ahb_properties =
            vk::AndroidHardwareBufferPropertiesANDROID::default().push_next(&mut format_properties);

        unsafe {
            external_memory_ahb.get_android_hardware_buffer_properties(
                ahb as *const std::ffi::c_void,
                &mut ahb_properties,
            )
        }
        .map_err(AndroidError::HardwareBufferPropertiesFailed)?;

        // Extract values after the mutable borrow ends
        let allocation_size = ahb_properties.allocation_size;
        let memory_type_bits = ahb_properties.memory_type_bits;
        let format = format_properties.format;
        let external_format = format_properties.external_format;

        // Create external format info if using external format (format == UNDEFINED)
        let mut external_format_info =
            vk::ExternalFormatANDROID::default().external_format(external_format);

        // Create image with external memory
        let mut external_memory_image_info = vk::ExternalMemoryImageCreateInfo::default()
            .handle_types(vk::ExternalMemoryHandleTypeFlags::ANDROID_HARDWARE_BUFFER_ANDROID);

        let mut image_create_info = vk::ImageCreateInfo::default()
            .image_type(vk::ImageType::TYPE_2D)
            .format(format)
            .extent(vk::Extent3D {
                width,
                height,
                depth: 1,
            })
            .mip_levels(1)
            .array_layers(1)
            .samples(vk::SampleCountFlags::TYPE_1)
            .tiling(vk::ImageTiling::OPTIMAL)
            .usage(vk::ImageUsageFlags::COLOR_ATTACHMENT)
            .sharing_mode(vk::SharingMode::EXCLUSIVE)
            .initial_layout(vk::ImageLayout::UNDEFINED)
            .flags(vk::ImageCreateFlags::MUTABLE_FORMAT)
            .push_next(&mut external_memory_image_info);

        // If format is UNDEFINED, use external format
        if format == vk::Format::UNDEFINED {
            image_create_info = image_create_info.push_next(&mut external_format_info);
        }

        let image = VulkanImageHandle::new(
            unsafe { device.create_image(&image_create_info, None) }
                .map_err(AndroidError::HardwareBufferImageCreationFailed)?,
            device.clone(),
        );

        // Import the hardware buffer as device memory
        let mut import_info = vk::ImportAndroidHardwareBufferInfoANDROID::default()
            .buffer(ahb as *mut std::ffi::c_void);

        let mut dedicated_alloc_info = vk::MemoryDedicatedAllocateInfo::default().image(*image);

        let alloc_info = vk::MemoryAllocateInfo::default()
            .allocation_size(allocation_size)
            .memory_type_index(find_memory_type_index(
                memory_type_bits,
                vk::MemoryPropertyFlags::empty(),
            )?)
            .push_next(&mut dedicated_alloc_info)
            .push_next(&mut import_info);

        let memory = VulkanMemoryHandle::new(
            unsafe { device.allocate_memory(&alloc_info, None) }
                .map_err(AndroidError::HardwareBufferMemoryAllocationFailed)?,
            device.clone(),
        );

        // Bind memory to image
        unsafe { device.bind_image_memory(*image, *memory, 0) }
            .map_err(AndroidError::ImageMemoryBindFailed)?;

        // Create image view
        let view_create_info = vk::ImageViewCreateInfo::default()
            .image(*image)
            .view_type(vk::ImageViewType::TYPE_2D)
            .format(format)
            .components(vk::ComponentMapping {
                r: vk::ComponentSwizzle::IDENTITY,
                g: vk::ComponentSwizzle::IDENTITY,
                b: vk::ComponentSwizzle::IDENTITY,
                a: vk::ComponentSwizzle::IDENTITY,
            })
            .subresource_range(vk::ImageSubresourceRange {
                aspect_mask: vk::ImageAspectFlags::COLOR,
                base_mip_level: 0,
                level_count: 1,
                base_array_layer: 0,
                layer_count: 1,
            });

        // If using external format, we need to use samplerYcbcrConversion
        // For now, we assume RGBA format which doesn't need this
        if format == vk::Format::UNDEFINED {
            // External format requires YCbCr conversion, which is more complex
            // For VIDEO_ENCODE usage with RGBA_8888, we shouldn't hit this path
            return Err(AndroidError::UnsupportedGraphicsFormat(0));
        }

        let view = VulkanImageViewHandle::new(
            unsafe { device.create_image_view(&view_create_info, None) }
                .map_err(AndroidError::ImageViewCreationFailed)?,
            device.clone(),
        );

        Ok(Self {
            image,
            _memory: memory,
            view,
            width,
            height,
            ahb,
        })
    }

    pub fn vk_image(&self) -> vk::Image {
        *self.image
    }

    pub fn vk_image_view(&self) -> vk::ImageView {
        *self.view
    }
}

impl Drop for HardwareBufferImage {
    fn drop(&mut self) {
        // Release the hardware buffer reference
        unsafe { ndk_sys::AHardwareBuffer_release(self.ahb) };
    }
}

fn find_memory_type_index(
    memory_type_bits: u32,
    _required_properties: vk::MemoryPropertyFlags,
) -> Result<u32> {
    // For imported AHardwareBuffer, we just use the first available type
    for i in 0..32 {
        if (memory_type_bits & (1 << i)) != 0 {
            return Ok(i);
        }
    }
    Err(AndroidError::NoSuitableMemoryType)
}
