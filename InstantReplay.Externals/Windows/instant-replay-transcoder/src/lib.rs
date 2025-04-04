// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

use std::{
    error::Error,
    ffi::{self, c_char},
    fs,
};

use turbojpeg::Image;

mod transcoder;
mod utils;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn instant_replay_create(
    width: u32,
    height: u32,
    frame_rate: u32,
    average_bitrate: u32,
    audio_sample_rate: u32,
    audio_channels: u32,
    output_path: *const c_char,
    on_error: unsafe extern "C" fn(usize, *const c_char),
    ctx: usize,
) -> *mut transcoder::Transcoder {
    unsafe {
        move || -> Result<_, Box<dyn Error>> {
            let output_path = ffi::CStr::from_ptr(output_path).to_str()?;
            let transcoder = transcoder::Transcoder::new(&transcoder::OutputOptions {
                width,
                height,
                frame_rate,
                average_bitrate,
                audio_sample_rate,
                audio_channels,
                output_path: output_path.to_string(),
            })?;
            Ok(Box::into_raw(Box::new(transcoder)))
        }()
        .handle(on_error, ctx, std::ptr::null_mut())
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn instant_replay_load_frame(
    path: *const c_char,
    on_error: unsafe extern "C" fn(usize, *const c_char),
    ctx: usize,
) -> *mut Image<Vec<u8>> {
    unsafe {
        move || -> Result<_, Box<dyn Error>> {
            let path = ffi::CStr::from_ptr(path).to_str()?;
            let data = fs::read(path)?;
            let src = turbojpeg::decompress(&data, turbojpeg::PixelFormat::BGRA)?;

            // flip pixels vertically
            let mut dest = vec![0u8; src.pixels.len()];
            let stride = src.pitch;
            for y in 0..src.height {
                let src_offset = y * stride;
                let dest_offset = (src.height - y - 1) * stride;
                dest[dest_offset..dest_offset + stride]
                    .copy_from_slice(&src.pixels[src_offset..src_offset + stride]);
            }

            let src = Image {
                width: src.width,
                height: src.height,
                pixels: dest,
                pitch: src.pitch,
                format: src.format,
            };

            Ok(Box::into_raw(Box::new(src)))
        }()
        .handle(on_error, ctx, std::ptr::null_mut())
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn instant_replay_push_frame(
    transcoder: *mut transcoder::Transcoder,
    src: *mut Image<Vec<u8>>,
    timestamp: f64,
    on_error: unsafe extern "C" fn(usize, *const c_char),
    ctx: usize,
) {
    unsafe {
        move || -> Result<(), Box<dyn Error>> {
            let src = Box::from_raw(src);

            (*transcoder).push_frame(&src, timestamp)?;
            Ok(())
        }()
        .handle(on_error, ctx, ())
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn instant_replay_push_audio_samples(
    transcoder: *mut transcoder::Transcoder,
    samples: *const i16,
    count: usize,
    timestamp: f64,
    on_error: unsafe extern "C" fn(usize, *const c_char),
    ctx: usize,
) {
    unsafe {
        move || -> Result<(), Box<dyn Error>> {
            let samples = std::slice::from_raw_parts(samples, count);
            (*transcoder).push_audio_samples(samples, timestamp)?;
            Ok(())
        }()
        .handle(on_error, ctx, ())
    }
}

// You have to call this to use Transcoder APIs from another thread than the one instant_replay_create was called on.
// You have to call instant_replay_drop_mf_lifetime from the same thread when you're done with the lifetime.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn instant_replay_new_mf_lifetime_for_thread(
    on_error: unsafe extern "C" fn(usize, *const c_char),
    ctx: usize,
) -> *mut utils::MediaFoundationLifetime {
    move || -> Result<_, Box<dyn Error>> {
        Ok(Box::into_raw(Box::new(
            utils::MediaFoundationLifetime::new()?,
        )))
    }()
    .handle(on_error, ctx, std::ptr::null_mut())
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn instant_replay_drop_mf_lifetime(lifetime: *mut utils::MediaFoundationLifetime) {
    unsafe {
        drop(Box::from_raw(lifetime));
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn instant_replay_complete(
    transcoder: *mut transcoder::Transcoder,
    on_error: unsafe extern "C" fn(usize, *const c_char),
    ctx: usize,
) {
    unsafe {
        Box::from_raw(transcoder)
            .complete()
            .handle(on_error, ctx, ())
    }
}

trait ResultExt<T> {
    fn handle(
        self,
        on_error: unsafe extern "C" fn(usize, *const c_char),
        ctx: usize,
        default: T,
    ) -> T;
}

impl<T, E> ResultExt<T> for Result<T, E>
where
    E: std::fmt::Display,
{
    fn handle(
        self,
        on_error: unsafe extern "C" fn(usize, *const c_char),
        ctx: usize,
        default: T,
    ) -> T {
        match self {
            Ok(src) => src,
            Err(err) => {
                let err = format!("{}", err);
                unsafe { on_error(ctx, ffi::CString::new(err).unwrap().as_ptr()) };
                default
            }
        }
    }
}
