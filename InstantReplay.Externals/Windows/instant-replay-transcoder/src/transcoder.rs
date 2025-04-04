// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

use std::{error::Error, ptr};

use turbojpeg::Image;
use windows::{Win32::Media::MediaFoundation::*, core::HSTRING};

use crate::utils::MediaFoundationLifetime;

pub struct Transcoder {
    _mf_lifetime: MediaFoundationLifetime,
    writer: IMFSinkWriter,
    video_stream_index: u32,
    audio_stream_index: u32,
    input_width: u32,
    input_height: u32,
    audio_sample_rate: u32,
}

pub struct OutputOptions {
    pub width: u32,
    pub height: u32,
    pub frame_rate: u32,
    pub average_bitrate: u32,
    pub audio_sample_rate: u32,
    pub audio_channels: u32,
    pub output_path: String,
}

#[inline(always)]
const fn align_to(x: usize, align: usize) -> usize {
    (x + (align - 1)) & !(align - 1)
}

impl Transcoder {
    pub fn new(options: &OutputOptions) -> Result<Self, Box<dyn Error>> {
        let lifetime = MediaFoundationLifetime::new()?;

        unsafe {
            let s: HSTRING = HSTRING::from(&options.output_path);
            let writer = MFCreateSinkWriterFromURL(&s, None, None)?;

            // video

            // H.264 encoder may not support odd size
            let input_width = align_to(options.width as usize, 2) as u32;
            let input_height = align_to(options.height as usize, 2) as u32;

            let video_output_type = MFCreateMediaType()?;

            video_output_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
            video_output_type.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_H264)?;
            video_output_type.SetUINT32(&MF_MT_AVG_BITRATE, options.average_bitrate)?;
            video_output_type
                .SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)?;
            video_output_type.SetUINT64(
                &MF_MT_FRAME_SIZE,
                ((input_width as u64) << 32) + input_height as u64,
            )?;

            video_output_type
                .SetUINT64(&MF_MT_FRAME_RATE, ((options.frame_rate as u64) << 32) + 1)?;

            let video_stream_index = writer.AddStream(&video_output_type)?;

            let video_input_type = MFCreateMediaType()?;

            video_input_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video)?;
            video_input_type.SetGUID(&MF_MT_SUBTYPE, &MFVideoFormat_ARGB32)?;
            video_input_type
                .SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32)?;
            video_input_type.SetUINT64(
                &MF_MT_FRAME_SIZE,
                ((input_width as u64) << 32) + input_height as u64,
            )?;
            video_input_type
                .SetUINT64(&MF_MT_FRAME_RATE, ((options.frame_rate as u64) << 32) + 1)?;

            writer.SetInputMediaType(video_stream_index, &video_input_type, None)?;

            // audio

            let audio_output_type = MFCreateMediaType()?;
            audio_output_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio)?;
            audio_output_type.SetGUID(&MF_MT_SUBTYPE, &MFAudioFormat_AAC)?;
            audio_output_type
                .SetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND, options.audio_sample_rate)?;
            audio_output_type.SetUINT32(&MF_MT_AUDIO_NUM_CHANNELS, options.audio_channels)?;

            let audio_stream_index = writer.AddStream(&audio_output_type)?;

            let audio_input_type = MFCreateMediaType()?;

            audio_input_type.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio)?;
            audio_input_type.SetGUID(&MF_MT_SUBTYPE, &MFAudioFormat_PCM)?;
            audio_input_type.SetUINT32(&MF_MT_AUDIO_BITS_PER_SAMPLE, 16)?;
            audio_input_type
                .SetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND, options.audio_sample_rate)?;
            audio_input_type.SetUINT32(&MF_MT_AUDIO_NUM_CHANNELS, options.audio_channels)?;

            writer.SetInputMediaType(audio_stream_index, &audio_input_type, None)?;

            writer.BeginWriting()?;
            Ok(Self {
                _mf_lifetime: lifetime,
                writer,
                video_stream_index,
                audio_stream_index,
                input_width,
                input_height,
                audio_sample_rate: options.audio_sample_rate,
            })
        }
    }

    pub fn push_frame(&self, src: &Image<Vec<u8>>, timestamp: f64) -> Result<(), Box<dyn Error>> {
        let stride = 4 * self.input_width;
        let length = stride * self.input_height;
        let buffer = unsafe { MFCreateMemoryBuffer(length)? };

        let mut buffer_locked: *mut u8 = ptr::null_mut();
        unsafe {
            buffer.Lock(&mut buffer_locked, None, None)?;
            MFCopyImage(
                buffer_locked,
                stride as i32,
                src.pixels.as_ptr(),
                src.pitch as i32,
                (src.width * 4) as u32,
                src.height as u32,
            )?;

            buffer.Unlock()?;

            buffer.SetCurrentLength(length)?;

            let sample = MFCreateSample()?;
            sample.AddBuffer(&buffer)?;
            sample.SetSampleTime((timestamp * 10_000_000.0) as i64)?;
            sample.SetSampleDuration((10_000_000.0 / 30.0) as i64)?;

            self.writer.WriteSample(self.video_stream_index, &sample)?;
        }

        Ok(())
    }

    pub fn push_audio_samples(
        &self,
        samples: &[i16],
        timestamp: f64,
    ) -> Result<(), Box<dyn Error>> {
        let buffer_size = samples.len() * 2;
        let buffer = unsafe { MFCreateMemoryBuffer(buffer_size as u32)? };

        let mut buffer_locked: *mut i16 = ptr::null_mut();
        unsafe {
            buffer.Lock(
                &mut buffer_locked as *mut *mut i16 as *mut *mut u8,
                None,
                None,
            )?;
            buffer_locked.copy_from_nonoverlapping(samples.as_ptr(), samples.len());

            buffer.Unlock()?;

            buffer.SetCurrentLength(buffer_size as u32)?;

            let sample = MFCreateSample()?;
            sample.AddBuffer(&buffer)?;
            sample.SetSampleTime((timestamp * 10_000_000.0) as i64)?;
            sample.SetSampleDuration(
                (samples.len() as f64 / self.audio_sample_rate as f64 * 10_000_000.0) as i64,
            )?;

            self.writer.WriteSample(self.audio_stream_index, &sample)?;
        }

        Ok(())
    }

    pub fn complete(self) -> Result<(), Box<dyn Error>> {
        unsafe {
            self.writer.Finalize()?;
        }
        Ok(())
    }
}
