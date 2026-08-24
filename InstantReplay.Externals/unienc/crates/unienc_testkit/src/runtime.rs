use futures::channel::oneshot::Canceled;
use futures::executor::ThreadPool;
use futures::task::SpawnExt;
use std::pin::Pin;
use unienc_common::{Spawn, SpawnBlocking};

/// The runtime the test harness drives the encoders with.
///
/// It deliberately mirrors `unienc_c::runtime::RuntimeSpawner`, the runtime used
/// in production: a `futures::executor::ThreadPool` for futures and the
/// `blocking` crate's pool for blocking work, with no Tokio runtime anywhere. A
/// backend that reaches for an ambient Tokio reactor therefore fails here
/// exactly as it would in a Unity player.
#[derive(Clone)]
pub struct TestRuntime {
    pool: ThreadPool,
}

impl TestRuntime {
    pub fn new() -> Self {
        Self {
            pool: ThreadPool::new().expect("Failed to build thread pool"),
        }
    }

    /// Spawns `future` and returns a handle that resolves to its output.
    pub fn spawn_with_result<Output: Send + 'static>(
        &self,
        future: impl Future<Output = Output> + Send + 'static,
    ) -> impl Future<Output = Result<Output, Canceled>> + Send + 'static {
        let (tx, rx) = futures::channel::oneshot::channel();
        self.spawn(async move {
            let _ = tx.send(future.await);
        });
        rx
    }
}

impl Default for TestRuntime {
    fn default() -> Self {
        Self::new()
    }
}

impl Spawn for TestRuntime {
    fn spawn(&self, future: impl Future<Output = ()> + Send + 'static) {
        self.pool
            .spawn(future)
            .expect("Failed to spawn task on threaded executor");
    }
}

impl SpawnBlocking for TestRuntime {
    fn spawn_blocking<Result: Send + 'static>(
        &self,
        f: impl FnOnce() -> Result + Send + 'static,
    ) -> Pin<Box<dyn Future<Output = Result> + Send + 'static>> {
        Box::pin(blocking::unblock(f))
    }
}

impl unienc_common::Runtime for TestRuntime {}
