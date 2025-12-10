use anyhow::Result;
use bincode::{Decode, Encode};
use jni::{
    objects::{JByteArray, JObject, JString, JValue},
    sys::{jboolean, jint, jlong},
    JNIEnv,
};
use std::{collections::HashMap, fmt::Display, sync::Arc, time::Duration};
use unienc_common::{EncodedData, UniencSampleKind, VideoFrameBgra32};

use crate::java::*;

/// Inner struct for MediaCodec
struct MediaCodecInner {
    codec: SafeGlobalRef,
}

/// Wrapper struct for MediaCodec (Arc-wrapped for safe sharing)
#[derive(Clone)]
pub struct MediaCodec {
    inner: Arc<MediaCodecInner>,
}

impl MediaCodec {
    /// Create a new MediaCodec encoder
    pub fn create_encoder(mime_type: &str) -> Result<Self> {
        let env = &mut attach_current_thread()?;
        let codec_class = env.find_class("android/media/MediaCodec")?;
        let method_id = env.get_static_method_id(
            &codec_class,
            "createEncoderByType",
            "(Ljava/lang/String;)Landroid/media/MediaCodec;",
        )?;

        let mime = to_java_string(env, mime_type)?;
        let codec = unsafe {
            env.call_static_method_unchecked(
                codec_class,
                method_id,
                jni::signature::ReturnType::Object,
                &[JValue::Object(&mime).as_jni()],
            )
        }?;

        let codec = SafeGlobalRef::new(env, codec.l()?)?;

        Ok(Self {
            inner: Arc::new(MediaCodecInner { codec }),
        })
    }

    /// Configure the codec
    pub fn configure(&self, format: &SafeGlobalRef) -> Result<()> {
        let env = &attach_current_thread()?;
        call_void_method(
            env,
            self.inner.codec.as_obj(),
            "configure",
            "(Landroid/media/MediaFormat;Landroid/view/Surface;Landroid/media/MediaCrypto;I)V",
            &[
                JValue::Object(format.as_obj()),
                JValue::Object(&JObject::null()),
                JValue::Object(&JObject::null()),
                JValue::Int(1), // CONFIGURE_FLAG_ENCODE
            ],
        )
    }

    /// Start the codec
    pub fn start(&self) -> Result<()> {
        let env = &attach_current_thread()?;
        call_void_method(env, self.inner.codec.as_obj(), "start", "()V", &[])
    }

    /// Stop the codec
    pub fn stop(&self) -> Result<()> {
        let env = &attach_current_thread()?;
        call_void_method(env, self.inner.codec.as_obj(), "stop", "()V", &[])
    }

    /// Release the codec
    pub fn release(&self) -> Result<()> {
        let env = &attach_current_thread()?;
        call_void_method(env, self.inner.codec.as_obj(), "release", "()V", &[])
    }

    /// Dequeue an input buffer
    pub fn dequeue_input_buffer(&self, timeout: Duration) -> Result<jint> {
        let env = &mut attach_current_thread()?;
        call_int_method(
            env,
            self.inner.codec.as_obj(),
            "dequeueInputBuffer",
            "(J)I",
            &[JValue::Long(timeout.as_micros() as jlong)],
        )
    }

    /// Get an input buffer
    pub fn get_input_buffer(&self, index: jint) -> Result<SafeGlobalRef> {
        let env = &mut attach_current_thread()?;
        let buffer = call_object_method(
            env,
            self.inner.codec.as_obj(),
            "getInputBuffer",
            "(I)Ljava/nio/ByteBuffer;",
            &[JValue::Int(index)],
        )?;
        SafeGlobalRef::new(env, buffer)
    }

    /// Get an input image (API Level 21+)
    pub fn get_input_image(&self, index: jint) -> Result<MediaImage> {
        let env = &mut attach_current_thread()?;

        // Call getInputImage - it may return null on some devices
        let result = env.call_method(
            self.inner.codec.as_obj(),
            "getInputImage",
            "(I)Landroid/media/Image;",
            &[JValue::Int(index)],
        )?;

        let image = result.l()?;
        if image.is_null() {
            return Err(anyhow::anyhow!("Image is null"));
        }

        // Get width and height
        let width = env.call_method(&image, "getWidth", "()I", &[])?.i()? as u32;
        let height = env.call_method(&image, "getHeight", "()I", &[])?.i()? as u32;

        let image_ref = SafeGlobalRef::new(env, image)?;
        Ok(MediaImage {
            image: image_ref,
            width,
            height,
        })
    }

    /// Queue an input buffer
    pub fn queue_input_buffer(
        &self,
        index: jint,
        offset: usize,
        size: usize,
        timestamp: i64,
        flags: jint,
    ) -> Result<()> {
        let env = &attach_current_thread()?;
        call_void_method(
            env,
            self.inner.codec.as_obj(),
            "queueInputBuffer",
            "(IIIJI)V",
            &[
                JValue::Int(index),
                JValue::Int(offset as jint),
                JValue::Int(size as jint),
                JValue::Long(timestamp as jlong),
                JValue::Int(flags),
            ],
        )
    }

    /// Dequeue an output buffer
    pub fn dequeue_output_buffer(
        &self,
        buffer_info: &SafeGlobalRef,
        timeout_us: i64,
    ) -> Result<jint> {
        let env = &mut attach_current_thread()?;
        call_int_method(
            env,
            self.inner.codec.as_obj(),
            "dequeueOutputBuffer",
            "(Landroid/media/MediaCodec$BufferInfo;J)I",
            &[
                JValue::Object(buffer_info.as_obj()),
                JValue::Long(timeout_us as jlong),
            ],
        )
    }

    /// Get an output buffer
    pub fn get_output_buffer(&self, index: jint) -> Result<SafeGlobalRef> {
        let env = &mut attach_current_thread()?;
        let buffer = call_object_method(
            env,
            self.inner.codec.as_obj(),
            "getOutputBuffer",
            "(I)Ljava/nio/ByteBuffer;",
            &[JValue::Int(index)],
        )?;
        SafeGlobalRef::new(env, buffer)
    }

    /// Release an output buffer
    pub fn release_output_buffer(&self, index: jint, render: bool) -> Result<()> {
        let env = &attach_current_thread()?;
        call_void_method(
            env,
            self.inner.codec.as_obj(),
            "releaseOutputBuffer",
            "(IZ)V",
            &[JValue::Int(index), JValue::Bool(render as jboolean)],
        )
    }

    /// Get the output format
    pub fn get_output_format(&self) -> Result<HashMap<String, MediaFormatValue>> {
        let env = &mut attach_current_thread()?;
        let format = env.call_method(
            self.inner.codec.as_obj(),
            "getOutputFormat",
            "()Landroid/media/MediaFormat;",
            &[],
        )?;
        let format_obj = format.l()?;
        format_to_map(env, &format_obj)
    }

    pub fn create_input_surface(&self) -> Result<SafeGlobalRef> {
        let env = &mut attach_current_thread()?;
        let surface = call_object_method(
            env,
            self.inner.codec.as_obj(),
            "createInputSurface",
            "()Landroid/view/Surface;",
            &[],
        )?;
        SafeGlobalRef::new(env, surface)
    }

    pub fn signal_end_of_input_stream(&self) -> Result<()> {
        let env = &attach_current_thread()?;
        call_void_method(
            env,
            self.inner.codec.as_obj(),
            "signalEndOfInputStream",
            "()V",
            &[],
        )
    }

    pub fn print_codec_info(&self) -> Result<()> {
        let env = &mut attach_current_thread()?;
        let codec_info = call_object_method(
            env,
            self.inner.codec.as_obj(),
            "getCodecInfo",
            "()Landroid/media/MediaCodecInfo;",
            &[],
        )?;

        // getCanonicalName
        let canonical_name = env
            .call_method(&codec_info, "getCanonicalName", "()Ljava/lang/String;", &[])?
            .l()?;
        let canonical_name_str = JString::from(canonical_name);
        let canonical_name_rust = env.get_string(&canonical_name_str)?.to_str()?.to_string();

        // isHardwareAccelerated
        let is_hardware_accelerated = env
            .call_method(codec_info, "isHardwareAccelerated", "()Z", &[])?
            .z()?;

        println!(
            "MediaCodec Info: Canonical Name: {}, Hardware Accelerated: {}",
            canonical_name_rust, is_hardware_accelerated
        );

        Ok(())
    }

    pub fn print_metrics(&self) -> Result<()> {
        let env = &mut attach_current_thread()?;
        let metrics = call_object_method(
            env,
            self.inner.codec.as_obj(),
            "getMetrics",
            "()Landroid/os/PersistableBundle;",
            &[],
        )?;

        // Get the key set
        let key_set = env
            .call_method(&metrics, "keySet", "()Ljava/util/Set;", &[])?
            .l()?;

        let iterator = env
            .call_method(&key_set, "iterator", "()Ljava/util/Iterator;", &[])?
            .l()?;

        println!("MediaCodec Metrics:");
        while env.call_method(&iterator, "hasNext", "()Z", &[])?.z()? {
            let key = env
                .call_method(&iterator, "next", "()Ljava/lang/Object;", &[])?
                .l()?;
            let key_str = JString::from(key);
            let key_rust = env.get_string(&key_str)?.to_str()?.to_string();

            let value = env
                .call_method(
                    &metrics,
                    "get",
                    "(Ljava/lang/String;)Ljava/lang/Object;",
                    &[JValue::Object(&key_str)],
                )?
                .l()?;

            let value_str = env
                .call_method(&value, "toString", "()Ljava/lang/String;", &[])?
                .l()?;
            let value_jstr = JString::from(value_str);
            let value_rust = env.get_string(&value_jstr)?.to_str()?.to_string();

            println!("  {}: {}", key_rust, value_rust);
        }

        Ok(())
    }
}

impl Drop for MediaCodecInner {
    fn drop(&mut self) {
        // Try to stop and release the codec, but don't panic on error
        if let Ok(env) = attach_current_thread() {
            // Stop the codec
            let _ = call_void_method(&env, self.codec.as_obj(), "stop", "()V", &[]);
            // Release the codec
            let _ = call_void_method(&env, self.codec.as_obj(), "release", "()V", &[]);
        }
    }
}

/// Wrapper for Android Media Image
pub struct MediaImage {
    image: SafeGlobalRef,
    width: u32,
    height: u32,
}

impl MediaImage {
    /// Get the image planes (Y, U, V or Y, UV depending on format)
    pub fn get_planes(&self) -> Result<Vec<ImagePlane>> {
        let env = &mut attach_current_thread()?;

        // Call getPlanes() which returns Image.Plane[]
        let planes_array = env
            .call_method(
                self.image.as_obj(),
                "getPlanes",
                "()[Landroid/media/Image$Plane;",
                &[],
            )?
            .l()?;

        let planes_array_ref = jni::objects::JObjectArray::from(planes_array);
        let plane_count = env.get_array_length(&planes_array_ref)? as usize;
        let mut planes = Vec::with_capacity(plane_count);

        for i in 0..plane_count {
            let plane = env.get_object_array_element(&planes_array_ref, i as jint)?;

            // Get buffer
            let buffer = env
                .call_method(&plane, "getBuffer", "()Ljava/nio/ByteBuffer;", &[])?
                .l()?;

            // Get pixel stride
            let pixel_stride = env.call_method(&plane, "getPixelStride", "()I", &[])?.i()?;

            // Get row stride
            let row_stride = env.call_method(&plane, "getRowStride", "()I", &[])?.i()?;

            let buffer_ref = SafeGlobalRef::new(env, buffer)?;

            let (base_ptr, _capacity, position) = get_direct_buffer_info(env, buffer_ref.as_obj())?;
            let ptr = unsafe { base_ptr.add(position) };
            planes.push(ImagePlane {
                _buffer: buffer_ref,
                ptr,
                pixel_stride,
                row_stride,
            });
        }

        Ok(planes)
    }

    pub fn width(&self) -> u32 {
        self.width
    }

    pub fn height(&self) -> u32 {
        self.height
    }
}

impl Drop for MediaImage {
    fn drop(&mut self) {
        // Close the image to release resources
        if let Ok(mut env) = attach_current_thread() {
            let _ = env.call_method(self.image.as_obj(), "close", "()V", &[]);
        }
    }
}

/// Wrapper for Image.Plane
pub struct ImagePlane {
    pub _buffer: SafeGlobalRef,
    pub ptr: *mut u8,
    pub pixel_stride: jint,
    pub row_stride: jint,
}

impl Display for ImagePlane {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "ImagePlane(ptr: {:?}, pixel_stride: {}, row_stride: {})",
            self.ptr, self.pixel_stride, self.row_stride
        )
    }
}

impl ImagePlane {
    /// Write data to this plane with the given subsample factors using direct memory access
    pub fn write_component_data(
        &self,
        data: &[u8],
        width: u32,
        height: u32,
        h_subsample: u32,
        v_subsample: u32,
    ) -> Result<()> {
        let plane_width = width / h_subsample;
        let plane_height = height / v_subsample;

        // Get direct buffer address, capacity and current position

        unsafe {
            // Calculate the actual buffer start considering position
            let buffer_ptr = self.ptr; //base_ptr.add(position);

            if self.pixel_stride == 1 {
                // Optimized path for contiguous pixels (I420 format)
                for y in 0..plane_height {
                    let src_start = (y * plane_width) as usize;
                    let dst_start = (y as i32 * self.row_stride) as usize;

                    // Direct memory copy for the entire row
                    let src_slice = &data[src_start..(src_start + plane_width as usize)];
                    let dst_ptr = buffer_ptr.add(dst_start);

                    std::ptr::copy_nonoverlapping(
                        src_slice.as_ptr(),
                        dst_ptr,
                        plane_width as usize,
                    );
                }
            } else {
                // Generic path for any pixel stride (NV12/NV21 format)
                for y in 0..plane_height {
                    for x in 0..plane_width {
                        let src_idx = (y * plane_width + x) as usize;
                        let dst_offset =
                            (y as i32 * self.row_stride + x as i32 * self.pixel_stride) as usize;

                        // Direct memory write
                        let dst_ptr = buffer_ptr.add(dst_offset);
                        *dst_ptr = data[src_idx];
                    }
                }
            }
        }

        Ok(())
    }
}

/// MediaCodec error codes
pub mod media_codec_errors {
    use jni::sys::jint;

    pub const INFO_TRY_AGAIN_LATER: jint = -1;
    pub const INFO_OUTPUT_FORMAT_CHANGED: jint = -2;
    pub const INFO_OUTPUT_BUFFERS_CHANGED: jint = -3;
}

pub mod media_codec_buffer_flag {
    use jni::sys::jint;

    pub const BUFFER_FLAG_KEY_FRAME: jint = 1;
    pub const BUFFER_FLAG_CODEC_CONFIG: jint = 2;
    pub const BUFFER_FLAG_END_OF_STREAM: jint = 4;
    pub const BUFFER_FLAG_PARTIAL_FRAME: jint = 8;
    pub const BUFFER_FLAG_DECODE_ONLY: jint = 32;
}

pub mod media_format_key_type {
    pub const NULL: i32 = 0;
    pub const INTEGER: i32 = 1;
    pub const LONG: i32 = 2;
    pub const FLOAT: i32 = 3;
    pub const STRING: i32 = 4;
    pub const BYTEBUFFER: i32 = 5;
}

#[derive(Encode, Decode)]
pub enum MediaFormatValue {
    Integer(i32),
    Long(i64),
    Float(f32),
    String(String),
    ByteBuffer(Vec<u8>),
}

/// Create MediaCodec BufferInfo
pub fn create_buffer_info(env: &mut JNIEnv) -> Result<SafeGlobalRef> {
    let class = env.find_class("android/media/MediaCodec$BufferInfo")?;
    let obj = env.new_object(class, "()V", &[])?;
    SafeGlobalRef::new(env, obj)
}

/// Read common buffer info fields (returns offset, size, flags, timestamp)
pub fn read_buffer_info_common(
    env: &mut JNIEnv,
    buffer_info: &SafeGlobalRef,
) -> Result<(usize, usize, jint, i64)> {
    let offset = get_int_field(env, buffer_info.as_obj(), "offset")? as usize;
    let size = get_int_field(env, buffer_info.as_obj(), "size")? as usize;
    let flags = get_int_field(env, buffer_info.as_obj(), "flags")?;
    let timestamp = get_long_field(env, buffer_info.as_obj(), "presentationTimeUs")? as i64;

    Ok((offset, size, flags, timestamp))
}

/// Write data to ByteBuffer
pub fn write_to_buffer(env: &JNIEnv, buffer: &SafeGlobalRef, data: &[u8]) -> Result<()> {
    let byte_array = env.new_byte_array(data.len() as jint)?;
    env.set_byte_array_region(&byte_array, 0, unsafe {
        std::slice::from_raw_parts(data.as_ptr() as *const i8, data.len())
    })?;

    call_void_method(
        env,
        buffer.as_obj(),
        "put",
        "([B)Ljava/nio/ByteBuffer;",
        &[JValue::Object(&JByteArray::from(byte_array).into())],
    )?;

    Ok(())
}

/// Read data from ByteBuffer
pub fn read_from_buffer(
    env: &JNIEnv,
    buffer: &SafeGlobalRef,
    offset: usize,
    size: usize,
) -> Result<Vec<u8>> {
    // Set position
    call_void_method(
        env,
        buffer.as_obj(),
        "position",
        "(I)Ljava/nio/Buffer;",
        &[JValue::Int(offset as jint)],
    )?;

    // Create byte array
    let byte_array = env.new_byte_array(size as jint)?;

    // Get data
    call_void_method(
        env,
        buffer.as_obj(),
        "get",
        "([BII)Ljava/nio/ByteBuffer;",
        &[
            JValue::Object(&byte_array),
            JValue::Int(0),
            JValue::Int(size as jint),
        ],
    )?;

    // Convert to Vec<u8>
    let mut result = vec![0u8; size];
    env.get_byte_array_region(&byte_array, 0, unsafe {
        std::slice::from_raw_parts_mut(result.as_mut_ptr() as *mut i8, size)
    })?;

    Ok(result)
}

/// Read data from ByteBuffer
pub fn read_from_buffer_all(env: &mut JNIEnv, buffer: &JObject) -> Result<Vec<u8>> {
    // Set position
    call_void_method(
        env,
        buffer,
        "position",
        "(I)Ljava/nio/Buffer;",
        &[JValue::Int(0 as jint)],
    )?;

    let size = env.call_method(buffer, "limit", "()I", &[])?.i()? as usize;

    // Create byte array
    let byte_array = env.new_byte_array(size as jint)?;

    // Get data
    call_void_method(
        env,
        buffer,
        "get",
        "([BII)Ljava/nio/ByteBuffer;",
        &[
            JValue::Object(&byte_array),
            JValue::Int(0),
            JValue::Int(size as jint),
        ],
    )?;

    // Convert to Vec<u8>
    let mut result = vec![0u8; size];
    env.get_byte_array_region(&byte_array, 0, unsafe {
        std::slice::from_raw_parts_mut(result.as_mut_ptr() as *mut i8, size)
    })?;

    Ok(result)
}

/// Set integer parameter on MediaFormat
pub fn set_format_integer(env: &JNIEnv, format: &JObject, key: &str, value: jint) -> Result<()> {
    let key_str = to_java_string(env, key)?;
    call_void_method(
        env,
        format,
        "setInteger",
        "(Ljava/lang/String;I)V",
        &[JValue::Object(&key_str), JValue::Int(value)],
    )
}

#[derive(Encode, Decode)]
pub struct CommonEncodedData {
    pub content: CommonEncodedDataContent,
    pub timestamp: f64,
}

#[derive(Encode, Decode)]
pub enum CommonEncodedDataContent {
    Buffer { data: Vec<u8>, buffer_flag: jint },
    FormatInfo(HashMap<String, MediaFormatValue>),
}

impl EncodedData for CommonEncodedData {
    fn timestamp(&self) -> f64 {
        self.timestamp
    }

    fn set_timestamp(&mut self, timestamp: f64) {
        self.timestamp = timestamp;
    }

    fn kind(&self) -> UniencSampleKind {
        match self.content {
            CommonEncodedDataContent::Buffer { buffer_flag, .. } => {
                if (buffer_flag & media_codec_buffer_flag::BUFFER_FLAG_KEY_FRAME) != 0 {
                    UniencSampleKind::Key
                } else if (buffer_flag & media_codec_buffer_flag::BUFFER_FLAG_CODEC_CONFIG) != 0 {
                    UniencSampleKind::Metadata
                } else {
                    UniencSampleKind::Interpolated
                }
            }
            CommonEncodedDataContent::FormatInfo(_) => UniencSampleKind::Metadata,
        }
    }
}

pub(crate) async fn pull_encoded_data_with_codec(
    codec: &MediaCodec,
    end_of_stream: &mut bool,
) -> Result<Option<CommonEncodedData>> {
    if *end_of_stream {
        return Ok(None);
    }
    loop {
        let mut sleep = false;
        {
            let env = &mut attach_current_thread()?;
            let buffer_info = create_buffer_info(env)?;
            let buffer_index = codec.dequeue_output_buffer(&buffer_info, 0)?;

            if buffer_index >= 0 {
                let output_buffer = codec.get_output_buffer(buffer_index)?;
                let (offset, size, flags, timestamp) = read_buffer_info_common(env, &buffer_info)?;

                // Read encoded data
                let encoded_data = read_from_buffer(env, &output_buffer, offset, size)?;

                // println!("new frame data: is_video: {}, flags: {:?}, length: {}, timestamp: {}, offset: {}, {:?}", is_video, flags, encoded_data.len(), timestamp, offset, encoded_data.iter().take(32).collect::<Vec<_>>());

                let video_data = CommonEncodedData {
                    content: CommonEncodedDataContent::Buffer {
                        data: encoded_data,
                        buffer_flag: flags,
                    },
                    timestamp: timestamp as f64 / 1_000_000.0, // Convert from microseconds
                };

                codec.release_output_buffer(buffer_index, false)?;

                if (flags & media_codec_buffer_flag::BUFFER_FLAG_END_OF_STREAM) != 0 {
                    *end_of_stream = true;
                }
                return Ok(Some(video_data));
            } else if buffer_index == media_codec_errors::INFO_TRY_AGAIN_LATER {
                sleep = true;
            } else if buffer_index == media_codec_errors::INFO_OUTPUT_FORMAT_CHANGED {
                let map = codec.get_output_format()?;

                let metadata = CommonEncodedData {
                    content: CommonEncodedDataContent::FormatInfo(map),
                    timestamp: 0.0,
                };
                return Ok(Some(metadata));
            }
        }
        if sleep {
            tokio::task::yield_now().await;
        }
    }
}

pub(crate) fn format_to_map(
    env: &mut JNIEnv,
    format: &JObject,
) -> Result<HashMap<String, MediaFormatValue>> {
    // serialize
    let keys = env
        .call_method(format, "getKeys", "()Ljava/util/Set;", &[])?
        .l()?;
    let keys_iter = env
        .call_method(keys, "iterator", "()Ljava/util/Iterator;", &[])?
        .l()?;
    let mut map = HashMap::<String, MediaFormatValue>::new();
    while env.call_method(&keys_iter, "hasNext", "()Z", &[])?.z()? {
        // key is string

        let key = env
            .call_method(&keys_iter, "next", "()Ljava/lang/Object;", &[])?
            .l()?;
        let key = JString::from(key);
        let key_type = env
            .call_method(
                format,
                "getValueTypeForKey",
                "(Ljava/lang/String;)I",
                &[JValue::Object(&key)],
            )?
            .i()?;
        let key_str = env.get_string(&key)?;

        match key_type {
            media_format_key_type::NULL => {}
            media_format_key_type::INTEGER => {
                let value = env
                    .call_method(
                        format,
                        "getInteger",
                        "(Ljava/lang/String;)I",
                        &[JValue::Object(&key)],
                    )?
                    .i()?;
                map.insert(key_str.into(), MediaFormatValue::Integer(value));
            }
            media_format_key_type::LONG => {
                let value = env
                    .call_method(
                        format,
                        "getLong",
                        "(Ljava/lang/String;)J",
                        &[JValue::Object(&key)],
                    )?
                    .j()?;
                map.insert(key_str.into(), MediaFormatValue::Long(value));
            }
            media_format_key_type::FLOAT => {
                let value = env
                    .call_method(
                        format,
                        "getFloat",
                        "(Ljava/lang/String;)F",
                        &[JValue::Object(&key)],
                    )?
                    .f()?;
                map.insert(key_str.into(), MediaFormatValue::Float(value));
            }
            media_format_key_type::STRING => {
                let value = env
                    .call_method(
                        format,
                        "getString",
                        "(Ljava/lang/String;)Ljava/lang/String;",
                        &[JValue::Object(&key)],
                    )?
                    .l()?;
                let value_str = JString::from(value);
                let value = env.get_string(&value_str)?;
                map.insert(key_str.into(), MediaFormatValue::String(value.into()));
            }
            media_format_key_type::BYTEBUFFER => {
                let value = env
                    .call_method(
                        format,
                        "getByteBuffer",
                        "(Ljava/lang/String;)Ljava/nio/ByteBuffer;",
                        &[JValue::Object(&key)],
                    )?
                    .l()?;
                let encoded_data = crate::common::read_from_buffer_all(env, &value)?;
                map.insert(key_str.into(), MediaFormatValue::ByteBuffer(encoded_data));
            }
            _ => {}
        }
    }
    Ok(map)
}

/// ImageWriter wrapper (API 29+)
/// Used to write HardwareBuffer-backed images to MediaCodec input surface
pub struct ImageWriter {
    writer: SafeGlobalRef,
}

impl ImageWriter {
    /// Create a new ImageWriter using ImageWriter.Builder (API 29+)
    /// with explicit HardwareBuffer usage flags for VIDEO_ENCODE
    pub fn new(surface: &SafeGlobalRef, max_images: i32, width: i32, height: i32) -> Result<Self> {
        let env = &mut attach_current_thread()?;

        // Create ImageWriter.Builder
        let builder_class = env.find_class("android/media/ImageWriter$Builder")?;
        let builder = env.new_object(
            &builder_class,
            "(Landroid/view/Surface;)V",
            &[JValue::Object(surface.as_obj())],
        )?;

        // Set max images
        let builder = env
            .call_method(
                &builder,
                "setMaxImages",
                "(I)Landroid/media/ImageWriter$Builder;",
                &[JValue::Int(max_images)],
            )?
            .l()?;

        // Set size
        let builder = env
            .call_method(
                &builder,
                "setWidthAndHeight",
                "(II)Landroid/media/ImageWriter$Builder;",
                &[JValue::Int(width), JValue::Int(height)],
            )?
            .l()?;

        // Set HardwareBuffer format (RGBA_8888 = 1)
        let builder = env
            .call_method(
                &builder,
                "setHardwareBufferFormat",
                "(I)Landroid/media/ImageWriter$Builder;",
                &[JValue::Int(1)], // HardwareBuffer.RGBA_8888
            )?
            .l()?;

        // Set usage flags:
        // USAGE_GPU_SAMPLED_IMAGE (0x100) | USAGE_GPU_COLOR_OUTPUT (0x200) | USAGE_VIDEO_ENCODE (0x10000)
        const USAGE_GPU_SAMPLED_IMAGE: i64 = 0x100;
        const USAGE_GPU_COLOR_OUTPUT: i64 = 0x200;
        const USAGE_VIDEO_ENCODE: i64 = 0x10000;
        let usage = USAGE_GPU_SAMPLED_IMAGE | USAGE_GPU_COLOR_OUTPUT | USAGE_VIDEO_ENCODE;

        let builder = env
            .call_method(
                &builder,
                "setUsage",
                "(J)Landroid/media/ImageWriter$Builder;",
                &[JValue::Long(usage)],
            )?
            .l()?;

        // Build the ImageWriter
        let writer = env
            .call_method(&builder, "build", "()Landroid/media/ImageWriter;", &[])?
            .l()?;

        let writer = SafeGlobalRef::new(env, writer)?;
        Ok(Self { writer })
    }

    /// Dequeue an available input image
    pub fn dequeue_input_image(&self) -> Result<ImageWriterImage> {
        let env = &mut attach_current_thread()?;
        let image = env
            .call_method(
                self.writer.as_obj(),
                "dequeueInputImage",
                "()Landroid/media/Image;",
                &[],
            )?
            .l()?;

        if image.is_null() {
            return Err(anyhow::anyhow!("dequeueInputImage returned null"));
        }

        let image_ref = SafeGlobalRef::new(env, image)?;
        Ok(ImageWriterImage { image: image_ref })
    }

    /// Queue an input image with timestamp
    pub fn queue_input_image(&self, image: ImageWriterImage, timestamp_ns: i64) -> Result<()> {
        let env = &mut attach_current_thread()?;

        // Set timestamp on the image
        env.call_method(
            image.image.as_obj(),
            "setTimestamp",
            "(J)V",
            &[JValue::Long(timestamp_ns)],
        )?;

        // Queue the image
        env.call_method(
            self.writer.as_obj(),
            "queueInputImage",
            "(Landroid/media/Image;)V",
            &[JValue::Object(image.image.as_obj())],
        )?;

        Ok(())
    }
}

impl Drop for ImageWriter {
    fn drop(&mut self) {
        if let Ok(env) = attach_current_thread() {
            let _ = call_void_method(&env, self.writer.as_obj(), "close", "()V", &[]);
        }
    }
}

/// Image from ImageWriter
pub struct ImageWriterImage {
    image: SafeGlobalRef,
}

impl ImageWriterImage {
    /// Get the HardwareBuffer associated with this image
    pub fn get_hardware_buffer(&self) -> Result<*mut ndk_sys::AHardwareBuffer> {
        let env = &mut attach_current_thread()?;

        // Get HardwareBuffer from Image
        let hardware_buffer = env
            .call_method(
                self.image.as_obj(),
                "getHardwareBuffer",
                "()Landroid/hardware/HardwareBuffer;",
                &[],
            )?
            .l()?;

        if hardware_buffer.is_null() {
            return Err(anyhow::anyhow!("getHardwareBuffer returned null"));
        }

        // Convert Java HardwareBuffer to native AHardwareBuffer*
        // This acquires a reference to the AHardwareBuffer
        let ahb = unsafe {
            ndk_sys::AHardwareBuffer_fromHardwareBuffer(env.get_raw(), hardware_buffer.as_raw())
        };

        // Close the Java HardwareBuffer object to prevent resource leak warning
        // The native AHardwareBuffer reference is still valid
        env.call_method(&hardware_buffer, "close", "()V", &[])?;

        if ahb.is_null() {
            return Err(anyhow::anyhow!(
                "AHardwareBuffer_fromHardwareBuffer returned null"
            ));
        }

        Ok(ahb)
    }
}

impl Drop for ImageWriterImage {
    fn drop(&mut self) {
        if let Ok(mut env) = attach_current_thread() {
            let _ = env.call_method(self.image.as_obj(), "close", "()V", &[]);
        }
    }
}

/// Write ARGB data to YUV image planes with padding for 16-byte alignment
pub fn write_bgra_to_yuv_planes_with_padding(
    sample: &VideoFrameBgra32,
    padded_width: u32,
    padded_height: u32,
    planes: &[ImagePlane],
) -> Result<()> {
    if planes.len() != 3 {
        return Err(anyhow::anyhow!(
            "Unsupported number of planes: {}",
            planes.len()
        ));
    }

    let (y_data, u_data, v_data) = sample.to_yuv420_planes(Some((padded_width, padded_height)))?;
    /*
    println!("padded: {}x{}", padded_width, padded_height);
    println!("Y: {}", planes[0]);
    println!("U: {}", planes[1]);
    println!("V: {}", planes[2]);
    */

    // Write to planes using padded dimensions
    planes[0].write_component_data(&y_data, padded_width, padded_height, 1, 1)?;
    planes[1].write_component_data(&u_data, padded_width, padded_height, 2, 2)?;
    planes[2].write_component_data(&v_data, padded_width, padded_height, 2, 2)?;

    Ok(())
}

pub(crate) fn map_to_format<'a>(
    env: &mut JNIEnv<'a>,
    map: &HashMap<String, MediaFormatValue>,
) -> Result<JObject<'a>> {
    let mut format = env.new_object("android/media/MediaFormat", "()V", &[])?;
    for (key, value) in map {
        match value {
            MediaFormatValue::Integer(value) => {
                env.call_method(
                    &mut format,
                    "setInteger",
                    "(Ljava/lang/String;I)V",
                    &[
                        JValue::Object(&env.new_string(key)?.into()),
                        JValue::Int(*value),
                    ],
                )?;
            }
            MediaFormatValue::Long(value) => {
                env.call_method(
                    &mut format,
                    "setLong",
                    "(Ljava/lang/String;J)V",
                    &[
                        JValue::Object(&env.new_string(key)?.into()),
                        JValue::Long(*value),
                    ],
                )?;
            }
            MediaFormatValue::Float(value) => {
                env.call_method(
                    &mut format,
                    "setFloat",
                    "(Ljava/lang/String;F)V",
                    &[
                        JValue::Object(&env.new_string(key)?.into()),
                        JValue::Float(*value),
                    ],
                )?;
            }
            MediaFormatValue::String(value) => {
                env.call_method(
                    &mut format,
                    "setString",
                    "(Ljava/lang/String;Ljava/lang/String;)V",
                    &[
                        JValue::Object(&env.new_string(key)?.into()),
                        JValue::Object(&env.new_string(value)?.into()),
                    ],
                )?;
            }
            MediaFormatValue::ByteBuffer(value) => {
                let byte_array = env.new_byte_array(value.len() as jint)?;
                env.set_byte_array_region(&byte_array, 0, unsafe {
                    std::slice::from_raw_parts(value.as_ptr() as *const i8, value.len())
                })?;
                // create a new byte buffer
                let jni::objects::JValueGen::Object(byte_buffer) = env.call_static_method(
                    "java/nio/ByteBuffer",
                    "wrap",
                    "([B)Ljava/nio/ByteBuffer;",
                    &[JValue::Object(&byte_array)],
                )?
                else {
                    return Err(anyhow::anyhow!("Failed to create byte buffer"));
                };

                env.call_method(
                    &mut format,
                    "setByteBuffer",
                    "(Ljava/lang/String;Ljava/nio/ByteBuffer;)V",
                    &[
                        JValue::Object(&env.new_string(key)?.into()),
                        JValue::Object(&byte_buffer),
                    ],
                )?;
            }
        }
    }
    Ok(format)
}
