use jni::{JNIEnv, sys::jint};
use std::marker::PhantomData;
use std::sync::{Arc, Mutex};
use unienc_common::{AudioSample, Encoder, EncoderInput, EncoderOutput};

use crate::error::{AndroidError, Result};
use crate::{
    common::{media_codec_buffer_flag::BUFFER_FLAG_END_OF_STREAM, *},
    config::{format_keys::*, *},
};

use crate::java::*;

pub struct MediaCodecAudioEncoder<R: unienc_common::Runtime + 'static> {
    input: MediaCodecAudioEncoderInput<R>,
    output: MediaCodecAudioEncoderOutput<R>,
}

pub struct MediaCodecAudioEncoderInput<R: unienc_common::Runtime + 'static> {
    channels: u32,
    /// Expected input timestamp (in samples) of the next push, i.e. the previous push's timestamp plus
    /// the number of frames it delivered. Used to detect discontinuities in the input timeline.
    next_input_position: Option<u64>,
    /// Hands PCM to the input thread. Dropping it lets the thread signal the end of the stream.
    tx: tokio::sync::mpsc::Sender<AudioInputChunk>,
    /// The error that stopped the input thread, if any.
    error: Arc<Mutex<Option<AndroidError>>>,
    _runtime: PhantomData<R>,
}

unsafe impl<R: unienc_common::Runtime + 'static> Send for MediaCodecAudioEncoderInput<R> {}

pub struct MediaCodecAudioEncoderOutput<R: unienc_common::Runtime + 'static> {
    drainer: OutputDrainer,
    _runtime: PhantomData<R>,
}

/// PCM handed from `push` to the input thread.
struct AudioInputChunk {
    /// Input timestamp (in samples) of the first frame of `bytes`. Only the first chunk's is used;
    /// the thread derives the rest from the frames it has queued.
    timestamp_in_samples: u64,
    /// Signed 16-bit little-endian PCM, including any leading silence that fills a gap.
    bytes: Vec<u8>,
}

/// Chunks `push` may hand over before it waits for the input thread.
const INPUT_QUEUE_CAPACITY: usize = 2;

/// Timed-out dequeues tolerated while waiting for the input buffer that carries the end of stream.
const EOS_DEQUEUE_ATTEMPTS: u32 = 20;

impl<R: unienc_common::Runtime + 'static> Encoder for MediaCodecAudioEncoder<R> {
    type InputType = MediaCodecAudioEncoderInput<R>;
    type OutputType = MediaCodecAudioEncoderOutput<R>;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl<R: unienc_common::Runtime + 'static> MediaCodecAudioEncoder<R> {
    pub fn new<A: unienc_common::AudioEncoderOptions>(options: &A, _runtime: R) -> Result<Self> {
        let env = &mut attach_current_thread()?;

        // Create MediaFormat
        let format = create_audio_format(env, options)?;

        // Create encoder using the wrapper
        let codec = MediaCodec::create_encoder(MIME_TYPE_AUDIO_AAC)?;

        // Configure encoder
        codec.configure(&format)?;

        // Start encoder
        codec.start()?;

        let drainer = OutputDrainer::start(&codec, "unienc-aud-out")?;

        let (tx, rx) = tokio::sync::mpsc::channel(INPUT_QUEUE_CAPACITY);
        let error = Arc::new(Mutex::new(None));
        let input_thread = AudioInputThread {
            codec,
            sample_rate: options.sample_rate(),
            channels: options.channels(),
            last_timestamp: 0,
            position_in_samples: None,
        };
        {
            let error = error.clone();
            std::thread::Builder::new()
                .name("unienc-aud-in".to_owned())
                .spawn(move || input_thread.run(rx, error))
                .map_err(AndroidError::ThreadSpawnFailed)?;
        }

        Ok(Self {
            input: MediaCodecAudioEncoderInput {
                channels: options.channels(),
                next_input_position: None,
                tx,
                error,
                _runtime: PhantomData,
            },
            output: MediaCodecAudioEncoderOutput {
                drainer,
                _runtime: PhantomData,
            },
        })
    }
}

impl<R: unienc_common::Runtime + 'static> EncoderInput for MediaCodecAudioEncoderInput<R> {
    type Data = AudioSample;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        push_impl(self, data).await.map_err(Into::into)
    }
}

async fn push_impl<R: unienc_common::Runtime + 'static>(
    this: &mut MediaCodecAudioEncoderInput<R>,
    data: AudioSample,
) -> Result<()> {
    // Keep the encoded audio contiguous in sample-count terms (see the Apple backend for the rationale):
    // when the input timeline jumps forward (dropped or suspended audio that advances `timestamp_in_samples`
    // by more than the number of samples actually delivered), fill the gap with leading silence so
    // MediaCodec / the muxer keep audio aligned with video instead of collapsing a PTS gap. Backward jumps
    // are ignored by `forward_audio_discontinuity`, so the timeline never regresses.
    let channels = (this.channels as u64).max(1);
    let frames_in_push = data.data.len() as u64 / channels;
    let gap = unienc_common::forward_audio_discontinuity(
        this.next_input_position,
        data.timestamp_in_samples,
    );
    this.next_input_position = Some(data.timestamp_in_samples + frames_in_push);

    // Convert i16 samples to signed 16-bit little-endian PCM bytes, prefixed with `gap` frames of silence
    // when the input timeline jumped forward (2 bytes per i16 sample, `channels` per frame).
    let byte_data = data.data_as_s16le_bytes();
    let bytes = if gap > 0 {
        let mut padded = vec![0u8; (gap * channels) as usize * 2];
        padded.extend_from_slice(&byte_data);
        padded
    } else {
        byte_data
    };

    let chunk = AudioInputChunk {
        timestamp_in_samples: data.timestamp_in_samples,
        bytes,
    };
    if this.tx.send(chunk).await.is_err() {
        let error = this
            .error
            .lock()
            .map_err(|_| AndroidError::MutexPoisoned)?
            .take();
        return Err(error.unwrap_or(AndroidError::EncoderInputThreadStopped));
    }

    Ok(())
}

impl<R: unienc_common::Runtime + 'static> EncoderOutput for MediaCodecAudioEncoderOutput<R> {
    type Data = CommonEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        self.drainer.pull().await.map_err(Into::into)
    }
}

/// Feeds PCM to the codec on a dedicated thread, which waits in `dequeueInputBuffer` itself rather
/// than handing every wait to the blocking pool (see [`OutputDrainer`] for the rationale). Once the
/// input is dropped and the queued PCM has been fed, it signals the end of the stream.
struct AudioInputThread {
    codec: MediaCodec,
    sample_rate: u32,
    channels: u32,
    last_timestamp: i64,
    /// Running presentation position (in samples) of the next input chunk. Anchored to the first input
    /// buffer's timestamp and advanced by the number of frames actually queued, so that a single large
    /// (batched) input buffer split across multiple MediaCodec input buffers yields strictly increasing,
    /// contiguous timestamps instead of repeating the same input timestamp.
    position_in_samples: Option<u64>,
}

impl AudioInputThread {
    fn run(
        mut self,
        mut rx: tokio::sync::mpsc::Receiver<AudioInputChunk>,
        error: Arc<Mutex<Option<AndroidError>>>,
    ) {
        let result = (|| -> Result<()> {
            while let Some(chunk) = rx.blocking_recv() {
                self.queue(chunk)?;
            }
            self.queue_end_of_stream()
        })();

        if let Err(e) = result {
            log::error!("unienc: audio encoder input failed: {e}");
            if let Ok(mut error) = error.lock() {
                *error = Some(e);
            }
        }
    }

    /// Waits for a free input buffer, giving up after `max_attempts` timed-out dequeues if given.
    fn dequeue_input_buffer(&self, max_attempts: Option<u32>) -> Result<jint> {
        let mut attempts = 0;
        loop {
            let buffer_index = self.codec.dequeue_input_buffer(DEQUEUE_TIMEOUT)?;
            if buffer_index >= 0 {
                return Ok(buffer_index);
            }
            // The dequeue above already waited, so retry straight away.
            if buffer_index != media_codec_errors::INFO_TRY_AGAIN_LATER {
                return Err(AndroidError::NoInputBuffer);
            }
            attempts += 1;
            if max_attempts.is_some_and(|max| attempts >= max) {
                return Err(AndroidError::NoInputBuffer);
            }
        }
    }

    fn queue(&mut self, chunk: AudioInputChunk) -> Result<()> {
        self.position_in_samples
            .get_or_insert(chunk.timestamp_in_samples);
        let mut byte_data = chunk.bytes.as_slice();

        while !byte_data.is_empty() {
            let buffer_index = self.dequeue_input_buffer(None)?;
            let input_buffer = self.codec.get_input_buffer(buffer_index)?;
            let env = &mut attach_current_thread()?;
            let (base_ptr, capacity, position) =
                get_direct_buffer_info(env, input_buffer.as_obj())?;

            let bytes_to_write = std::cmp::min(byte_data.len(), capacity - position);
            // SAFETY: The input buffer is direct and has `capacity - position` bytes left from
            // `position`, and the codec does not read it until it is queued.
            unsafe {
                std::ptr::copy_nonoverlapping(
                    byte_data.as_ptr(),
                    base_ptr.add(position),
                    bytes_to_write,
                );
            }
            byte_data = &byte_data[bytes_to_write..];

            // A single push can be split across multiple MediaCodec input buffers (e.g. when a large
            // batched buffer is pushed while the main thread is stalled by framerate jitter). Assigning
            // every chunk the same input timestamp would make MediaCodec emit output packets with
            // duplicate / non-monotonic PTS that the muxer rejects. Instead, derive a contiguous
            // timeline that advances by the number of frames queued. The running position is seeded to
            // the first input timestamp; forward discontinuities are materialized by `push` as leading
            // silence, so the position simply advances by the frames queued here.
            let position_in_samples = self
                .position_in_samples
                .expect("position_in_samples is initialized above");

            // Calculate timestamp in microseconds
            let timestamp_us =
                (position_in_samples as f64 / self.sample_rate as f64 * 1_000_000.0) as i64;

            self.last_timestamp = timestamp_us;

            // Queue input buffer
            self.codec.queue_input_buffer(
                buffer_index,
                position,
                bytes_to_write,
                timestamp_us,
                0,
            )?;

            // Advance by the number of whole frames written (2 bytes per i16 sample, `channels` per frame).
            let frames_written = (bytes_to_write / (2 * self.channels as usize)) as u64;
            self.position_in_samples = Some(position_in_samples + frames_written);
        }

        Ok(())
    }

    fn queue_end_of_stream(&self) -> Result<()> {
        // A free input buffer appears only while the output is drained. If the output has already been
        // dropped, none will, so give up rather than keep the thread waiting forever.
        let buffer_index = self.dequeue_input_buffer(Some(EOS_DEQUEUE_ATTEMPTS))?;
        self.codec.queue_input_buffer(
            buffer_index,
            0,
            0,
            self.last_timestamp,
            BUFFER_FLAG_END_OF_STREAM,
        )
    }
}

// Helper functions for audio encoding

fn create_audio_format<A: unienc_common::AudioEncoderOptions>(
    env: &mut JNIEnv,
    options: &A,
) -> Result<SafeGlobalRef> {
    let mime = to_java_string(env, MIME_TYPE_AUDIO_AAC)?;
    let format_obj = crate::bindings::MediaFormat::create_audio_format(
        env,
        &mime,
        options.sample_rate() as jint,
        options.channels() as jint,
    )?;

    // Set additional parameters
    crate::common::set_format_integer(env, &format_obj, KEY_BITRATE, options.bitrate() as jint)?;
    crate::common::set_format_integer(
        env,
        &format_obj,
        KEY_AAC_PROFILE,
        crate::config::AAC_OBJECT_TYPE_AAC_LC,
    )?;

    SafeGlobalRef::new(env, format_obj)
}
