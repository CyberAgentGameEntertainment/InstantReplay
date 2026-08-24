use futures::channel::oneshot::Canceled;
use std::pin::Pin;
use unienc_common::{Spawn, SpawnBlocking};

/// The runtime the test harness drives the encoders with.
///
/// It deliberately mirrors the runtime `unienc_c` builds in production, because a
/// harness that gives the encoders a more capable runtime than they will really
/// have proves very little. That means two shapes, matching `unienc_c`'s
/// `multi-thread` feature:
///
/// - Everywhere with threads: a `futures::executor::ThreadPool`, and blocking
///   work on the `blocking` crate's pool. No Tokio runtime exists here, so a
///   backend reaching for an ambient Tokio reactor fails exactly as it would in a
///   player.
/// - On Emscripten, which is built without pthreads: a single-threaded
///   `LocalPool`, whose owner has to keep driving it. See
///   [`crate::web::drive_to_completion`].
#[derive(Clone)]
pub struct TestRuntime {
    #[cfg(not(target_os = "emscripten"))]
    pool: futures::executor::ThreadPool,
    #[cfg(target_os = "emscripten")]
    spawner: SingleThreaded<futures::executor::LocalSpawner>,
}

impl TestRuntime {
    /// Builds a runtime backed by a thread pool.
    #[cfg(not(target_os = "emscripten"))]
    pub fn new() -> Self {
        Self {
            pool: futures::executor::ThreadPool::new().expect("Failed to build thread pool"),
        }
    }

    /// Builds a runtime that spawns onto a caller-owned `LocalPool`.
    ///
    /// The pool has to be driven by whoever owns it; nothing here runs on its
    /// own.
    #[cfg(target_os = "emscripten")]
    pub fn from_spawner(spawner: futures::executor::LocalSpawner) -> Self {
        Self {
            spawner: SingleThreaded(spawner),
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

#[cfg(not(target_os = "emscripten"))]
impl Default for TestRuntime {
    fn default() -> Self {
        Self::new()
    }
}

impl Spawn for TestRuntime {
    fn spawn(&self, future: impl Future<Output = ()> + Send + 'static) {
        #[cfg(not(target_os = "emscripten"))]
        let result = {
            use futures::task::SpawnExt;
            self.pool.spawn(future)
        };

        // The backends spawn from `Drop`, which runs inside a task the pool is
        // already running, so this must not need the pool itself. A cloned
        // spawner queues the task without touching it.
        #[cfg(target_os = "emscripten")]
        let result = {
            use futures::task::LocalSpawnExt;
            self.spawner.0.spawn_local(future)
        };

        result.expect("Failed to spawn task");
    }
}

impl SpawnBlocking for TestRuntime {
    fn spawn_blocking<Result: Send + 'static>(
        &self,
        f: impl FnOnce() -> Result + Send + 'static,
    ) -> Pin<Box<dyn Future<Output = Result> + Send + 'static>> {
        #[cfg(not(target_os = "emscripten"))]
        return Box::pin(blocking::unblock(f));

        // Nowhere to offload to on a target without threads, so the work happens
        // here and the future is already resolved.
        #[cfg(target_os = "emscripten")]
        return Box::pin(std::future::ready(f()));
    }
}

impl unienc_common::Runtime for TestRuntime {}

/// Asserts `Send` for a value only ever used on one thread.
///
/// `Runtime` requires `Send`, but this target is built without pthreads, so the
/// process has a single thread and there is nowhere a value could be sent to.
#[cfg(target_os = "emscripten")]
#[derive(Clone)]
struct SingleThreaded<T>(T);

// SAFETY: see the type's documentation; the target has one thread.
#[cfg(target_os = "emscripten")]
unsafe impl<T> Send for SingleThreaded<T> {}
