
use unienc_common::{
    buffer::SharedBuffer, AudioSample, CompletionHandle, EncodedData, Encoder, EncoderInput, EncoderOutput, EncodingSystem, Muxer, MuxerInput, VideoSample
};

use unienc::PlatformEncodingSystem;

#[tokio::test(flavor = "multi_thread")]
async fn test_e2e() {

    test_e2e_typed(
        PlatformEncodingSystem::new(
            &unienc::VideoEncoderOptionsNative {
                width: 1280,
                height: 720,
                fps_hint: 5,
                bitrate: 1000000,
            },
            &unienc::AudioEncoderOptionsNative {
                sample_rate: 48000,
                channels: 2,
                bitrate: 128000,
            },
        ))
    .await;
}

async fn test_e2e_typed<T: EncodingSystem + Send>(encoding_system: T) {

    let video_encoder = encoding_system.new_video_encoder().unwrap();

    let audio_encoder = encoding_system.new_audio_encoder().unwrap();

    let muxer = encoding_system.new_muxer("test.mp4".as_ref()).unwrap();

    let (mut video_input, mut video_output) = video_encoder.get().unwrap();
    let (mut audio_input, mut audio_output) = audio_encoder.get().unwrap();

    let target_duration = 10.0;

    let emit_video = tokio::spawn(async move {
        let frames = (target_duration * 10.0) as u32;
        for i in 0..frames {
            let data = vec![0; 1280 * 720 * 4];

            {
                // let mut rng = rand::rng();
                // rng.fill_bytes(&mut data);
            }

            video_input
                .push(VideoSample {
                    buffer: SharedBuffer::new_unmanaged(data),
                    width: 1280,
                    height: 720,
                    timestamp: (i as f64) / 10.0 + 100.0,
                })
                .await
                .unwrap();
        }
    });

    let emit_audio = tokio::spawn(async move {
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

    let transfer_video = tokio::spawn(async move {
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

    let transfer_audio = tokio::spawn(async move {
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
