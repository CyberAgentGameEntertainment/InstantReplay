mod serialization;
use std::{ffi::c_void, ptr::NonNull};

use crate::allocator;
use crate::error::{AppleError, OsStatusExt, Result};
use objc2::rc::Retained;
use objc2_core_foundation::{
    CFBoolean, CFDictionary, CFNumber, CFString, CFType, kCFBooleanFalse, kCFBooleanTrue,
};
use objc2_core_media::{
    CMSampleBuffer, CMTime, kCMSampleAttachmentKey_NotSync, kCMTimeInvalid, kCMVideoCodecType_H264,
};
use objc2_core_video::{CVPixelBuffer, CVPixelBufferCreateWithBytes, kCVPixelFormatType_32BGRA};
use objc2_video_toolbox::{
    VTCompressionSession, VTEncodeInfoFlags, VTSessionSetProperty,
    kVTCompressionPropertyKey_AllowFrameReordering, kVTCompressionPropertyKey_AverageBitRate,
    kVTCompressionPropertyKey_RealTime, kVTInvalidSessionErr,
};
use tokio::sync::mpsc;
use unienc_common::{
    EncodedData, Encoder, EncoderInput, EncoderOutput, VideoSample, buffer::SharedBuffer,
};

use crate::{MetalTexture, common::UnsafeSendRetained, metal};
use unienc_common::TryFromUnityNativeTexturePointer;

/// Capacity of the bounded channel carrying encoded frames from the VideoToolbox callback to the
/// consumer. `push` reserves a slot before submitting each frame, so when the consumer falls behind
/// and the channel fills up, `push` suspends (backpressure) instead of the callback dropping
/// already-encoded frames (which would break the P-frame reference chain).
const OUTPUT_CHANNEL_CAPACITY: usize = 32;

/// A reserved output-channel slot handed to a single encode callback via the per-frame source-frame
/// ref-con. Completing the send through it can neither block the callback nor drop the frame.
type OutputPermit = mpsc::OwnedPermit<Result<VideoEncodedData>>;

pub struct VideoToolboxEncoder {
    input: VideoToolboxEncoderInput,
    output: VideoToolboxEncoderOutput,
}
pub struct VideoToolboxEncoderInput {
    session: CompressionSession,
    tx: Box<mpsc::Sender<Result<VideoEncodedData>>>,
    width: u32,
    height: u32,
    bitrate: u32,
}

struct CompressionSession {
    inner: Retained<VTCompressionSession>,
}

unsafe impl Send for VideoToolboxEncoderInput {}

pub struct VideoToolboxEncoderOutput {
    rx: mpsc::Receiver<Result<VideoEncodedData>>,
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

    fn kind(&self) -> unienc_common::UniencSampleKind {
        if self.not_sync {
            unienc_common::UniencSampleKind::Interpolated
        } else {
            unienc_common::UniencSampleKind::Key
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
    _output_callback_ref_con: *mut c_void,
    source_frame_ref_con: *mut c_void,
    status: i32,
    _info_flags: VTEncodeInfoFlags,
    sample_buffer: *mut CMSampleBuffer,
) {
    // Each submitted frame carries a pre-reserved output-channel slot (an OwnedPermit) as its
    // source-frame ref-con. Completing the send through that permit means this callback never
    // blocks (it runs on VideoToolbox's internal queue, which complete_frames() waits on) and
    // never drops an encoded frame (dropping would break the reference chain of subsequent
    // P-frames). Backpressure is instead applied in `push`, which awaits the reservation before
    // submitting the frame, so a full channel throttles the producer rather than corrupting output.
    let permit = unsafe { *Box::from_raw(source_frame_ref_con as *mut OutputPermit) };

    // Propagate encode failures to the output side instead of silently ignoring them.
    if let Err(err) = status.to_result() {
        permit.send(Err(err));
        return;
    }

    if let Some(sample_buffer) = unsafe { Retained::retain(sample_buffer) } {
        permit.send(Ok(VideoEncodedData::new(sample_buffer.into())));
    } else {
        // The encoder itself dropped the frame (e.g. kVTEncodeInfo_FrameDropped); dropping the
        // permit returns the reserved slot to the channel.
        drop(permit);
    }
}

unsafe extern "C-unwind" fn release_pixel_buffer(
    release_ref_con: *mut c_void,
    _base_address: *const c_void,
) {
    unsafe {
        drop(Box::<SharedBuffer>::from_raw(release_ref_con as *mut _));
    }
}

impl Encoder for VideoToolboxEncoder {
    type InputType = VideoToolboxEncoderInput;

    type OutputType = VideoToolboxEncoderOutput;

    fn get(self) -> unienc_common::Result<(Self::InputType, Self::OutputType)> {
        Ok((self.input, self.output))
    }
}

impl EncoderInput for VideoToolboxEncoderInput {
    type Data = VideoSample<MetalTexture>;

    async fn push(&mut self, data: Self::Data) -> unienc_common::Result<()> {
        // Reserve an output-channel slot before doing any encode work. When the consumer is behind
        // and the channel is full, this await suspends `push`, propagating backpressure up the
        // pipeline so the C# side drops *input* frames (via DroppingChannelInput) instead of us
        // dropping already-encoded output. Holding the OwnedPermit (Send) across the blit await
        // below is fine; if building the frame fails, dropping the permit releases the slot.
        let permit = self
            .tx
            .as_ref()
            .clone()
            .reserve_owned()
            .await
            .map_err(AppleError::from)?;

        let buffer = match data.frame {
            unienc_common::VideoFrame::Bgra32(bgra32) => {
                let buffer = bgra32.buffer;
                let buffer_boxed = Box::new(buffer);
                let pixel_data_ptr = buffer_boxed.data().as_ptr();
                let buffer_boxed_raw = Box::into_raw(buffer_boxed);

                let mut buffer: *mut CVPixelBuffer = std::ptr::null_mut();
                unsafe {
                    CVPixelBufferCreateWithBytes(
                        allocator::default(),
                        bgra32.width as usize,
                        bgra32.height as usize,
                        kCVPixelFormatType_32BGRA,
                        NonNull::new(pixel_data_ptr as *mut c_void)
                            .ok_or(AppleError::NonNullCreationFailed)?,
                        (bgra32.width * 4) as usize,
                        Some(release_pixel_buffer),
                        buffer_boxed_raw as *mut _,
                        None,
                        NonNull::new(&mut buffer).ok_or(AppleError::NonNullCreationFailed)?,
                    )
                }
                .to_result()
                .inspect_err(|_err| {
                    // free pixel data if failed
                    _ = unsafe { Box::from_raw(buffer_boxed_raw) };
                })?;

                unsafe { Retained::from_raw(buffer) }.ok_or(AppleError::PixelBufferNull)?
            }
            unienc_common::VideoFrame::BlitSource {
                texture_token,
                width: _,
                height: _,
                graphics_format: _,
                flip_vertically,
                is_gamma_workflow,
                event_issuer,
                _phantom,
            } => {
                let width = self.width;
                let height = self.height;

                let (tx, rx) = tokio::sync::oneshot::channel();
                event_issuer.issue_graphics_event(
                    Box::new(move |native_texture_ptr| {
                        let r = MetalTexture::try_from_unity_native_texture_ptr(native_texture_ptr)
                            .map_err(|_| AppleError::MetalTextureRetainFailed)
                            .and_then(|texture| {
                                metal::custom_blit(
                                    &texture.texture,
                                    width,
                                    height,
                                    flip_vertically,
                                    is_gamma_workflow,
                                )
                            });
                        tx.send(r)
                            .map_err(|_e| AppleError::BlitFutureSendFailed)
                            .unwrap();
                    }),
                    *crate::metal::EVENT_ID
                        .get()
                        .ok_or(AppleError::EventIdNotReserved)?,
                    texture_token,
                );

                let texture = rx
                    .await
                    .map_err(AppleError::from)?? // failed to receive
                    // failed to issue blit
                    .await?; // blit failed
                texture.pixel_buffer()
            }
        };

        // Hand the reserved slot to this frame's encode callback via the source-frame ref-con. No
        // await happens past this point, so the raw pointer is never held across a suspension.
        let permit_ptr = Box::into_raw(Box::new(permit));

        let mut retry = 0;

        let res = loop {
            let res = unsafe {
                self.session.inner.encode_frame(
                    &buffer,
                    CMTime::with_seconds(data.timestamp, 720),
                    kCMTimeInvalid,
                    None,
                    permit_ptr as *mut c_void,
                    std::ptr::null_mut(),
                )
            };

            if res == kVTInvalidSessionErr && retry == 0 {
                // VTCompressionSession turns invalid when the app enters background on iOS; retry
                // once with a fresh session, re-passing the same reserved permit.
                retry += 1;
                match CompressionSession::new(self.width, self.height, self.bitrate) {
                    Ok(session) => {
                        self.session = session;
                        continue;
                    }
                    Err(err) => {
                        // No callback will run for this frame; reclaim the permit to free the slot.
                        drop(unsafe { Box::from_raw(permit_ptr) });
                        return Err(err.into());
                    }
                }
            }

            break res;
        };

        if let Err(err) = res.to_result() {
            // The frame was not accepted, so the callback will never consume the permit; reclaim it
            // to release the reserved slot.
            drop(unsafe { Box::from_raw(permit_ptr) });
            return Err(err.into());
        }

        Ok(())
    }
}

impl EncoderOutput for VideoToolboxEncoderOutput {
    type Data = VideoEncodedData;

    async fn pull(&mut self) -> unienc_common::Result<Option<Self::Data>> {
        match self.rx.recv().await {
            Some(Ok(data)) => Ok(Some(data)),
            Some(Err(err)) => Err(err.into()),
            None => Ok(None),
        }
    }
}

impl Drop for VideoToolboxEncoderInput {
    fn drop(&mut self) {
        let res = unsafe { self.session.inner.complete_frames(kCMTimeInvalid) };

        if res == kVTInvalidSessionErr {
            // already invalid (e.g., app in background)
            return;
        }

        res.to_result().unwrap();
        unsafe {
            self.session.inner.invalidate();
        }
    }
}

impl CompressionSession {
    fn new(width: u32, height: u32, bitrate: u32) -> Result<Self> {
        let mut session: *mut VTCompressionSession = std::ptr::null_mut();

        unsafe {
            VTCompressionSession::create(
                allocator::default(),
                width as i32,
                height as i32,
                kCMVideoCodecType_H264,
                None,
                None,
                None,
                Some(handle_video_encode_output),
                // The output callback receives its channel slot per-frame via the source-frame
                // ref-con (see `push`), so no session-level ref-con is needed.
                std::ptr::null_mut(),
                NonNull::new(&mut session).ok_or(AppleError::NonNullCreationFailed)?,
            )
            .to_result()?;
        }

        let session =
            unsafe { Retained::from_raw(session).ok_or(AppleError::CompressionSessionNull)? };
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
        let (tx, rx) = mpsc::channel(OUTPUT_CHANNEL_CAPACITY);
        let tx = Box::new(tx);

        let (width, height, bitrate) = (options.width(), options.height(), options.bitrate());

        Ok(VideoToolboxEncoder {
            input: VideoToolboxEncoderInput {
                session: CompressionSession::new(width, height, bitrate)?,
                tx,
                width,
                height,
                bitrate,
            },
            output: VideoToolboxEncoderOutput { rx },
        })
    }
}
