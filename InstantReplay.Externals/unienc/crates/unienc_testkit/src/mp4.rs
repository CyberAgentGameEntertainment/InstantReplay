//! Just enough MP4 introspection to assert what the encoders produced.
//!
//! This reads the sample tables rather than decoding, which is what makes it
//! usable everywhere the encoders run: it needs no decoder, no external tool and
//! no dependency, so the same assertions can later run on a device or in a
//! browser instead of only on a build host.
//!
//! Only the boxes the assertions need are understood; anything else is skipped.

use std::fmt;

#[derive(Debug)]
pub enum Mp4Error {
    Truncated { at: usize, need: usize },
    MissingBox(&'static str),
    UnsupportedVersion { box_type: &'static str, version: u8 },
}

impl fmt::Display for Mp4Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Mp4Error::Truncated { at, need } => {
                write!(f, "truncated MP4: need {need} bytes at offset {at}")
            }
            Mp4Error::MissingBox(name) => write!(f, "MP4 is missing the '{name}' box"),
            Mp4Error::UnsupportedVersion { box_type, version } => {
                write!(f, "unsupported '{box_type}' version {version}")
            }
        }
    }
}

impl std::error::Error for Mp4Error {}

type Result<T> = std::result::Result<T, Mp4Error>;

/// A whole file, summarised.
#[derive(Debug, Clone)]
pub struct Mp4Summary {
    pub timescale: u32,
    pub duration: f64,
    pub tracks: Vec<Track>,
}

impl Mp4Summary {
    pub fn track(&self, kind: TrackKind) -> Option<&Track> {
        self.tracks.iter().find(|track| track.kind == kind)
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrackKind {
    Video,
    Audio,
    Other,
}

/// One track, summarised.
#[derive(Debug, Clone)]
pub struct Track {
    pub kind: TrackKind,
    /// The `stsd` sample format, e.g. `avc1` or `mp4a`.
    pub format: String,
    /// True when the sample entry carries a decoder configuration, i.e. an
    /// `avcC` or `esds` box. A track without one does not play back.
    pub has_decoder_config: bool,
    pub timescale: u32,
    /// Track duration in seconds, from `mdhd`.
    ///
    /// This is the media duration, so it counts an audio encoder's priming and
    /// padding samples. A player reports the presentation duration instead,
    /// which an edit list may have trimmed, and the two differ by a fraction of
    /// a second on the platforms that write one.
    pub duration: f64,
    pub width: u32,
    pub height: u32,
    pub sample_rate: u32,
    pub channels: u32,
    /// Number of samples, from `stts`. For video this is the frame count.
    pub sample_count: u32,
    /// `stts` run-length entries as `(sample_count, delta)` in timescale units.
    pub sample_deltas: Vec<(u32, u32)>,
    /// Seconds of silence or blackness before the media starts, taken from the
    /// leading empty edits of `elst`.
    ///
    /// This is how a container expresses a stream that begins late, so two
    /// tracks with different values are out of sync by their difference.
    pub start_time: f64,
    /// 1-based sample numbers listed in `stss`. `None` means the box is absent,
    /// which by definition makes every sample a sync sample.
    pub sync_samples: Option<Vec<u32>>,
}

impl Track {
    /// True when sample `number` (1-based) is a sync sample, i.e. a keyframe.
    pub fn is_sync_sample(&self, number: u32) -> bool {
        match &self.sync_samples {
            None => true,
            Some(samples) => samples.contains(&number),
        }
    }

    /// The presentation time of each sample in seconds, accumulated from the
    /// `stts` deltas. Composition offsets are not applied; the encoders here
    /// produce no reordered frames, and `verify` checks that separately.
    pub fn sample_times(&self) -> Vec<f64> {
        let mut times = Vec::with_capacity(self.sample_count as usize);
        let mut ticks: u64 = 0;
        for &(count, delta) in &self.sample_deltas {
            for _ in 0..count {
                times.push(ticks as f64 / self.timescale.max(1) as f64);
                ticks += delta as u64;
            }
        }
        times
    }
}

/// Parses the summary out of a complete MP4 file.
pub fn summarize(data: &[u8]) -> Result<Mp4Summary> {
    let moov = find_box(data, b"moov").ok_or(Mp4Error::MissingBox("moov"))?;

    let mvhd = find_box(moov, b"mvhd").ok_or(Mp4Error::MissingBox("mvhd"))?;
    let (timescale, duration) = parse_header_durations(mvhd, "mvhd")?;

    let mut tracks = Vec::new();
    for trak in find_boxes(moov, b"trak") {
        tracks.push(parse_track(trak, timescale)?);
    }

    Ok(Mp4Summary {
        timescale,
        duration: duration as f64 / timescale.max(1) as f64,
        tracks,
    })
}

fn parse_track(trak: &[u8], movie_timescale: u32) -> Result<Track> {
    let mdia = find_box(trak, b"mdia").ok_or(Mp4Error::MissingBox("mdia"))?;
    let mdhd = find_box(mdia, b"mdhd").ok_or(Mp4Error::MissingBox("mdhd"))?;
    let (timescale, duration) = parse_header_durations(mdhd, "mdhd")?;

    let hdlr = find_box(mdia, b"hdlr").ok_or(Mp4Error::MissingBox("hdlr"))?;
    let kind = match &read_array::<4>(hdlr, 8)? {
        b"vide" => TrackKind::Video,
        b"soun" => TrackKind::Audio,
        _ => TrackKind::Other,
    };

    let minf = find_box(mdia, b"minf").ok_or(Mp4Error::MissingBox("minf"))?;
    let stbl = find_box(minf, b"stbl").ok_or(Mp4Error::MissingBox("stbl"))?;

    let stsd = find_box(stbl, b"stsd").ok_or(Mp4Error::MissingBox("stsd"))?;
    let entry = SampleEntry::parse(stsd, kind)?;

    let stts = find_box(stbl, b"stts").ok_or(Mp4Error::MissingBox("stts"))?;
    let sample_deltas = parse_stts(stts)?;
    let sample_count = sample_deltas.iter().map(|&(count, _)| count).sum();

    let sync_samples = match find_box(stbl, b"stss") {
        Some(stss) => Some(parse_stss(stss)?),
        None => None,
    };

    let start_time = match find_box(trak, b"edts").and_then(|edts| find_box(edts, b"elst")) {
        Some(elst) => parse_leading_empty_edits(elst)? as f64 / movie_timescale.max(1) as f64,
        None => 0.0,
    };

    Ok(Track {
        kind,
        format: entry.format,
        has_decoder_config: entry.has_decoder_config,
        timescale,
        duration: duration as f64 / timescale.max(1) as f64,
        width: entry.width,
        height: entry.height,
        sample_rate: entry.sample_rate,
        channels: entry.channels,
        sample_count,
        sample_deltas,
        start_time,
        sync_samples,
    })
}

/// Sums the durations of the empty edits at the front of an `elst` box.
///
/// An entry whose media time is -1 plays nothing, so a leading run of them is
/// exactly the delay before the track's media begins. The durations are in the
/// movie timescale.
fn parse_leading_empty_edits(elst: &[u8]) -> Result<u64> {
    let version = *elst.first().ok_or(Mp4Error::Truncated { at: 0, need: 1 })?;
    let count = read_u32(elst, 4)? as usize;

    let (entry_size, wide) = match version {
        0 => (12, false),
        1 => (20, true),
        version => {
            return Err(Mp4Error::UnsupportedVersion {
                box_type: "elst",
                version,
            });
        }
    };

    let mut delay = 0;
    for i in 0..count {
        let at = 8 + i * entry_size;
        let (duration, media_time) = if wide {
            (read_u64(elst, at)?, read_u64(elst, at + 8)? as i64)
        } else {
            (
                read_u32(elst, at)? as u64,
                read_u32(elst, at + 4)? as i32 as i64,
            )
        };

        if media_time >= 0 {
            break;
        }
        delay += duration;
    }

    Ok(delay)
}

#[derive(Default)]
struct SampleEntry {
    format: String,
    has_decoder_config: bool,
    width: u32,
    height: u32,
    sample_rate: u32,
    channels: u32,
}

impl SampleEntry {
    /// Reads the first entry of a `stsd` box. The encoders here describe a
    /// single format per track, so later entries carry nothing to assert.
    fn parse(stsd: &[u8], kind: TrackKind) -> Result<Self> {
        // full box header (4) + entry count (4)
        let body = stsd
            .get(8..)
            .ok_or(Mp4Error::Truncated { at: 0, need: 8 })?;
        let found = read_box(body, 0)?.ok_or(Mp4Error::MissingBox("stsd entry"))?;
        let entry = found.body;

        let mut parsed = Self {
            format: String::from_utf8_lossy(&found.box_type).into_owned(),
            ..Default::default()
        };

        // Offsets are counted from the start of the entry's body, i.e. after
        // its own size and type fields.
        let children_at = match kind {
            TrackKind::Video => {
                parsed.width = read_u16(entry, 24)? as u32;
                parsed.height = read_u16(entry, 26)? as u32;
                78
            }
            TrackKind::Audio => {
                parsed.channels = read_u16(entry, 16)? as u32;
                // 16.16 fixed point; the integer half is the rate.
                parsed.sample_rate = read_u32(entry, 24)? >> 16;
                // A version 1 entry carries four extra 32-bit fields.
                match read_u16(entry, 8)? {
                    0 => 28,
                    1 => 44,
                    version => {
                        return Err(Mp4Error::UnsupportedVersion {
                            box_type: "audio sample entry",
                            version: version as u8,
                        });
                    }
                }
            }
            TrackKind::Other => entry.len(),
        };

        if let Some(children) = entry.get(children_at..) {
            parsed.has_decoder_config = iter_boxes(children)
                .any(|(box_type, _)| &box_type == b"avcC" || &box_type == b"esds");
        }

        Ok(parsed)
    }
}

/// Reads the timescale and duration out of an `mvhd` or `mdhd` box.
fn parse_header_durations(header: &[u8], box_type: &'static str) -> Result<(u32, u64)> {
    match header.first() {
        // version, then creation and modification times
        Some(0) => Ok((read_u32(header, 12)?, read_u32(header, 16)? as u64)),
        Some(1) => Ok((read_u32(header, 20)?, read_u64(header, 24)?)),
        Some(&version) => Err(Mp4Error::UnsupportedVersion { box_type, version }),
        None => Err(Mp4Error::Truncated { at: 0, need: 1 }),
    }
}

fn parse_stts(stts: &[u8]) -> Result<Vec<(u32, u32)>> {
    let count = read_u32(stts, 4)? as usize;
    (0..count)
        .map(|i| {
            let at = 8 + i * 8;
            Ok((read_u32(stts, at)?, read_u32(stts, at + 4)?))
        })
        .collect()
}

fn parse_stss(stss: &[u8]) -> Result<Vec<u32>> {
    let count = read_u32(stss, 4)? as usize;
    (0..count).map(|i| read_u32(stss, 8 + i * 4)).collect()
}

struct BoxRef<'a> {
    box_type: [u8; 4],
    body: &'a [u8],
    /// Size of the whole box including its header, i.e. how far to advance.
    total: usize,
}

/// Reads the box starting at `at`.
///
/// `Ok(None)` means `at` is at or past the end of `data`.
fn read_box(data: &[u8], at: usize) -> Result<Option<BoxRef<'_>>> {
    if at >= data.len() {
        return Ok(None);
    }

    let declared = read_u32(data, at)? as usize;
    let box_type = read_array::<4>(data, at + 4)?;

    let (header, total) = match declared {
        // A size of 0 means the box runs to the end of the file.
        0 => (8, data.len() - at),
        // A size of 1 means the real size follows the type as 64 bits.
        1 => (16, read_u64(data, at + 8)? as usize),
        declared => (8, declared),
    };

    if total < header || at + total > data.len() {
        return Err(Mp4Error::Truncated { at, need: total });
    }

    Ok(Some(BoxRef {
        box_type,
        body: &data[at + header..at + total],
        total,
    }))
}

/// Iterates the boxes laid out consecutively in `data`, stopping at the first
/// malformed one so that a truncated tail cannot loop forever.
fn iter_boxes(data: &[u8]) -> impl Iterator<Item = ([u8; 4], &[u8])> {
    let mut at = 0;
    std::iter::from_fn(move || {
        let found = read_box(data, at).ok()??;
        // read_box rejects a box smaller than its own header, so `at` always
        // advances and the iteration terminates.
        at += found.total;
        Some((found.box_type, found.body))
    })
}

fn find_boxes<'a>(data: &'a [u8], wanted: &'a [u8; 4]) -> impl Iterator<Item = &'a [u8]> {
    iter_boxes(data).filter_map(move |(box_type, body)| (&box_type == wanted).then_some(body))
}

fn find_box<'a>(data: &'a [u8], wanted: &[u8; 4]) -> Option<&'a [u8]> {
    iter_boxes(data).find_map(|(box_type, body)| (&box_type == wanted).then_some(body))
}

fn read_array<const N: usize>(data: &[u8], at: usize) -> Result<[u8; N]> {
    data.get(at..at + N)
        .and_then(|slice| slice.try_into().ok())
        .ok_or(Mp4Error::Truncated { at, need: N })
}

fn read_u16(data: &[u8], at: usize) -> Result<u16> {
    Ok(u16::from_be_bytes(read_array(data, at)?))
}

fn read_u32(data: &[u8], at: usize) -> Result<u32> {
    Ok(u32::from_be_bytes(read_array(data, at)?))
}

fn read_u64(data: &[u8], at: usize) -> Result<u64> {
    Ok(u64::from_be_bytes(read_array(data, at)?))
}
