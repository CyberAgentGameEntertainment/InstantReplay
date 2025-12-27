pub trait Spawn {
    fn spawn(&self, future: impl Future<Output = ()> + Send + 'static);
}

pub trait SpawnBlocking {
    fn spawn_blocking(&self, f: impl FnOnce() + Send + 'static);
}

pub trait Runtime: Spawn + SpawnBlocking + Send {}