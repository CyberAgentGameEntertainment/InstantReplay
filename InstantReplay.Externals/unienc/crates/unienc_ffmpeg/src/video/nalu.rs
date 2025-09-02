use std::io::Cursor;

use anyhow::{anyhow, Result};
use cros_codecs::codec::h264::parser::Nalu;

#[derive(Default)]
pub struct NaluReader {
    current: Vec<u8>,
}

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

impl NaluReader {
    pub fn push(&mut self, data: &[u8], emit: &mut impl FnMut(&Nalu)) -> Result<()> {
        self.current.extend_from_slice(data);

        if let Some((start_code_pos, mut nalu_pos)) = get_start_position(&self.current) {
            if start_code_pos != 0 {
                return Err(anyhow!("Invalid start code"));
            }
            // println!("{start_code_pos}, {nalu_pos}");

            while let Some((next, next_nalu_pos)) = get_start_position(&self.current[nalu_pos..]) {
                let Ok(nalu) = Nalu::next(&mut Cursor::new(&self.current)) else {
                    return Err(anyhow!("Invalid NALU"));
                };

                emit(&nalu);

                self.current.drain(0..nalu_pos+next);
                nalu_pos = next_nalu_pos - next;
            }
        }
        Ok(())
    }

    pub fn end(self, emit: &mut impl FnMut(&Nalu)) {
        let mut cursor = Cursor::new(self.current.as_slice());

        while let Ok(nalu) = Nalu::next(&mut cursor) {
            emit(&nalu);
        }
    }
}
