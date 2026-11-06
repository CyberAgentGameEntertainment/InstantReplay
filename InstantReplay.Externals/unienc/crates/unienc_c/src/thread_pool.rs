//! A minimal thread pool executor for `Send` futures.
//!
//! It replaces `futures::executor::ThreadPool` so that a job can be run on every worker thread
//! (see [`ThreadPool::broadcast`]). This is required to detach the workers from the IL2CPP runtime
//! before the host shuts down, because IL2CPP only allows a thread to detach itself.
//!
//! The lifetime semantics follow `futures::executor::ThreadPool`: every spawned task holds a handle
//! to the pool, and the workers exit when the last handle is dropped. The worker threads are not
//! joined.

use futures::future::BoxFuture;
use futures::task::{ArcWake, waker_ref};
use std::cell::Cell;
use std::collections::VecDeque;
use std::sync::atomic::{AtomicU8, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::task::{Context, Poll};
use std::time::{Duration, Instant};

type Hook = Box<dyn Fn(usize) + Send + Sync>;

thread_local! {
    /// The pool and the index of the worker running on the current thread, if any.
    static CURRENT_WORKER: Cell<Option<(usize, usize)>> = const { Cell::new(None) };
}

/// Returns `true` if the current thread is a worker thread of any [`ThreadPool`].
pub fn is_worker_thread() -> bool {
    CURRENT_WORKER.get().is_some()
}

pub struct ThreadPool {
    inner: Arc<Inner>,
}

struct Inner {
    state: Mutex<State>,
    condvar: Condvar,
    handles: AtomicUsize,
    size: usize,
}

struct State {
    tasks: VecDeque<Arc<Task>>,
    /// Jobs queued for a specific worker. They take precedence over the tasks.
    worker_jobs: Vec<VecDeque<Arc<BroadcastJob>>>,
    closed: bool,
}

impl ThreadPool {
    pub fn new(
        name_prefix: &str,
        after_start: impl Fn(usize) + Send + Sync + 'static,
        before_stop: impl Fn(usize) + Send + Sync + 'static,
    ) -> std::io::Result<Self> {
        let size = std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(1);

        let inner = Arc::new(Inner {
            state: Mutex::new(State {
                tasks: VecDeque::new(),
                worker_jobs: (0..size).map(|_| VecDeque::new()).collect(),
                closed: false,
            }),
            condvar: Condvar::new(),
            handles: AtomicUsize::new(1),
            size,
        });

        let after_start: Arc<Hook> = Arc::new(Box::new(after_start));
        let before_stop: Arc<Hook> = Arc::new(Box::new(before_stop));

        for index in 0..size {
            let inner = inner.clone();
            let after_start = after_start.clone();
            let before_stop = before_stop.clone();
            std::thread::Builder::new()
                .name(format!("{name_prefix}{index}"))
                .spawn(move || worker_main(inner, index, &after_start, &before_stop))?;
        }

        Ok(Self { inner })
    }

    pub fn spawn(&self, future: impl Future<Output = ()> + Send + 'static) {
        let task = Arc::new(Task {
            future: Mutex::new(Some(Box::pin(future))),
            state: AtomicU8::new(SCHEDULED),
            pool: self.clone(),
        });
        self.inner.push_task(task);
    }

    /// Runs `job` once on every worker thread and waits until all of them have run it or the
    /// timeout elapses. A worker runs the job after the task it is currently polling, if any.
    ///
    /// If the current thread is a worker of this pool, the job is run on it immediately.
    ///
    /// Returns `true` if every worker has run the job within the timeout.
    pub fn broadcast(&self, job: impl Fn() + Send + Sync + 'static, timeout: Duration) -> bool {
        let job = Arc::new(BroadcastJob {
            job: Box::new(job),
            remaining: Mutex::new(self.inner.size),
            condvar: Condvar::new(),
        });

        let current = CURRENT_WORKER
            .get()
            .filter(|(pool, _)| *pool == self.inner.id())
            .map(|(_, index)| index);

        {
            let mut state = self.inner.state.lock().unwrap();
            for (index, jobs) in state.worker_jobs.iter_mut().enumerate() {
                if Some(index) != current {
                    jobs.push_back(job.clone());
                }
            }
        }
        self.inner.condvar.notify_all();

        if current.is_some() {
            job.run();
        }

        job.wait(timeout)
    }
}

impl Clone for ThreadPool {
    fn clone(&self) -> Self {
        self.inner.handles.fetch_add(1, Ordering::Relaxed);
        Self {
            inner: self.inner.clone(),
        }
    }
}

impl Drop for ThreadPool {
    fn drop(&mut self) {
        if self.inner.handles.fetch_sub(1, Ordering::AcqRel) == 1 {
            // No task can exist at this point because every task holds a handle.
            self.inner.state.lock().unwrap().closed = true;
            self.inner.condvar.notify_all();
        }
    }
}

impl Inner {
    fn id(&self) -> usize {
        self as *const Self as usize
    }

    fn push_task(&self, task: Arc<Task>) {
        self.state.lock().unwrap().tasks.push_back(task);
        self.condvar.notify_one();
    }
}

enum Work {
    Job(Arc<BroadcastJob>),
    Task(Arc<Task>),
    Exit,
}

fn worker_main(inner: Arc<Inner>, index: usize, after_start: &Hook, before_stop: &Hook) {
    CURRENT_WORKER.set(Some((inner.id(), index)));
    after_start(index);

    loop {
        let work = {
            let mut state = inner.state.lock().unwrap();
            loop {
                if let Some(job) = state.worker_jobs[index].pop_front() {
                    break Work::Job(job);
                }
                if let Some(task) = state.tasks.pop_front() {
                    break Work::Task(task);
                }
                if state.closed {
                    break Work::Exit;
                }
                state = inner.condvar.wait(state).unwrap();
            }
        };

        match work {
            Work::Job(job) => job.run(),
            Work::Task(task) => task.run(),
            Work::Exit => break,
        }
    }

    before_stop(index);
    CURRENT_WORKER.set(None);
}

struct BroadcastJob {
    job: Box<dyn Fn() + Send + Sync>,
    remaining: Mutex<usize>,
    condvar: Condvar,
}

impl BroadcastJob {
    fn run(&self) {
        (self.job)();
        let mut remaining = self.remaining.lock().unwrap();
        *remaining -= 1;
        if *remaining == 0 {
            self.condvar.notify_all();
        }
    }

    fn wait(&self, timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        let mut remaining = self.remaining.lock().unwrap();
        while *remaining > 0 {
            let now = Instant::now();
            if now >= deadline {
                return false;
            }
            remaining = self
                .condvar
                .wait_timeout(remaining, deadline - now)
                .unwrap()
                .0;
        }
        true
    }
}

// Task states.
/// Not scheduled; waiting for a wake-up.
const IDLE: u8 = 0;
/// In the queue of the pool.
const SCHEDULED: u8 = 1;
/// Being polled by a worker.
const RUNNING: u8 = 2;
/// Woken while being polled; the worker polls it again.
const NOTIFIED: u8 = 3;
/// The future has completed.
const COMPLETE: u8 = 4;

struct Task {
    future: Mutex<Option<BoxFuture<'static, ()>>>,
    state: AtomicU8,
    pool: ThreadPool,
}

impl Task {
    fn run(self: Arc<Self>) {
        let waker = waker_ref(&self);
        let mut cx = Context::from_waker(&waker);
        let mut future = self.future.lock().unwrap();

        self.state.store(RUNNING, Ordering::Release);
        loop {
            let Some(fut) = future.as_mut() else {
                self.state.store(COMPLETE, Ordering::Release);
                return;
            };

            match fut.as_mut().poll(&mut cx) {
                Poll::Ready(()) => {
                    *future = None;
                    self.state.store(COMPLETE, Ordering::Release);
                    return;
                }
                Poll::Pending => {
                    match self.state.compare_exchange(
                        RUNNING,
                        IDLE,
                        Ordering::AcqRel,
                        Ordering::Acquire,
                    ) {
                        Ok(_) => return,
                        Err(NOTIFIED) => {
                            self.state.store(RUNNING, Ordering::Release);
                        }
                        Err(state) => unreachable!("unexpected task state {state}"),
                    }
                }
            }
        }
    }
}

impl ArcWake for Task {
    fn wake_by_ref(arc_self: &Arc<Self>) {
        let mut state = arc_self.state.load(Ordering::Acquire);
        loop {
            let next = match state {
                IDLE => SCHEDULED,
                RUNNING => NOTIFIED,
                _ => return,
            };
            match arc_self
                .state
                .compare_exchange(state, next, Ordering::AcqRel, Ordering::Acquire)
            {
                Ok(_) => {
                    if next == SCHEDULED {
                        arc_self.pool.inner.push_task(arc_self.clone());
                    }
                    return;
                }
                Err(actual) => state = actual,
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicBool;
    use std::sync::mpsc;

    fn pool() -> ThreadPool {
        ThreadPool::new("test-worker-", |_| {}, |_| {}).unwrap()
    }

    #[test]
    fn runs_spawned_futures() {
        let pool = pool();
        let (tx, rx) = mpsc::channel();
        for i in 0..100 {
            let tx = tx.clone();
            pool.spawn(async move {
                tx.send(i).unwrap();
            });
        }
        let mut received: Vec<_> = (0..100)
            .map(|_| rx.recv_timeout(Duration::from_secs(5)).unwrap())
            .collect();
        received.sort();
        assert_eq!(received, (0..100).collect::<Vec<_>>());
    }

    #[test]
    fn wakes_pending_futures() {
        let pool = pool();
        let (tx, rx) = mpsc::channel();
        for i in 0..100 {
            let tx = tx.clone();
            let (wake_tx, wake_rx) = futures::channel::oneshot::channel::<usize>();
            pool.spawn(async move {
                tx.send(wake_rx.await.unwrap()).unwrap();
            });
            std::thread::spawn(move || wake_tx.send(i).unwrap());
        }
        let mut received: Vec<_> = (0..100)
            .map(|_| rx.recv_timeout(Duration::from_secs(5)).unwrap())
            .collect();
        received.sort();
        assert_eq!(received, (0..100).collect::<Vec<_>>());
    }

    #[test]
    fn broadcast_runs_on_every_worker() {
        let pool = pool();
        let threads = Arc::new(Mutex::new(Vec::new()));
        let threads_clone = threads.clone();
        let completed = pool.broadcast(
            move || {
                threads_clone
                    .lock()
                    .unwrap()
                    .push(std::thread::current().id())
            },
            Duration::from_secs(5),
        );
        assert!(completed);
        let mut threads = threads.lock().unwrap().clone();
        let count = threads.len();
        threads.sort_by_key(|id| format!("{id:?}"));
        threads.dedup();
        assert_eq!(count, pool.inner.size);
        assert_eq!(threads.len(), pool.inner.size);
    }

    #[test]
    fn broadcast_from_worker_does_not_deadlock() {
        let pool = pool();
        let (tx, rx) = mpsc::channel();
        let pool_clone = pool.clone();
        pool.spawn(async move {
            tx.send(pool_clone.broadcast(|| {}, Duration::from_secs(5)))
                .unwrap();
        });
        assert!(rx.recv_timeout(Duration::from_secs(10)).unwrap());
    }

    #[test]
    fn broadcast_times_out_while_a_worker_is_busy() {
        let pool = pool();
        let release = Arc::new(AtomicBool::new(false));
        let (started_tx, started_rx) = mpsc::channel();
        // Keep every worker busy so that none of them can run the job.
        for _ in 0..pool.inner.size {
            let release = release.clone();
            let started_tx = started_tx.clone();
            pool.spawn(async move {
                started_tx.send(()).unwrap();
                while !release.load(Ordering::Acquire) {
                    std::thread::sleep(Duration::from_millis(1));
                }
            });
        }
        for _ in 0..pool.inner.size {
            started_rx.recv_timeout(Duration::from_secs(5)).unwrap();
        }
        assert!(!pool.broadcast(|| {}, Duration::from_millis(50)));
        release.store(true, Ordering::Release);
    }

    #[test]
    fn workers_exit_when_last_handle_is_dropped() {
        let stopped = Arc::new(AtomicUsize::new(0));
        let stopped_clone = stopped.clone();
        let pool = ThreadPool::new(
            "test-worker-",
            |_| {},
            move |_| {
                stopped_clone.fetch_add(1, Ordering::AcqRel);
            },
        )
        .unwrap();
        let size = pool.inner.size;
        let (tx, rx) = mpsc::channel();
        pool.spawn(async move {
            tx.send(()).unwrap();
        });
        rx.recv_timeout(Duration::from_secs(5)).unwrap();
        drop(pool);

        let deadline = Instant::now() + Duration::from_secs(5);
        while stopped.load(Ordering::Acquire) < size {
            assert!(Instant::now() < deadline, "workers did not stop");
            std::thread::sleep(Duration::from_millis(1));
        }
    }
}
