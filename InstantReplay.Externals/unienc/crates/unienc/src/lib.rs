pub fn add(left: u64, right: u64) -> u64 {
    left + right
}

#[cfg(test)]
mod tests {
    use rand::RngCore;
    use unienc_apple_vt::VideoToolboxEncodingSystem;
    use unienc_common::{
        AudioEncoderOptions, AudioSample, Encoder, EncoderInput, EncoderOutput, EncodingSystem,
        Muxer, MuxerCompletionHandle, MuxerInput, VideoEncoderOptions, VideoSample,
    };

    #[tokio::test(flavor = "multi_thread")]
    async fn test_e2e() {
        test_e2e_typed::<VideoToolboxEncodingSystem>().await;
    }

    async fn test_e2e_typed<T: EncodingSystem + Send>() {
        let encoding_system = T::new(
            &VideoEncoderOptions {
                width: 1920,
                height: 1080,
                fps_hint: 5,
                bitrate: 1000000,
            },
            &AudioEncoderOptions {
                sample_rate: 48000,
                channels: 2,
                bitrate: 128000,
            },
        );

        let video_encoder = encoding_system.new_video_encoder().unwrap();

        let audio_encoder = encoding_system.new_audio_encoder().unwrap();

        let muxer = encoding_system.new_muxer("test.mp4").unwrap();

        let (mut video_input, mut video_output) = video_encoder.get().unwrap();
        let (mut audio_input, mut audio_output) = audio_encoder.get().unwrap();

        let emit_video = tokio::spawn(async move {
            for i in 0..6 {
                let mut data = vec![0; 1920 * 1080 * 4];

                {
                    let mut rng = rand::rng();
                    rng.fill_bytes(&mut data);
                }

                video_input
                    .push(&VideoSample {
                        data,
                        width: 1920,
                        height: 1080,
                        timestamp: (i as f64) / 5.0,
                    })
                    .await
                    .unwrap();
            }
        });

        let emit_audio = tokio::spawn(async move {
            for i in 0..2 {
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
                    .push(&AudioSample {
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
                let (data, _size) = bincode::decode_from_slice::<_, _>(
                    encoded.as_slice(),
                    bincode::config::standard(),
                )
                .unwrap();
                video_input.push(&data).await.unwrap();
            }
            video_input.finish().await.unwrap();
        });

        let transfer_audio = tokio::spawn(async move {
            while let Some(data) = audio_output.pull().await.unwrap() {
                let encoded = bincode::encode_to_vec(data, bincode::config::standard()).unwrap();
                let (data, _size) = bincode::decode_from_slice::<_, _>(
                    encoded.as_slice(),
                    bincode::config::standard(),
                )
                .unwrap();
                audio_input.push(&data).await.unwrap();
            }
            audio_input.finish().await.unwrap();
        });

        emit_video.await.unwrap();
        emit_audio.await.unwrap();
        transfer_video.await.unwrap();
        transfer_audio.await.unwrap();
        completion_handle.finish().await.unwrap();
    }
}
