// an utility to calculate frame repeating / discarding to keep constant frame rate
//
// Raw H.264 carries no timestamps, so the stream FFmpeg receives has to be at a
// constant frame rate: every slot of `1 / cfr` seconds gets exactly one frame.
// Input frames rarely line up with those slots, so a frame that spans several
// slots has to be repeated, and a frame landing in a slot that is already
// written has to be dropped.
//
// The decision is made from the slot a frame lands in and the slots written so
// far, never from the frame that follows it. That matters at the end of the
// stream: holding a frame back until its successor arrives would leave the last
// frame unwritten, and `EncoderInput` has no end-of-stream hook to flush it
// with.
pub struct Cfr {
    cfr: u32,
    /// Index of the first slot that has not been written yet, or `None` before
    /// the first frame settles the origin of the timeline.
    next_slot: Option<u64>,
}

impl Cfr {
    pub fn new(cfr: u32) -> Self {
        Self {
            cfr,
            next_slot: None,
        }
    }

    /// Accepts a frame at `timestamp` and reports how many times the previously
    /// written frame has to be repeated before it, to fill the slots that no
    /// frame landed in.
    ///
    /// Returns `None` when the frame belongs to a slot that is already written,
    /// meaning it has to be discarded to keep the frame rate constant.
    pub fn advance(&mut self, timestamp: f64) -> Option<u32> {
        // A negative timestamp saturates to slot 0, which is harmless because
        // only differences between slot indices are used.
        let index = f64::round(timestamp * self.cfr as f64) as u64;

        match self.next_slot {
            // The first frame defines the origin; nothing precedes it.
            None => {
                self.next_slot = Some(index + 1);
                Some(0)
            }
            // Its slot has already been written by an earlier frame.
            Some(next_slot) if index < next_slot => None,
            Some(next_slot) => {
                self.next_slot = Some(index + 1);
                Some((index - next_slot) as u32)
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::Cfr;

    #[test]
    fn consecutive_frames_need_no_repeats() {
        let mut cfr = Cfr::new(1);
        assert_eq!(cfr.advance(100.0), Some(0));
        assert_eq!(cfr.advance(101.0), Some(0));
        assert_eq!(cfr.advance(102.0), Some(0));
    }

    #[test]
    fn gaps_repeat_the_previous_frame() {
        let mut cfr = Cfr::new(1);
        assert_eq!(cfr.advance(0.0), Some(0));
        // Slots 1 and 2 got no frame, so the frame written for slot 0 fills
        // them before this one takes slot 3.
        assert_eq!(cfr.advance(3.0), Some(2));
        assert_eq!(cfr.advance(4.0), Some(0));
    }

    #[test]
    fn frames_sharing_a_slot_are_discarded() {
        let mut cfr = Cfr::new(1);
        assert_eq!(cfr.advance(0.0), Some(0));
        // Rounds onto slot 0, which the first frame already occupies.
        assert_eq!(cfr.advance(0.2), None);
        assert_eq!(cfr.advance(1.0), Some(0));
    }

    #[test]
    fn backward_timestamps_are_discarded_without_underflow() {
        let mut cfr = Cfr::new(30);
        assert_eq!(cfr.advance(10.0), Some(0));
        assert_eq!(cfr.advance(9.0), None);
        assert_eq!(cfr.advance(5.0), None);
        // The timeline resumes from where it left off.
        assert_eq!(cfr.advance(10.1), Some(2));
    }

    #[test]
    fn rounding_picks_the_nearest_slot() {
        let mut cfr = Cfr::new(2);
        assert_eq!(cfr.advance(0.0), Some(0));
        // 0.9 s rounds to slot 2 at 2 fps, so slot 1 is filled by a repeat.
        assert_eq!(cfr.advance(0.9), Some(1));
        // 1.0 s rounds to slot 2 as well, which is now already written.
        assert_eq!(cfr.advance(1.0), None);
    }
}
