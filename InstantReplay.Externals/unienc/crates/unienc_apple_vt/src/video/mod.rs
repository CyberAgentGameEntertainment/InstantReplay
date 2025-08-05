mod serialization;
use std::{ffi::c_void, ptr::NonNull};

use anyhow::{Context, Result};
use objc2::rc::Retained;
use objc2_core_foundation::{
    kCFAllocatorDefault, kCFBooleanFalse, kCFBooleanTrue, CFBoolean, CFDictionary, CFString, CFType,
};
use objc2_core_media::{
    kCMSampleAttachmentKey_NotSync, kCMTimeInvalid, kCMVideoCodecType_H264, CMSampleBuffer, CMTime,
};
use objc2_core_video::{kCVPixelFormatType_32BGRA, CVPixelBuffer, CVPixelBufferCreateWithBytes};
use objc2_video_toolbox::{
    kVTCompressionPropertyKey_AllowFrameReordering, kVTCompressionPropertyKey_RealTime,
    VTCompressionSession, VTEncodeInfoFlags, VTSessionSetProperty,
};
use tokio::sync::mpsc;
use unienc_common::{EncodedData, Encoder, EncoderInput, EncoderOutput, VideoSample};

use crate::{common::UnsafeSendRetained, OsStatus};

pub struct VideoToolboxEncoder {
    input: VideoToolboxEncoderInput,
    output: VideoToolboxEncoderOutput,
}
pub struct VideoToolboxEncoderInput {
    session: Retained<VTCompressionSession>,
    _tx: Box<mpsc::Sender<VideoEncodedData>>,
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
    source_frame_ref_con: *mut c_void,
    _status: i32,
    _info_flags: VTEncodeInfoFlags,
    sample_bufer: *mut CMSampleBuffer,
) {
    let tx = unsafe { &*(output_callback_ref_con as *const mpsc::Sender<VideoEncodedData>) };

    drop(unsafe { Retained::from_raw(source_frame_ref_con as *mut CVPixelBuffer) });

    if let Some(sample_buffer) = unsafe { Retained::retain(sample_bufer) } {
        _ = tx.try_send(VideoEncodedData::new(sample_buffer.into()));
    } // otherwise dropped
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

    async fn push(&mut self, data: &Self::Data) -> Result<()> {
        let pixel_data = &data.data;
        let pixel_data_ptr = pixel_data.as_ptr();

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
                None,
                std::ptr::null_mut(),
                None,
                NonNull::new(&mut buffer).context("Failed to create CVPixelBuffer")?,
            )
            .to_result()?;
        }

        let buffer = unsafe { Retained::from_raw(buffer) }.context("CVPixelBuffer is null")?;

        let buffer_retained = Retained::into_raw(buffer.clone());

        unsafe {
            self.session
                .encode_frame(
                    &buffer,
                    CMTime::with_seconds(data.timestamp, 720),
                    kCMTimeInvalid,
                    None,
                    buffer_retained as *mut c_void,
                    std::ptr::null_mut(),
                )
                .to_result()?;
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
        unsafe { self.session.complete_frames(kCMTimeInvalid) }
            .to_result()
            .unwrap();
        unsafe {
            self.session.invalidate();
        }
    }
}

impl VideoToolboxEncoder {
    pub fn new(options: &impl unienc_common::VideoEncoderOptions) -> Result<Self> {
        let mut session: *mut VTCompressionSession = std::ptr::null_mut();
        let (tx, rx) = mpsc::channel(32);

        let tx = Box::new(tx);

        unsafe {
            VTCompressionSession::create(
                kCFAllocatorDefault,
                options.width() as i32,
                options.height() as i32,
                kCMVideoCodecType_H264,
                None,
                None,
                None,
                Some(handle_video_encode_output),
                &*tx as *const mpsc::Sender<_> as *mut c_void,
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
        Ok(VideoToolboxEncoder {
            input: VideoToolboxEncoderInput { session, _tx: tx },
            output: VideoToolboxEncoderOutput { rx },
        })
    }
}
