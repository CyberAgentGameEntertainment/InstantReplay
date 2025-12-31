use std::rc::Rc;
use std::sync::Arc;
use bincode::{Decode, Encode};
use futures::channel::mpsc;
use futures::StreamExt;
use unienc_common::{AudioSample, EncodedData, Encoder, EncoderInput, EncoderOutput, OptionExt, ResultExt, Runtime, UniencSampleKind, UnsupportedBlitData};
use crate::js::{AudioEncoderHandle, VideoEncoderHandle};
use crate::video::{VideoEncodedData, WebCodecsVideoEncoderInput};

pub struct WebCodecsAudioEncoder<R: Runtime> {
    input: WebCodecsAudioEncoderInput<R>,
    output: WebCodecsAudioEncoderOutput,
}
pub struct WebCodecsAudioEncoderInput<R: Runtime> {
    encoder_handle: Option<AudioEncoderHandle>,
    bitrate: u32,
    channels: u32,
    sample_rate: u32,
    tx: mpsc::Sender<AudioEncodedData>,
    runtime: R,
}
pub struct WebCodecsAudioEncoderOutput {
    rx: mpsc::Receiver<AudioEncodedData>,
}
#[derive(Encode, Decode, Debug)]
pub struct AudioEncodedData {
    pub(crate) data: Vec<u8>,
    timestamp: f64,
}

impl<R: Runtime> WebCodecsAudioEncoder<R> {
    pub fn new<A: unienc_common::AudioEncoderOptions>(options: &A, runtime: &R) -> unienc_common::Result<Self> {
        let (tx, rx) = mpsc::channel(16);
        Ok(Self {
            input: WebCodecsAudioEncoderInput {
                encoder_handle: None,
                bitrate: options.bitrate(),
                channels: options.channels(),
                sample_rate: options.sample_rate(),
                tx,
                runtime: runtime.clone(),
            },
            output: WebCodecsAudioEncoderOutput {
                rx,
            },
        })
    }
}

impl<R: Runtime + 'static> Encoder for WebCodecsAudioEncoder<R> {
    type InputType = WebCodecsAudioEncoderInput<R>;
    type OutputType = WebCodecsAudioEncoderOutput;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl<R: Runtime + 'static> EncoderInput for WebCodecsAudioEncoderInput<R> {
    type Data = AudioSample;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {

        if self.encoder_handle.is_none() {
            let tx = self.tx.clone();
            self.encoder_handle = Some(AudioEncoderHandle::new(
                self.bitrate,
                self.channels,
                self.sample_rate,
                move |data, timestamp| {
                    let mut tx = tx.clone();
                    let encoded_data = AudioEncodedData {
                        data: data.to_vec(),
                        timestamp,
                    };
                    if let Err(err) = tx.try_send(encoded_data) {
                        println!(
                            "WebCodecsAudioEncoder: Failed to send encoded data: {}",
                            err
                        );
                    };
                },
            ).await.context("Failed to create WebCodecs EncoderHandle")?)
        }

        let encoder_handle = self.encoder_handle.as_ref().unwrap();

        encoder_handle.push_audio_frame(
            unsafe { data.data.align_to::<u8>() }.1,
            self.channels,
            self.sample_rate,
            data.timestamp_in_samples as f64 / self.sample_rate as f64,
        ).context("Failed to push audio frame to WebCodecs EncoderHandle")?;

        Ok(())
    }
}

impl<R: Runtime> Drop for WebCodecsAudioEncoderInput<R> {
    fn drop(&mut self) {
        let Some(encoder) = self.encoder_handle.take() else {
            return;
        };

        let encoder = Arc::new(encoder);
        let encoder_long = encoder.clone();

        self.runtime.spawn(async move {
            _ = encoder.flush().await;
            // keep encoder alive until flush is done
            drop(encoder_long);
        })
    }
}

impl EncoderOutput for WebCodecsAudioEncoderOutput {
    type Data = AudioEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        let res = self.rx.next().await;
        Ok(res)
    }
}

impl EncodedData for AudioEncodedData {
    fn timestamp(&self) -> f64 {
        self.timestamp
    }

    fn set_timestamp(&mut self, timestamp: f64) {
        self.timestamp = timestamp;
    }

    fn kind(&self) -> UniencSampleKind {
        UniencSampleKind::Key
    }
}