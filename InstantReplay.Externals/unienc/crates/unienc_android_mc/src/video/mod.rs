use jni::{objects::JValue, signature::ReturnType, sys::jint, JNIEnv};
use std::sync::Arc;
use std::time::Duration;
use unienc_common::{Encoder, EncoderInput, EncoderOutput, VideoFrame, VideoSample};

use crate::error::{AndroidError, OptionExt, Result};
use crate::{java::*, VulkanTexture};

use crate::vulkan::hardware_buffer_surface::HardwareBufferSurface;
use crate::{
    common::{media_codec_buffer_flag::BUFFER_FLAG_END_OF_STREAM, *},
    config::{format_keys::*, *},
};

pub struct MediaCodecVideoEncoder {
    input: MediaCodecVideoEncoderInput,
    output: MediaCodecVideoEncoderOutput,
}

#[allow(dead_code)]
pub struct MediaCodecVideoEncoderInput {
    codec: MediaCodec,
    original_width: u32,
    original_height: u32,
    padded_width: u32,
    padded_height: u32,
    last_timestamp: i64,
    processor: MediaCodecVideoEncoderInputProcessor,
}

struct UninitializedState {
    tx: tokio::sync::oneshot::Sender<()>,
    bitrate: u32,
    fps_hint: u32,
}

enum MediaCodecVideoEncoderInputProcessor {
    Uninitialized(UninitializedState),
    Buffer(),
    HardwareBuffer(Arc<HardwareBufferSurface>),
}

unsafe impl Send for MediaCodecVideoEncoderInput {}

pub struct MediaCodecVideoEncoderOutput {
    codec: MediaCodec,
    end_of_stream: bool,
    initialization: Option<tokio::sync::oneshot::Receiver<()>>,
}

impl Encoder for MediaCodecVideoEncoder {
    type InputType = MediaCodecVideoEncoderInput;
    type OutputType = MediaCodecVideoEncoderOutput;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl Drop for MediaCodecVideoEncoderInput {
    fn drop(&mut self) {
        // notify end of stream
        || -> Result<()> {
            match &self.processor {
                MediaCodecVideoEncoderInputProcessor::Uninitialized(_) => Ok(()),
                MediaCodecVideoEncoderInputProcessor::Buffer() => loop {
                    let buffer_index = self
                        .codec
                        .dequeue_input_buffer(Duration::from_millis(100))?;
                    if buffer_index >= 0 {
                        self.codec.queue_input_buffer(
                            buffer_index,
                            0,
                            0,
                            self.last_timestamp,
                            BUFFER_FLAG_END_OF_STREAM,
                        )?;
                        return Ok(());
                    }
                    if buffer_index == media_codec_errors::INFO_TRY_AGAIN_LATER {
                        std::thread::sleep(Duration::from_millis(10));
                    } else {
                        return Err(AndroidError::NoInputBuffer);
                    }
                },
                MediaCodecVideoEncoderInputProcessor::HardwareBuffer(_) => {
                    self.codec.print_metrics()?;
                    self.codec.signal_end_of_input_stream()?;
                    Ok(())
                }
            }
        }()
        .unwrap();
    }
}

impl MediaCodecVideoEncoder {
    pub fn new<V: unienc_common::VideoEncoderOptions>(options: &V) -> Result<Self> {
        // Calculate original and padded sizes
        let original_width = options.width();
        let original_height = options.height();

        fn round_up_to_16(value: u32) -> u32 {
            (value + 15) & !15
        }
        let padded_width = round_up_to_16(original_width);
        let padded_height = round_up_to_16(original_height);

        // Create encoder using the wrapper (configure is deferred until first frame)
        let codec = MediaCodec::create_encoder(MIME_TYPE_VIDEO_AVC)?;

        // Clone for both input and output
        let codec_input = codec.clone();
        let codec_output = codec;

        // initialization
        let (tx, rx) = tokio::sync::oneshot::channel();

        Ok(Self {
            input: MediaCodecVideoEncoderInput {
                codec: codec_input,
                original_width,
                original_height,
                padded_width,
                padded_height,
                last_timestamp: 0,
                processor: MediaCodecVideoEncoderInputProcessor::Uninitialized(UninitializedState {
                    tx,
                    bitrate: options.bitrate(),
                    fps_hint: options.fps_hint(),
                }),
            },
            output: MediaCodecVideoEncoderOutput {
                codec: codec_output,
                end_of_stream: false,
                initialization: rx.into(),
            },
        })
    }
}

impl EncoderInput for MediaCodecVideoEncoderInput {
    type Data = VideoSample<VulkanTexture>;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        push_video_impl(self, data).await.map_err(Into::into)
    }
}

async fn push_video_impl(
    this: &mut MediaCodecVideoEncoderInput,
    data: VideoSample<VulkanTexture>,
) -> Result<()> {
    match data.frame {
        VideoFrame::Bgra32(frame) => {
            match &this.processor {
                MediaCodecVideoEncoderInputProcessor::Uninitialized(_) => {
                    // setup for buffer input mode with YUV420_FLEXIBLE
                    let MediaCodecVideoEncoderInputProcessor::Uninitialized(state) =
                        std::mem::replace(
                            &mut this.processor,
                            MediaCodecVideoEncoderInputProcessor::Buffer(),
                        )
                    else {
                        unreachable!();
                    };

                    // Configure encoder with YUV420_FLEXIBLE format for buffer input
                    let env = &mut attach_current_thread()?;
                    let format = create_video_format_raw(
                        env,
                        this.padded_width,
                        this.padded_height,
                        state.bitrate,
                        state.fps_hint,
                        false, // use_surface = false for buffer mode
                    )?;
                    this.codec.configure(&format)?;
                    _ = this.codec.print_codec_info();

                    this.codec.start()?;
                    _ = state.tx.send(());
                }
                MediaCodecVideoEncoderInputProcessor::Buffer() => {}
                _ => {
                    return Err(AndroidError::EncoderInputMismatch);
                }
            }

            let mut buffer_index;
            loop {
                let sleep;
                {
                    // Get input buffer
                    buffer_index = this
                        .codec
                        .dequeue_input_buffer(Duration::from_millis(100))?;
                    if buffer_index == media_codec_errors::INFO_TRY_AGAIN_LATER {
                        sleep = true;
                    } else if buffer_index < 0 {
                        return Err(AndroidError::NoInputBuffer);
                    } else {
                        break;
                    }
                }
                if sleep {
                    tokio::time::sleep(Duration::from_millis(10)).await;
                }
            }

            let buffer = this.codec.get_input_buffer(buffer_index)?;
            let env = &mut attach_current_thread()?;
            let (_base_ptr, capacity, position) = get_direct_buffer_info(env, buffer.as_obj())?;
            let size = capacity - position;

            let image = this.codec.get_input_image(buffer_index)?;

            // Use Image-based approach with dynamic plane layout and padding
            let planes = image.get_planes()?;
            crate::common::write_bgra_to_yuv_planes_with_padding(
                &frame,
                this.padded_width,
                this.padded_height,
                &planes,
            )?;

            let timestamp = (data.timestamp * 1_000_000.0) as i64;
            this.last_timestamp = timestamp;

            // Queue input buffer - size is determined by the Image object
            this.codec.queue_input_buffer(
                buffer_index,
                0,
                size,
                timestamp, // Convert to microseconds
                0,
            )?;

            Ok(())
        }
        VideoFrame::BlitSource {
            source,
            width,
            height,
            graphics_format,
            flip_vertically,
            is_gamma_workflow,
            event_issuer,
        } => {
            // Use HardwareBuffer mode for better compatibility with Tensor/Exynos SoCs
            if let MediaCodecVideoEncoderInputProcessor::Uninitialized(_) = &this.processor {
                let MediaCodecVideoEncoderInputProcessor::Uninitialized(state) =
                    std::mem::replace(
                        &mut this.processor,
                        MediaCodecVideoEncoderInputProcessor::Buffer(), // temporary placeholder
                    )
                else {
                    unreachable!();
                };

                // Configure encoder with SURFACE format for hardware buffer input
                let env = &mut attach_current_thread()?;
                let format = create_video_format_raw(
                    env,
                    this.padded_width,
                    this.padded_height,
                    state.bitrate,
                    state.fps_hint,
                    true, // use_surface = true for hardware buffer mode
                )?;
                this.codec.configure(&format)?;
                _ = this.codec.print_codec_info();

                // Create input surface after configure, before start
                let surface = this.codec.create_input_surface()?;
                let hardware_buffer_surface = HardwareBufferSurface::new(
                    &surface,
                    this.padded_width,
                    this.padded_height,
                    3, // max_images
                )?;
                this.codec.start()?;

                // Replace temporary placeholder with actual HardwareBuffer processor
                this.processor = MediaCodecVideoEncoderInputProcessor::HardwareBuffer(Arc::new(
                    hardware_buffer_surface,
                ));
                _ = state.tx.send(());
            }

            let MediaCodecVideoEncoderInputProcessor::HardwareBuffer(hb_surface) =
                &this.processor
            else {
                return Err(AndroidError::EncoderInputMismatch);
            };

            // Dequeue a frame from ImageWriter
            let frame = hb_surface.dequeue_frame()?;

            let (tx, rx) = tokio::sync::oneshot::channel();

            event_issuer.issue_graphics_event(
                Box::new(move || {
                    let image = source.tex;
                    // Blit to hardware buffer and return the future
                    let result = crate::vulkan::blit_to_hardware_buffer(
                        &image,
                        width,
                        height,
                        graphics_format,
                        flip_vertically,
                        is_gamma_workflow,
                        &frame,
                    );
                    tx.send((result, frame))
                        .map_err(|_| AndroidError::RenderThreadSendFailed)
                        .unwrap();
                }),
                *crate::vulkan::EVENT_ID
                    .get()
                    .context("Event ID is not reserved")?,
            );

            let (blit_result, frame) = rx.await?;
            let future = blit_result?;
            future.await?;

            // Queue the frame to MediaCodec
            hb_surface
                .queue_frame(frame, (data.timestamp * 1000.0 * 1000.0 * 1000.0) as i64)?;

            Ok(())
        }
    }
}

impl EncoderOutput for MediaCodecVideoEncoderOutput {
    type Data = CommonEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        pull_video_output_impl(self).await.map_err(Into::into)
    }
}

async fn pull_video_output_impl(
    this: &mut MediaCodecVideoEncoderOutput,
) -> Result<Option<CommonEncodedData>> {
    if let Some(rx) = &mut this.initialization {
        rx.await?;
        this.initialization = None;
    }

    pull_encoded_data_with_codec(&this.codec, &mut this.end_of_stream).await
}

// Helper functions for JNI MediaCodec calls

fn create_video_format_raw(
    env: &mut JNIEnv,
    padded_width: u32,
    padded_height: u32,
    bitrate: u32,
    fps_hint: u32,
    use_surface: bool,
) -> Result<SafeGlobalRef> {
    let format_class = env.find_class("android/media/MediaFormat")?;
    let method_id = env.get_static_method_id(
        &format_class,
        "createVideoFormat",
        "(Ljava/lang/String;II)Landroid/media/MediaFormat;",
    )?;

    let mime = to_java_string(env, MIME_TYPE_VIDEO_AVC)?;
    let format = unsafe {
        env.call_static_method_unchecked(
            format_class,
            method_id,
            ReturnType::Object,
            &[
                JValue::Object(&mime).as_jni(),
                JValue::Int(padded_width as jint).as_jni(),
                JValue::Int(padded_height as jint).as_jni(),
            ],
        )
    }?;

    let format_obj = format.l()?;

    // Set additional parameters
    set_format_integer(
        env,
        &format_obj,
        KEY_COLOR_FORMAT,
        if use_surface {
            COLOR_FORMAT_SURFACE
        } else {
            COLOR_FORMAT_YUV420_FLEXIBLE
        },
    )?;

    set_format_integer(env, &format_obj, KEY_BITRATE, bitrate as jint)?;
    set_format_integer(env, &format_obj, KEY_FRAME_RATE, fps_hint as jint)?;
    set_format_integer(env, &format_obj, KEY_I_FRAME_INTERVAL, 1)?;

    set_format_integer(env, &format_obj, KEY_PRIORITY, 0)?;
    set_format_integer(
        env,
        &format_obj,
        KEY_OPERATING_RATE,
        fps_hint as jint,
    )?;

    SafeGlobalRef::new(env, format_obj)
}
