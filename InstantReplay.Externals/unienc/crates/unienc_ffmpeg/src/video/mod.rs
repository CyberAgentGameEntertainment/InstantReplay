use std::{
    process::Command,
    sync::{Arc, LazyLock},
    vec,
};

use anyhow::{Context, Result};
use bincode::{Decode, Encode};
use cros_codecs::codec::h264::parser::NaluType;
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    process::ChildStdout,
};
use unienc_common::{
    buffer::SharedBuffer, EncodedData, Encoder, EncoderInput, EncoderOutput, UniencDataKind, VideoEncoderOptions, VideoSample
};

use crate::{
    ffmpeg,
    utils::Cfr,
    video::nalu::{NalUnit, NaluReader},
};

mod nalu;

pub struct FFmpegVideoEncoder {
    input: FFmpegVideoEncoderInput,
    output: FFmpegVideoEncoderOutput,
}

pub struct FFmpegVideoEncoderInput {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    input: ffmpeg::Input,
    cfr: Cfr<VideoSample>,
    width: u32,
    height: u32,
}

struct ReaderState {
    buffer_tx: std::sync::mpsc::Sender<VideoEncodedData>,
    frame_index: u64,
}
pub struct FFmpegVideoEncoderOutput {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    output: ChildStdout,
    reader_state: Option<ReaderState>,
    buffer_rx: std::sync::mpsc::Receiver<VideoEncodedData>,
    cfr: u32,
    reader: Option<NaluReader>,
}

static FFMPEG_CODEC: LazyLock<String> = LazyLock::new(|| {
    (|| -> Result<String> {
        // enumerate supported encoders
        let codecs = Command::new(ffmpeg::FFMPEG_PATH.as_os_str())
            .args(["-y", "-loglevel", "error", "-encoders"])
            .stdout(std::process::Stdio::piped())
            .spawn()?
            .wait_with_output()?;

        // read stdout
        let stdout = String::from_utf8_lossy(&codecs.stdout);
        // grep h264 and extract encoder name
        // example:
        // V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
        let encoders = stdout
            .lines()
            .filter(|line| line.contains("(codec h264)"))
            .flat_map(|s| s.split(" ").nth(2))
            .collect::<Vec<_>>();

        // we would like to use hardware encoder if available
        let preferred_encoders = [
            "h264_nvenc",
            "h264_videotoolbox",
            "h264_qsv",
            "h264_vaapi",
            "h264_mf",
            "libx264",
        ];

        // filter available encoders by preferred list order
        let mut encoder_candidates = preferred_encoders
            .iter()
            .filter_map(|e| encoders.iter().find(|&&enc| enc == *e));

        // ffmpeg -encoders returns encoders including not actually available on the system
        // so we need to verify by trying to create a simple command line
        let encoder = encoder_candidates.find(|e| {
            println!("Testing ffmpeg H.264 encoder: {}", e);
            let res = Command::new(ffmpeg::FFMPEG_PATH.as_os_str())
                .args([
                    "-y",
                    "-loglevel",
                    "error",
                    "-f",
                    "lavfi",
                    "-i",
                    "testsrc=s=256x256:r=2:d=1",
                    "-c:v",
                    e,
                    "-f",
                    "null",
                    "-",
                ])
                .status();

            match res {
                Ok(status) => status.success(),
                Err(_) => false,
            }
        });

        let encoder = encoder.context("No suitable H.264 encoder found")?;

        println!("Using H.264 encoder: {}", encoder);

        Ok(encoder.to_string())
    })()
    .map_err(|e| {
        println!("Error determining ffmpeg H.264 encoder: {}", e);
        e
    })
    .unwrap_or("h264".to_string())
});

impl FFmpegVideoEncoder {
    pub fn new<V: VideoEncoderOptions>(options: &V) -> Result<Self> {
        let width = options.width();
        let height = options.height();
        let cfr = options.fps_hint();

        // encode raw BGRA frames into H.264 stream
        let mut ffmpeg = ffmpeg::Builder::new()
            .use_stdin(true)
            .input([
                "-f",
                "rawvideo",
                "-pixel_format",
                "bgra",
                "-video_size",
                &format!("{}x{}", width, height),
                "-framerate",
                &format!("{cfr}"),
            ])
            .build(
                [
                    "-f",
                    "h264",
                    "-pix_fmt",
                    "yuv420p",
                    "-r",
                    &format!("{cfr}"),
                    "-c:v",
                    &*FFMPEG_CODEC,
                    "-b:v",
                    &format!("{}", options.bitrate()),
                    "-force_key_frames",
                    "expr:gte(t,n_forced*1)",
                ],
                ffmpeg::Destination::Stdout,
            )?;

        let input = ffmpeg
            .inputs
            .take()
            .context("failed to get input")?
            .remove(0);
        let output = ffmpeg.stdout.take().context("failed to get output")?;

        let (buffer_tx, buffer_rx) = std::sync::mpsc::channel();

        let ffmpeg = Arc::new(ffmpeg);

        Ok(Self {
            input: FFmpegVideoEncoderInput {
                _ffmpeg: ffmpeg.clone(),
                input,
                cfr: Cfr::new(cfr),
                width,
                height,
            },
            output: FFmpegVideoEncoderOutput {
                _ffmpeg: ffmpeg,
                output,
                reader_state: Some(ReaderState {
                    buffer_tx,
                    frame_index: 0,
                }),
                buffer_rx,
                cfr,
                reader: Some(NaluReader::default()),
            },
        })
    }
}

impl Encoder for FFmpegVideoEncoder {
    type InputType = FFmpegVideoEncoderInput;
    type OutputType = FFmpegVideoEncoderOutput;

    fn get(self) -> Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl EncoderInput for FFmpegVideoEncoderInput {
    type Data = VideoSample;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        let timestamp = data.timestamp;
        let data = if data.width != self.width || data.height != self.height {
            // resize (crop or trim)
            let bgra = data.buffer.data();
            let mut resized = vec![0u8; (self.width * self.height * 4) as usize];

            let w = u32::min(self.width, data.width);
            let h = u32::min(self.height, data.height);

            for y in 0..h {
                let src_start = (y * data.width * 4) as usize;
                let src_end = src_start + (w * 4) as usize;
                let dst_start = (y * self.width * 4) as usize;
                let dst_end = dst_start + (w * 4) as usize;

                resized[dst_start..dst_end].copy_from_slice(&bgra[src_start..src_end]);
            }

            Self::Data {
                width: self.width,
                height: self.height,
                buffer: SharedBuffer::new_unmanaged(resized),
                timestamp: data.timestamp,
            }
        } else {
            data
        };

        // raw H.264 frames cannot have timestamps, so we need to assume CFR
        // we need to repeat or discard frames to match frame rate specified as fps_hint
        let Some((data, count)) = self.cfr.push(data, timestamp)? else {
            return Ok(());
        };

        for _i in 0..count {
            self.input.write_all(data.buffer.data()).await?;
        }
        drop(data);

        self.input.flush().await?;

        Ok(())
    }
}

impl EncoderOutput for FFmpegVideoEncoderOutput {
    type Data = VideoEncodedData;

    async fn pull(&mut self) -> Result<Option<Self::Data>> {
        loop {
            match self.buffer_rx.try_recv() {
                Ok(data) => {
                    return Ok(Some(data));
                }
                Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                    return Ok(None);
                }
                Err(std::sync::mpsc::TryRecvError::Empty) => {
                    // fallthrough
                }
            }

            // read H.264 stream
            // H.264 byte stream is sequence of NAL units and each frame is a NAL unit
            let mut buf = vec![0; 65536];

            let read = self.output.read(&mut buf).await?;

            fn create_emit<'a>(state: &'a mut ReaderState, cfr: u32) -> impl FnMut(&NalUnit) + 'a {
                move |nalu: &NalUnit| {
                    match nalu.nalu.header.type_ {
                        // parameter set used by decoder
                        NaluType::Sps | NaluType::Pps => {
                            _ = state
                                .buffer_tx
                                .send(VideoEncodedData::ParameterSet(nalu.data.to_vec()));
                        }
                        // interpolated frame
                        NaluType::Slice => {
                            let frame_index = state.frame_index;
                            state.frame_index += 1;
                            _ = state.buffer_tx.send(VideoEncodedData::Slice {
                                payload: nalu.data.to_vec(),
                                timestamp: frame_index as f64 / cfr as f64,
                                is_idr: false,
                            });
                        }
                        // key frame
                        NaluType::SliceIdr => {
                            let frame_index = state.frame_index;
                            state.frame_index += 1;
                            _ = state.buffer_tx.send(VideoEncodedData::Slice {
                                payload: nalu.data.to_vec(),
                                timestamp: frame_index as f64 / cfr as f64,
                                is_idr: true,
                            });
                        }
                        _ => {
                            println!("Ignoring NALU type: {:?}", nalu.nalu.header.type_);
                        }
                    };
                }
            }

            if read == 0 {
                // end of stream
                let Some(mut state) = self.reader_state.take() else {
                    unreachable!();
                };

                let Some(reader) = self.reader.take() else {
                    unreachable!();
                };
                reader.end(&mut create_emit(&mut state, self.cfr))?;
            } else {
                let Some(state) = &mut self.reader_state else {
                    unreachable!();
                };

                let Some(reader) = &mut self.reader else {
                    unreachable!();
                };

                let buf = &buf[..read];
                reader.push(buf, &mut create_emit(state, self.cfr))?;
                continue;
            }
        }
    }
}

#[derive(Clone, Encode, Decode, Debug)]
pub enum VideoEncodedData {
    ParameterSet(Vec<u8>),
    Slice {
        payload: Vec<u8>,
        timestamp: f64,
        is_idr: bool,
    },
}

impl EncodedData for VideoEncodedData {
    fn timestamp(&self) -> f64 {
        match self {
            VideoEncodedData::ParameterSet(_) => 0.0,
            VideoEncodedData::Slice { timestamp, .. } => *timestamp,
        }
    }

    fn set_timestamp(&mut self, value: f64) {
        match self {
            VideoEncodedData::ParameterSet(_items) => {}
            VideoEncodedData::Slice {
                payload: _,
                timestamp,
                is_idr: _,
            } => {
                *timestamp = value;
            }
        }
    }

    fn kind(&self) -> UniencDataKind {
        match self {
            VideoEncodedData::ParameterSet(_items) => UniencDataKind::Metadata,
            VideoEncodedData::Slice {
                payload: _,
                timestamp: _,
                is_idr: true,
            } => UniencDataKind::Key,
            VideoEncodedData::Slice {
                payload: _,
                timestamp: _,
                is_idr: false,
            } => UniencDataKind::Interpolated,
        }
    }
}
