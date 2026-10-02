//! Adds a `colr` box to the video sample entry of a finished MP4 file.
//!
//! The MPEG-4 file sink writes no `colr` box, whatever color attributes the media type it is
//! given carries: the e2e output on Windows had none although `MF_MT_VIDEO_PRIMARIES`,
//! `MF_MT_TRANSFER_FUNCTION`, `MF_MT_YUV_MATRIX` and `MF_MT_VIDEO_NOMINAL_RANGE` were all set on
//! the sink's video type, and the sink's documentation does not mention the box at all. The box is
//! therefore added after the sink has finalized the file.
//!
//! The sink writes `moov` after `mdat` as the last top-level box, so the box can be added by
//! rewriting `moov` in place, which moves no media data and invalidates no chunk offset. A file laid
//! out any other way is left untouched.

use std::fs::OpenOptions;
use std::io::{self, Read, Seek, SeekFrom, Write};
use std::path::Path;

/// A `colr` box of type `nclx` declaring BT.709 primaries, transfer and matrix in limited range
/// (ISO/IEC 14496-12, 12.1.5).
const BT709_LIMITED_COLR: [u8; 19] = [
    0, 0, 0, 19, b'c', b'o', b'l', b'r', b'n', b'c', b'l', b'x', //
    0, 1, // colour_primaries
    0, 1, // transfer_characteristics
    0, 1,    // matrix_coefficients
    0x00, // full_range_flag = 0, reserved = 0
];

/// Offset of the child boxes within a visual sample entry's body: the sample entry header (8)
/// followed by the fields of `VisualSampleEntry` (70).
const VISUAL_SAMPLE_ENTRY_FIELDS: usize = 78;

#[derive(Debug, PartialEq, Eq)]
pub(crate) enum ColrOutcome {
    Added,
    AlreadyPresent,
    /// The file was left untouched for the stated reason.
    Skipped(&'static str),
}

/// Adds a BT.709 limited range `colr` box to the first video track of the MP4 file at `path`.
pub(crate) fn add_bt709_colr(path: &Path) -> io::Result<ColrOutcome> {
    let mut file = OpenOptions::new().read(true).write(true).open(path)?;
    let file_len = file.metadata()?.len();

    let mut at = 0u64;
    let mut moov = None;
    while at < file_len {
        file.seek(SeekFrom::Start(at))?;
        let mut header = [0u8; 16];
        let available = (file_len - at).min(16) as usize;
        file.read_exact(&mut header[..available])?;
        if available < 8 {
            return Ok(ColrOutcome::Skipped("the file ends inside a box header"));
        }
        let size = match u32::from_be_bytes(header[0..4].try_into().unwrap()) {
            0 => file_len - at,
            1 if available == 16 => u64::from_be_bytes(header[8..16].try_into().unwrap()),
            1 => return Ok(ColrOutcome::Skipped("the file ends inside a box header")),
            size => size as u64,
        };
        if size < 8 || at + size > file_len {
            return Ok(ColrOutcome::Skipped("a top-level box overruns the file"));
        }
        if &header[4..8] == b"moov" {
            moov = Some((at, size));
        }
        at += size;
    }

    let Some((moov_at, moov_size)) = moov else {
        return Ok(ColrOutcome::Skipped("the file has no moov box"));
    };
    if moov_at + moov_size != file_len {
        return Ok(ColrOutcome::Skipped("moov is not the last box in the file"));
    }

    let mut moov = vec![0u8; moov_size as usize];
    file.seek(SeekFrom::Start(moov_at))?;
    file.read_exact(&mut moov)?;

    let patched = match insert_bt709_colr(&moov) {
        Ok(Some(patched)) => patched,
        Ok(None) => return Ok(ColrOutcome::AlreadyPresent),
        Err(reason) => return Ok(ColrOutcome::Skipped(reason)),
    };

    // The new moov is longer than the old one and starts at the same offset, so it overwrites the
    // old one entirely and extends the file.
    file.seek(SeekFrom::Start(moov_at))?;
    file.write_all(&patched)?;
    file.flush()?;
    Ok(ColrOutcome::Added)
}

/// One box found in a buffer: where it starts, how long its header is and its total size.
#[derive(Clone, Copy)]
struct BoxRef {
    at: usize,
    header: usize,
    size: usize,
    box_type: [u8; 4],
}

impl BoxRef {
    fn body(&self) -> (usize, usize) {
        (self.at + self.header, self.at + self.size)
    }
}

fn read_box(data: &[u8], at: usize, end: usize) -> Option<BoxRef> {
    let header = data.get(at..at + 8)?;
    let box_type: [u8; 4] = header[4..8].try_into().unwrap();
    let (header_len, size) = match u32::from_be_bytes(header[0..4].try_into().unwrap()) {
        0 => (8, end - at),
        1 => (
            16,
            u64::from_be_bytes(data.get(at + 8..at + 16)?.try_into().unwrap()) as usize,
        ),
        size => (8, size as usize),
    };
    if size < header_len || at + size > end {
        return None;
    }
    Some(BoxRef {
        at,
        header: header_len,
        size,
        box_type,
    })
}

fn find_child(data: &[u8], (start, end): (usize, usize), wanted: &[u8; 4]) -> Option<BoxRef> {
    children(data, (start, end)).find(|found| &found.box_type == wanted)
}

fn children(data: &[u8], (start, end): (usize, usize)) -> impl Iterator<Item = BoxRef> + '_ {
    let mut at = start;
    std::iter::from_fn(move || {
        let found = read_box(data, at, end)?;
        at += found.size;
        Some(found)
    })
}

/// Returns `moov` with a BT.709 limited range `colr` box appended to the first video track's
/// sample entry, or `None` when that entry already has a `colr` box.
fn insert_bt709_colr(moov: &[u8]) -> Result<Option<Vec<u8>>, &'static str> {
    let moov_box = read_box(moov, 0, moov.len()).ok_or("moov is malformed")?;
    if &moov_box.box_type != b"moov" {
        return Err("the buffer is not a moov box");
    }

    // Every box from moov down to the sample entry contains the insertion point, so each of their
    // sizes grows by the size of the new box.
    let mut ancestors = vec![moov_box];
    let trak = children(moov, moov_box.body())
        .filter(|found| &found.box_type == b"trak")
        .find(|trak| is_video_track(moov, *trak))
        .ok_or("moov has no video track")?;
    ancestors.push(trak);
    let mut parent = trak;
    for wanted in [b"mdia", b"minf", b"stbl", b"stsd"] {
        parent = find_child(moov, parent.body(), wanted).ok_or("the video track is incomplete")?;
        ancestors.push(parent);
    }

    // stsd is a full box: version and flags (4) and the entry count (4) precede the entries.
    let (stsd_start, stsd_end) = parent.body();
    let entry = read_box(moov, stsd_start + 8, stsd_end).ok_or("stsd has no sample entry")?;
    if !matches!(&entry.box_type, b"avc1" | b"avc3") {
        return Err("the video sample entry is not H.264");
    }
    ancestors.push(entry);

    let (entry_start, entry_end) = entry.body();
    let entry_children = (entry_start + VISUAL_SAMPLE_ENTRY_FIELDS, entry_end);
    if entry_children.0 > entry_end {
        return Err("the video sample entry is truncated");
    }
    if find_child(moov, entry_children, b"colr").is_some() {
        return Ok(None);
    }

    let insert_at = entry.at + entry.size;
    let mut patched = Vec::with_capacity(moov.len() + BT709_LIMITED_COLR.len());
    patched.extend_from_slice(&moov[..insert_at]);
    patched.extend_from_slice(&BT709_LIMITED_COLR);
    patched.extend_from_slice(&moov[insert_at..]);

    // Every ancestor starts before the insertion point, so its offset is unchanged.
    let grow = BT709_LIMITED_COLR.len();
    for ancestor in ancestors {
        let field = &mut patched[ancestor.at..];
        match ancestor.header {
            16 => {
                let size = u64::from_be_bytes(field[8..16].try_into().unwrap()) + grow as u64;
                field[8..16].copy_from_slice(&size.to_be_bytes());
            }
            _ => {
                let size = u32::from_be_bytes(field[0..4].try_into().unwrap());
                // A size of 0 runs to the end of the enclosing space and stays valid as it is.
                if size != 0 {
                    let size = size
                        .checked_add(grow as u32)
                        .ok_or("a box would exceed the 32-bit size limit")?;
                    field[0..4].copy_from_slice(&size.to_be_bytes());
                }
            }
        }
    }

    Ok(Some(patched))
}

fn is_video_track(moov: &[u8], trak: BoxRef) -> bool {
    find_child(moov, trak.body(), b"mdia")
        .and_then(|mdia| find_child(moov, mdia.body(), b"hdlr"))
        // hdlr: version and flags (4), pre_defined (4), handler_type (4)
        .and_then(|hdlr| moov.get(hdlr.at + hdlr.header + 8..hdlr.at + hdlr.header + 12))
        .is_some_and(|handler| handler == b"vide")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn boxed(box_type: &[u8; 4], body: &[u8]) -> Vec<u8> {
        let mut out = ((body.len() + 8) as u32).to_be_bytes().to_vec();
        out.extend_from_slice(box_type);
        out.extend_from_slice(body);
        out
    }

    fn full_box(box_type: &[u8; 4], body: &[u8]) -> Vec<u8> {
        boxed(box_type, &[&[0u8; 4][..], body].concat())
    }

    fn hdlr(handler: &[u8; 4]) -> Vec<u8> {
        full_box(b"hdlr", &[&[0u8; 4][..], handler, &[0u8; 13]].concat())
    }

    fn track(handler: &[u8; 4], entry: Vec<u8>) -> Vec<u8> {
        let stsd = full_box(b"stsd", &[&1u32.to_be_bytes()[..], &entry].concat());
        let stbl = boxed(b"stbl", &[stsd, full_box(b"stts", &[0; 4])].concat());
        let minf = boxed(b"minf", &stbl);
        let mdia = boxed(b"mdia", &[hdlr(handler), minf].concat());
        boxed(b"trak", &mdia)
    }

    fn avc1(extra_children: &[u8]) -> Vec<u8> {
        let avcc = boxed(b"avcC", &[1, 0x42, 0xc0, 0x1f, 0xff, 0xe0, 0x00]);
        boxed(
            b"avc1",
            &[
                &[0u8; VISUAL_SAMPLE_ENTRY_FIELDS][..],
                &avcc,
                extra_children,
            ]
            .concat(),
        )
    }

    fn moov(tracks: &[Vec<u8>]) -> Vec<u8> {
        boxed(
            b"moov",
            &[full_box(b"mvhd", &[0; 96]), tracks.concat()].concat(),
        )
    }

    /// Checks that every box in `data[start..end]` exactly fills its parent, recursing into the
    /// containers this module edits.
    fn assert_well_formed(data: &[u8], (start, end): (usize, usize)) {
        let mut at = start;
        while at < end {
            let found = read_box(data, at, end).expect("box overruns its parent");
            match &found.box_type {
                b"moov" | b"trak" | b"mdia" | b"minf" | b"stbl" => {
                    assert_well_formed(data, found.body())
                }
                b"stsd" => assert_well_formed(data, (found.body().0 + 8, found.body().1)),
                b"avc1" => assert_well_formed(
                    data,
                    (found.body().0 + VISUAL_SAMPLE_ENTRY_FIELDS, found.body().1),
                ),
                _ => {}
            }
            at += found.size;
        }
        assert_eq!(at, end, "children do not fill their parent");
    }

    #[test]
    fn appends_colr_to_the_video_sample_entry_and_grows_every_ancestor() {
        let mp4a = boxed(b"mp4a", &[0; 28]);
        let original = moov(&[track(b"soun", mp4a), track(b"vide", avc1(&[]))]);

        let patched = insert_bt709_colr(&original).unwrap().unwrap();

        assert_eq!(patched.len(), original.len() + BT709_LIMITED_COLR.len());
        assert_well_formed(&patched, (0, patched.len()));
        // The box lands at the end of the avc1 entry, which is the last thing before the video
        // track's stts.
        let stts_at = patched
            .windows(4)
            .rposition(|window| window == b"stts")
            .unwrap()
            - 4;
        assert_eq!(
            &patched[stts_at - BT709_LIMITED_COLR.len()..stts_at],
            &BT709_LIMITED_COLR
        );
        // Past moov's own size field, everything before the video track (mvhd and the audio
        // track) is byte-for-byte unchanged.
        let video_trak_at = patched
            .windows(4)
            .rposition(|window| window == b"trak")
            .unwrap()
            - 4;
        assert_eq!(&patched[4..video_trak_at], &original[4..video_trak_at]);
    }

    #[test]
    fn leaves_an_existing_colr_box_alone() {
        let original = moov(&[track(b"vide", avc1(&BT709_LIMITED_COLR))]);
        assert_eq!(insert_bt709_colr(&original), Ok(None));
    }

    #[test]
    fn refuses_a_file_without_a_video_track() {
        let original = moov(&[track(b"soun", boxed(b"mp4a", &[0; 28]))]);
        assert_eq!(insert_bt709_colr(&original), Err("moov has no video track"));
    }

    #[test]
    fn rewrites_a_trailing_moov_in_place() {
        let path = std::env::temp_dir().join(format!("unienc-colr-{}.mp4", std::process::id()));
        let mdat = boxed(b"mdat", &[0xaa; 64]);
        let original_moov = moov(&[track(b"vide", avc1(&[]))]);
        let file = [
            boxed(b"ftyp", b"isom\0\0\0\0"),
            mdat.clone(),
            original_moov.clone(),
        ]
        .concat();
        std::fs::write(&path, &file).unwrap();

        assert_eq!(add_bt709_colr(&path).unwrap(), ColrOutcome::Added);
        let written = std::fs::read(&path).unwrap();
        // ftyp and mdat are unchanged, so no chunk offset moved.
        assert_eq!(&written[..16 + mdat.len()], &file[..16 + mdat.len()]);
        assert_eq!(written.len(), file.len() + BT709_LIMITED_COLR.len());
        assert_well_formed(&written, (0, written.len()));

        assert_eq!(add_bt709_colr(&path).unwrap(), ColrOutcome::AlreadyPresent);
        std::fs::remove_file(&path).unwrap();
    }

    #[test]
    fn leaves_a_file_whose_moov_precedes_mdat_untouched() {
        let path =
            std::env::temp_dir().join(format!("unienc-colr-front-{}.mp4", std::process::id()));
        let file = [
            boxed(b"ftyp", b"isom\0\0\0\0"),
            moov(&[track(b"vide", avc1(&[]))]),
            boxed(b"mdat", &[0xaa; 64]),
        ]
        .concat();
        std::fs::write(&path, &file).unwrap();

        assert_eq!(
            add_bt709_colr(&path).unwrap(),
            ColrOutcome::Skipped("moov is not the last box in the file")
        );
        assert_eq!(std::fs::read(&path).unwrap(), file);
        std::fs::remove_file(&path).unwrap();
    }
}
