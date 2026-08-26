use std::{
    io::{Read, Write},
    sync::Arc,
    vec,
};

use bincode::{Decode, Encode};
use unienc_common::{
    AudioEncoderOptions, AudioSample, EncodedData, Encoder, EncoderInput, EncoderOutput, Runtime,
    UniencSampleKind,
};

use crate::error::{FFmpegError, Result};
use crate::ffmpeg;

pub struct FFmpegAudioEncoder<R: Runtime> {
    input: FFmpegAudioEncoderInput<R>,
    output: FFmpegAudioEncoderOutput<R>,
}

pub struct FFmpegAudioEncoderInput<R: Runtime> {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    input: ffmpeg::Input<R>,
    channels: u32,
    /// Expected input timestamp (in samples) of the next push, i.e. the previous push's timestamp plus
    /// the number of frames it delivered. Used to detect discontinuities in the input timeline.
    next_input_position: Option<u64>,
}

pub struct FFmpegAudioEncoderOutput<R: Runtime> {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    output: ffmpeg::Output<R>,
    timestamp_in_samples: u64,
    sample_rate: u32,
}

impl<R: Runtime + 'static> FFmpegAudioEncoder<R> {
    pub fn new<V: AudioEncoderOptions>(options: &V, runtime: R) -> Result<Self> {
        let sample_rate = options.sample_rate();
        let channels = options.channels();

        // encode raw s16le PCM stream to ADTS
        let mut spawned = ffmpeg::Builder::new()
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

        let input = ffmpeg::Input::new(runtime.clone(), spawned.inputs.remove(0));
        let output = ffmpeg::Output::new(
            runtime,
            spawned
                .stdout
                .take()
                .ok_or(FFmpegError::OutputNotAvailable)?,
        );

        let ffmpeg = Arc::new(spawned.ffmpeg);

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

impl<R: Runtime + 'static> Encoder for FFmpegAudioEncoder<R> {
    type InputType = FFmpegAudioEncoderInput<R>;
    type OutputType = FFmpegAudioEncoderOutput<R>;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl<R: Runtime + 'static> EncoderInput for FFmpegAudioEncoderInput<R> {
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
                .with_writer(move |writer| writer.write_all(&silence))
                .await?;
        }
        self.next_input_position = Some(data.timestamp_in_samples + frames_in_push);

        let data = data.data_as_s16le_bytes();

        self.input
            .with_writer(move |writer| {
                writer.write_all(&data)?;
                writer.flush()
            })
            .await?;

        Ok(())
    }
}

impl<R: Runtime + 'static> EncoderOutput for FFmpegAudioEncoderOutput<R> {
    type Data = AudioEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        // read ADTS header
        let header = vec![0u8; 7];
        let (header, eof) = self
            .output
            .with_reader(move |reader| {
                let mut header = header;
                match reader.read_exact(&mut header) {
                    Ok(()) => Ok((header, false)),
                    // The stream ended; anything else is a real failure and
                    // must not leave the header partially filled.
                    Err(err) if err.kind() == std::io::ErrorKind::UnexpectedEof => {
                        Ok((header, true))
                    }
                    Err(err) => Err(err),
                }
            })
            .await?;
        if eof {
            return Ok(None);
        }

        // get frame length
        let mut length = ((header[3] & 0b11) as u16) << 11;
        length |= (header[4] as u16) << 3;
        length |= (header[5] as u16) >> 5;

        length -= 7;

        // ADTS always contains 1024 samples per channel
        let timestamp_in_samples = self.timestamp_in_samples;
        self.timestamp_in_samples += 1024;

        let buf = vec![0u8; length as usize];
        let buf = self
            .output
            .with_reader(move |reader| {
                let mut buf = buf;
                reader.read_exact(&mut buf)?;
                Ok(buf)
            })
            .await?;

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
