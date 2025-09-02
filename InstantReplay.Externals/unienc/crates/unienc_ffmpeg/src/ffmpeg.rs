use std::{
    ffi::{OsStr, OsString},
    os::fd::{AsRawFd, FromRawFd},
    process::{ExitStatus, Stdio},
    sync::LazyLock,
};

use anyhow::{Context, Result};
use tokio::{
    io::AsyncWrite,
    process::{Child, ChildStdin, ChildStdout, Command},
};

pub static FFMPEG_PATH: LazyLock<OsString> = LazyLock::new(|| {
    let res = std::process::Command::new("which")
        .arg("ffmpeg")
        .output()
        .context("Failed to find ffmpeg")
        .and_then(|o| {
            if o.status.success() {
                Ok(String::from_utf8_lossy(&o.stdout).trim().into())
            } else {
                Err(anyhow::anyhow!("Failed to find ffmpeg"))
            }
        })
        .unwrap_or_else(|_| {
            std::process::Command::new("/bin/bash")
                .arg("-cl")
                .arg("which ffmpeg")
                .output()
                .context("Failed to find ffmpeg")
                .and_then(|o| {
                    if o.status.success() {
                        Ok(String::from_utf8_lossy(&o.stdout).trim().into())
                    } else {
                        Err(anyhow::anyhow!("Failed to find ffmpeg"))
                    }
                })
                .unwrap_or(OsString::from("ffmpeg"))
        });

    println!("{}", res.to_str().unwrap());

    res
});

#[derive(Default)]
pub struct Builder {
    inputs: Vec<Vec<OsString>>,
}

pub enum Input {
    Pipe(tokio::net::unix::pipe::Sender),
    Stdin(ChildStdin),
}

impl AsyncWrite for Input {
    fn poll_write(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
        buf: &[u8],
    ) -> std::task::Poll<std::result::Result<usize, std::io::Error>> {
        match &mut *self {
            Input::Pipe(sender) => std::pin::pin!(sender).poll_write(cx, buf),
            Input::Stdin(child_stdin) => std::pin::pin!(child_stdin).poll_write(cx, buf),
        }
    }

    fn poll_flush(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
    ) -> std::task::Poll<std::result::Result<(), std::io::Error>> {
        match &mut *self {
            Input::Pipe(sender) => std::pin::pin!(sender).poll_flush(cx),
            Input::Stdin(child_stdin) => std::pin::pin!(child_stdin).poll_flush(cx),
        }
    }

    fn poll_shutdown(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
    ) -> std::task::Poll<std::result::Result<(), std::io::Error>> {
        match &mut *self {
            Input::Pipe(sender) => std::pin::pin!(sender).poll_shutdown(cx),
            Input::Stdin(child_stdin) => std::pin::pin!(child_stdin).poll_shutdown(cx),
        }
    }
}

pub struct FFmpeg {
    child: Child,
    pub inputs: Option<Vec<Input>>,
    pub stdout: Option<ChildStdout>,
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

    pub fn build(
        self,
        output_options: impl IntoIterator<Item: AsRef<OsStr>>,
        dest: Destination,
    ) -> Result<FFmpeg> {
        let mut command = Command::new(FFMPEG_PATH.as_os_str());

        command
            .kill_on_drop(true)
            .args(["-y", "-loglevel", "error"]);

        let mut inputs = Vec::new();
        let mut pending_fd = Vec::new();

        for input in self.inputs {
            if inputs.is_empty() {
                // use stdin
                command.args(input).args(["-i", "-"]).stdin(Stdio::piped());
                inputs.push(None);
            } else {
                // use pipe
                // both tx and rx have O_CLOEXEC by default
                let (tx, rx) = tokio::net::unix::pipe::pipe()?;

                // dup will remove O_CLOEXEC
                let rx_dup = unsafe { libc::dup(rx.as_raw_fd()) };
                if rx_dup < 0 {
                    return Err(anyhow::anyhow!("Failed to dup pipe read end"));
                }

                // keep rx lifetime until fork
                let rx_dup = unsafe { std::os::fd::OwnedFd::from_raw_fd(rx_dup) };

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

        // println!("{:?}", command);

        let mut child = command.spawn()?;

        drop(pending_fd);

        let mut inputs_result = Vec::new();

        for input in inputs {
            inputs_result.push(match input {
                Some(tx) => Input::Pipe(tx),
                None => Input::Stdin(child.stdin.take().context("Failed to get stdin")?),
            });
        }

        let stdout = child.stdout.take();

        Ok(FFmpeg {
            child,
            inputs: Some(inputs_result),
            stdout,
        })
    }
}

impl FFmpeg {
    pub async fn wait(mut self) -> Result<ExitStatus> {
        Ok(self.child.wait().await?)
    }
}
