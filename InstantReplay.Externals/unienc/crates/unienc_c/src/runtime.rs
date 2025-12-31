use futures::executor::{LocalPool, LocalSpawner};
use futures::task::{SpawnExt, noop_waker_ref};
use std::cell::RefCell;
use std::io;
use std::marker::PhantomData;
use std::sync::{Arc, Mutex, Weak};
use std::task::{Context, Poll};
use thiserror::Error;

thread_local! {
    static CURRENT: RefCell<Option<Runtime>> = None.into();
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
        let lazy_runtime: Arc<Mutex<Option<Runtime>>> = Arc::new(Mutex::new(None));
        let mut lock = lazy_runtime.lock().unwrap();
        let runtime = Self {
            executor: Arc::new(new_executor(lazy_runtime.clone())),
        };

        *lock = Some(runtime.clone());
        Ok(runtime)
    }

    pub fn enter(&self) -> RuntimeGuard<'_> {
        CURRENT.with_borrow_mut(|local| match local {
            Some(_) => RuntimeGuard {
                acquired: false,
                _lifetime: PhantomData,
            },
            None => {
                *local = Some(self.clone());
                RuntimeGuard {
                    acquired: true,
                    _lifetime: PhantomData,
                }
            }
        })
    }

    pub fn spawn(fut: impl Future<Output = ()> + Send + 'static) {
        CURRENT.with_borrow(|local| {
            if let Some(runtime) = &*local {
                #[cfg(feature = "multi-thread")]
                {
                    runtime.executor.spawn(fut)
                        .expect("Failed to spawn task on threaded executor");
                }
                #[cfg(not(feature = "multi-thread"))]
                {
                    let pool = runtime.executor.pool.lock().expect("Failed to lock local executor");
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
            let mut pool = self.executor
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

fn new_executor(lazy_runtime: Arc<Mutex<Option<Runtime>>>) -> Executor {
    #[cfg(not(feature = "multi-thread"))]
    {
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
                        *local = Some(lazy_runtime.lock().unwrap().as_ref().unwrap().clone());
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
    /*
    fn spawn_blocking(&self, f: impl FnOnce() + Send + 'static) {
        let fut = async move {
            f();
        };
        self.spawn(fut)
    }
    */
}

impl unienc::Runtime for RuntimeSpawner {}
