use std::cell::RefCell;
use std::ffi::{c_char, c_void};
use std::fs;
use std::sync::Mutex;
use std::{path::Path, ptr::NonNull};

use anyhow::Result;
use block2::RcBlock;
use dispatch2::DispatchQueue;
use objc2::rc::Retained;
use objc2_av_foundation::{
    AVAssetWriter, AVAssetWriterInput, AVAssetWriterStatus, AVFileTypeMPEG4, AVMediaTypeAudio,
    AVMediaTypeVideo,
};
use objc2_core_audio_types::{
    kAudioFormatMPEG4AAC, AudioStreamBasicDescription, AudioStreamPacketDescription, MPEG4ObjectID,
};
use objc2_core_foundation::kCFAllocatorDefault;
use objc2_core_media::{
    kCMBlockBufferAssureMemoryNowFlag, kCMTimeZero, kCMVideoCodecType_H264,
    CMAudioFormatDescriptionCreate, CMAudioSampleBufferCreateReadyWithPacketDescriptions,
    CMBlockBuffer, CMFormatDescription, CMSampleBuffer, CMTime, CMVideoFormatDescriptionCreate,
};
use objc2_foundation::{NSString, NSURL};
use tokio::sync::{mpsc, oneshot};
use unienc_common::{CompletionHandle, Muxer, MuxerInput};

use crate::common::UnsafeSendRetained;
use crate::OsStatus;
use crate::{audio::AudioPacket, video::VideoEncodedData};

pub struct AVFMuxer {
    writer: objc2::rc::Retained<AVAssetWriter>,
    video_input: AVFMuxerVideoInput,
    audio_input: AVFMuxerAudioInput,
}

pub struct AVFMuxerVideoInput {
    tx: mpsc::Sender<Mutex<UnsafeSendRetained<CMSampleBuffer>>>,
    finish_rx: oneshot::Receiver<Result<()>>,
}

pub struct AVFMuxerAudioInput {
    asbd: AudioStreamBasicDescription,
    tx: mpsc::Sender<Mutex<UnsafeSendRetained<CMSampleBuffer>>>,
    finish_rx: oneshot::Receiver<Result<()>>,
    magic_cookie_applied: bool,
}

impl MuxerInput for AVFMuxerVideoInput {
    type Data = VideoEncodedData;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        self.tx.send(Mutex::new(data.sample_buffer)).await?;

        Ok(())
    }

    async fn finish(self) -> Result<()> {
        drop(self.tx);
        match self.finish_rx.await {
            Ok(inner) => inner,
            Err(inner) => Err(inner.into()),
        }
    }
}

impl MuxerInput for AVFMuxerAudioInput {
    type Data = AudioPacket;

    async fn push(&mut self, data: Self::Data) -> Result<()> {
        let sample_buffer =
            create_audio_sample_buffer(&data, &mut self.asbd, !self.magic_cookie_applied)?;
        self.magic_cookie_applied = true;
        self.tx.send(Mutex::new(sample_buffer.into())).await?;

        Ok(())
    }

    async fn finish(self) -> Result<()> {
        drop(self.tx);
        match self.finish_rx.await {
            Ok(inner) => inner,
            Err(inner) => Err(inner.into()),
        }
    }
}

unsafe impl Send for AVFMuxer {}

pub struct AVFMuxerCompletionHandle {
    writer: UnsafeSendRetained<AVAssetWriter>,
}

impl CompletionHandle for AVFMuxerCompletionHandle {
    async fn finish(self) -> Result<()> {
        let writer = self.writer;
        let (tx, rx) = oneshot::channel();
        let tx = RefCell::new(Some(tx));
        unsafe {
            writer.finishWritingWithCompletionHandler(&RcBlock::new(move || {
                if let Some(tx) = tx.borrow_mut().take() {
                    tx.send(()).unwrap();
                }
            }));
        }

        rx.await.unwrap();

        Ok(())
    }
}

impl Muxer for AVFMuxer {
    type VideoInputType = AVFMuxerVideoInput;
    type AudioInputType = AVFMuxerAudioInput;
    type CompletionHandleType = AVFMuxerCompletionHandle;

    fn get_inputs(
        self,
    ) -> Result<(
        Self::VideoInputType,
        Self::AudioInputType,
        Self::CompletionHandleType,
    )> {
        Ok((
            self.video_input,
            self.audio_input,
            AVFMuxerCompletionHandle {
                writer: self.writer.into(),
            },
        ))
    }
}

impl AVFMuxer {
    pub fn new<P: AsRef<Path>>(
        output_path: P,
        video_options: &impl unienc_common::VideoEncoderOptions,
        audio_options: &impl unienc_common::AudioEncoderOptions,
    ) -> Result<Self> {
        let path = output_path.as_ref();
        _ = fs::remove_file(path);
        let url =
            NSURL::fileURLWithPath(&NSString::from_str(path.to_string_lossy().as_ref()));

        let file_type = unsafe { AVFileTypeMPEG4.unwrap() };
        let writer = unsafe {
            objc2_av_foundation::AVAssetWriter::assetWriterWithURL_fileType_error(&url, file_type)?
        };

        let source_format_hint = unsafe {
            let mut format_desc: *const CMFormatDescription = std::ptr::null();
            CMVideoFormatDescriptionCreate(
                kCFAllocatorDefault,
                kCMVideoCodecType_H264,
                video_options.width() as i32,
                video_options.height() as i32,
                None,
                NonNull::new(&mut format_desc).unwrap(),
            )
            .to_result()?;
            format_desc
        };
        let video_input = unsafe {
            objc2_av_foundation::AVAssetWriterInput::assetWriterInputWithMediaType_outputSettings_sourceFormatHint(
                AVMediaTypeVideo.unwrap(),
                None,
                Some(&*source_format_hint)
            )
        };

        let mut asbd = AudioStreamBasicDescription {
            mSampleRate: audio_options.sample_rate() as f64,
            mFormatID: kAudioFormatMPEG4AAC,
            mFormatFlags: MPEG4ObjectID::AAC_LC.0 as u32,
            mBytesPerPacket: 0,
            mFramesPerPacket: 1024,
            mBytesPerFrame: 0,
            mChannelsPerFrame: audio_options.channels(),
            mBitsPerChannel: 0,
            mReserved: 0,
        };
        let source_format_hint = unsafe {
            let mut format_desc: *const CMFormatDescription = std::ptr::null();
            CMAudioFormatDescriptionCreate(
                kCFAllocatorDefault,
                NonNull::new(&mut asbd).unwrap(),
                0,
                std::ptr::null(),
                0,
                std::ptr::null(),
                None,
                NonNull::new(&mut format_desc).unwrap(),
            )
            .to_result()?;
            format_desc
        };
        let audio_input = unsafe {
            objc2_av_foundation::AVAssetWriterInput::assetWriterInputWithMediaType_outputSettings_sourceFormatHint(
                AVMediaTypeAudio.unwrap(),
                None,
                Some(&*source_format_hint)
            )
        };

        unsafe {
            writer.addInput(&video_input);
            writer.addInput(&audio_input);
        }

        if !unsafe { writer.startWriting() } {
            if unsafe { writer.status() } == AVAssetWriterStatus::Failed {
                if let Some(err) = unsafe { writer.error() } {
                    return Err(anyhow::anyhow!("Failed to start writing: {}", err));
                }
            }
            return Err(anyhow::anyhow!("Failed to start writing"));
        }

        unsafe { writer.startSessionAtSourceTime(kCMTimeZero) };

        fn connect_input(
            writer: Retained<AVAssetWriter>,
            input: Retained<AVAssetWriterInput>,
            label: &str,
        ) -> (
            mpsc::Sender<Mutex<UnsafeSendRetained<CMSampleBuffer>>>,
            oneshot::Receiver<Result<()>>,
        ) {
            let (tx, rx) = mpsc::channel::<Mutex<UnsafeSendRetained<CMSampleBuffer>>>(100);
            let (finish_tx, finish_rx) = oneshot::channel::<Result<()>>();

            let rx = RefCell::new(rx);
            let finish_tx = RefCell::new(Some(finish_tx));

            let input_clone = input.clone();
            let label = label.to_string();
            let label_clone = label.clone();

            let on_ready = RcBlock::new(move || {
                while unsafe { input_clone.isReadyForMoreMediaData() } {
                    match rx.borrow_mut().try_recv() {
                        Ok(sample_buffer) => {
                            if !unsafe { input_clone.appendSampleBuffer(&sample_buffer.lock().unwrap()) } {
                                // TODO: handle error
                                println!(
                                    "failed to append sample buffer: {label_clone}, {}",
                                    unsafe { writer.error().unwrap() }
                                );
                                return;
                            }
                        }
                        Err(mpsc::error::TryRecvError::Empty) => {
                            return;
                        }
                        Err(mpsc::error::TryRecvError::Disconnected) => unsafe {
                            if let Some(finish_tx) = finish_tx.borrow_mut().take() {
                                input_clone.markAsFinished();
                                finish_tx.send(Ok(())).unwrap_or_else(|e| {
                                    println!("failed to send finish signal: {e:?}");
                                });
                            }
                            return;
                        },
                    }
                }
            });

            unsafe {
                input.requestMediaDataWhenReadyOnQueue_usingBlock(
                    &DispatchQueue::new(&label, None),
                    &on_ready,
                )
            };

            (tx, finish_rx)
        }

        let (video_tx, video_finish_rx) = connect_input(writer.clone(), video_input, "video input");
        let (audio_tx, audio_finish_rx) = connect_input(writer.clone(), audio_input, "audio input");

        Ok(Self {
            writer,
            video_input: AVFMuxerVideoInput {
                tx: video_tx,
                finish_rx: video_finish_rx,
            },
            audio_input: AVFMuxerAudioInput {
                asbd,
                tx: audio_tx,
                finish_rx: audio_finish_rx,
                magic_cookie_applied: false,
            },
        })
    }
}

fn create_audio_sample_buffer(
    audio: &AudioPacket,
    asbd: &mut AudioStreamBasicDescription,
    apply_magic_cookie: bool,
) -> Result<Retained<CMSampleBuffer>> {
    let format_desc = unsafe {
        let mut format_desc: *const CMFormatDescription = std::ptr::null();
        objc2_core_media::CMAudioFormatDescriptionCreate(
            kCFAllocatorDefault,
            NonNull::new(asbd).unwrap(),
            0,
            std::ptr::null(),
            if apply_magic_cookie {
                audio.magic_cookie.len()
            } else {
                0
            },
            if apply_magic_cookie {
                audio.magic_cookie.as_ptr() as *const c_void
            } else {
                std::ptr::null()
            },
            None,
            NonNull::new(&mut format_desc).unwrap(),
        )
        .to_result()?;
        Retained::from_raw(format_desc as _).unwrap()
    };

    let block_buffer = unsafe {
        let mut block_buffer: *mut objc2_core_media::CMBlockBuffer = std::ptr::null_mut();

        CMBlockBuffer::create_with_memory_block(
            kCFAllocatorDefault,
            std::ptr::null_mut(),
            audio.data.len(),
            kCFAllocatorDefault,
            std::ptr::null(),
            0,
            audio.data.len(),
            kCMBlockBufferAssureMemoryNowFlag,
            NonNull::new(&mut block_buffer).unwrap(),
        )
        .to_result()?;
        Retained::from_raw(block_buffer).unwrap()
    };

    let mut length_at_offset_out = 0_usize;
    let mut total_length_out = 0_usize;
    let mut data_pointer_out: *mut c_char = std::ptr::null_mut();
    let mut data_t = audio.data.as_slice();
    while !data_t.is_empty() {
        unsafe {
            block_buffer.data_pointer(
                0,
                &mut length_at_offset_out,
                &mut total_length_out,
                &mut data_pointer_out,
            );
        }

        assert_eq!(total_length_out, data_t.len());

        let buffer = unsafe {
            std::slice::from_raw_parts_mut::<u8>(data_pointer_out as *mut u8, length_at_offset_out)
        };

        buffer.copy_from_slice(&data_t[..buffer.len()]);

        data_t = &data_t[buffer.len()..];
    }

    let sample_buffer = unsafe {
        let timestamp = CMTime::new(audio.timestamp_in_samples as i64, asbd.mSampleRate as i32);

        let packet_desc = AudioStreamPacketDescription {
            mStartOffset: 0,
            mDataByteSize: audio.data.len() as u32,
            mVariableFramesInPacket: 0,
        };

        let mut sample_buffer: *mut CMSampleBuffer = std::ptr::null_mut();
        CMAudioSampleBufferCreateReadyWithPacketDescriptions(
            kCFAllocatorDefault,
            &block_buffer,
            &format_desc,
            1_isize,
            timestamp,
            &packet_desc as *const _,
            NonNull::new(&mut sample_buffer).unwrap(),
        )
        .to_result()?;
        Retained::from_raw(sample_buffer).unwrap()
    };

    Ok(sample_buffer)
}
