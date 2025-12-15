use std::io;
use std::sync::{Arc, Weak};
use thiserror::Error;
use tokio::runtime::EnterGuard;

#[derive(Error, Debug)]
pub enum RuntimeError {
    #[error("Failed to create tokio runtime: {0}")]
    TokioRuntimeCreation(#[from] io::Error),
}

pub struct Runtime {
    tokio_runtime: Arc<tokio::runtime::Runtime>,
}

impl Runtime {
    pub fn new() -> Result<Runtime, RuntimeError> {
        let tokio_runtime = Arc::new(tokio::runtime::Runtime::new()?);
        Ok(Self { tokio_runtime })
    }

    pub fn enter(&self) -> EnterGuard<'_> {
        self.tokio_runtime.enter()
    }

    pub fn weak(&self) -> WeakRuntime {
        WeakRuntime(Arc::downgrade(&self.tokio_runtime))
    }
}

#[derive(Debug, Clone)]
pub struct WeakRuntime(Weak<tokio::runtime::Runtime>);

impl WeakRuntime {
    pub fn upgrade(&self) -> Option<Runtime> {
        self.0
            .upgrade()
            .map(|tokio_runtime| Runtime { tokio_runtime })
    }
}