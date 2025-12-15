use crate::error::{CommonError, Result};
use std::{sync::Arc, sync::Weak};

pub struct SharedBufferPool {
    buffers: Vec<Weak<usize>>,
    limit: usize,
}

pub struct SharedBuffer {
    len: Arc<usize>,
    data: Vec<u8>,
}

impl SharedBufferPool {
    pub fn new(limit: usize) -> Self {
        Self {
            buffers: Vec::new(),
            limit,
        }
    }

    pub fn alloc(&mut self, size: usize) -> Result<SharedBuffer> {
        // release old buffers
        self.buffers.retain(|b| b.strong_count() > 0);

        let mut current = 0;
        for len in self.buffers.iter() {
            if let Some(len) = len.upgrade() {
                current += *len;
            }
        }

        // limit=0 means unlimited
        if self.limit > 0 && current + size > self.limit {
            return Err(CommonError::BufferPoolExceeded);
        }

        let len = Arc::new(size);
        self.buffers.push(Arc::downgrade(&len));

        let buffer = SharedBuffer {
            data: vec![0u8; size],
            len,
        };
        Ok(buffer)
    }
}

impl SharedBuffer {
    pub fn new_unmanaged(vec: Vec<u8>) -> Self {
        let len = Arc::new(vec.len());
        SharedBuffer {
            data: vec,
            len,
        }
    }

    pub fn len(&self) -> usize {
        *self.len
    }

    pub fn data(&self) -> &[u8] {
        &self.data
    }

    pub fn data_mut(&mut self) -> &mut [u8] {
        &mut self.data
    }
}
