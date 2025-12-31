pub trait Spawn {
    fn spawn(&self, future: impl Future<Output = ()> + Send + 'static);
}

pub trait SpawnBlocking {
    // fn spawn_blocking(&self, f: impl FnOnce() + Send + 'static);
}

pub trait Runtime: Spawn + SpawnBlocking + Send + Clone {}


pub trait SpawnExt: Spawn {
    fn spawn_ret<F, R>(&self, f: F)
    where
        F: Future<Output = R> + Send + 'static,
    {
        let fut = async move {
            f.await;
        };
        Spawn::spawn(self, fut);
    }
}

impl<T: Spawn + ?Sized> SpawnExt for T {}