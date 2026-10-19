//! Just enough H.264 bitstream parsing to read the color description out of a
//! sequence parameter set.
//!
//! An MP4 can state a track's color space twice: in the container's `colr` box
//! and in the VUI of the SPS that the decoder itself reads. Muxers that write no
//! `colr` box leave the VUI as the only statement, and a player that trusts one
//! over the other renders a mismatch wrongly, so both are worth checking.

use crate::mp4::ColorDescription;

/// The `profile_idc` values whose SPS carries the chroma format, bit depths
/// and scaling matrices before the fields every profile shares.
const PROFILES_WITH_CHROMA_INFO: [u8; 12] =
    [100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134];

/// The value H.264 assigns to primaries, transfer and matrix when the VUI
/// signals a video type but no colour description.
const UNSPECIFIED: u16 = 2;

/// Reads the color description from the VUI of the first SPS in an `avcC`
/// body.
///
/// `Ok(None)` means the SPS carries no video signal type at all, i.e. it says
/// nothing about color. An SPS that does carry one but omits the colour
/// description reports every component as unspecified.
pub(crate) fn sps_color_from_avcc(avcc: &[u8]) -> Result<Option<ColorDescription>, &'static str> {
    // configurationVersion, profile, compatibility, level, lengthSizeMinusOne,
    // then the SPS count in the low five bits.
    let sps_count = avcc.get(5).ok_or("avcC is truncated")? & 0x1f;
    if sps_count == 0 {
        return Err("avcC carries no SPS");
    }
    let length = u16::from_be_bytes(
        avcc.get(6..8)
            .ok_or("avcC is truncated")?
            .try_into()
            .unwrap(),
    ) as usize;
    let nal = avcc.get(8..8 + length).ok_or("avcC SPS is truncated")?;
    // The first byte is the NAL unit header.
    let rbsp = remove_emulation_prevention(nal.get(1..).ok_or("SPS is empty")?);
    parse_sps_color(&rbsp).ok_or("SPS ends before its VUI does")
}

/// Undoes the escaping that keeps `00 00 0x` start codes out of a NAL unit's
/// payload.
fn remove_emulation_prevention(data: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(data.len());
    let mut zeros = 0;
    for &byte in data {
        if zeros >= 2 && byte == 3 {
            zeros = 0;
            continue;
        }
        zeros = if byte == 0 { zeros + 1 } else { 0 };
        out.push(byte);
    }
    out
}

/// Walks the SPS (ITU-T H.264 7.3.2.1.1) up to the video signal type in its
/// VUI (E.1.1). `None` means the data ran out first.
fn parse_sps_color(rbsp: &[u8]) -> Option<Option<ColorDescription>> {
    let mut bits = BitReader::new(rbsp);

    let profile_idc = bits.read(8)? as u8;
    bits.read(8)?; // constraint flags and reserved bits
    bits.read(8)?; // level_idc
    bits.read_ue()?; // seq_parameter_set_id

    if PROFILES_WITH_CHROMA_INFO.contains(&profile_idc) {
        let chroma_format_idc = bits.read_ue()?;
        if chroma_format_idc == 3 {
            bits.read(1)?; // separate_colour_plane_flag
        }
        bits.read_ue()?; // bit_depth_luma_minus8
        bits.read_ue()?; // bit_depth_chroma_minus8
        bits.read(1)?; // qpprime_y_zero_transform_bypass_flag
        if bits.read(1)? == 1 {
            let lists = if chroma_format_idc == 3 { 12 } else { 8 };
            for index in 0..lists {
                if bits.read(1)? == 1 {
                    skip_scaling_list(&mut bits, if index < 6 { 16 } else { 64 })?;
                }
            }
        }
    }

    bits.read_ue()?; // log2_max_frame_num_minus4
    match bits.read_ue()? {
        0 => {
            bits.read_ue()?; // log2_max_pic_order_cnt_lsb_minus4
        }
        1 => {
            bits.read(1)?; // delta_pic_order_always_zero_flag
            bits.read_se()?; // offset_for_non_ref_pic
            bits.read_se()?; // offset_for_top_to_bottom_field
            for _ in 0..bits.read_ue()? {
                bits.read_se()?; // offset_for_ref_frame
            }
        }
        _ => {}
    }
    bits.read_ue()?; // max_num_ref_frames
    bits.read(1)?; // gaps_in_frame_num_value_allowed_flag
    bits.read_ue()?; // pic_width_in_mbs_minus1
    bits.read_ue()?; // pic_height_in_map_units_minus1
    if bits.read(1)? == 0 {
        bits.read(1)?; // mb_adaptive_frame_field_flag, present unless frame_mbs_only
    }
    bits.read(1)?; // direct_8x8_inference_flag
    if bits.read(1)? == 1 {
        for _ in 0..4 {
            bits.read_ue()?; // frame_crop_{left,right,top,bottom}_offset
        }
    }

    if bits.read(1)? == 0 {
        return Some(None); // no VUI
    }
    if bits.read(1)? == 1 {
        // aspect_ratio_idc, with an explicit ratio after Extended_SAR.
        if bits.read(8)? == 255 {
            bits.read(16)?; // sar_width
            bits.read(16)?; // sar_height
        }
    }
    if bits.read(1)? == 1 {
        bits.read(1)?; // overscan_appropriate_flag
    }
    if bits.read(1)? == 0 {
        return Some(None); // no video signal type
    }
    bits.read(3)?; // video_format
    let full_range = bits.read(1)? == 1;
    let (primaries, transfer, matrix) = if bits.read(1)? == 1 {
        (
            bits.read(8)? as u16,
            bits.read(8)? as u16,
            bits.read(8)? as u16,
        )
    } else {
        (UNSPECIFIED, UNSPECIFIED, UNSPECIFIED)
    };

    Some(Some(ColorDescription {
        primaries,
        transfer,
        matrix,
        full_range: Some(full_range),
    }))
}

/// Skips a `scaling_list()` (7.3.2.1.1.1), whose length depends on its own
/// contents.
fn skip_scaling_list(bits: &mut BitReader<'_>, size: usize) -> Option<()> {
    let mut last = 8i64;
    let mut next = 8i64;
    for _ in 0..size {
        if next != 0 {
            next = (last + bits.read_se()? + 256) % 256;
        }
        if next != 0 {
            last = next;
        }
    }
    Some(())
}

struct BitReader<'a> {
    data: &'a [u8],
    position: usize,
}

impl<'a> BitReader<'a> {
    fn new(data: &'a [u8]) -> Self {
        Self { data, position: 0 }
    }

    /// Reads `count` bits, at most 32, most significant first.
    fn read(&mut self, count: u32) -> Option<u32> {
        let mut value = 0u32;
        for _ in 0..count {
            let byte = *self.data.get(self.position / 8)?;
            let bit = (byte >> (7 - self.position % 8)) & 1;
            value = (value << 1) | bit as u32;
            self.position += 1;
        }
        Some(value)
    }

    /// Reads an unsigned Exp-Golomb code.
    fn read_ue(&mut self) -> Option<u32> {
        let mut leading_zeros = 0;
        while self.read(1)? == 0 {
            leading_zeros += 1;
            if leading_zeros > 31 {
                return None;
            }
        }
        Some((1u32 << leading_zeros) - 1 + self.read(leading_zeros)?)
    }

    /// Reads a signed Exp-Golomb code.
    fn read_se(&mut self) -> Option<i64> {
        let code = self.read_ue()? as i64;
        Some(if code % 2 == 1 {
            (code + 1) / 2
        } else {
            -(code / 2)
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Wraps an SPS NAL unit in the smallest `avcC` that carries it.
    fn avcc_with_sps(nal: &[u8]) -> Vec<u8> {
        let mut avcc = vec![1, nal[1], nal[2], nal[3], 0xff, 0xe1];
        avcc.extend_from_slice(&(nal.len() as u16).to_be_bytes());
        avcc.extend_from_slice(nal);
        avcc
    }

    // Every SPS below was written by libx264 (FFmpeg 8.0.1), with the VUI set by
    // the `h264_metadata` bitstream filter where noted.

    #[test]
    fn reads_bt709_limited_range_from_a_high_profile_sps() {
        // 1280x720 High 3.1, `colour_primaries=1:transfer_characteristics=1:
        // matrix_coefficients=1:video_full_range_flag=0`.
        let nal = [
            0x67, 0x64, 0x00, 0x1f, 0xac, 0xd9, 0x40, 0x50, 0x05, 0xbb, 0x01, 0x6a, 0x02, 0x02,
            0x02, 0x80, 0x00, 0x00, 0x03, 0x00, 0x80, 0x00, 0x00, 0x19, 0x07, 0x8c, 0x18, 0xcb,
        ];
        assert_eq!(
            sps_color_from_avcc(&avcc_with_sps(&nal)),
            Ok(Some(ColorDescription::BT709_LIMITED))
        );
    }

    #[test]
    fn reads_other_color_spaces_and_full_range() {
        // 640x480 High 3.0, `colour_primaries=6:transfer_characteristics=6:
        // matrix_coefficients=6:video_full_range_flag=1`.
        let nal = [
            0x67, 0x64, 0x00, 0x1e, 0xac, 0xd9, 0x40, 0xa0, 0x3d, 0xb0, 0x16, 0xe0, 0xc0, 0xc0,
            0xc8, 0x00, 0x00, 0x03, 0x00, 0x08, 0x00, 0x00, 0x03, 0x01, 0x90, 0x78, 0xb1, 0x6c,
            0xb0,
        ];
        assert_eq!(
            sps_color_from_avcc(&avcc_with_sps(&nal)),
            Ok(Some(ColorDescription {
                primaries: 6,
                transfer: 6,
                matrix: 6,
                full_range: Some(true),
            }))
        );
    }

    #[test]
    fn reports_unspecified_components_for_a_signal_type_without_colour_description() {
        // 320x240 Baseline 1.3, `video_format=5:video_full_range_flag=0`.
        let nal = [
            0x67, 0x42, 0xc0, 0x0d, 0xd9, 0x01, 0x41, 0xfb, 0x01, 0x68, 0x80, 0x00, 0x00, 0x03,
            0x00, 0x80, 0x00, 0x00, 0x19, 0x07, 0x8a, 0x15, 0x24,
        ];
        assert_eq!(
            sps_color_from_avcc(&avcc_with_sps(&nal)),
            Ok(Some(ColorDescription {
                primaries: UNSPECIFIED,
                transfer: UNSPECIFIED,
                matrix: UNSPECIFIED,
                full_range: Some(false),
            }))
        );
    }

    #[test]
    fn reports_no_color_for_a_vui_without_signal_type() {
        // 320x240 Baseline 1.3 as libx264 writes it by default: a VUI with
        // timing information only.
        let nal = [
            0x67, 0x42, 0xc0, 0x0d, 0xd9, 0x01, 0x41, 0xfb, 0x01, 0x10, 0x00, 0x00, 0x03, 0x00,
            0x10, 0x00, 0x00, 0x03, 0x03, 0x20, 0xf1, 0x42, 0xa4, 0x80,
        ];
        assert_eq!(sps_color_from_avcc(&avcc_with_sps(&nal)), Ok(None));
    }

    #[test]
    fn removes_emulation_prevention_bytes() {
        assert_eq!(
            remove_emulation_prevention(&[0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x03, 0x00]),
            vec![0x00, 0x00, 0x01, 0x00, 0x00, 0x00]
        );
    }
}
