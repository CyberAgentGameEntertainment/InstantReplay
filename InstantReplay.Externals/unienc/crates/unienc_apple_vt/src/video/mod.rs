mod serialization;
use std::{ffi::c_void, ptr::NonNull};

use anyhow::{Context, Result};
use objc2::rc::Retained;
use objc2_core_foundation::{
    kCFAllocatorDefault, kCFBooleanFalse, kCFBooleanTrue, CFBoolean, CFDictionary, CFNumber,
    CFString, CFType,
};
use objc2_core_media::{
    kCMSampleAttachmentKey_NotSync, kCMTimeInvalid, kCMVideoCodecType_H264, CMSampleBuffer, CMTime,
};
use objc2_core_video::{kCVPixelFormatType_32BGRA, CVPixelBuffer, CVPixelBufferCreateWithBytes};
use objc2_video_toolbox::{
    kVTCompressionPropertyKey_AllowFrameReordering, kVTCompressionPropertyKey_AverageBitRate,
    kVTCompressionPropertyKey_RealTime, kVTInvalidSessionErr, VTCompressionSession,
    VTEncodeInfoFlags, VTSessionSetProperty,
};
use tokio::sync::mpsc;
use unienc_common::{buffer::SharedBuffer, EncodedData, Encoder, EncoderInput, EncoderOutput, VideoSample};

use crate::{common::UnsafeSendRetained, OsStatus};

pub struct VideoToolboxEncoder {
    input: VideoToolboxEncoderInput,
    output: VideoToolboxEncoderOutput,
}
pub struct VideoToolboxEncoderInput {
    session: CompressionSession,
    tx: Box<mpsc::Sender<VideoEncodedData>>,
    width: u32,
    height: u32,
    bitrate: u32,
}

struct CompressionSession {
    inner: Retained<VTCompressionSession>,
}

unsafe impl Send for VideoToolboxEncoderInput {}

pub struct VideoToolboxEncoderOutput {
    rx: mpsc::Receiver<VideoEncodedData>,
}

pub struct VideoEncodedData {
    pub sample_buffer: UnsafeSendRetained<CMSampleBuffer>,
    pub not_sync: bool,
}

impl VideoEncodedData {
    pub fn new(sample_buffer: UnsafeSendRetained<CMSampleBuffer>) -> Self {
        let attachments = unsafe { sample_buffer.sample_attachments_array(false) };
        let not_sync = attachments
            .map(|attachments| {
                assert_eq!(attachments.len(), 1);
                let dict = unsafe {
                    Retained::<CFDictionary<CFString, CFType>>::retain(
                        attachments.value_at_index(0) as *mut _,
                    )
                    .unwrap()
                };
                dict.get(unsafe { kCMSampleAttachmentKey_NotSync })
                    .map(|v| {
                        v.downcast::<CFBoolean>()
                            .map(|v| v.as_bool())
                            .unwrap_or_default()
                    })
                    .unwrap_or_default()
            })
            .unwrap_or_default();
        Self {
            sample_buffer,
            not_sync,
        }
    }
}

impl EncodedData for VideoEncodedData {
    fn timestamp(&self) -> f64 {
        unsafe {
            self.sample_buffer
                .output_presentation_time_stamp()
                .seconds()
        }
    }

    fn kind(&self) -> unienc_common::UniencDataKind {
        if self.not_sync {
            unienc_common::UniencDataKind::Interpolated
        } else {
            unienc_common::UniencDataKind::Key
        }
    }

    fn set_timestamp(&mut self, timestamp: f64) {
        unsafe {
            self.sample_buffer
                .set_output_presentation_time_stamp(CMTime::with_seconds(timestamp, 240))
        };
    }
}

unsafe extern "C-unwind" fn handle_video_encode_output(
    output_callback_ref_con: *mut c_void,
    _source_frame_ref_con: *mut c_void,
    _status: i32,
    _info_flags: VTEncodeInfoFlags,
    sample_buffer: *mut CMSampleBuffer,
) {
    let tx = unsafe { &*(output_callback_ref_con as *const mpsc::Sender<VideoEncodedData>) };

    if let Some(sample_buffer) = unsafe { Retained::retain(sample_buffer) } {
        _ = tx.try_send(VideoEncodedData::new(sample_buffer.into()));
    } // otherwise dropped
}

unsafe extern "C-unwind" fn release_pixel_buffer(
    release_ref_con: *mut c_void,
    _base_address: *const c_void,
) {
    drop(Box::<SharedBuffer>::from_raw(release_ref_con as *mut _));
}

impl Encoder for VideoToolboxEncoder {
    type InputType = VideoToolboxEncoderInput;

    type OutputType = VideoToolboxEncoderOutput;

    fn get(self) -> Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl EncoderInput for VideoToolboxEncoderInput {
    type Data = VideoSample;

    async fn push(&mut self, data: Self::Data) -> Result<()> {

        let buffer = data.buffer;
        let buffer_boxed = Box::new(buffer);
        let pixel_data_ptr = buffer_boxed.data().as_ptr();
        let buffer_boxed_raw = Box::into_raw(buffer_boxed);

        let mut buffer: *mut CVPixelBuffer = std::ptr::null_mut();
        unsafe {
            CVPixelBufferCreateWithBytes(
                kCFAllocatorDefault,
                data.width as usize,
                data.height as usize,
                kCVPixelFormatType_32BGRA,
                NonNull::new(pixel_data_ptr as *mut c_void)
                    .context("Failed to create NonNull from pixel data pointer")?,
                (data.width * 4) as usize,
                Some(release_pixel_buffer),
                buffer_boxed_raw as *mut _,
                None,
                NonNull::new(&mut buffer).context("Failed to create CVPixelBuffer")?,
            )
        }
        .to_result()
        .map_err(|err| {
            // free pixel data if failed
            _ = unsafe { Box::from_raw(buffer_boxed_raw) };
            err
        })?;

        let buffer = unsafe { Retained::from_raw(buffer) }.context("CVPixelBuffer is null")?;

        let mut retry = 0;

        loop {
            let res = unsafe {
                self.session.inner.encode_frame(
                    &buffer,
                    CMTime::with_seconds(data.timestamp, 720),
                    kCMTimeInvalid,
                    None,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                )
            };

            if res == kVTInvalidSessionErr && retry == 0 {
                // VTCompressionSession turns invalid when the app enters background on iOS
                // retrying once
                retry += 1;
                self.session =
                    CompressionSession::new(self.width, self.height, self.bitrate, &*self.tx)?;
                continue;
            }

            break res.to_result()?;
        }

        Ok(())
    }
}

impl EncoderOutput for VideoToolboxEncoderOutput {
    type Data = VideoEncodedData;

    async fn pull(&mut self) -> Result<Option<Self::Data>> {
        Ok(self.rx.recv().await)
    }
}

impl Drop for VideoToolboxEncoderInput {
    fn drop(&mut self) {
        unsafe { self.session.inner.complete_frames(kCMTimeInvalid) }
            .to_result()
            .unwrap();
        unsafe {
            self.session.inner.invalidate();
        }
    }
}

impl CompressionSession {
    fn new(
        width: u32,
        height: u32,
        bitrate: u32,
        tx: *const mpsc::Sender<VideoEncodedData>,
    ) -> Result<Self> {
        let mut session: *mut VTCompressionSession = std::ptr::null_mut();

        unsafe {
            VTCompressionSession::create(
                kCFAllocatorDefault,
                width as i32,
                height as i32,
                kCMVideoCodecType_H264,
                None,
                None,
                None,
                Some(handle_video_encode_output),
                tx as *mut c_void,
                NonNull::new(&mut session)
                    .context("Failed to create NonNull from session pointer")?,
            )
            .to_result()?;
        }

        let session =
            unsafe { Retained::from_raw(session).context("VTCompressionSession is null.")? };
        unsafe {
            VTSessionSetProperty(
                &session,
                kVTCompressionPropertyKey_RealTime,
                kCFBooleanTrue.map(|b| b as &CFType),
            )
        }
        .to_result()?;
        unsafe {
            VTSessionSetProperty(
                &session,
                kVTCompressionPropertyKey_AllowFrameReordering,
                kCFBooleanFalse.map(|b| b as &CFType),
            )
        }
        .to_result()?;
        unsafe {
            VTSessionSetProperty(
                &session,
                kVTCompressionPropertyKey_AverageBitRate,
                Some(&CFNumber::new_i32(bitrate as i32)),
            )
        }
        .to_result()?;

        Ok(CompressionSession { inner: session })
    }
}

impl VideoToolboxEncoder {
    pub fn new(options: &impl unienc_common::VideoEncoderOptions) -> Result<Self> {
        let (tx, rx) = mpsc::channel(32);
        let tx = Box::new(tx);

        let (width, height, bitrate) = (options.width(), options.height(), options.bitrate());

        Ok(VideoToolboxEncoder {
            input: VideoToolboxEncoderInput {
                session: CompressionSession::new(width, height, bitrate, &*tx)?,
                tx,
                width,
                height,
                bitrate,
            },
            output: VideoToolboxEncoderOutput { rx },
        })
    }
}
