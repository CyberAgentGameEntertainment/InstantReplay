use std::{fmt::Debug, ops::Deref};

use objc2::rc::Retained;

pub struct UnsafeSendRetained<T> {
    pub inner: Retained<T>,
}

unsafe impl<T> Send for UnsafeSendRetained<T> {}
// unsafe impl<T> Sync for UnsafeSendRetained<T> {}

impl<T> Debug for UnsafeSendRetained<T> where T: Debug {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UnsafeSendRetained")
            .field("inner", &self.inner)
            .finish()
    }
}

impl<T> Deref for UnsafeSendRetained<T> {
    type Target = Retained<T>;

    fn deref(&self) -> &Self::Target {
        &self.inner
    }
}

impl<T> From<Retained<T>> for UnsafeSendRetained<T> {
    fn from(inner: Retained<T>) -> Self {
        Self { inner }
    }
}
