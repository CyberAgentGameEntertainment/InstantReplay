use crate::emscripten::{run_script, run_script_int};
use futures::channel::oneshot;
use std::ffi::CString;
use std::sync::LazyLock;

static LIBRARY: LazyLock<Library> = LazyLock::new(Library::new);

struct Library;

pub struct VideoEncoderHandle {
    id: i32,
    callback: *mut Box<dyn Fn(&[u8], f64, bool)>,
}

unsafe impl Send for VideoEncoderHandle {}

impl VideoEncoderHandle {
    pub async fn new(
        width: u32,
        height: u32,
        bitrate: u32,
        framerate: f64,
        callback: impl Fn(&[u8], f64, bool) + 'static,
    ) -> Option<Self> {
        LIBRARY
            .new_video_encoder(width, height, bitrate, framerate, callback)
            .await
    }

    pub fn push_video_frame(
        &self,
        data: &[u8],
        width: u32,
        height: u32,
        timestamp: f64,
        is_key: bool,
    ) {
        LIBRARY.push_video_frame(self.id, data, width, height, timestamp, is_key);
    }

    pub fn flush<F: FnOnce() + 'static>(&self, callback: F) {
        LIBRARY.flush_video(self.id, callback);
    }
}
impl Drop for VideoEncoderHandle {
    fn drop(&mut self) {
        LIBRARY.free_video_encoder(self.id);
        unsafe {
            let _ = Box::from_raw(self.callback);
        }
    }
}


pub struct AudioEncoderHandle {
    id: i32,
    callback: *mut Box<dyn Fn(&[u8], f64)>,
}

unsafe impl Send for AudioEncoderHandle {}

impl AudioEncoderHandle {
    pub async fn new(
        bitrate: u32,
        channels: u32,
        sample_rate: u32,
        callback: impl Fn(&[u8], f64) + 'static,
    ) -> Option<Self> {
        LIBRARY
            .new_audio_encoder(bitrate, channels, sample_rate, callback)
            .await
    }

    pub fn push_audio_frame(
        &self,
        data: &[u8],
        channels: u32,
        sample_rate: u32,
        timestamp: f64,
    ) {
        LIBRARY.push_audio_frame(self.id, data, channels, sample_rate, timestamp);
    }

    pub fn flush<F: FnOnce() + 'static>(&self, callback: F) {
        LIBRARY.flush_audio(self.id, callback);
    }
}
impl Drop for AudioEncoderHandle {
    fn drop(&mut self) {
        LIBRARY.free_audio_encoder(self.id);
        unsafe {
            let _ = Box::from_raw(self.callback);
        }
    }
}

pub fn make_download(parts: &[Vec<u8>], mime: &str, filename: &str) {
    LIBRARY.make_download(parts, mime, filename);
}

impl Library {
    fn new() -> Self {
        let script = include_str!("library.js");
        let script = std::ffi::CString::new(script).unwrap();
        run_script(&script);
        Library {}
    }

    async fn new_video_encoder(
        &self,
        width: u32,
        height: u32,
        bitrate: u32,
        framerate: f64,
        on_output_closure: impl Fn(&[u8], f64, bool) + 'static,
    ) -> Option<VideoEncoderHandle> {
        extern "system" fn on_output_fn(
            data_ptr: usize,
            data_length: i32,
            timestamp: f64,
            is_keyframe: bool,
            callback_ptr: usize,
        ) {
            let data =
                unsafe { std::slice::from_raw_parts(data_ptr as *const u8, data_length as usize) };
            let callback = unsafe { &mut *(callback_ptr as *mut Box<dyn Fn(&[u8], f64, bool)>) };
            callback(data, timestamp, is_keyframe);
        }

        extern "system" fn on_complete_fn(index: i32, tx: *mut oneshot::Sender<i32>) {
            let tx = unsafe { Box::from_raw(tx) };
            tx.send(index).unwrap();
        }

        let on_output = on_output_fn as usize;
        let on_output_ctx = Box::<Box<dyn Fn(&[u8], f64, bool)>>::new(Box::new(on_output_closure));
        let on_output_ctx = Box::into_raw(on_output_ctx) as usize;

        let (tx, rx) = oneshot::channel();
        let on_complete = on_complete_fn as usize;
        let on_complete_ctx = Box::into_raw(Box::new(tx)) as usize;
        let script = format!(
            "
            (function() {{
                const width = {width};
                const height = {height};
                const bitrate = {bitrate};
                const framerate = {framerate};
                const onOutput = {on_output};
                const onOutputCtx = {on_output_ctx};
                const onComplete = {on_complete};
                const onCompleteCtx = {on_complete_ctx};
                window.unienc_webcodecs.video.new({{ width, height, bitrate, framerate }}, onOutput, onOutputCtx, onComplete, onCompleteCtx);
            }})();
            "
        );
        run_script_int(&std::ffi::CString::new(script).unwrap());
        rx.await.ok().and_then(|id| {
            if id < 0 {
                None
            } else {
                Some(VideoEncoderHandle {
                    id,
                    callback: on_output_ctx as *mut _,
                })
            }
        })
    }

    fn push_video_frame(
        &self,
        encoder_index: i32,
        data: &[u8],
        width: u32,
        height: u32,
        timestamp: f64,
        is_key: bool,
    ) {
        let script = format!(
            "
            (function() {{
                const encoderIndex = {encoder_index};
                const dataPtr = {data_ptr};
                const dataLength = {data_length};
                const width = {width};
                const height = {height};
                const timestamp = {timestamp};
                const isKey = {is_key};
                const dataArray = Module.HEAPU8.subarray(dataPtr, dataPtr + dataLength);
                window.unienc_webcodecs.video.push(encoderIndex, dataArray, {{width, height, timestamp, isKey}});
            }})();
            ",
            data_ptr = data.as_ptr() as usize,
            data_length = data.len(),
            timestamp = timestamp
        );
        run_script(&CString::new(script).unwrap());
    }

    fn flush_video<F: FnOnce() + 'static>(
        &self,
        id: i32,
        callback: F
    ) {
        extern "system" fn on_complete_fn<F: FnOnce() + 'static>(callback: *mut F) {
            let callback = unsafe { Box::from_raw(callback) };
            callback();
        }

        let on_complete = on_complete_fn::<F> as usize;
        let on_complete_ctx = Box::into_raw(Box::new(callback)) as usize;
        let script = format!(
            "
            (function() {{
                const index = {id};
                const onComplete = {on_complete};
                const onCompleteCtx = {on_complete_ctx};
                window.unienc_webcodecs.video.flush(index, onComplete, onCompleteCtx);
            }})();
            "
        );
        run_script_int(&CString::new(script).unwrap());
    }

    fn free_video_encoder(&self, encoder_id: i32) {
        let script = format!(
            "
            (function() {{
                const encoderId = {encoder_id};
                window.unienc_webcodecs.video.free(encoderId);
            }})();
            ",
            encoder_id = encoder_id
        );
        run_script(&CString::new(script).unwrap());
    }

    async fn new_audio_encoder(
        &self,
        bitrate: u32,
        channels: u32,
        sample_rate: u32,
        on_output_closure: impl Fn(&[u8], f64) + 'static,
    ) -> Option<AudioEncoderHandle> {
        extern "system" fn on_output_fn(
            data_ptr: usize,
            data_length: i32,
            timestamp: f64,
            callback_ptr: usize,
        ) {
            let data =
                unsafe { std::slice::from_raw_parts(data_ptr as *const u8, data_length as usize) };
            let callback = unsafe { &mut *(callback_ptr as *mut Box<dyn Fn(&[u8], f64)>) };
            callback(data, timestamp);
        }

        extern "system" fn on_complete_fn(index: i32, tx: *mut oneshot::Sender<i32>) {
            let tx = unsafe { Box::from_raw(tx) };
            tx.send(index).unwrap();
        }

        let on_output = on_output_fn as usize;
        let on_output_ctx = Box::<Box<dyn Fn(&[u8], f64)>>::new(Box::new(on_output_closure));
        let on_output_ctx = Box::into_raw(on_output_ctx) as usize;

        let (tx, rx) = oneshot::channel();
        let on_complete = on_complete_fn as usize;
        let on_complete_ctx = Box::into_raw(Box::new(tx)) as usize;
        let script = format!(
            "
            (function() {{
                const bitrate = {bitrate};
                const channels = {channels};
                const sample_rate = {sample_rate};
                const onOutput = {on_output};
                const onOutputCtx = {on_output_ctx};
                const onComplete = {on_complete};
                const onCompleteCtx = {on_complete_ctx};
                window.unienc_webcodecs.video.new({{ bitrate, channels, sample_rate }}, onOutput, onOutputCtx, onComplete, onCompleteCtx);
            }})();
            "
        );
        run_script_int(&CString::new(script).unwrap());
        rx.await.ok().and_then(|id| {
            if id < 0 {
                None
            } else {
                Some(AudioEncoderHandle {
                    id,
                    callback: on_output_ctx as *mut _,
                })
            }
        })
    }

    fn push_audio_frame(
        &self,
        encoder_index: i32,
        data: &[u8],
        channels: u32,
        sample_rate: u32,
        timestamp: f64,
    ) {
        let script = format!(
            "
            (function() {{
                const encoderIndex = {encoder_index};
                const dataPtr = {data_ptr};
                const dataLength = {data_length};
                const channels = {channels};
                const sample_rate = {sample_rate};
                const timestamp = {timestamp};
                const dataArray = new Uint8Array(Module.HEAPU8.buffer, dataPtr, dataLength);
                window.unienc_webcodecs.video.push(encoderIndex, dataArray, {{channels, sample_rate, timestamp}});
            }})();
            ",
            data_ptr = data.as_ptr() as usize,
            data_length = data.len(),
            timestamp = timestamp
        );
        run_script(&CString::new(script).unwrap());
    }

    fn flush_audio<F: FnOnce() + 'static>(
        &self,
        id: i32,
        callback: F
    ) {
        extern "system" fn on_complete_fn<F: FnOnce() + 'static>(callback: *mut F) {
            let callback = unsafe { Box::from_raw(callback) };
            callback();
        }

        let on_complete = on_complete_fn::<F> as usize;
        let on_complete_ctx = Box::into_raw(Box::new(callback)) as usize;
        let script = format!(
            "
            (function() {{
                const index = {id};
                const onComplete = {on_complete};
                const onCompleteCtx = {on_complete_ctx};
                window.unienc_webcodecs.audio.flush(index, onComplete, onCompleteCtx);
            }})();
            "
        );
        run_script_int(&CString::new(script).unwrap());
    }

    fn free_audio_encoder(&self, encoder_id: i32) {
        let script = format!(
            "
            (function() {{
                const encoderId = {encoder_id};
                window.unienc_webcodecs.audio.free(encoderId);
            }})();
            ",
            encoder_id = encoder_id
        );
        run_script(&CString::new(script).unwrap());
    }
    fn make_download(&self, parts: &[Vec<u8>], mime: &str, filename: &str) {

        let parts = parts.iter().map(|p| Part { ptr: p.as_ptr(), len: p.len()}).collect::<Vec<Part>>();

        let parts_ptr = parts.as_ptr() as usize;
        let parts_len = parts.len();

        let mime = CString::new(mime).unwrap();
        let filename = CString::new(filename).unwrap();

        let script = format!(
            "
            (function() {{
                const partsPtr = {parts_ptr};
                const partsLen = {parts_len};
                const mimePtr = '{mime_ptr}';
                const filenamePtr = '{filename_ptr}';
                window.unienc_webcodecs.makeDownload(partsPtr, partsLen, mimePtr, filenamePtr);
            }})();
            ",
            mime_ptr = mime.as_ptr() as usize,
            filename_ptr = filename.as_ptr() as usize,
        );

        run_script(&std::ffi::CString::new(script).unwrap());
    }
}

#[repr(C)]
struct Part {
    ptr: *const u8,
    len: usize,
}