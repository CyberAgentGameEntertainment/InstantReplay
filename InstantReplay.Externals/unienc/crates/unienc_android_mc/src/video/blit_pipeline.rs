//! Pipelining of HardwareBuffer blits.
//!
//! A blit spans several frames of latency: the graphics event is drained by the C# side once per
//! frame, the render thread executes it later, and the fence signals only after the GPU has also
//! finished the work Unity submitted before it. Waiting for all of that inside `push` caps the
//! recording frame rate at one frame per round trip. Instead, `push` returns as soon as the event
//! is issued, and a single completion task awaits the outstanding blits in issue order and queues
//! each frame to the encoder, so successive frames overlap on the GPU.

use crate::common::MediaCodec;
use crate::error::{AndroidError, Result};
use crate::vulkan::hardware_buffer_surface::{HardwareBufferFrame, HardwareBufferSurface};
use std::future::Future;
use std::pin::Pin;
use std::sync::{Arc, Mutex};
use tokio::sync::{OwnedSemaphorePermit, Semaphore, mpsc, oneshot};

pub(super) type BlitFuture = Pin<Box<dyn Future<Output = Result<()>> + Send>>;

/// What the render-thread callback hands back for one frame: the future that completes when the
/// GPU is done (or the error that prevented the blit), and the frame it rendered into.
pub(super) type BlitOutcome = (Result<BlitFuture>, HardwareBufferFrame);

pub(super) struct PendingBlit {
    pub rx: oneshot::Receiver<BlitOutcome>,
    pub timestamp_ns: i64,
    /// Released when the frame has been queued to the encoder (or abandoned).
    pub permit: OwnedSemaphorePermit,
}

enum State {
    Running,
    /// The error is reported by the first `push` that observes it; later pushes get a generic one.
    Failed(Option<AndroidError>),
}

pub(super) struct BlitPipeline {
    /// `None` once the pipeline is being torn down.
    tx: Option<mpsc::UnboundedSender<PendingBlit>>,
    permits: Arc<Semaphore>,
    state: Arc<Mutex<State>>,
}

impl BlitPipeline {
    /// Spawns the completion task. `max_in_flight` bounds the frames dequeued from the
    /// `ImageWriter` but not yet queued back, so that `dequeue_frame` never has to block.
    pub fn start<R: unienc_common::Runtime + 'static>(
        runtime: &R,
        surface: Arc<HardwareBufferSurface>,
        codec: MediaCodec,
        max_in_flight: usize,
    ) -> Self {
        let (tx, rx) = mpsc::unbounded_channel();
        let state = Arc::new(Mutex::new(State::Running));

        runtime.spawn(complete_blits(rx, surface, codec, state.clone()));

        Self {
            tx: Some(tx),
            permits: Arc::new(Semaphore::new(max_in_flight)),
            state,
        }
    }

    /// Fails if the completion task has hit an error.
    pub fn check(&self) -> Result<()> {
        match &mut *self.state.lock().map_err(|_| AndroidError::MutexPoisoned)? {
            State::Running => Ok(()),
            State::Failed(error) => Err(error.take().unwrap_or(AndroidError::BlitPipelineFailed)),
        }
    }

    /// Waits until fewer than `max_in_flight` frames are outstanding.
    pub async fn acquire(&self) -> Result<OwnedSemaphorePermit> {
        self.permits
            .clone()
            .acquire_owned()
            .await
            .map_err(|_| AndroidError::BlitPipelineFailed)
    }

    /// Hands an issued blit to the completion task. Blits must be submitted in frame order.
    pub fn submit(&self, pending: PendingBlit) -> Result<()> {
        self.tx
            .as_ref()
            .ok_or(AndroidError::BlitPipelineFailed)?
            .send(pending)
            .map_err(|_| AndroidError::BlitPipelineFailed)
    }
}

impl Drop for BlitPipeline {
    fn drop(&mut self) {
        // Closing the channel lets the completion task drain the outstanding blits and then end
        // the stream; `Drop` cannot await, so the end-of-stream signal lives there.
        self.tx.take();
    }
}

async fn complete_blits(
    mut rx: mpsc::UnboundedReceiver<PendingBlit>,
    surface: Arc<HardwareBufferSurface>,
    codec: MediaCodec,
    state: Arc<Mutex<State>>,
) {
    let mut failed = false;
    // Records the first error only; the pipeline stays failed afterwards.
    fn fail(state: &Mutex<State>, failed: &mut bool, error: AndroidError) {
        if !*failed {
            *failed = true;
            if let Ok(mut state) = state.lock() {
                *state = State::Failed(Some(error));
            }
        }
    }

    while let Some(pending) = rx.recv().await {
        let PendingBlit {
            rx,
            timestamp_ns,
            permit,
        } = pending;

        // The frame comes back through the callback; if the callback never ran the frame was
        // dropped with it and nothing was submitted.
        let (blit, frame) = match rx.await {
            Ok(outcome) => outcome,
            Err(error) => {
                fail(&state, &mut failed, error.into());
                continue;
            }
        };

        // Always wait for the GPU before the frame (and the image it renders into) is released,
        // even after an earlier failure.
        let result = match blit {
            Ok(future) => future.await,
            Err(error) => Err(error),
        };

        let result = result.and_then(|()| {
            if failed {
                // Encoder input is considered dead; keep the frame order intact by not queueing.
                drop(frame);
                Ok(())
            } else {
                surface.queue_frame(frame, timestamp_ns)
            }
        });

        if let Err(error) = result {
            fail(&state, &mut failed, error);
        }

        drop(permit);
    }

    // Every accepted frame has been queued (or abandoned): end the stream.
    _ = codec.print_metrics();
    if let Err(error) = codec.signal_end_of_input_stream() {
        fail(&state, &mut failed, error);
    }
}
