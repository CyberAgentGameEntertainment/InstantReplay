use std::io;
use std::sync::{Arc, Mutex, Weak};
use std::task::{Context, Poll};
use futures::executor::{LocalPool, LocalSpawner};
use futures::task::{noop_waker_ref, SpawnExt};
use thiserror::Error;

#[derive(Error, Debug)]
pub enum RuntimeError {
    #[error("Failed to create tokio runtime: {0}")]
    ExecutorCreation(#[from] io::Error),
}

#[derive(Clone)]
pub struct Runtime {
    executor: Arc<Executor>,
}

enum Executor {
    Local(LocalExecutor),
    #[cfg(feature = "multi-thread")]
    Threaded(futures::executor::ThreadPool),
}

struct LocalExecutor {
    pool: Mutex<LocalPool>,
    spawner: LocalSpawner,
}

impl LocalExecutor {
    fn new() -> Self {
        let pool = LocalPool::new();
        let spawner = pool.spawner();

        Self {
            pool: Mutex::new(pool),
            spawner,
        }
    }
}

impl Runtime {
    pub fn new() -> Result<Runtime, RuntimeError> {
        Ok(Self { executor: Arc::new(new_executor()) })
    }

    pub fn spawn(&self, fut: impl Future<Output = ()> + Send + 'static) {
        match &*self.executor {
            Executor::Local(executor) => {
                executor.spawner.spawn(fut).expect("Failed to spawn");
            }
            #[cfg(feature = "multi-thread")]
            Executor::Threaded(pool) => {
                pool.spawn(fut).expect("Failed to spawn task");
            }
        }
    }

    pub fn spawn_optimistically(&self, fut: impl Future<Output = ()> + Send + 'static)
    {
        let mut pinned = Box::pin(fut);

        let waker = noop_waker_ref();
        let mut cx = Context::from_waker(waker);

        match pinned.as_mut().poll(&mut cx) {
            Poll::Ready(_) => {
            }
            Poll::Pending => {
                self.spawn(pinned);
            }
        }
    }

    pub fn tick(&self) {
        let Executor::Local(executor) = &*self.executor else {
            return;
        };

        let mut pool = executor.pool.lock().expect("Failed to local local executor");
        loop {
            if !(pool.try_run_one()) {
                break;
            }
        }
    }

    pub fn weak(&self) -> WeakRuntime {
        WeakRuntime(Arc::downgrade(&self.executor))
    }
}

#[derive(Debug, Clone)]
pub struct WeakRuntime(Weak<Executor>);

impl WeakRuntime {
    pub fn upgrade(&self) -> Option<Runtime> {
        self.0
            .upgrade()
            .map(|tokio_runtime| Runtime { executor: tokio_runtime })
    }
}

fn new_executor() -> Executor {
    #[cfg(not(feature = "multi-thread"))]
    {
        println!("Using current thread runtime");
        Executor::Local(LocalExecutor::new())
    }

    #[cfg(feature = "multi-thread")]
    {
        Executor::Threaded(futures::executor::ThreadPool::new().expect("Failed to create thread pool"))
    }
}