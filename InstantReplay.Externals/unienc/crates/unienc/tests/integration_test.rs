use futures::channel::oneshot::Canceled;
use futures::executor;
use futures::executor::ThreadPool;
use futures::task::SpawnExt;
use rand::RngCore;
use std::pin::Pin;
use unienc_common::{
    AudioSample, CompletionHandle, EncodedData, Encoder, EncoderInput, EncoderOutput,
    EncodingSystem, Muxer, MuxerInput, Spawn, SpawnBlocking, VideoFrame, VideoFrameBgra32,
    VideoSample, buffer::SharedBuffer,
};

use unienc::PlatformEncodingSystem;

#[derive(Copy, Clone)]
pub struct VideoEncoderOptions {
    pub width: u32,
    pub height: u32,
    pub fps_hint: u32,
    pub bitrate: u32,
}

#[derive(Copy, Clone)]
pub struct AudioEncoderOptions {
    pub sample_rate: u32,
    pub channels: u32,
    pub bitrate: u32,
}

impl unienc::VideoEncoderOptions for VideoEncoderOptions {
    fn width(&self) -> u32 {
        self.width
    }

    fn height(&self) -> u32 {
        self.height
    }

    fn fps_hint(&self) -> u32 {
        self.fps_hint
    }

    fn bitrate(&self) -> u32 {
        self.bitrate
    }
}

impl unienc::AudioEncoderOptions for AudioEncoderOptions {
    fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    fn channels(&self) -> u32 {
        self.channels
    }

    fn bitrate(&self) -> u32 {
        self.bitrate
    }
}

#[derive(Clone)]
struct Runtime {
    pool: ThreadPool,
}

impl Spawn for Runtime {
    fn spawn(&self, future: impl Future<Output = ()> + Send + 'static) {
        self.pool
            .spawn(future)
            .expect("Failed to spawn task on threaded executor");
    }
}

impl SpawnBlocking for Runtime {
    fn spawn_blocking<Result: Send + 'static>(
        &self,
        f: impl FnOnce() -> Result + Send + 'static,
    ) -> Pin<Box<dyn Future<Output = Result> + Send + 'static>> {
        Box::pin(blocking::unblock(f))
    }
}

impl unienc_common::Runtime for Runtime {}

impl Runtime {
    pub fn spawn_fut<Output: Send + 'static>(
        &self,
        future: impl Future<Output = Output> + Send + 'static,
    ) -> impl Future<Output = Result<Output, Canceled>> + Send + 'static {
        let (tx, rx) = futures::channel::oneshot::channel();
        self.spawn(async move {
            let _ = tx.send(future.await);
        });

        rx
    }
}

#[test]
fn test_e2e() {
    let pool = ThreadPool::new().expect("Failed to build pool");
    let runtime = Runtime { pool: pool.clone() };

    executor::block_on(test_e2e_typed(
        PlatformEncodingSystem::new(
            &VideoEncoderOptions {
                width: 1280,
                height: 720,
                fps_hint: 1,
                bitrate: 1000000,
            },
            &AudioEncoderOptions {
                sample_rate: 48000,
                channels: 2,
                bitrate: 128000,
            },
            runtime.clone(),
        ),
        runtime,
    ));
}

async fn test_e2e_typed<T: EncodingSystem + Send>(encoding_system: T, runtime: Runtime) {
    let video_encoder = encoding_system.new_video_encoder().unwrap();

    let audio_encoder = encoding_system.new_audio_encoder().unwrap();

    let muxer = encoding_system.new_muxer("test.mp4".as_ref()).unwrap();

    let (mut video_input, mut video_output) = video_encoder.get().unwrap();
    let (mut audio_input, mut audio_output) = audio_encoder.get().unwrap();

    let target_duration = 10.0;

    let emit_video = runtime.spawn_fut(async move {
        let frames = (target_duration * 1.0) as u32;
        for i in 0..frames {
            let mut data = vec![0; 1280 * 720 * 4];

            {
                let mut rng = rand::rng();
                rng.fill_bytes(&mut data);
            }

            video_input
                .push(VideoSample {
                    frame: VideoFrame::Bgra32(VideoFrameBgra32 {
                        buffer: SharedBuffer::new_unmanaged(data),
                        width: 1280,
                        height: 720,
                    }),
                    timestamp: (i as f64) / 1.0 + 100.0,
                })
                .await
                .unwrap();
        }
    });

    let emit_audio = runtime.spawn_fut(async move {
        for i in 0..target_duration as u64 {
            let mut data = vec![0_i16; 48000 * 2];
            {
                // 442Hz sine wave
                for (i, sample) in data.iter_mut().enumerate() {
                    let sample_pos = (i / 2) as f32 / 48000.0;
                    *sample = ((sample_pos * 442.0 * 2.0 * std::f32::consts::PI).sin()
                        * (i16::MAX / 2) as f32) as i16;
                    *sample += ((sample_pos * 442.0 * 2.0 * 2.0 * std::f32::consts::PI).sin()
                        * (i16::MAX / 2) as f32) as i16;
                }
            }

            audio_input
                .push(AudioSample {
                    data,
                    timestamp_in_samples: i * 48000,
                })
                .await
                .unwrap();
        }
    });

    let (mut video_input, mut audio_input, completion_handle) = muxer.get_inputs().unwrap();

    let transfer_video = runtime.spawn_fut(async move {
        while let Some(data) = video_output.pull().await.unwrap() {
            let encoded = bincode::encode_to_vec(data, bincode::config::standard()).unwrap();
            let (mut data, _size) =
                bincode::decode_from_slice::<<<<T as EncodingSystem>::VideoEncoderType as Encoder>::OutputType as EncoderOutput>::Data, _>(encoded.as_slice(), bincode::config::standard())
                    .unwrap();
            data.set_timestamp(data.timestamp() - 100.0);
            video_input.push(data).await.unwrap();
        }
        video_input.finish().await.unwrap();
    });

    let transfer_audio = runtime.spawn_fut(async move {
        while let Some(data) = audio_output.pull().await.unwrap() {
            let encoded = bincode::encode_to_vec(data, bincode::config::standard()).unwrap();
            let (data, _size) =
                bincode::decode_from_slice::<_, _>(encoded.as_slice(), bincode::config::standard())
                    .unwrap();
            audio_input.push(data).await.unwrap();
        }
        audio_input.finish().await.unwrap();
    });

    emit_video.await.unwrap();
    emit_audio.await.unwrap();
    transfer_video.await.unwrap();
    transfer_audio.await.unwrap();
    completion_handle.finish().await.unwrap();
}
