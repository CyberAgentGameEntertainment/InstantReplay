use std::io::Cursor;

use cros_codecs::codec::h264::parser::Nalu;

use crate::error::{FFmpegError, Result};

#[derive(Default)]
pub struct NaluReader {
    current: Vec<u8>,
}


// finds start code of NAL unit 0x000001 and returns its position
// the first value is the start position of the NAL unit including preceding zero bytes and the second is the position the content starts at
fn get_start_position(data: &[u8]) -> Option<(usize, usize)> {
    data.windows(3)
        .position(|window| window == [0x00, 0x00, 0x01])
        .map(|pos| {
            let mut start = pos;
            while start > 0 && data[start - 1] == 00 {
                start -= 1;
            }
            (start, pos + 3)
        })
}

pub struct NalUnit<'a> {
    pub nalu: Nalu<'a>,
    pub data: &'a [u8],
}

impl NaluReader {
    pub fn push(&mut self, data: &[u8], emit: &mut impl FnMut(&NalUnit)) -> Result<()> {
        self.current.extend_from_slice(data);
        self.drain(emit)
    }

    fn drain(&mut self, emit: &mut impl FnMut(&NalUnit)) -> Result<()> {
        if let Some((start_pos, mut nalu_pos)) = get_start_position(&self.current) {
            if start_pos != 0 {
                return Err(FFmpegError::Other("Invalid start code".into()));
            }

            while let Some((next, next_nalu_pos)) = get_start_position(&self.current[nalu_pos..]) {
                let Ok(nalu) = Nalu::next(&mut Cursor::new(&self.current)) else {
                    return Err(FFmpegError::Other("Invalid NALU".into()));
                };

                let nal_unit = NalUnit {
                    nalu,
                    data: &self.current[..nalu_pos + next],
                };

                emit(&nal_unit);

                self.current.drain(..nalu_pos + next);
                nalu_pos = next_nalu_pos - next;
            }
        }
        Ok(())
    }

    pub fn end(mut self, emit: &mut impl FnMut(&NalUnit)) -> Result<()> {
        self.drain(emit)?;
        let mut cursor = Cursor::new(self.current.as_slice());
        match Nalu::next(&mut cursor) {
            Ok(nalu) => {
                let nal_unit = NalUnit {
                    nalu,
                    data: &self.current,
                };
                emit(&nal_unit);
                Ok(())
            }
            Err(err) => Err(FFmpegError::Other(format!("{}", err))),
        }
    }
}
