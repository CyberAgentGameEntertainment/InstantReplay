use bincode::{Decode, Encode};
use jni::{
    JNIEnv,
    objects::{JObject, JString},
    sys::{jint, jlong},
};
use std::future::Future;
use std::pin::Pin;
use std::{collections::HashMap, fmt::Display, sync::Arc, time::Duration};
use unienc_common::{EncodedData, SpawnBlocking, UniencSampleKind, VideoFrameBgra32};

use crate::bindings;
use crate::error::{AndroidError, Result};
use crate::java::*;
use crate::java_api::ApiLevel;

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
        let mime = to_java_string(env, mime_type)?;
        let codec = bindings::MediaCodec::create_encoder_by_type(env, &mime)?;
        let codec = SafeGlobalRef::new(env, codec)?;

        Ok(Self {
            inner: Arc::new(MediaCodecInner { codec }),
        })
    }

    /// Configure the codec
    pub fn configure(&self, format: &SafeGlobalRef) -> Result<()> {
        let env = &mut attach_current_thread()?;
        bindings::MediaCodec::configure(
            env,
            self.inner.codec.as_obj(),
            format.as_obj(),
            &JObject::null(),
            &JObject::null(),
            CONFIGURE_FLAG_ENCODE,
        )?;
        Ok(())
    }

    /// Start the codec
    pub fn start(&self) -> Result<()> {
        let env = &mut attach_current_thread()?;
        bindings::MediaCodec::start(env, self.inner.codec.as_obj())?;
        Ok(())
    }

    /// Stop the codec
    pub fn stop(&self) -> Result<()> {
        let env = &mut attach_current_thread()?;
        bindings::MediaCodec::stop(env, self.inner.codec.as_obj())?;
        Ok(())
    }

    /// Release the codec
    pub fn release(&self) -> Result<()> {
        let env = &mut attach_current_thread()?;
        bindings::MediaCodec::release(env, self.inner.codec.as_obj())?;
        Ok(())
    }

    /// Dequeue an input buffer
    pub fn dequeue_input_buffer(&self, timeout: Duration) -> Result<jint> {
        let env = &mut attach_current_thread()?;
        Ok(bindings::MediaCodec::dequeue_input_buffer(
            env,
            self.inner.codec.as_obj(),
            timeout.as_micros() as jlong,
        )?)
    }

    /// Get an input buffer
    pub fn get_input_buffer(&self, index: jint) -> Result<SafeGlobalRef> {
        let env = &mut attach_current_thread()?;
        let buffer = bindings::MediaCodec::get_input_buffer(env, self.inner.codec.as_obj(), index)?;
        SafeGlobalRef::new(env, buffer)
    }

    /// Get an input image
    pub fn get_input_image(&self, index: jint) -> Result<MediaImage> {
        let env = &mut attach_current_thread()?;

        // getInputImage may return null on some devices
        let image = bindings::MediaCodec::get_input_image(env, self.inner.codec.as_obj(), index)?;
        if image.is_null() {
            return Err(AndroidError::ImageNull);
        }

        let width = bindings::Image::get_width(env, &image)? as u32;
        let height = bindings::Image::get_height(env, &image)? as u32;

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
        let env = &mut attach_current_thread()?;
        bindings::MediaCodec::queue_input_buffer(
            env,
            self.inner.codec.as_obj(),
            index,
            offset as jint,
            size as jint,
            timestamp as jlong,
            flags,
        )?;
        Ok(())
    }

    /// Dequeue an output buffer
    pub fn dequeue_output_buffer(
        &self,
        buffer_info: &SafeGlobalRef,
        timeout_us: i64,
    ) -> Result<jint> {
        let env = &mut attach_current_thread()?;
        Ok(bindings::MediaCodec::dequeue_output_buffer(
            env,
            self.inner.codec.as_obj(),
            buffer_info.as_obj(),
            timeout_us as jlong,
        )?)
    }

    /// Get an output buffer
    pub fn get_output_buffer(&self, index: jint) -> Result<SafeGlobalRef> {
        let env = &mut attach_current_thread()?;
        let buffer =
            bindings::MediaCodec::get_output_buffer(env, self.inner.codec.as_obj(), index)?;
        SafeGlobalRef::new(env, buffer)
    }

    /// Release an output buffer
    pub fn release_output_buffer(&self, index: jint, render: bool) -> Result<()> {
        let env = &mut attach_current_thread()?;
        bindings::MediaCodec::release_output_buffer(env, self.inner.codec.as_obj(), index, render)?;
        Ok(())
    }

    /// Get the output format
    pub fn get_output_format(&self) -> Result<HashMap<String, MediaFormatValue>> {
        let env = &mut attach_current_thread()?;
        let format = bindings::MediaCodec::get_output_format(env, self.inner.codec.as_obj())?;
        format_to_map(env, &format)
    }

    pub fn create_input_surface(&self) -> Result<SafeGlobalRef> {
        let env = &mut attach_current_thread()?;
        let surface = bindings::MediaCodec::create_input_surface(env, self.inner.codec.as_obj())?;
        SafeGlobalRef::new(env, surface)
    }

    pub fn signal_end_of_input_stream(&self) -> Result<()> {
        let env = &mut attach_current_thread()?;
        bindings::MediaCodec::signal_end_of_input_stream(env, self.inner.codec.as_obj())?;
        Ok(())
    }

    pub fn print_codec_info(&self) -> Result<()> {
        let env = &mut attach_current_thread()?;
        let codec_info = bindings::MediaCodec::get_codec_info(env, self.inner.codec.as_obj())?;

        // MediaCodecInfo.getCanonicalName() and isHardwareAccelerated() were added in API 29. On
        // older API levels, fall back to getName() and omit the hardware acceleration flag.
        match ApiLevel::<29>::check()? {
            Some(api) => {
                let name = bindings::MediaCodecInfo::get_canonical_name(env, api, &codec_info)?;
                let name = env.get_string(&name)?.to_str()?.to_string();
                let is_hardware_accelerated =
                    bindings::MediaCodecInfo::is_hardware_accelerated(env, api, &codec_info)?;
                println!(
                    "MediaCodec Info: Name: {}, Hardware Accelerated: {}",
                    name, is_hardware_accelerated
                );
            }
            None => {
                let name = bindings::MediaCodecInfo::get_name(env, &codec_info)?;
                let name = env.get_string(&name)?.to_str()?.to_string();
                println!("MediaCodec Info: Name: {}", name);
            }
        }

        Ok(())
    }

    pub fn print_metrics(&self) -> Result<()> {
        let env = &mut attach_current_thread()?;
        let metrics = bindings::MediaCodec::get_metrics(env, self.inner.codec.as_obj())?;

        let key_set = bindings::PersistableBundle::key_set(env, &metrics)?;
        let iterator = bindings::JavaSet::iterator(env, &key_set)?;

        println!("MediaCodec Metrics:");
        while bindings::JavaIterator::has_next(env, &iterator)? {
            let key = JString::from(bindings::JavaIterator::next(env, &iterator)?);
            let key_rust = env.get_string(&key)?.to_str()?.to_string();

            let value = bindings::PersistableBundle::get(env, &metrics, &key)?;
            let value_str = bindings::JavaObject::to_string(env, &value)?;
            let value_rust = env.get_string(&value_str)?.to_str()?.to_string();

            println!("  {}: {}", key_rust, value_rust);
        }

        Ok(())
    }
}

impl Drop for MediaCodecInner {
    fn drop(&mut self) {
        if let Ok(mut env) = attach_current_thread() {
            // Stop the codec before releasing it
            let _ = bindings::MediaCodec::stop(&mut env, self.codec.as_obj());
            // Release the codec
            let _ = bindings::MediaCodec::release(&mut env, self.codec.as_obj());
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

        let planes_array = bindings::Image::get_planes(env, self.image.as_obj())?;
        let planes_array = jni::objects::JObjectArray::from(planes_array);
        let plane_count = env.get_array_length(&planes_array)? as usize;
        let mut planes = Vec::with_capacity(plane_count);

        for i in 0..plane_count {
            let plane = env.get_object_array_element(&planes_array, i as jint)?;

            let buffer = bindings::ImagePlane::get_buffer(env, &plane)?;
            let pixel_stride = bindings::ImagePlane::get_pixel_stride(env, &plane)?;
            let row_stride = bindings::ImagePlane::get_row_stride(env, &plane)?;

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
            let _ = bindings::Image::close(&mut env, self.image.as_obj());
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

/// `MediaCodec.CONFIGURE_FLAG_ENCODE`
pub const CONFIGURE_FLAG_ENCODE: jint = 1;

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
    let obj = bindings::BufferInfo::new(env)?;
    SafeGlobalRef::new(env, obj)
}

/// Read common buffer info fields (returns offset, size, flags, timestamp)
pub fn read_buffer_info_common(
    env: &mut JNIEnv,
    buffer_info: &SafeGlobalRef,
) -> Result<(usize, usize, jint, i64)> {
    let info = buffer_info.as_obj();
    let offset = bindings::BufferInfo::offset(env, info)? as usize;
    let size = bindings::BufferInfo::size(env, info)? as usize;
    let flags = bindings::BufferInfo::flags(env, info)?;
    let timestamp = bindings::BufferInfo::presentation_time_us(env, info)? as i64;

    Ok((offset, size, flags, timestamp))
}

/// Write data to ByteBuffer
pub fn write_to_buffer(env: &mut JNIEnv, buffer: &SafeGlobalRef, data: &[u8]) -> Result<()> {
    let byte_array = env.new_byte_array(data.len() as jint)?;
    env.set_byte_array_region(&byte_array, 0, unsafe {
        std::slice::from_raw_parts(data.as_ptr() as *const i8, data.len())
    })?;

    bindings::ByteBuffer::put(env, buffer.as_obj(), &byte_array)?;

    Ok(())
}

/// Read data from ByteBuffer
pub fn read_from_buffer(
    env: &mut JNIEnv,
    buffer: &SafeGlobalRef,
    offset: usize,
    size: usize,
) -> Result<Vec<u8>> {
    bindings::ByteBuffer::set_position(env, buffer.as_obj(), offset as jint)?;

    let byte_array = env.new_byte_array(size as jint)?;
    bindings::ByteBuffer::get(env, buffer.as_obj(), &byte_array, 0, size as jint)?;

    // Convert to Vec<u8>
    let mut result = vec![0u8; size];
    env.get_byte_array_region(&byte_array, 0, unsafe {
        std::slice::from_raw_parts_mut(result.as_mut_ptr() as *mut i8, size)
    })?;

    Ok(result)
}

/// Read data from ByteBuffer
pub fn read_from_buffer_all(env: &mut JNIEnv, buffer: &JObject) -> Result<Vec<u8>> {
    bindings::ByteBuffer::set_position(env, buffer, 0)?;

    let size = bindings::ByteBuffer::limit(env, buffer)? as usize;

    let byte_array = env.new_byte_array(size as jint)?;
    bindings::ByteBuffer::get(env, buffer, &byte_array, 0, size as jint)?;

    // Convert to Vec<u8>
    let mut result = vec![0u8; size];
    env.get_byte_array_region(&byte_array, 0, unsafe {
        std::slice::from_raw_parts_mut(result.as_mut_ptr() as *mut i8, size)
    })?;

    Ok(result)
}

/// Set integer parameter on MediaFormat
pub fn set_format_integer(
    env: &mut JNIEnv,
    format: &JObject,
    key: &str,
    value: jint,
) -> Result<()> {
    let key_str = to_java_string(env, key)?;
    bindings::MediaFormat::set_integer(env, format, &key_str, value)?;
    Ok(())
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

/// How long a MediaCodec dequeue is allowed to block before it is retried.
///
/// The call runs on the blocking pool rather than on an executor worker, so the
/// wait costs nothing but a parked thread, and a real timeout is what keeps the
/// retry loop from becoming a busy loop.
pub(crate) const DEQUEUE_TIMEOUT: Duration = Duration::from_millis(100);

/// Waits for a MediaCodec input buffer without occupying an executor worker.
///
/// `MediaCodec.dequeueInputBuffer` blocks its calling thread for up to the
/// timeout. Awaited directly from a task, it therefore holds the worker the task
/// is running on, and the executor has no more workers than the machine has
/// cores. On a two-core device the two push tasks hold both, the pull tasks that
/// release the codec's output buffers never run, the codec never frees an input
/// buffer, and the push tasks wait forever. Handing the call to the blocking pool
/// parks the task instead, which leaves the worker free for the rest of the
/// pipeline.
///
/// Returns an owned future rather than being an `async fn` so that no borrow of
/// the runtime is held across the await; `Runtime` is `Send` but not `Sync`.
pub(crate) fn dequeue_input_buffer_off_executor<R: SpawnBlocking>(
    runtime: &R,
    codec: &MediaCodec,
    timeout: Duration,
) -> Pin<Box<dyn Future<Output = Result<jint>> + Send + 'static>> {
    let codec = codec.clone();
    runtime.spawn_blocking(move || codec.dequeue_input_buffer(timeout))
}

/// The output-side counterpart of [`dequeue_input_buffer_off_executor`], for the
/// same reason.
pub(crate) fn dequeue_output_buffer_off_executor<R: SpawnBlocking>(
    runtime: &R,
    codec: &MediaCodec,
    buffer_info: &SafeGlobalRef,
    timeout: Duration,
) -> Pin<Box<dyn Future<Output = Result<jint>> + Send + 'static>> {
    let codec = codec.clone();
    let buffer_info = buffer_info.clone();
    let timeout_us = timeout.as_micros() as i64;
    runtime.spawn_blocking(move || codec.dequeue_output_buffer(&buffer_info, timeout_us))
}

/// Takes the codec's runtime by value rather than by reference because the
/// future has to be `Send` and `Runtime` is not `Sync`.
pub(crate) async fn pull_encoded_data_with_codec<R: SpawnBlocking>(
    runtime: R,
    codec: &MediaCodec,
    end_of_stream: &mut bool,
) -> Result<Option<CommonEncodedData>> {
    if *end_of_stream {
        return Ok(None);
    }

    // One `BufferInfo` serves every iteration: `dequeueOutputBuffer` overwrites
    // its fields on each successful call.
    let buffer_info = {
        let env = &mut attach_current_thread()?;
        create_buffer_info(env)?
    };

    loop {
        let buffer_index =
            dequeue_output_buffer_off_executor(&runtime, codec, &buffer_info, DEQUEUE_TIMEOUT)
                .await?;

        if buffer_index >= 0 {
            let env = &mut attach_current_thread()?;
            let output_buffer = codec.get_output_buffer(buffer_index)?;
            let (offset, size, flags, timestamp) = read_buffer_info_common(env, &buffer_info)?;

            // Read encoded data
            let encoded_data = read_from_buffer(env, &output_buffer, offset, size)?;

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
        }

        if buffer_index == media_codec_errors::INFO_OUTPUT_FORMAT_CHANGED {
            let map = codec.get_output_format()?;

            let metadata = CommonEncodedData {
                content: CommonEncodedDataContent::FormatInfo(map),
                timestamp: 0.0,
            };
            return Ok(Some(metadata));
        }

        // Anything else, `INFO_TRY_AGAIN_LATER` included, means there is nothing
        // to take yet. The dequeue above already waited, so retry straight away.
    }
}

pub(crate) fn format_to_map(
    env: &mut JNIEnv,
    format: &JObject,
) -> Result<HashMap<String, MediaFormatValue>> {
    // MediaFormat.getKeys() and getValueTypeForKey() were added in API 29. On older API levels
    // the format has to be probed with a fixed list of well-known keys instead.
    let Some(api) = ApiLevel::<29>::check()? else {
        return format_to_map_legacy(env, format);
    };

    // serialize
    let keys = bindings::MediaFormat::get_keys(env, api, format)?;
    let keys_iter = bindings::JavaSet::iterator(env, &keys)?;
    let mut map = HashMap::<String, MediaFormatValue>::new();
    while bindings::JavaIterator::has_next(env, &keys_iter)? {
        // key is string
        let key = JString::from(bindings::JavaIterator::next(env, &keys_iter)?);
        let key_type = bindings::MediaFormat::get_value_type_for_key(env, api, format, &key)?;
        let key_str: String = env.get_string(&key)?.into();

        match key_type {
            media_format_key_type::NULL => {}
            media_format_key_type::INTEGER => {
                let value = bindings::MediaFormat::get_integer(env, format, &key)?;
                map.insert(key_str, MediaFormatValue::Integer(value));
            }
            media_format_key_type::LONG => {
                let value = bindings::MediaFormat::get_long(env, format, &key)?;
                map.insert(key_str, MediaFormatValue::Long(value));
            }
            media_format_key_type::FLOAT => {
                let value = bindings::MediaFormat::get_float(env, format, &key)?;
                map.insert(key_str, MediaFormatValue::Float(value));
            }
            media_format_key_type::STRING => {
                let value = bindings::MediaFormat::get_string(env, format, &key)?;
                let value: String = env.get_string(&value)?.into();
                map.insert(key_str, MediaFormatValue::String(value));
            }
            media_format_key_type::BYTEBUFFER => {
                let value = bindings::MediaFormat::get_byte_buffer(env, format, &key)?;
                let encoded_data = read_from_buffer_all(env, &value)?;
                map.insert(key_str, MediaFormatValue::ByteBuffer(encoded_data));
            }
            _ => {}
        }
    }
    Ok(map)
}

/// Well-known MediaFormat keys probed by [`format_to_map_legacy`].
///
/// MediaFormat.getKeys() (API 29+) enumerates the keys actually present in the format. On older
/// API levels there is no way to enumerate them, so this fixed list is probed instead. It covers
/// the keys that encoder output formats are known to carry, including the codec-specific data
/// ("csd-*") required by MediaMuxer.addTrack().
const LEGACY_MEDIA_FORMAT_KEYS: &[&str] = &[
    // common
    "mime",
    "bitrate",
    "bitrate-mode",
    "max-input-size",
    "durationUs",
    "track-id",
    "language",
    "profile",
    "level",
    "csd-0",
    "csd-1",
    "csd-2",
    // video
    "width",
    "height",
    "color-format",
    "color-range",
    "color-standard",
    "color-transfer",
    "frame-rate",
    "i-frame-interval",
    "rotation-degrees",
    "crop-left",
    "crop-right",
    "crop-top",
    "crop-bottom",
    "stride",
    "slice-height",
    "display-width",
    "display-height",
    "sar-width",
    "sar-height",
    "max-width",
    "max-height",
    "hdr-static-info",
    // audio
    "sample-rate",
    "channel-count",
    "channel-mask",
    "aac-profile",
    "aac-sbr-mode",
    "is-adts",
    "pcm-encoding",
    "encoder-delay",
    "encoder-padding",
];

/// Serialize a MediaFormat on API levels below 29, where MediaFormat.getKeys() and
/// MediaFormat.getValueTypeForKey() are unavailable.
///
/// Instead of enumerating the keys, each key in [`LEGACY_MEDIA_FORMAT_KEYS`] is tested with
/// MediaFormat.containsKey() (available since API 16) and read back through
/// [`get_media_format_value_untyped`].
fn format_to_map_legacy(
    env: &mut JNIEnv,
    format: &JObject,
) -> Result<HashMap<String, MediaFormatValue>> {
    let mut map = HashMap::<String, MediaFormatValue>::new();

    for key in LEGACY_MEDIA_FORMAT_KEYS {
        let key_obj = env.new_string(key)?;

        if !bindings::MediaFormat::contains_key(env, format, &key_obj)? {
            continue;
        }

        match get_media_format_value_untyped(env, format, &key_obj)? {
            Some(value) => {
                map.insert((*key).to_string(), value);
            }
            None => {
                println!(
                    "MediaFormat key '{}' has an unsupported value type; skipped",
                    key
                );
            }
        }
    }

    Ok(map)
}

/// Read a single MediaFormat entry without knowing its value type in advance.
///
/// MediaFormat.getValueTypeForKey() requires API 29, so every typed getter is tried in turn and
/// the ClassCastException raised by a type mismatch is discarded. Returns `None` when none of the
/// supported types match.
fn get_media_format_value_untyped(
    env: &mut JNIEnv,
    format: &JObject,
    key: &JString,
) -> Result<Option<MediaFormatValue>> {
    if let Some(value) = probe(bindings::MediaFormat::get_integer(env, format, key))? {
        return Ok(Some(MediaFormatValue::Integer(value)));
    }
    if let Some(value) = probe(bindings::MediaFormat::get_long(env, format, key))? {
        return Ok(Some(MediaFormatValue::Long(value)));
    }
    if let Some(value) = probe(bindings::MediaFormat::get_float(env, format, key))? {
        return Ok(Some(MediaFormatValue::Float(value)));
    }
    if let Some(value) = probe(bindings::MediaFormat::get_string(env, format, key))? {
        let value: String = env.get_string(&value)?.into();
        return Ok(Some(MediaFormatValue::String(value)));
    }
    if let Some(value) = probe(bindings::MediaFormat::get_byte_buffer(env, format, key))? {
        let data = read_from_buffer_all(env, &value)?;
        return Ok(Some(MediaFormatValue::ByteBuffer(data)));
    }

    Ok(None)
}

/// Maps the exception raised by a getter whose stored value is of another type to `None`.
///
/// The exception itself has already been cleared by the generated wrapper (the getters are
/// declared `#[may_throw]`); a pending JNI exception that is left uncleared aborts the process as
/// soon as control returns to the JVM.
fn probe<T>(result: jni::errors::Result<T>) -> Result<Option<T>> {
    match result {
        Ok(value) => Ok(Some(value)),
        Err(jni::errors::Error::JavaException) => Ok(None),
        Err(error) => Err(error.into()),
    }
}

/// ImageWriter wrapper (API 29+)
/// Used to write HardwareBuffer-backed images to MediaCodec input surface
pub struct ImageWriter {
    writer: SafeGlobalRef,
}

impl ImageWriter {
    /// Create a new ImageWriter
    /// - API 33+: Uses ImageWriter.Builder with explicit HardwareBuffer usage flags for VIDEO_ENCODE
    /// - API 29-32: Uses ImageWriter.newInstance(Surface, int, int) with RGBA_8888 format
    /// - API 28 and below: Not supported (caller should use Bgra32 mode instead)
    pub fn new(surface: &SafeGlobalRef, max_images: i32, width: i32, height: i32) -> Result<Self> {
        let env = &mut attach_current_thread()?;

        let api_level = crate::java_api::device_api_level()?;

        let writer = if let Some(api) = ApiLevel::<33>::check()? {
            // API 33+: Use ImageWriter.Builder with explicit usage flags
            println!("Using ImageWriter.Builder for API level {}", api_level);
            Self::new_with_builder(env, api, surface, max_images, width, height)?
        } else if let Some(api) = ApiLevel::<29>::check()? {
            // API 29-32: Use ImageWriter.newInstance with format parameter
            println!(
                "Using ImageWriter.newInstance with RGBA_8888 format for API level {}",
                api_level
            );
            Self::new_with_static_method(env, api, surface, max_images)?
        } else {
            return Err(AndroidError::UnsupportedApiLevel { required: 29 });
        };

        let writer = SafeGlobalRef::new(env, writer)?;
        Ok(Self { writer })
    }

    /// Create ImageWriter using Builder (API 33+)
    fn new_with_builder<'local>(
        env: &mut JNIEnv<'local>,
        api: ApiLevel<33>,
        surface: &SafeGlobalRef,
        max_images: i32,
        width: i32,
        height: i32,
    ) -> Result<JObject<'local>> {
        let builder = bindings::ImageWriterBuilder::new(env, api, surface.as_obj())?;
        let builder = bindings::ImageWriterBuilder::set_max_images(env, api, &builder, max_images)?;
        let builder =
            bindings::ImageWriterBuilder::set_width_and_height(env, api, &builder, width, height)?;

        // HardwareBuffer.RGBA_8888
        let builder =
            bindings::ImageWriterBuilder::set_hardware_buffer_format(env, api, &builder, 1)?;

        // Set usage flags:
        // USAGE_GPU_SAMPLED_IMAGE (0x100) | USAGE_GPU_COLOR_OUTPUT (0x200) | USAGE_VIDEO_ENCODE (0x10000)
        const USAGE_GPU_SAMPLED_IMAGE: i64 = 0x100;
        const USAGE_GPU_COLOR_OUTPUT: i64 = 0x200;
        const USAGE_VIDEO_ENCODE: i64 = 0x10000;
        let usage = USAGE_GPU_SAMPLED_IMAGE | USAGE_GPU_COLOR_OUTPUT | USAGE_VIDEO_ENCODE;
        let builder = bindings::ImageWriterBuilder::set_usage(env, api, &builder, usage)?;

        Ok(bindings::ImageWriterBuilder::build(env, api, &builder)?)
    }

    /// Create ImageWriter using static newInstance method (API 29-32)
    /// Uses newInstance(Surface, int, int) to specify RGBA_8888 format
    fn new_with_static_method<'local>(
        env: &mut JNIEnv<'local>,
        api: ApiLevel<29>,
        surface: &SafeGlobalRef,
        max_images: i32,
    ) -> Result<JObject<'local>> {
        // PixelFormat.RGBA_8888 = 0x1 (1)
        const PIXEL_FORMAT_RGBA_8888: i32 = 0x1;

        Ok(bindings::ImageWriter::new_instance(
            env,
            api,
            surface.as_obj(),
            max_images,
            PIXEL_FORMAT_RGBA_8888,
        )?)
    }

    /// Dequeue an available input image
    pub fn dequeue_input_image(&self) -> Result<ImageWriterImage> {
        let env = &mut attach_current_thread()?;
        let image = bindings::ImageWriter::dequeue_input_image(env, self.writer.as_obj())?;

        if image.is_null() {
            return Err(AndroidError::DequeueImageNull);
        }

        let image_ref = SafeGlobalRef::new(env, image)?;
        Ok(ImageWriterImage { image: image_ref })
    }

    /// Queue an input image with timestamp
    pub fn queue_input_image(&self, image: ImageWriterImage, timestamp_ns: i64) -> Result<()> {
        let env = &mut attach_current_thread()?;

        bindings::Image::set_timestamp(env, image.image.as_obj(), timestamp_ns)?;
        bindings::ImageWriter::queue_input_image(env, self.writer.as_obj(), image.image.as_obj())?;

        Ok(())
    }
}

impl Drop for ImageWriter {
    fn drop(&mut self) {
        if let Ok(mut env) = attach_current_thread() {
            let _ = bindings::ImageWriter::close(&mut env, self.writer.as_obj());
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

        // Image.getHardwareBuffer() was added in API 28. This type is only reachable through
        // ImageWriter, which this crate only uses on API 29 and later.
        let Some(api) = ApiLevel::<28>::check()? else {
            return Err(AndroidError::UnsupportedApiLevel { required: 28 });
        };
        let hardware_buffer = bindings::Image::get_hardware_buffer(env, api, self.image.as_obj())?;

        if hardware_buffer.is_null() {
            return Err(AndroidError::HardwareBufferNull);
        }

        // Convert Java HardwareBuffer to native AHardwareBuffer*
        // This acquires a reference to the AHardwareBuffer
        let ahb = unsafe {
            ndk_sys::AHardwareBuffer_fromHardwareBuffer(env.get_raw(), hardware_buffer.as_raw())
        };

        // Close the Java HardwareBuffer object to prevent resource leak warning
        // The native AHardwareBuffer reference is still valid
        bindings::HardwareBuffer::close(env, &hardware_buffer)?;

        if ahb.is_null() {
            return Err(AndroidError::AHardwareBufferNull);
        }

        Ok(ahb)
    }
}

impl Drop for ImageWriterImage {
    fn drop(&mut self) {
        if let Ok(mut env) = attach_current_thread() {
            let _ = bindings::Image::close(&mut env, self.image.as_obj());
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
        return Err(AndroidError::UnsupportedPlaneCount(planes.len()));
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
    let format = bindings::MediaFormat::new(env)?;
    for (key, value) in map {
        let key = env.new_string(key)?;
        match value {
            MediaFormatValue::Integer(value) => {
                bindings::MediaFormat::set_integer(env, &format, &key, *value)?;
            }
            MediaFormatValue::Long(value) => {
                bindings::MediaFormat::set_long(env, &format, &key, *value)?;
            }
            MediaFormatValue::Float(value) => {
                bindings::MediaFormat::set_float(env, &format, &key, *value)?;
            }
            MediaFormatValue::String(value) => {
                let value = env.new_string(value)?;
                bindings::MediaFormat::set_string(env, &format, &key, &value)?;
            }
            MediaFormatValue::ByteBuffer(value) => {
                let byte_array = env.new_byte_array(value.len() as jint)?;
                env.set_byte_array_region(&byte_array, 0, unsafe {
                    std::slice::from_raw_parts(value.as_ptr() as *const i8, value.len())
                })?;
                // create a new byte buffer
                let byte_buffer = bindings::ByteBuffer::wrap(env, &byte_array)?;
                if byte_buffer.is_null() {
                    return Err(AndroidError::ByteBufferCreationFailed);
                }

                bindings::MediaFormat::set_byte_buffer(env, &format, &key, &byte_buffer)?;
            }
        }
    }
    Ok(format)
}

/// Get Android API level from `Build.VERSION.SDK_INT` (cached).
pub fn get_android_api_level() -> Result<u32> {
    crate::java_api::device_api_level()
}
