use std::{sync::Arc, vec};

use anyhow::{Context, Result};
use bincode::{Decode, Encode};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    process::ChildStdout,
};
use unienc_common::{
    AudioEncoderOptions, AudioSample, EncodedData, Encoder, EncoderInput, EncoderOutput,
    UniencDataKind,
};

use crate::ffmpeg;

pub struct FFmpegAudioEncoder {
    input: FFmpegAudioEncoderInput,
    output: FFmpegAudioEncoderOutput,
}

pub struct FFmpegAudioEncoderInput {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    input: ffmpeg::Input,
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

        let mut ffmpeg = ffmpeg::Builder::new()
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
            .context("failed to get input")?
            .remove(0);
        let output = ffmpeg.stdout.take().context("failed to get output")?;

        let ffmpeg = Arc::new(ffmpeg);

        Ok(Self {
            input: FFmpegAudioEncoderInput { _ffmpeg: ffmpeg.clone(), input },
            output: FFmpegAudioEncoderOutput { _ffmpeg: ffmpeg, output, timestamp_in_samples: 0, sample_rate },
        })
    }
}

impl Encoder for FFmpegAudioEncoder {
    type InputType = FFmpegAudioEncoderInput;
    type OutputType = FFmpegAudioEncoderOutput;

    fn get(self) -> Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl EncoderInput for FFmpegAudioEncoderInput {
    type Data = AudioSample;

    async fn push(&mut self, data: &Self::Data) -> Result<()> {
        let data = unsafe {
            std::slice::from_raw_parts::<u8>(
                data.data.as_ptr() as *const u8,
                data.data.len() * std::mem::size_of::<i16>(),
            )
        };

        self.input.write_all(data).await?;
        self.input.flush().await?;

        Ok(())
    }
}

impl EncoderOutput for FFmpegAudioEncoderOutput {
    type Data = AudioEncodedData;

    async fn pull(&mut self) -> Result<Option<Self::Data>> {
        let mut header = vec![0u8; 7];
        if let Err(err) = self.output.read_exact(&mut header).await {
            if err.kind() == std::io::ErrorKind::UnexpectedEof {
                return Ok(None);
            }
        }

        // get frame length
        let mut length = ((header[3]& 0b11) as u16) << 11;
        length |= (header[4] as u16) << 3;
        length |= (header[5] as u16) >> 5;

        length -= 7;

        let timestamp_in_samples = self.timestamp_in_samples;
        self.timestamp_in_samples += 1024;

        let mut buf = vec![0u8; length as usize];
        self.output.read_exact(&mut buf).await?;

        let data = AudioEncodedData { header, payload: buf, timestamp_in_samples, sample_rate: self.sample_rate };

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

    fn kind(&self) -> UniencDataKind {
        UniencDataKind::Interpolated
    }
}
