use std::rc::Rc;
use std::sync::Arc;
use crate::js::VideoEncoderHandle;
use bincode::{Decode, Encode};
use futures::channel::mpsc;
use futures::{SinkExt, StreamExt};
use unienc_common::{EncodedData, Encoder, EncoderInput, EncoderOutput, OptionExt, UnsupportedBlitData, VideoFrame, VideoSample};

pub struct WebCodecsVideoEncoder {
    input: WebCodecsVideoEncoderInput,
    output: WebCodecsVideoEncoderOutput,
}
pub struct WebCodecsVideoEncoderInput {
    encoder_handle: Option<VideoEncoderHandle>,
    width: u32,
    height: u32,
    bitrate: u32,
    fps_hint: f64,
    tx: mpsc::Sender<VideoEncodedData>,
    prev_key_timestamp: Option<f64>,
}

pub struct WebCodecsVideoEncoderOutput {
    rx: mpsc::Receiver<VideoEncodedData>,
}

#[derive(Encode, Decode, Debug)]
pub struct VideoEncodedData {
    pub(crate) data: Vec<u8>,
    timestamp: f64,
    pub(crate) is_key: bool,
}

impl WebCodecsVideoEncoder {
    pub fn new<V: unienc_common::VideoEncoderOptions>(options: &V) -> unienc_common::Result<Self> {
        let (tx, rx) = mpsc::channel(16);
        Ok(Self {
            input: WebCodecsVideoEncoderInput {
                width: options.width(),
                height: options.height(),
                bitrate: options.bitrate(),
                fps_hint: options.fps_hint() as f64,
                encoder_handle: None,
                tx,
                prev_key_timestamp: None,
            },
            output: WebCodecsVideoEncoderOutput { rx },
        })
    }
}

impl Encoder for WebCodecsVideoEncoder {
    type InputType = WebCodecsVideoEncoderInput;
    type OutputType = WebCodecsVideoEncoderOutput;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl EncoderInput for WebCodecsVideoEncoderInput {
    type Data = VideoSample<UnsupportedBlitData>;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        let VideoFrame::Bgra32(frame) = data.frame else {
            return Err(unienc_common::CommonError::BlitNotSupported);
        };

        if self.encoder_handle.is_none() {
            let tx = self.tx.clone();
            self.encoder_handle = Some(VideoEncoderHandle::new(
                self.width,
                self.height,
                self.bitrate,
                self.fps_hint,
                move |data, timestamp, is_key| {
                    let mut tx = tx.clone();
                    let encoded_data = VideoEncodedData {
                        data: data.to_vec(),
                        timestamp,
                        is_key,
                    };
                    if let Err(err) = tx.try_send(encoded_data) {
                        println!(
                            "WebCodecsVideoEncoder: Failed to send encoded data: {}",
                            err
                        );
                    };
                },
            ).await.context("Failed to create WebCodecs EncoderHandle")?)
        }

        let encoder_handle = self.encoder_handle.as_ref().unwrap();

        let pixels = &frame.buffer.data()[..frame.buffer.len()];
        let since_prev_key = match self.prev_key_timestamp {
            Some(prev) => data.timestamp - prev,
            None => f64::INFINITY,
        };
        if since_prev_key >= 1.0 {
            self.prev_key_timestamp = Some(data.timestamp);
        }
        encoder_handle.push_video_frame(
            pixels,
            frame.width,
            frame.height,
            data.timestamp,
            since_prev_key >= 1.0,
        );
        Ok(())
    }
}

impl Drop for WebCodecsVideoEncoderInput {
    fn drop(&mut self) {
        let Some(encoder) = self.encoder_handle.take() else {
            return;
        };

        let encoder = Rc::new(encoder);
        let encoder_long = encoder.clone();

        encoder.flush(move || {
            // keep encoder alive until flush is done
            drop(encoder_long);
        })
    }
}
impl EncoderOutput for WebCodecsVideoEncoderOutput {
    type Data = VideoEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        Ok(self.rx.next().await)
    }
}

impl EncodedData for VideoEncodedData {
    fn timestamp(&self) -> f64 {
        self.timestamp
    }

    fn set_timestamp(&mut self, timestamp: f64) {
        self.timestamp = timestamp;
    }

    fn kind(&self) -> unienc_common::UniencSampleKind {
        if self.is_key {
            unienc_common::UniencSampleKind::Key
        } else {
            unienc_common::UniencSampleKind::Interpolated
        }
    }
}
