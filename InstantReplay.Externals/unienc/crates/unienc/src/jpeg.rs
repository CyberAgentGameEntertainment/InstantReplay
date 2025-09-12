use std::ffi::{c_char, c_void, CString};

use anyhow::Context;

type Callback = extern "C" fn(
    error: *const c_char,
    data: *const u8,
    width: usize,
    height: usize,
    pitch: usize,
    user_data: *const c_void,
);

#[no_mangle]
pub extern "C" fn unienc_jpeg_decode(
    data: *const u8,
    size: usize,
    callback: usize,
    user_data: *const c_void,
) {
    let callback = unsafe { std::mem::transmute::<usize, Callback>(callback) };
    let data = unsafe { std::slice::from_raw_parts(data, size) };
    match turbojpeg::decompress(data, turbojpeg::PixelFormat::BGRA).context("Failed to decompress JPEG image") {
        Ok(image) => {
            callback(
                std::ptr::null(),
                image.pixels.as_ptr(),
                image.width,
                image.height,
                image.pitch,
                user_data,
            );
        }
        Err(err) => {
            callback(
                CString::new(err.to_string()).unwrap().as_ptr(),
                std::ptr::null(),
                0,
                0,
                0,
                user_data,
            );
        }
    }
}
