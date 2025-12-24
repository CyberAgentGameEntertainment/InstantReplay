use std::future::Future;
use std::task::{Context, Poll};
use futures::task::noop_waker_ref;

// poll once on the current thread, and if not ready, spawn onto Tokio runtime
pub fn spawn_optimistically<F>(future: F)
where
    F: Future<Output = ()> + Send + 'static,
{
    let mut pinned = Box::pin(future);

    let waker = noop_waker_ref();
    let mut cx = Context::from_waker(waker);

    match pinned.as_mut().poll(&mut cx) {
        Poll::Ready(_) => {
        }
        Poll::Pending => {
            tokio::spawn(pinned);
        }
    }
}