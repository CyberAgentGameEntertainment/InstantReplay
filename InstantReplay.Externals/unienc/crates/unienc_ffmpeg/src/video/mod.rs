use std::{process::Command, sync::Arc, vec};

use anyhow::{anyhow, Context, Result};
use bincode::{Decode, Encode};
use cros_codecs::codec::h264::parser::{Nalu, NaluType};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    process::ChildStdout,
};
use unienc_common::{
    EncodedData, Encoder, EncoderInput, EncoderOutput, UniencDataKind, VideoEncoderOptions,
    VideoSample,
};

use crate::{ffmpeg, utils::Cfr, video::nalu::NaluReader};

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

impl FFmpegVideoEncoder {
    pub fn new<V: VideoEncoderOptions>(options: &V) -> Result<Self> {
        let width = options.width();
        let height = options.height();
        let cfr = options.fps_hint();

        println!("FFmpegVideoEncoder::new()");
        println!("{}", ffmpeg::FFMPEG_PATH.to_str().unwrap());

        let codecs = Command::new(ffmpeg::FFMPEG_PATH.as_os_str())
            .args(["-y", "-loglevel", "error", "-codecs"])
            .stdout(std::process::Stdio::piped())
            .spawn()?
            .wait_with_output()?;

        // read stdout
        let stdout = String::from_utf8_lossy(&codecs.stdout);
        // grep h264
        let Some(h264_line) = stdout.lines().find(|line| line.contains("h264")) else {
            return Err(anyhow!("Failed to find h264 codec"));
        };

        // find "(encoders: ...)"
        let encoders = h264_line
            .find("(encoders:")
            .and_then(|start| {
                h264_line[start + "(encoders:".len()..]
                    .find(')')
                    .map(|end| {
                        &h264_line[start + "(encoders:".len()..start + "(encoders:".len() + end]
                    })
            })
            .map(|s| s.split(' ').collect::<Vec<_>>())
            .context("failed to find H.264 encoder")?;

        let preferred_encoders = [
            "h264_nvenc",
            "h264_videotoolbox",
            "h264_qsv",
            "h264_vaapi",
            "h264_mf",
            "libx264",
        ];

        let encoder = preferred_encoders
            .iter()
            .find(|&&e| encoders.contains(&e))
            .copied()
            .unwrap_or("h264");

        let mut ffmpeg = ffmpeg::Builder::new()
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
                    encoder,
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

    async fn push(&mut self, data: &Self::Data) -> Result<()> {
        let timestamp = data.timestamp;
        let data = if data.width != self.width || data.height != self.height {
            // resize (crop or trim)
            let bgra = &data.data;
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
                data: resized,
                timestamp: data.timestamp,
            }
        } else {
            data.clone()
        };

        let Some((data, count)) = self.cfr.push(data, timestamp)? else {
            return Ok(());
        };

        for _i in 0..count {
            self.input.write_all(&data.data).await?;
        }

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

            let mut buf = vec![0; 1024];

            let read = self.output.read(&mut buf).await?;

            // let mut buf = Vec::new();
            // let read = self.output.read_to_end(&mut buf).await?;

            fn create_emit<'a>(state: &'a mut ReaderState, cfr: u32) -> impl FnMut(&Nalu) + 'a {
                move |nalu: &Nalu| {
                    // println!("NALU type: {:?}", nalu.header.type_);
                    match nalu.header.type_ {
                        NaluType::Sps | NaluType::Pps => {
                            _ = state
                                .buffer_tx
                                .send(VideoEncodedData::ParameterSet(nalu.data.to_vec()));
                        }
                        /*x
                        NaluType::Sei => {
                            _ = state
                                .buffer_tx
                                .send(VideoEncodedData::ParameterSet(nalu.data.to_vec()));
                        },
                        */
                        NaluType::Slice => {
                            let frame_index = state.frame_index;
                            state.frame_index += 1;
                            _ = state.buffer_tx.send(VideoEncodedData::Slice {
                                payload: nalu.data.to_vec(),
                                timestamp: frame_index as f64 / cfr as f64,
                                is_idr: false,
                            });
                        }
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
                            println!("Ignoring unsupported NALU type: {:?}", nalu.header.type_);
                        }
                    };
                }
            }

            if read == 0 {
                // end
                let Some(mut state) = self.reader_state.take() else {
                    unreachable!();
                };

                let Some(reader) = self.reader.take() else {
                    unreachable!();
                };
                reader.end(&mut create_emit(&mut state, self.cfr));
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
