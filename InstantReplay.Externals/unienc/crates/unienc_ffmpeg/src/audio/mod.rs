use std::{sync::Arc, vec};

use bincode::{Decode, Encode};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    process::ChildStdout,
};
use unienc_common::{
    AudioEncoderOptions, AudioSample, EncodedData, Encoder, EncoderInput, EncoderOutput,
    UniencSampleKind,
};

use crate::error::{FFmpegError, Result};
use crate::ffmpeg;

pub struct FFmpegAudioEncoder {
    input: FFmpegAudioEncoderInput,
    output: FFmpegAudioEncoderOutput,
}

pub struct FFmpegAudioEncoderInput {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    input: ffmpeg::Input,
    channels: u32,
    /// Expected input timestamp (in samples) of the next push, i.e. the previous push's timestamp plus
    /// the number of frames it delivered. Used to detect discontinuities in the input timeline.
    next_input_position: Option<u64>,
}

pub struct FFmpegAudioEncoderOutput {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    output: ChildStdout,
    timestamp_in_samples: u64,
    sample_rate: u32,
}

impl FFmpegAudioEncoder {
    pub fn new<V: AudioEncoderOptions>(options: &V) -> Result<Self> {
        let sample_rate = options.sample_rate();
        let channels = options.channels();

        // encode raw s16le PCM stream to ADTS
        let mut ffmpeg = ffmpeg::Builder::new()
            .use_stdin(true)
            .input([
                "-f",
                "s16le",
                "-ar",
                &format!("{}", sample_rate),
                "-ac",
                &format!("{}", channels),
            ])
            .build(["-f", "adts"], ffmpeg::Destination::Stdout)?;

        let input = ffmpeg
            .inputs
            .take()
            .ok_or(FFmpegError::InputNotAvailable)?
            .remove(0);
        let output = ffmpeg
            .stdout
            .take()
            .ok_or(FFmpegError::OutputNotAvailable)?;

        let ffmpeg = Arc::new(ffmpeg);

        Ok(Self {
            input: FFmpegAudioEncoderInput {
                _ffmpeg: ffmpeg.clone(),
                input,
                channels,
                next_input_position: None,
            },
            output: FFmpegAudioEncoderOutput {
                _ffmpeg: ffmpeg,
                output,
                timestamp_in_samples: 0,
                sample_rate,
            },
        })
    }
}

impl Encoder for FFmpegAudioEncoder {
    type InputType = FFmpegAudioEncoderInput;
    type OutputType = FFmpegAudioEncoderOutput;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl EncoderInput for FFmpegAudioEncoderInput {
    type Data = AudioSample;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        // The ffmpeg output PTS is derived purely from the number of encoded frames (see the output's
        // `timestamp_in_samples` counter), so a discontinuity in the input timeline would otherwise be
        // swallowed and make audio drift ahead of video. Materialize forward gaps as silence so the
        // encoded stream length matches the real timeline. Backward jumps are ignored to keep the stream
        // monotonic.
        let channels = (self.channels as u64).max(1);
        let frames_in_push = data.data.len() as u64 / channels;
        let gap = unienc_common::forward_audio_discontinuity(
            self.next_input_position,
            data.timestamp_in_samples,
        );
        if gap > 0 {
            // s16le PCM: 2 bytes per sample, silence is all-zero.
            let silence = vec![0u8; gap as usize * channels as usize * 2];
            self.input
                .write_all(&silence)
                .await
                .map_err(FFmpegError::from)?;
        }
        self.next_input_position = Some(data.timestamp_in_samples + frames_in_push);

        let data = data.data_as_s16le_bytes();

        self.input
            .write_all(data.as_ref())
            .await
            .map_err(FFmpegError::from)?;
        self.input.flush().await.map_err(FFmpegError::from)?;

        Ok(())
    }
}

impl EncoderOutput for FFmpegAudioEncoderOutput {
    type Data = AudioEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        // read ADTS header
        let mut header = vec![0u8; 7];
        if let Err(err) = self.output.read_exact(&mut header).await {
            if err.kind() == std::io::ErrorKind::UnexpectedEof {
                return Ok(None);
            }
        }

        // get frame length
        let mut length = ((header[3] & 0b11) as u16) << 11;
        length |= (header[4] as u16) << 3;
        length |= (header[5] as u16) >> 5;

        length -= 7;

        // ADTS always contains 1024 samples per channel
        let timestamp_in_samples = self.timestamp_in_samples;
        self.timestamp_in_samples += 1024;

        let mut buf = vec![0u8; length as usize];
        self.output
            .read_exact(&mut buf)
            .await
            .map_err(FFmpegError::from)?;

        let data = AudioEncodedData {
            header,
            payload: buf,
            timestamp_in_samples,
            sample_rate: self.sample_rate,
        };

        // println!("{data:?}");

        Ok(Some(data))
    }
}

#[derive(Clone, Encode, Decode, Debug)]
pub struct AudioEncodedData {
    pub(crate) header: Vec<u8>,
    pub(crate) payload: Vec<u8>,
    timestamp_in_samples: u64,
    sample_rate: u32,
}

impl EncodedData for AudioEncodedData {
    fn timestamp(&self) -> f64 {
        self.timestamp_in_samples as f64 / self.sample_rate as f64
    }

    fn set_timestamp(&mut self, value: f64) {
        self.timestamp_in_samples = (value * self.sample_rate as f64) as u64;
    }

    fn kind(&self) -> UniencSampleKind {
        UniencSampleKind::Interpolated
    }
}
