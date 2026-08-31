use std::{
    io::{Read, Write},
    process::Command,
    sync::{Arc, LazyLock},
    vec,
};

use bincode::{Decode, Encode};
use cros_codecs::codec::h264::parser::NaluType;
use unienc_common::{
    EncodedData, Encoder, EncoderInput, EncoderOutput, Runtime, UniencSampleKind,
    UnsupportedBlitData, VideoEncoderOptions, VideoFrame, VideoFrameBgra32, VideoSample,
    buffer::SharedBuffer,
};

use crate::{
    error::{FFmpegError, Result},
    ffmpeg,
    utils::Cfr,
    video::nalu::{NalUnit, NaluReader},
};

mod nalu;

pub struct FFmpegVideoEncoder<R: Runtime> {
    input: FFmpegVideoEncoderInput<R>,
    output: FFmpegVideoEncoderOutput<R>,
}

pub struct FFmpegVideoEncoderInput<R: Runtime> {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    input: ffmpeg::Input<R>,
    cfr: Cfr,
    /// The most recently written frame, kept so that it can be repeated to fill
    /// constant-rate slots that no input frame landed in.
    last_written: Option<VideoFrameBgra32>,
    width: u32,
    height: u32,
}

struct ReaderState {
    buffer_tx: std::sync::mpsc::Sender<VideoEncodedData>,
    frame_index: u64,
}
pub struct FFmpegVideoEncoderOutput<R: Runtime> {
    _ffmpeg: Arc<ffmpeg::FFmpeg>,
    output: ffmpeg::Output<R>,
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

        let encoder = encoder.ok_or(FFmpegError::NoSuitableEncoder)?;

        println!("Using H.264 encoder: {}", encoder);

        Ok(encoder.to_string())
    })()
    .map_err(|e| {
        println!("Error determining ffmpeg H.264 encoder: {}", e);
        e
    })
    .unwrap_or("h264".to_string())
});

impl<R: Runtime + 'static> FFmpegVideoEncoder<R> {
    pub fn new<V: VideoEncoderOptions>(options: &V, runtime: R) -> Result<Self> {
        let width = options.width();
        let height = options.height();
        let cfr = options.fps_hint();

        // encode raw BGRA frames into H.264 stream
        let mut spawned = ffmpeg::Builder::new()
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
                    // Raw H.264 has no timestamps and this pipeline carries a
                    // single timestamp per sample, so it cannot express the
                    // reordering B-frames require. Left enabled, the encoder
                    // declares a two frame reordering delay in the SPS, the
                    // demuxer synthesizes negative timestamps from it while
                    // muxing, and the tail of the video track is dropped.
                    "-bf",
                    "0",
                    "-b:v",
                    &format!("{}", options.bitrate()),
                    "-force_key_frames",
                    "expr:gte(t,n_forced*1)",
                    // Convert to and tag BT.709 limited range. Tagging the frames makes the
                    // auto-inserted scale filter use the BT.709 matrix for the BGRA to YUV
                    // conversion (FFmpeg otherwise derives the matrix from the frame size, which
                    // yields BT.601 at the sizes this library records) and makes the encoder write
                    // the color information into the SPS VUI. The muxer stream-copies this
                    // elementary stream, so the VUI is what carries the tags into the MP4.
                    //
                    // The `-color_primaries` / `-color_trc` output options would be the more
                    // obvious spelling, but FFmpeg does not reliably forward them to the encoder:
                    // as of 8.0.1 only the matrix and the range reach the VUI that way, leaving
                    // the primaries and the transfer function unspecified. Setting them on the
                    // frames is honored by every encoder because it does not depend on the
                    // encoder wrapper reading them off the codec context.
                    "-vf",
                    "setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv",
                ],
                ffmpeg::Destination::Stdout,
            )?;

        let input = ffmpeg::Input::new(runtime.clone(), spawned.inputs.remove(0));
        let output = ffmpeg::Output::new(
            runtime,
            spawned
                .stdout
                .take()
                .ok_or(FFmpegError::OutputNotAvailable)?,
        );

        let (buffer_tx, buffer_rx) = std::sync::mpsc::channel();

        let ffmpeg = Arc::new(spawned.ffmpeg);

        Ok(Self {
            input: FFmpegVideoEncoderInput {
                _ffmpeg: ffmpeg.clone(),
                input,
                cfr: Cfr::new(cfr),
                last_written: None,
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

impl<R: Runtime + 'static> Encoder for FFmpegVideoEncoder<R> {
    type InputType = FFmpegVideoEncoderInput<R>;
    type OutputType = FFmpegVideoEncoderOutput<R>;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl<R: Runtime + 'static> EncoderInput for FFmpegVideoEncoderInput<R> {
    type Data = VideoSample<UnsupportedBlitData>;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        let VideoFrame::Bgra32(frame) = data.frame else {
            return Err(FFmpegError::UnsupportedFrameFormat.into());
        };

        let timestamp = data.timestamp;
        let frame = if frame.width != self.width || frame.height != self.height {
            // resize (crop or trim)
            let bgra = frame.buffer.data();
            let mut resized = vec![0u8; (self.width * self.height * 4) as usize];

            let w = u32::min(self.width, frame.width);
            let h = u32::min(self.height, frame.height);

            for y in 0..h {
                let src_start = (y * frame.width * 4) as usize;
                let src_end = src_start + (w * 4) as usize;
                let dst_start = (y * self.width * 4) as usize;
                let dst_end = dst_start + (w * 4) as usize;

                resized[dst_start..dst_end].copy_from_slice(&bgra[src_start..src_end]);
            }

            VideoFrameBgra32 {
                width: self.width,
                height: self.height,
                buffer: SharedBuffer::new_unmanaged(resized),
            }
        } else {
            frame
        };

        // raw H.264 frames cannot have timestamps, so we need to assume CFR
        // we need to repeat or discard frames to match frame rate specified as fps_hint
        let Some(repeats) = self.cfr.advance(timestamp) else {
            // The frame's slot is already written, so it is dropped to keep the
            // frame rate constant.
            return Ok(());
        };

        // Both frames are handed to the blocking pool and back so that neither
        // the repeats nor the retained frame require copying the pixel buffer.
        let previous = self.last_written.take();
        let (previous, frame) = self
            .input
            .with_writer(move |writer| {
                if let Some(previous) = &previous {
                    for _i in 0..repeats {
                        writer.write_all(previous.buffer.data())?;
                    }
                }
                writer.write_all(frame.buffer.data())?;
                writer.flush()?;
                Ok((previous, frame))
            })
            .await?;
        drop(previous);

        // Retained for the repeats of a later gap. Writing the frame as soon as
        // its slot is known is what keeps the last frame of the stream from
        // being lost.
        self.last_written = Some(frame);

        Ok(())
    }
}

impl<R: Runtime + 'static> EncoderOutput for FFmpegVideoEncoderOutput<R> {
    type Data = VideoEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
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
            let buf = vec![0; 65536];

            let (buf, read) = self
                .output
                .with_reader(move |reader| {
                    let mut buf = buf;
                    let read = reader.read(&mut buf)?;
                    Ok((buf, read))
                })
                .await?;

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

    fn kind(&self) -> UniencSampleKind {
        match self {
            VideoEncodedData::ParameterSet(_items) => UniencSampleKind::Metadata,
            VideoEncodedData::Slice {
                payload: _,
                timestamp: _,
                is_idr: true,
            } => UniencSampleKind::Key,
            VideoEncodedData::Slice {
                payload: _,
                timestamp: _,
                is_idr: false,
            } => UniencSampleKind::Interpolated,
        }
    }
}
