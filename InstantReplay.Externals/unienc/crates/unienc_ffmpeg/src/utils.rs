use anyhow::Result;

// an utility to calculate frame repeating / discarding to keep constant frame rate
pub struct Cfr<T> {
    last: Option<T>,
    last_frame_index: u64,
    cfr: u32,
}

impl<T> Cfr<T> {
    pub fn new(cfr: u32) -> Self {
        Self {
            last: None,
            last_frame_index: 0,
            cfr,
        }
    }
    pub fn push(&mut self, value: T, timestamp: f64) -> Result<Option<(T, i32)>> {
        let prev = self.last.take();
        let prev_frame_index = self.last_frame_index;

        self.last = Some(value);
        self.last_frame_index = f64::round(timestamp * self.cfr as f64) as u64;

        let Some(prev) = prev else {
            return Ok(None);
        };

        Ok(Some((prev, (self.last_frame_index - prev_frame_index) as i32)))
    }
}
