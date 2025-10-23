use std::{
    ffi::c_void,
    sync::{Arc, Mutex},
};

use unienc_common::buffer::{SharedBuffer, SharedBufferPool};

use crate::{arc_from_raw_retained, ApplyCallback, SendPtr, UniencCallback, UniencError};

#[no_mangle]
pub extern "C" fn unienc_new_shared_buffer_pool(
    limit: usize,
    pool_out: *mut *const Mutex<SharedBufferPool>,
    _on_error: usize, /*UniencCallback*/
    _user_data: *const c_void,
) -> bool {
    let pool = Arc::new(Mutex::new(SharedBufferPool::new(limit)));
    unsafe {
        *pool_out = Arc::into_raw(pool);
    }

    true
}

#[no_mangle]
pub extern "C" fn unienc_shared_buffer_pool_alloc(
    pool: *mut Mutex<SharedBufferPool>,
    size: usize,
    buffer_out: *mut *mut SharedBuffer,
    ptr_out: *mut *mut u8,
    on_error: usize, /*UniencCallback*/
    user_data: SendPtr<c_void>,
) -> bool {
    let on_error: UniencCallback = unsafe { std::mem::transmute(on_error) };
    let pool = arc_from_raw_retained(pool);
    let mut guard = pool.lock().unwrap();
    match guard.alloc(size) {
        Ok(buffer) => {
            unsafe {
                let mut buffer = Box::new(buffer);
                *ptr_out = buffer.data_mut().as_mut_ptr();
                *buffer_out = Box::into_raw(buffer);
            }
            true
        }
        Err(err) => {
            UniencError::from_anyhow(err).apply_callback(on_error, user_data);
            false
        }
    }
}

#[no_mangle]
pub extern "C" fn unienc_free_shared_buffer_pool(pool: *const Mutex<SharedBufferPool>) {
    if !pool.is_null() {
        unsafe {
            drop(Arc::from_raw(pool));
        }
    }
}

#[no_mangle]
pub extern "C" fn unienc_free_shared_buffer(buffer: *mut SharedBuffer) {
    if !buffer.is_null() {
        unsafe {
            drop(Box::from_raw(buffer));
        }
    }
}
