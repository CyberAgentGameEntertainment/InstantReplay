//! Browser harness for the shared end-to-end test.
//!
//! The web is the one target where the harness cannot be a `cargo test`. The
//! encoders are the browser's own and report through callbacks delivered as
//! browser tasks, so the only thread must keep returning to the event loop —
//! while libtest expects a test function to run to completion and return. A
//! blocking driver would wait for results the browser cannot deliver.
//!
//! So this is a program instead, and it drives the pipeline the way Unity does in
//! production: a callback per animation frame, polling what has become ready.
//! `emscripten_set_main_loop` here plays the part `unienc_tick_runtime` plays
//! there.
//!
//! ASYNCIFY looks like an easier answer and is not one: it unwinds the wasm stack
//! while suspended, and the encoder callbacks re-enter wasm during exactly that
//! window, which is the reentrancy it does not support.
//!
//! Run it with `scripts/web-browser-test.sh`.

use std::ffi::c_void;
use std::path::PathBuf;
use std::pin::Pin;
use std::process::ExitCode;
use std::task::{Context, Poll};

use futures::executor::LocalPool;
use unienc_common::Result as UniencResult;
use unienc_testkit::e2e::{E2eConfig, E2eReport};
use unienc_testkit::{TestRuntime, e2e, verify_output};

unsafe extern "C" {
    fn emscripten_set_main_loop_arg(
        callback: extern "C" fn(*mut c_void),
        argument: *mut c_void,
        fps: i32,
        simulate_infinite_loop: i32,
    );
    fn emscripten_cancel_main_loop();
    fn emscripten_force_exit(status: i32);
}

/// Everything the per-frame callback needs, kept alive on the heap for as long as
/// the main loop runs.
struct Harness {
    config: E2eConfig,
    output_path: PathBuf,
    pool: LocalPool,
    future: Pin<Box<dyn Future<Output = UniencResult<E2eReport>>>>,
}

fn main() -> ExitCode {
    let config = E2eConfig::default();
    // Emscripten's filesystem is in memory; the root is the one directory
    // guaranteed to exist.
    let output_path = PathBuf::from("/e2e.mp4");

    // The muxer hands its bytes to a download rather than writing a file, so they
    // have to be diverted before the run for the verification to find anything.
    if let Err(message) = unienc_testkit::web::capture_muxed_output(&output_path) {
        println!("harness: FAILED\n{message}");
        return ExitCode::FAILURE;
    }

    let pool = LocalPool::new();
    let runtime = TestRuntime::from_spawner(pool.spawner());
    let encoding_system = e2e::new_platform_system(&config, runtime.clone());

    // Boxed and leaked into the main loop: `main` returns before the work is
    // done, so nothing here may live on its stack.
    let harness = Box::new(Harness {
        future: Box::pin(e2e::run_with(
            encoding_system,
            runtime,
            config,
            output_path.clone(),
        )),
        config,
        output_path,
        pool,
    });

    println!("harness: driving the pipeline from the browser's main loop");
    unsafe {
        // fps 0 means requestAnimationFrame, and not simulating an infinite loop
        // is what lets `main` return while the loop keeps running.
        emscripten_set_main_loop_arg(tick, Box::into_raw(harness) as *mut c_void, 0, 0);
    }

    // The real status is reported by `finish`; returning here would tear the
    // runtime down before the loop has run at all.
    ExitCode::SUCCESS
}

/// Polls the pipeline once per frame, letting the browser run in between.
extern "C" fn tick(argument: *mut c_void) {
    // SAFETY: the pointer is the box leaked in `main` and stays valid until
    // `finish` reclaims it.
    let harness = unsafe { &mut *(argument as *mut Harness) };

    // Spawned tasks first: the future below is normally waiting on one of them.
    harness.pool.run_until_stalled();

    let waker = futures::task::noop_waker();
    let mut context = Context::from_waker(&waker);

    match harness.future.as_mut().poll(&mut context) {
        Poll::Pending => {}
        Poll::Ready(result) => finish(argument, result),
    }
}

/// Reports the outcome and exits, having stopped the loop first.
fn finish(argument: *mut c_void, result: UniencResult<E2eReport>) {
    // SAFETY: as in `tick`; taking ownership back so nothing is polled again.
    let harness = unsafe { Box::from_raw(argument as *mut Harness) };
    unsafe { emscripten_cancel_main_loop() };

    let status = match result {
        Err(error) => {
            println!("harness: FAILED\nthe encode failed: {error}");
            1
        }
        Ok(report) => match verify_output(&harness.config, &harness.output_path, &report) {
            Ok(description) => {
                println!("harness: ok\n{description}");
                0
            }
            Err(message) => {
                println!("harness: FAILED\n{message}");
                1
            }
        },
    };

    // The page has no other way to report a status, and emrun turns the exit
    // status into the process's.
    unsafe { emscripten_force_exit(status) };
}
