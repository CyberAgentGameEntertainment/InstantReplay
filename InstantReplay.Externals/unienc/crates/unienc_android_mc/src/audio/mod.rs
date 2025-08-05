use anyhow::Result;
use jni::{objects::JValue, signature::ReturnType, sys::jint, JNIEnv};
use std::time::Duration;
use unienc_common::{AudioSample, Encoder, EncoderInput, EncoderOutput};

use crate::{
    common::{media_codec_buffer_flag::BUFFER_FLAG_END_OF_STREAM, *},
    config::{format_keys::*, *},
};

use crate::java::*;

pub struct MediaCodecAudioEncoder {
    input: MediaCodecAudioEncoderInput,
    output: MediaCodecAudioEncoderOutput,
}

pub struct MediaCodecAudioEncoderInput {
    codec: MediaCodec,
    sample_rate: u32,
    last_timestamp: i64,
}

unsafe impl Send for MediaCodecAudioEncoderInput {}

pub struct MediaCodecAudioEncoderOutput {
    codec: MediaCodec,
    end_of_stream: bool,
}

impl Encoder for MediaCodecAudioEncoder {
    type InputType = MediaCodecAudioEncoderInput;
    type OutputType = MediaCodecAudioEncoderOutput;

    fn get(self) -> Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl MediaCodecAudioEncoder {
    pub fn new<A: unienc_common::AudioEncoderOptions>(options: &A) -> Result<Self> {
        let env = &mut attach_current_thread()?;

        // Create MediaFormat
        let format = create_audio_format(env, options)?;

        // Create encoder using the wrapper
        let codec = MediaCodec::create_encoder(MIME_TYPE_AUDIO_AAC)?;

        // Configure encoder
        codec.configure(&format)?;

        // Start encoder
        codec.start()?;

        // Clone for both input and output
        let codec_input = codec.clone();
        let codec_output = codec;

        Ok(Self {
            input: MediaCodecAudioEncoderInput {
                codec: codec_input,
                sample_rate: options.sample_rate(),
                last_timestamp: 0,
            },
            output: MediaCodecAudioEncoderOutput {
                codec: codec_output,
                end_of_stream: false,
            },
        })
    }
}

impl Drop for MediaCodecAudioEncoderInput {
    fn drop(&mut self) {
        // notify end of stream
        || -> Result<()> {
            loop {
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
            }
        }()
        .unwrap();
    }
}

impl EncoderInput for MediaCodecAudioEncoderInput {
    type Data = AudioSample;

    async fn push(&mut self, data: &Self::Data) -> Result<()> {
        // Convert i16 samples to byte array
        let byte_data_vec = i16_to_bytes(&data.data);
        let mut byte_data = byte_data_vec.as_slice();

        while !byte_data.is_empty() {
            // Get input buffer
            let buffer_index = self
                .codec
                .dequeue_input_buffer(Duration::from_millis(100))?;
            if buffer_index >= 0 {
                let input_buffer = self.codec.get_input_buffer(buffer_index)?;
                {
                    let env: &mut jni::AttachGuard<'static> = &mut attach_current_thread()?;
                    let (_base_ptr, capacity, position) =
                        get_direct_buffer_info(env, input_buffer.as_obj())?;

                    let bytes_to_write = std::cmp::min(byte_data.len(), capacity - position);
                    crate::common::write_to_buffer(env, &input_buffer, &byte_data[..bytes_to_write])?;
                    byte_data = &byte_data[bytes_to_write..];

                    // Calculate timestamp in microseconds
                    let timestamp_us =
                        (data.timestamp_in_samples as f64 / self.sample_rate as f64 * 1_000_000.0) as i64;
        
                    self.last_timestamp = timestamp_us;
        
                    // Queue input buffer
                    self.codec
                        .queue_input_buffer(buffer_index, 0, bytes_to_write, timestamp_us, 0)?;
                }
            } else if buffer_index == media_codec_errors::INFO_TRY_AGAIN_LATER {
                std::thread::sleep(Duration::from_millis(10));
                continue;
            } else {
                return Err(anyhow::anyhow!("No input buffer available"));
            }
        }

        Ok(())
    }
}

impl EncoderOutput for MediaCodecAudioEncoderOutput {
    type Data = CommonEncodedData;

    async fn pull(&mut self) -> Result<Option<Self::Data>> {
        pull_encoded_data_with_codec(&self.codec, &mut self.end_of_stream).await
    }
}

// Helper functions for audio encoding

fn create_audio_format<A: unienc_common::AudioEncoderOptions>(
    env: &mut JNIEnv,
    options: &A,
) -> Result<SafeGlobalRef> {
    let format_class = env.find_class("android/media/MediaFormat")?;

    let method_id = env.get_static_method_id(
        &format_class,
        "createAudioFormat",
        "(Ljava/lang/String;II)Landroid/media/MediaFormat;",
    )?;

    let mime = to_java_string(env, MIME_TYPE_AUDIO_AAC)?;
    let format = unsafe {
        env.call_static_method_unchecked(
            format_class,
            method_id,
            ReturnType::Object,
            &[
                JValue::Object(&mime).as_jni(),
                JValue::Int(options.sample_rate() as jint).as_jni(),
                JValue::Int(options.channels() as jint).as_jni(),
            ],
        )
    }?;

    let format_obj = format.l()?;

    // Set additional parameters
    crate::common::set_format_integer(env, &format_obj, KEY_BITRATE, options.bitrate() as jint)?;
    crate::common::set_format_integer(
        env,
        &format_obj,
        KEY_AAC_PROFILE,
        aac_profiles::AAC_OBJECT_TYPE_AAC_LC,
    )?;

    SafeGlobalRef::new(env, format_obj)
}

// Convert i16 samples to byte array (little-endian)
fn i16_to_bytes(samples: &[i16]) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(samples.len() * 2);
    for &sample in samples {
        bytes.extend_from_slice(&sample.to_le_bytes());
    }
    bytes
}
