//! Deterministic input material for the end-to-end test.
//!
//! Random noise, which this harness used to feed in, is a poor test signal: it
//! is incompressible, so it defeats the encoders' rate control and inflates the
//! output by an order of magnitude, and every frame looks alike, so a dropped
//! or duplicated frame leaves no trace. These patterns are cheap to encode and
//! carry their own identity instead.

/// Colour bars, in the conventional order, as opaque BGRA.
const BARS: [[u8; 3]; 8] = [
    [255, 255, 255], // white
    [0, 255, 255],   // yellow
    [255, 255, 0],   // cyan
    [0, 255, 0],     // green
    [255, 0, 255],   // magenta
    [0, 0, 255],     // red
    [255, 0, 0],     // blue
    [0, 0, 0],       // black
];

/// Number of marker blocks, i.e. the number of frame index bits encoded.
const MARKER_BITS: u32 = 16;

/// Builds one BGRA32 frame of the video test pattern.
///
/// The frame is colour bars overlaid with a row of blocks along the top that
/// spell out `frame_index` in binary, most significant bit first. The blocks are
/// a sixteenth of the frame wide and an eighth of it high so that they survive
/// chroma subsampling and quantisation, which makes every frame identifiable in
/// the decoded output and in the artifact a human looks at.
pub fn video_frame_bgra32(width: u32, height: u32, frame_index: u32) -> Vec<u8> {
    let mut data = vec![0u8; (width as usize) * (height as usize) * 4];

    let marker_height = (height / 8).max(1);
    let marker_width = (width / MARKER_BITS).max(1);

    for y in 0..height {
        for x in 0..width {
            let colour = if y < marker_height && x < marker_width * MARKER_BITS {
                let bit = MARKER_BITS - 1 - (x / marker_width);
                if frame_index & (1 << bit) != 0 {
                    [255, 255, 255]
                } else {
                    [0, 0, 0]
                }
            } else {
                BARS[(x as usize * BARS.len() / width as usize).min(BARS.len() - 1)]
            };

            let offset = ((y as usize) * (width as usize) + (x as usize)) * 4;
            data[offset] = colour[0];
            data[offset + 1] = colour[1];
            data[offset + 2] = colour[2];
            data[offset + 3] = 255;
        }
    }

    data
}

/// Builds one second of interleaved s16 PCM: a 442 Hz tone with its octave.
///
/// The phase is derived from the absolute sample position rather than from a
/// per-chunk counter, so consecutive chunks join without a discontinuity and an
/// encoder that reorders or drops a chunk produces an audible click.
pub fn audio_samples_s16(sample_rate: u32, channels: u32, second_index: u64) -> Vec<i16> {
    let channels = channels.max(1) as usize;
    let mut data = vec![0i16; sample_rate as usize * channels];
    let start = second_index * sample_rate as u64;

    for (i, sample) in data.iter_mut().enumerate() {
        let position = (start + (i / channels) as u64) as f64 / sample_rate as f64;
        let amplitude = (i16::MAX / 2) as f64;
        let fundamental = (position * 442.0 * 2.0 * std::f64::consts::PI).sin();
        let octave = (position * 884.0 * 2.0 * std::f64::consts::PI).sin();
        *sample = ((fundamental + octave) * amplitude / 2.0) as i16;
    }

    data
}

#[cfg(test)]
mod tests {
    use super::*;

    fn marker_bits(frame: &[u8], width: u32, height: u32) -> u32 {
        let marker_width = (width / MARKER_BITS).max(1);
        let y = (height / 16).max(1);
        let mut bits = 0;
        for bit_index in 0..MARKER_BITS {
            let x = bit_index * marker_width + marker_width / 2;
            let offset = ((y as usize) * (width as usize) + (x as usize)) * 4;
            if frame[offset] > 127 {
                bits |= 1 << (MARKER_BITS - 1 - bit_index);
            }
        }
        bits
    }

    #[test]
    fn frame_marker_encodes_the_frame_index() {
        for index in [0u32, 1, 2, 9, 255, 4096] {
            let frame = video_frame_bgra32(640, 480, index);
            assert_eq!(marker_bits(&frame, 640, 480), index, "index {index}");
        }
    }

    #[test]
    fn frame_has_the_expected_size_and_is_opaque() {
        let frame = video_frame_bgra32(64, 32, 0);
        assert_eq!(frame.len(), 64 * 32 * 4);
        assert!(frame.chunks_exact(4).all(|pixel| pixel[3] == 255));
    }

    #[test]
    fn audio_chunks_are_continuous_across_seconds() {
        let rate = 48000;
        let first = audio_samples_s16(rate, 2, 0);
        let second = audio_samples_s16(rate, 2, 1);
        assert_eq!(first.len(), rate as usize * 2);
        // One sample period at 442 Hz spans far more than one sample, so the
        // seam may not jump by anywhere near full scale.
        let seam = (second[0] as i32 - first[first.len() - 2] as i32).abs();
        assert!(
            seam < i16::MAX as i32 / 8,
            "discontinuity of {seam} at seam"
        );
    }

    #[test]
    fn audio_is_not_silent() {
        let data = audio_samples_s16(48000, 2, 0);
        assert!(data.iter().any(|&sample| sample.abs() > i16::MAX / 8));
    }
}
