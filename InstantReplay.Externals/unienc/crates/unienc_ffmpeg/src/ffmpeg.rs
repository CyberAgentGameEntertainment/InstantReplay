use std::{
    ffi::{OsStr, OsString},
    fs::File,
    io::Write,
    os::fd::{AsRawFd, FromRawFd, OwnedFd},
    process::{Child, ChildStdin, ChildStdout, Command, ExitStatus, Stdio},
    sync::LazyLock,
};

use unienc_common::Runtime;

use crate::error::{FFmpegError, Result};

pub static FFMPEG_PATH: LazyLock<OsString> = LazyLock::new(|| {
    let res: Result<OsString> = Command::new("which")
        .arg("ffmpeg")
        .output()
        .map_err(|_| FFmpegError::FFmpegNotFound)
        .and_then(|o| {
            if o.status.success() {
                Ok(String::from_utf8_lossy(&o.stdout).trim().into())
            } else {
                Err(FFmpegError::FFmpegNotFound)
            }
        });

    let res = res.unwrap_or_else(|_| {
        let fallback: Result<OsString> = Command::new("/bin/bash")
            .arg("-cl")
            .arg("which ffmpeg")
            .output()
            .map_err(|_| FFmpegError::FFmpegNotFound)
            .and_then(|o| {
                if o.status.success() {
                    Ok(String::from_utf8_lossy(&o.stdout).trim().into())
                } else {
                    Err(FFmpegError::FFmpegNotFound)
                }
            });
        fallback.unwrap_or(OsString::from("ffmpeg"))
    });

    log::info!("using FFmpeg at: {}", res.to_str().unwrap());

    res
});

/// One FFmpeg input, either the child's stdin or a dedicated pipe.
///
/// These are the blocking standard-library handles. Every operation on them is
/// performed on the runtime's blocking pool (see [`Input`]) so that this
/// backend works under any executor. It must not depend on an ambient Tokio
/// reactor, because `unienc_c` drives the encoder futures on
/// `futures::executor::ThreadPool` and no Tokio runtime exists in the process.
pub enum Writer {
    Pipe(File),
    Stdin(ChildStdin),
}

impl Write for Writer {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        match self {
            Writer::Pipe(file) => file.write(buf),
            Writer::Stdin(stdin) => stdin.write(buf),
        }
    }

    fn flush(&mut self) -> std::io::Result<()> {
        match self {
            Writer::Pipe(file) => file.flush(),
            Writer::Stdin(stdin) => stdin.flush(),
        }
    }
}

/// An FFmpeg input that performs its writes on `runtime`'s blocking pool.
pub struct Input<R: Runtime> {
    runtime: R,
    /// Taken while a blocking operation is in flight, and permanently by
    /// [`Input::close`]. A write whose future is dropped mid-flight therefore
    /// leaves the input unusable, which is fine because the pipeline only ever
    /// drops an input together with the encoder that owns it.
    writer: Option<Writer>,
}

impl<R: Runtime + 'static> Input<R> {
    pub fn new(runtime: R, writer: Writer) -> Self {
        Self {
            runtime,
            writer: Some(writer),
        }
    }

    /// Runs `f` against the blocking writer on the runtime's blocking pool.
    ///
    /// `f` has to own everything it writes, since it is moved to another
    /// thread. Returning the payload from `f` lets the caller take ownership
    /// back afterwards, so no buffer needs to be copied to cross the boundary.
    pub async fn with_writer<T, F>(&mut self, f: F) -> Result<T>
    where
        F: FnOnce(&mut Writer) -> std::io::Result<T> + Send + 'static,
        T: Send + 'static,
    {
        let mut writer = self.writer.take().ok_or(FFmpegError::InputNotAvailable)?;
        let (writer, result) = self
            .runtime
            .spawn_blocking(move || {
                let result = f(&mut writer);
                (writer, result)
            })
            .await;
        self.writer = Some(writer);
        Ok(result?)
    }

    /// Flushes and closes the input, which signals end of stream to FFmpeg.
    pub async fn close(mut self) -> Result<()> {
        let Some(mut writer) = self.writer.take() else {
            return Ok(());
        };
        self.runtime
            .spawn_blocking(move || {
                let result = writer.flush();
                // Dropping the handle closes the underlying descriptor, which
                // is what makes FFmpeg see end of stream.
                drop(writer);
                result
            })
            .await?;
        Ok(())
    }
}

/// FFmpeg's stdout, read on `runtime`'s blocking pool.
pub struct Output<R: Runtime> {
    runtime: R,
    /// Taken while a blocking read is in flight; see [`Input::writer`].
    reader: Option<ChildStdout>,
}

impl<R: Runtime + 'static> Output<R> {
    pub fn new(runtime: R, reader: ChildStdout) -> Self {
        Self {
            runtime,
            reader: Some(reader),
        }
    }

    /// Runs `f` against the blocking reader on the runtime's blocking pool.
    ///
    /// As with [`Input::with_writer`], `f` owns its buffer and hands it back so
    /// that it can be reused without copying.
    pub async fn with_reader<T, F>(&mut self, f: F) -> Result<T>
    where
        F: FnOnce(&mut ChildStdout) -> std::io::Result<T> + Send + 'static,
        T: Send + 'static,
    {
        let mut reader = self.reader.take().ok_or(FFmpegError::OutputNotAvailable)?;
        let (reader, result) = self
            .runtime
            .spawn_blocking(move || {
                let result = f(&mut reader);
                (reader, result)
            })
            .await;
        self.reader = Some(reader);
        Ok(result?)
    }
}

#[derive(Default)]
pub struct Builder {
    inputs: Vec<Vec<OsString>>,
    use_stdin: bool,
}

/// A spawned FFmpeg process together with the handles to talk to it.
pub struct Spawned {
    pub ffmpeg: FFmpeg,
    pub inputs: Vec<Writer>,
    pub stdout: Option<ChildStdout>,
}

/// A running FFmpeg process.
///
/// Dropping this kills the process, mirroring the `kill_on_drop` behaviour the
/// Tokio-based implementation relied on.
pub struct FFmpeg {
    /// `None` only after [`FFmpeg::wait`] has taken the child.
    child: Option<Child>,
}

impl Drop for FFmpeg {
    fn drop(&mut self) {
        if let Some(child) = self.child.as_mut() {
            // Both calls fail harmlessly if the process already exited; the
            // wait is what keeps it from lingering as a zombie.
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

pub enum Destination {
    Path(OsString),
    Stdout,
}

impl Builder {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn input(mut self, options: impl IntoIterator<Item: AsRef<OsStr>>) -> Self {
        self.inputs
            .push(options.into_iter().map(|s| s.as_ref().to_owned()).collect());
        self
    }

    pub fn use_stdin(mut self, use_stdin: bool) -> Self {
        self.use_stdin = use_stdin;
        self
    }

    pub fn build(
        self,
        output_options: impl IntoIterator<Item: AsRef<OsStr>>,
        dest: Destination,
    ) -> Result<Spawned> {
        let mut command = Command::new(FFMPEG_PATH.as_os_str());

        command.args(["-y", "-loglevel", "error"]);

        let mut inputs = Vec::new();
        let mut pending_fd = Vec::new();

        for input in self.inputs {
            if self.use_stdin && inputs.is_empty() {
                // use stdin
                command.args(input).args(["-i", "-"]).stdin(Stdio::piped());
                inputs.push(None);
            } else {
                // use pipe
                // both tx and rx have O_CLOEXEC so that they are not leaked
                // into any child spawned later
                let (tx, rx) = pipe()?;

                // dup will remove O_CLOEXEC
                let rx_dup = unsafe { libc::dup(rx.as_raw_fd()) };
                if rx_dup < 0 {
                    return Err(FFmpegError::PipeDupFailed);
                }

                // keep rx lifetime until fork
                let rx_dup = unsafe { OwnedFd::from_raw_fd(rx_dup) };

                command
                    .args(input)
                    .args(["-i", &format!("pipe:{}", rx_dup.as_raw_fd())]);
                inputs.push(Some(tx));
                pending_fd.push(rx_dup);
            }
        }

        command.args(output_options);
        match dest {
            Destination::Path(path) => command.arg(path),
            Destination::Stdout => command.stdout(Stdio::piped()).arg(OsString::from("-")),
        };

        log::debug!("Running FFmpeg: {command:?}");

        let mut child = command.spawn()?;

        drop(pending_fd);

        let mut inputs_result = Vec::new();

        for input in inputs {
            inputs_result.push(match input {
                Some(tx) => Writer::Pipe(File::from(tx)),
                None => Writer::Stdin(child.stdin.take().ok_or(FFmpegError::StdinNotAvailable)?),
            });
        }

        let stdout = child.stdout.take();

        Ok(Spawned {
            ffmpeg: FFmpeg { child: Some(child) },
            inputs: inputs_result,
            stdout,
        })
    }
}

impl FFmpeg {
    /// Waits for the process to exit, blocking on `runtime`'s blocking pool.
    ///
    /// The runtime is taken by value because `Runtime` is not `Sync`, and a
    /// borrow would make the returned future non-`Send`.
    pub async fn wait<R: Runtime + 'static>(mut self, runtime: R) -> Result<ExitStatus> {
        let mut child = self.child.take().ok_or(FFmpegError::ProcessFailed)?;
        Ok(runtime.spawn_blocking(move || child.wait()).await?)
    }
}

/// Creates a blocking pipe whose ends are both close-on-exec.
fn pipe() -> Result<(OwnedFd, OwnedFd)> {
    let mut fds = [0 as libc::c_int; 2];
    // SAFETY: `fds` is a two-element array as pipe2 expects.
    if unsafe { libc::pipe2(fds.as_mut_ptr(), libc::O_CLOEXEC) } < 0 {
        return Err(FFmpegError::Io(std::io::Error::last_os_error()));
    }
    // SAFETY: pipe2 succeeded, so both descriptors are open and owned by us.
    unsafe { Ok((OwnedFd::from_raw_fd(fds[1]), OwnedFd::from_raw_fd(fds[0]))) }
}
