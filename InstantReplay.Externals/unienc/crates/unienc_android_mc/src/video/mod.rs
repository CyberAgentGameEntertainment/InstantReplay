use anyhow::{anyhow, Context, Result};
use jni::{objects::JValue, signature::ReturnType, sys::jint, JNIEnv};
use std::future::Future;
use std::sync::Arc;
use std::time::Duration;
use unienc_common::{
    Encoder, EncoderInput, EncoderOutput, GraphicsEventIssuer, VideoFrame, VideoSample,
};

use crate::{java::*, VulkanTexture};

use crate::vulkan::presentation::VulkanSurface;
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

enum MediaCodecVideoEncoderInputProcessor {
    Uninitialized(tokio::sync::oneshot::Sender<()>),
    Buffer(),
    Surface(Arc<VulkanSurface>),
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

    fn get(self) -> Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl Drop for MediaCodecVideoEncoderInput {
    fn drop(&mut self) {
        // notify end of stream
        || -> Result<()> {
            match self.processor {
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
                        return Err(anyhow::anyhow!("No input buffer available"));
                    }
                },
                MediaCodecVideoEncoderInputProcessor::Surface(_) => {
                    self.codec.print_metrics()?;
                    self.codec.signal_end_of_input_stream()?;
                    Ok(())
                }
            }
        }()
        .unwrap();
    }
}

pub(crate) struct Surface {
    pub surface: SafeGlobalRef,
}

impl Drop for Surface {
    fn drop(&mut self) {
        let _ = call_void_method(
            &attach_current_thread().unwrap(),
            self.surface.as_obj(),
            "release",
            "()V",
            &[],
        );
    }
}

impl MediaCodecVideoEncoder {
    pub fn new<V: unienc_common::VideoEncoderOptions>(options: &V) -> Result<Self> {
        let env = &mut attach_current_thread()?;

        // Calculate original and padded sizes
        let original_width = options.width();
        let original_height = options.height();

        fn round_up_to_16(value: u32) -> u32 {
            (value + 15) & !15
        }
        let padded_width = round_up_to_16(original_width);
        let padded_height = round_up_to_16(original_height);

        // Create MediaFormat with padded sizes
        let format = create_video_format(env, options, padded_width, padded_height)?;

        // Create encoder using the wrapper
        let codec = MediaCodec::create_encoder(MIME_TYPE_VIDEO_AVC)?;

        // Configure encoder
        codec.configure(&format)?;

        codec.print_codec_info();

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
                processor: MediaCodecVideoEncoderInputProcessor::Uninitialized(tx),
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

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        match data.frame {
            VideoFrame::Bgra32(frame) => {
                match &self.processor {
                    MediaCodecVideoEncoderInputProcessor::Uninitialized(_tx) => {
                        // setup for buffer input mode
                        self.codec.start()?;
                        let MediaCodecVideoEncoderInputProcessor::Uninitialized(tx) =
                            std::mem::replace(
                                &mut self.processor,
                                MediaCodecVideoEncoderInputProcessor::Buffer(),
                            )
                        else {
                            unreachable!();
                        };
                        _ = tx.send(());
                    }
                    MediaCodecVideoEncoderInputProcessor::Buffer() => {}
                    _ => {
                        return Err(anyhow::anyhow!(
                            "This encoder is initialized for other input"
                        ));
                    }
                }

                let mut buffer_index;
                loop {
                    let sleep;
                    {
                        // Get input buffer
                        buffer_index = self
                            .codec
                            .dequeue_input_buffer(Duration::from_millis(100))?;
                        if buffer_index == media_codec_errors::INFO_TRY_AGAIN_LATER {
                            sleep = true;
                        } else if buffer_index < 0 {
                            return Err(anyhow::anyhow!("No input buffer available"));
                        } else {
                            break;
                        }
                    }
                    if sleep {
                        tokio::time::sleep(Duration::from_millis(10)).await;
                    }
                }

                let buffer = self.codec.get_input_buffer(buffer_index)?;
                let env = &mut attach_current_thread()?;
                let (_base_ptr, capacity, position) = get_direct_buffer_info(env, buffer.as_obj())?;
                let size = capacity - position;

                let image = self.codec.get_input_image(buffer_index)?;

                // Use Image-based approach with dynamic plane layout and padding
                let planes = image.get_planes()?;
                crate::common::write_bgra_to_yuv_planes_with_padding(
                    &frame,
                    self.padded_width,
                    self.padded_height,
                    &planes,
                )?;

                let timestamp = (data.timestamp * 1_000_000.0) as i64;
                self.last_timestamp = timestamp;

                // Queue input buffer - size is determined by the Image object
                self.codec.queue_input_buffer(
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
                event_issuer,
            } => {
                if let MediaCodecVideoEncoderInputProcessor::Uninitialized(_tx) = &self.processor {
                    let env = &mut attach_current_thread()?;
                    let surface = self.codec.create_input_surface()?;
                    let vulkan_surface =
                        crate::vulkan::presentation::VulkanSurface::from_jni_surface(
                            env,
                            Surface { surface },
                        )?;
                    self.codec.start()?;
                    let MediaCodecVideoEncoderInputProcessor::Uninitialized(tx) = std::mem::replace(
                        &mut self.processor,
                        MediaCodecVideoEncoderInputProcessor::Surface(Arc::new(vulkan_surface)),
                    ) else {
                        unreachable!();
                    };
                    _ = tx.send(());
                }

                let MediaCodecVideoEncoderInputProcessor::Surface(vulkan_surface) = &self.processor
                else {
                    return Err(anyhow::anyhow!(
                        "This encoder is initialized for other input"
                    ));
                };

                let (tx, rx) = tokio::sync::oneshot::channel();
                let vulkan_surface = vulkan_surface.clone();

                let time = {
                    let env = &mut attach_current_thread()?;
                    let cls = env.find_class("java/lang/System")?;
                    env.call_static_method(cls, "nanoTime", "()J", &[])?.j()?
                } as u64;

                event_issuer.issue_graphics_event(
                    Box::new(move || {

                        /*
                        let now_ns = std::time::SystemTime::now()
                            .duration_since(std::time::UNIX_EPOCH)
                            .unwrap()
                            .as_nanos() as u64;*/

                        let image = source.tex;
                        tx.send(crate::vulkan::blit(
                            &image,
                            &vulkan_surface,
                            time,//(data.timestamp * 1000.0 * 1000.0 * 1000.0) as u64,
                        ))
                        .map_err(|_e| anyhow!("Failed to send from render thread to push"))
                        .unwrap();
                    }),
                    *crate::vulkan::EVENT_ID
                        .get()
                        .context("Event ID is not reserved")?,
                );

                let future = rx.await? // failed to receive
                    ?; // failed to issue blit
                if let Some(future) = future {
                    future.await?;
                } // failed to blit

                Ok(())
            }
        }
    }
}

impl EncoderOutput for MediaCodecVideoEncoderOutput {
    type Data = CommonEncodedData;

    async fn pull(&mut self) -> Result<Option<Self::Data>> {
        if let Some(rx) = &mut self.initialization {
            rx.await?;
            self.initialization = None;
        }

        pull_encoded_data_with_codec(&self.codec, &mut self.end_of_stream).await
    }
}

// Helper functions for JNI MediaCodec calls

fn create_video_format<V: unienc_common::VideoEncoderOptions>(
    env: &mut JNIEnv,
    options: &V,
    padded_width: u32,
    padded_height: u32,
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
    set_format_integer(env, &format_obj, KEY_COLOR_FORMAT, COLOR_FORMAT_SURFACE)?;

    set_format_integer(env, &format_obj, KEY_BITRATE, options.bitrate() as jint)?;
    set_format_integer(env, &format_obj, KEY_FRAME_RATE, options.fps_hint() as jint)?;
    set_format_integer(env, &format_obj, KEY_I_FRAME_INTERVAL, 1)?;

    set_format_integer(env, &format_obj, KEY_PRIORITY, 0)?;
    set_format_integer(
        env,
        &format_obj,
        KEY_OPERATING_RATE,
        options.fps_hint() as jint,
    )?;

    SafeGlobalRef::new(env, format_obj)
}
