use std::{
    ffi::{c_char, c_int},
    ptr::NonNull,
};

use bincode::{Decode, Encode};
use objc2::rc::Retained;
use objc2_core_foundation::{
    CFBoolean, CFDictionary, CFMutableDictionary, CFString, CFType, kCFAllocatorDefault,
    kCFBooleanTrue,
};
use objc2_core_media::{
    CMBlockBuffer, CMFormatDescription, CMSampleBuffer, CMSampleTimingInfo, CMTime, CMTimeFlags,
    CMVideoFormatDescription, CMVideoFormatDescriptionCreateFromH264ParameterSets,
    CMVideoFormatDescriptionGetH264ParameterSetAtIndex, kCMBlockBufferAssureMemoryNowFlag,
    kCMSampleAttachmentKey_NotSync, kCMVideoCodecType_H264,
};

use crate::{OsStatus, video::VideoEncodedData};

#[derive(Encode, Decode, Clone, Copy, Debug, PartialEq)]
struct CMTimeForSerialization {
    pub value: i64,
    pub timescale: i32,
    pub flags: u32,
    pub epoch: i64,
}

impl From<CMTime> for CMTimeForSerialization {
    fn from(value: CMTime) -> Self {
        Self {
            value: value.value,
            timescale: value.timescale,
            flags: value.flags.0,
            epoch: value.epoch,
        }
    }
}

impl From<CMTimeForSerialization> for CMTime {
    fn from(value: CMTimeForSerialization) -> Self {
        Self {
            value: value.value,
            timescale: value.timescale,
            flags: CMTimeFlags(value.flags),
            epoch: value.epoch,
        }
    }
}

#[derive(Encode, Decode, Clone, Copy, Debug, PartialEq)]
struct CMSampleTimingInfoForSerialization {
    pub duration: CMTimeForSerialization,
    pub presentation_time_stamp: CMTimeForSerialization,
    pub decode_time_stamp: CMTimeForSerialization,
}

impl From<CMSampleTimingInfo> for CMSampleTimingInfoForSerialization {
    fn from(value: CMSampleTimingInfo) -> Self {
        Self {
            duration: value.duration.into(),
            presentation_time_stamp: value.presentationTimeStamp.into(),
            decode_time_stamp: value.decodeTimeStamp.into(),
        }
    }
}

impl From<CMSampleTimingInfoForSerialization> for CMSampleTimingInfo {
    fn from(value: CMSampleTimingInfoForSerialization) -> Self {
        Self {
            duration: value.duration.into(),
            presentationTimeStamp: value.presentation_time_stamp.into(),
            decodeTimeStamp: value.decode_time_stamp.into(),
        }
    }
}

#[derive(Encode, Decode)]
struct VideoEncodedDataForSerialization {
    data_buffer: Option<Vec<u8>>,
    timing_info: CMSampleTimingInfoForSerialization,
    not_sync: bool,
    parameters: Option<H264ParameterSet>,
}

#[derive(Encode, Decode)]
struct H264ParameterSet {
    nal_unit_header_length: i32,
    sps: Vec<u8>,
    pps: Vec<u8>,
}

impl Encode for VideoEncodedData {
    fn encode<E: bincode::enc::Encoder>(
        &self,
        encoder: &mut E,
    ) -> std::result::Result<(), bincode::error::EncodeError> {
        if unsafe { self.sample_buffer.num_samples() } != 1 {
            todo!("not supported")
        }

        let data_buffer = unsafe { self.sample_buffer.data_buffer() };
        let data_buffer = data_buffer.map(|data_buffer| {
            let mut data: Vec<u8> = Vec::<u8>::new();
            while {
                let mut length_at_offset: usize = 0;
                let mut total_length: usize = 0;
                let mut data_pointer: *mut c_char = std::ptr::null_mut();
                unsafe {
                    CMBlockBuffer::data_pointer(
                        &data_buffer,
                        data.len(),
                        &mut length_at_offset,
                        &mut total_length,
                        &mut data_pointer,
                    )
                    .to_result()
                    .unwrap();
                };

                if data_pointer.is_null() {
                    assert_eq!(total_length, 0);
                    false
                } else {
                    let slice = unsafe {
                        std::slice::from_raw_parts(data_pointer as *const u8, length_at_offset)
                    };
                    data.extend_from_slice(slice);

                    total_length != length_at_offset
                }
            } {}

            data
        });

        // timing
        let timing_info: CMSampleTimingInfo = unsafe {
            let mut timing_info_out: CMSampleTimingInfo = std::mem::zeroed();
            self.sample_buffer
                .sample_timing_info(0, NonNull::new(&mut timing_info_out).unwrap())
                .to_result()
                .map_err(|err| {
                    bincode::error::EncodeError::OtherString(format!(
                        "Failed to get sample timing info: {:?}",
                        err
                    ))
                })?;
            timing_info_out
        };

        // is key frame
        let attachments = unsafe { self.sample_buffer.sample_attachments_array(false) };
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

        // format description
        let format_desc = unsafe { CMSampleBuffer::format_description(&self.sample_buffer) };

        let parameters = format_desc.map(|format_desc| {
            let Ok(format_desc) = format_desc.downcast::<CMVideoFormatDescription>() else {
                todo!()
            };
            if unsafe { format_desc.media_sub_type() } != kCMVideoCodecType_H264 {
                todo!()
            }
            let mut sps_ptr: *const u8 = std::ptr::null();
            let mut sps_size: usize = 0;
            let mut pps_ptr: *const u8 = std::ptr::null();
            let mut pps_size: usize = 0;

            let mut count: usize = 0;
            let mut nalu_header_length: c_int = 0;

            unsafe {
                CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    &format_desc,
                    0,
                    &mut sps_ptr,
                    &mut sps_size,
                    &mut count,
                    &mut nalu_header_length,
                )
                .to_result()
                .unwrap()
            };
            unsafe {
                CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    &format_desc,
                    1,
                    &mut pps_ptr,
                    &mut pps_size,
                    &mut count,
                    &mut nalu_header_length,
                )
                .to_result()
                .unwrap()
            };

            let sps = unsafe { std::slice::from_raw_parts(sps_ptr, sps_size) }.to_vec();
            let pps = unsafe { std::slice::from_raw_parts(pps_ptr, pps_size) }.to_vec();

            H264ParameterSet {
                nal_unit_header_length: nalu_header_length as i32,
                sps,
                pps,
            }
        });

        VideoEncodedDataForSerialization {
            data_buffer,
            timing_info: timing_info.into(),
            not_sync,
            parameters,
        }
        .encode(encoder)
    }
}

impl Decode<()> for VideoEncodedData {
    fn decode<D: bincode::de::Decoder<Context = ()>>(
        decoder: &mut D,
    ) -> std::result::Result<Self, bincode::error::DecodeError> {
        let VideoEncodedDataForSerialization {
            data_buffer,
            timing_info,
            not_sync,
            mut parameters,
        } = VideoEncodedDataForSerialization::decode(decoder)?;

        let sample_size = data_buffer
            .as_ref()
            .map_or(0, |data_buffer| data_buffer.len());

        // data
        let data_buffer = match data_buffer {
            Some(data_buffer) => {
                let block_buffer = unsafe {
                    let mut block_buffer: *mut objc2_core_media::CMBlockBuffer =
                        std::ptr::null_mut();

                    CMBlockBuffer::create_with_memory_block(
                        kCFAllocatorDefault,
                        std::ptr::null_mut(),
                        data_buffer.len(),
                        kCFAllocatorDefault,
                        std::ptr::null(),
                        0,
                        data_buffer.len(),
                        kCMBlockBufferAssureMemoryNowFlag,
                        NonNull::new(&mut block_buffer).unwrap(),
                    )
                    .to_result()
                    .map_err(|err| {
                        bincode::error::DecodeError::OtherString(format!(
                            "Failed to create CMBlockBuffer: {:?}",
                            err
                        ))
                    })?;
                    block_buffer
                };

                let mut length_at_offset_out = 0_usize;
                let mut total_length_out = 0_usize;
                let mut data_pointer_out: *mut c_char = std::ptr::null_mut();
                let mut data_t = data_buffer.as_slice();
                while !data_t.is_empty() {
                    unsafe {
                        (*block_buffer).data_pointer(
                            0,
                            &mut length_at_offset_out,
                            &mut total_length_out,
                            &mut data_pointer_out,
                        );
                    }

                    assert!(total_length_out == data_t.len());

                    let buffer = unsafe {
                        std::slice::from_raw_parts_mut::<u8>(
                            data_pointer_out as *mut u8,
                            length_at_offset_out,
                        )
                    };

                    buffer.copy_from_slice(&data_t[..buffer.len()]);

                    data_t = &data_t[buffer.len()..];
                }

                unsafe { Some(&*block_buffer) }
            }
            None => None,
        };

        // format
        let format_description = {
            let mut parameter_set_pointers = parameters.as_mut().map_or(vec![], |parameters| {
                vec![
                    NonNull::new(&mut parameters.sps[0]).unwrap(),
                    NonNull::new(&mut parameters.pps[0]).unwrap(),
                ]
            });

            let mut parameter_set_sizes = parameters.as_mut().map_or(vec![], |parameters| {
                vec![parameters.sps.len(), parameters.pps.len()]
            });

            let nal_unit_header_length = parameters
                .as_mut()
                .map_or(0, |parameters| parameters.nal_unit_header_length);
            let mut format_description_out: *const CMFormatDescription = std::ptr::null_mut();

            unsafe {
                CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    kCFAllocatorDefault,
                    parameter_set_pointers.len(),
                    NonNull::new(&mut parameter_set_pointers.as_mut_slice()[0]).unwrap(),
                    NonNull::new(&mut parameter_set_sizes.as_mut_slice()[0]).unwrap(),
                    nal_unit_header_length,
                    NonNull::new(&mut format_description_out).unwrap(),
                )
                .to_result()
                .map_err(|err| {
                    bincode::error::DecodeError::OtherString(format!(
                        "Failed to create CMVideoFormatDescription: {:?}",
                        err
                    ))
                })?;
            };
            drop(parameters);
            unsafe {
                Retained::from_raw(format_description_out as *mut CMVideoFormatDescription).unwrap()
            }
        };
        let sample_buffer = unsafe {
            let mut sample_buffer_out = std::ptr::null_mut();

            CMSampleBuffer::create_ready(
                kCFAllocatorDefault,
                data_buffer,
                Some(&format_description),
                1,
                1,
                &timing_info.into(),
                1,
                &sample_size,
                NonNull::new(&mut sample_buffer_out).unwrap(),
            )
            .to_result()
            .map_err(|err| {
                bincode::error::DecodeError::OtherString(format!(
                    "Failed to create CMSampleBuffer: {:?}",
                    err
                ))
            })?;

            Retained::from_raw(sample_buffer_out).unwrap()
        };

        if not_sync {
            let attachments = unsafe { sample_buffer.sample_attachments_array(false) };
            if let Some(attachments) = attachments {
                let dict = unsafe {
                    Retained::<CFMutableDictionary<CFString, CFType>>::retain(
                        attachments.value_at_index(0) as *mut _,
                    )
                    .unwrap()
                };

                dict.set(unsafe { kCMSampleAttachmentKey_NotSync }, unsafe {
                    kCFBooleanTrue.unwrap()
                });
            }
        }

        Ok(VideoEncodedData {
            sample_buffer: sample_buffer.into(),
        })
    }
}
