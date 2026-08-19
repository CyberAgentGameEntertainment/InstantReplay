use blocking::unblock;
use futures::executor::LocalPool;
use futures::task::{SpawnExt, noop_waker_ref};
use std::cell::RefCell;
use std::io;
use std::marker::PhantomData;
use std::pin::Pin;
use std::sync::{Arc, Mutex, Weak};
use std::task::{Context, Poll};
use thiserror::Error;

thread_local! {
    // Holds a weak reference so that thread-local storage on the executor's
    // own worker threads never keeps the executor alive (which would form a
    // reference cycle and leak the thread pool).
    static CURRENT: RefCell<Option<WeakRuntime>> = None.into();
}

#[derive(Error, Debug)]
pub enum RuntimeError {
    #[error("Failed to create tokio runtime: {0}")]
    ExecutorCreation(#[from] io::Error),
}

#[derive(Clone)]
pub struct Runtime {
    executor: Arc<Executor>,
}

#[derive(Clone)]
pub struct WeakRuntime {
    executor: Weak<Executor>,
}

impl WeakRuntime {
    pub fn upgrade(&self) -> Option<Runtime> {
        self.executor.upgrade().map(|executor| Runtime { executor })
    }
}

#[cfg(feature = "multi-thread")]
type Executor = futures::executor::ThreadPool;
#[cfg(not(feature = "multi-thread"))]
type Executor = LocalExecutor;

struct LocalExecutor {
    pool: Mutex<LocalPool>,
}

impl LocalExecutor {
    fn new() -> Self {
        let pool = LocalPool::new();

        Self {
            pool: Mutex::new(pool),
        }
    }
}

impl Runtime {
    pub fn new() -> Result<Runtime, RuntimeError> {
        // The cell stores only a weak reference: the `after_start` closure is
        // owned by the thread pool itself, so a strong `Runtime` here would
        // create a cycle (`ThreadPool -> closure -> Runtime -> ThreadPool`)
        // and the pool would never be dropped.
        let lazy_runtime: Arc<Mutex<Option<WeakRuntime>>> = Arc::new(Mutex::new(None));
        let mut lock = lazy_runtime.lock().unwrap();
        let runtime = Self {
            executor: Arc::new(new_executor(lazy_runtime.clone())),
        };

        *lock = Some(runtime.weak());
        Ok(runtime)
    }

    pub fn enter(&self) -> RuntimeGuard<'_> {
        CURRENT.with_borrow_mut(|local| match local {
            Some(_) => RuntimeGuard {
                acquired: false,
                _lifetime: PhantomData,
            },
            None => {
                *local = Some(self.weak());
                RuntimeGuard {
                    acquired: true,
                    _lifetime: PhantomData,
                }
            }
        })
    }

    pub fn spawn(fut: impl Future<Output = ()> + Send + 'static) {
        CURRENT.with_borrow(|local| {
            if let Some(runtime) = local.as_ref().and_then(WeakRuntime::upgrade) {
                #[cfg(feature = "multi-thread")]
                {
                    runtime
                        .executor
                        .spawn(fut)
                        .expect("Failed to spawn task on threaded executor");
                }
                #[cfg(not(feature = "multi-thread"))]
                {
                    let pool = runtime
                        .executor
                        .pool
                        .lock()
                        .expect("Failed to lock local executor");
                    let spawner = pool.spawner();
                    spawner
                        .spawn(fut)
                        .expect("Failed to spawn task on local executor");
                }
            } else {
                panic!("No runtime available to spawn task");
            }
        });
    }

    pub fn spawn_optimistically(fut: impl Future<Output = ()> + Send + 'static) {
        let mut pinned = Box::pin(fut);

        let waker = noop_waker_ref();
        let mut cx = Context::from_waker(waker);

        match pinned.as_mut().poll(&mut cx) {
            Poll::Ready(_) => {}
            Poll::Pending => {
                Self::spawn(pinned);
            }
        }
    }

    pub fn tick(&self) {
        #[cfg(not(feature = "multi-thread"))]
        {
            let mut pool = self
                .executor
                .pool
                .lock()
                .expect("Failed to local local executor");

            let _guard = self.enter();
            loop {
                if !(pool.try_run_one()) {
                    break;
                }
            }
        }
    }

    pub fn weak(&self) -> WeakRuntime {
        WeakRuntime {
            executor: Arc::downgrade(&self.executor),
        }
    }
}

pub struct RuntimeGuard<'a> {
    acquired: bool,
    _lifetime: PhantomData<&'a ()>,
}

impl Drop for RuntimeGuard<'_> {
    fn drop(&mut self) {
        if self.acquired {
            CURRENT.with_borrow_mut(|local| {
                *local = None;
            });
        }
    }
}

fn new_executor(lazy_runtime: Arc<Mutex<Option<WeakRuntime>>>) -> Executor {
    #[cfg(not(feature = "multi-thread"))]
    {
        let _ = lazy_runtime;
        println!("Using current thread runtime");
        LocalExecutor::new()
    }

    #[cfg(feature = "multi-thread")]
    {
        futures::executor::ThreadPoolBuilder::new()
            .after_start(move |_index| {
                CURRENT.with_borrow_mut(|local| match local {
                    Some(_) => {}
                    None => {
                        // Locking here synchronizes with `Runtime::new`, which
                        // holds the lock until the weak handle is stored, so
                        // worker threads started during pool creation observe
                        // the initialized value.
                        *local = lazy_runtime.lock().unwrap().clone();
                    }
                })
            })
            .before_stop(|_index| {
                CURRENT.with_borrow_mut(|local| {
                    *local = None;
                })
            })
            .create()
            .expect("Failed to create thread pool")
    }
}

#[derive(Clone)]
pub struct RuntimeSpawner;

impl unienc::Spawn for RuntimeSpawner {
    fn spawn(&self, future: impl Future<Output = ()> + Send + 'static) {
        Runtime::spawn(future);
    }
}

impl unienc::SpawnBlocking for RuntimeSpawner {
    fn spawn_blocking<Result: Send + 'static>(
        &self,
        f: impl FnOnce() -> Result + Send + 'static,
    ) -> Pin<Box<dyn Future<Output = Result> + Send + 'static>> {
        Box::pin(unblock(f))
    }
}

impl unienc::Runtime for RuntimeSpawner {}
