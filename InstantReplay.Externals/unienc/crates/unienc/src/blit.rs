use std::os::raw::c_void;

use unienc_common::{
    BlitOptions, EncodingSystem, IntoRaw, TryFromRaw, TryFromUnityNativeTexturePointer,
};
use unity_native_plugin::enums::RenderingExtEventType;

use crate::{
    platform_types::BlitTarget, ApplyCallback, PlatformEncodingSystem, Runtime, SendPtr,
    UniencDataCallback, UniencError, UniencErrorNative,
};

const EVENT_ID: RenderingExtEventType = RenderingExtEventType::UserEventsStart;

type EventClosure = Box<dyn FnOnce() + Send>;

#[no_mangle]
pub unsafe extern "C" fn unienc_is_blit_supported(system: *const PlatformEncodingSystem) -> bool {
    (&*system).is_blit_supported()
}

#[no_mangle]
pub unsafe extern "C" fn unienc_new_blit_closure(
    runtime: *mut Runtime,
    system: *const PlatformEncodingSystem,
    source_native_texture_ptr: *mut c_void,
    dst_width: u32,
    dst_height: u32,
    flip_vertically: bool,
    is_gamma_workflow: bool,
    event_function_ptr_out: *mut *const c_void,
    event_id_out: *mut u32,
    event_data_out: *mut *mut c_void,
    callback: usize, /*UniencDataCallback<UniencBlitTargetData>*/
    user_data: SendPtr<c_void>,
) -> bool {
    let _guard = (*runtime).enter();
    let runtime_weak = (*runtime).weak();
    let callback: UniencDataCallback<UniencBlitTargetData> = std::mem::transmute(callback);

    if system.is_null() {
        UniencError::invalid_input_error("Invalid input parameters")
            .apply_callback(callback, user_data);
        return false;
    }

    let source = match <<PlatformEncodingSystem as EncodingSystem>::BlitSourceType as TryFromUnityNativeTexturePointer>::try_from_unity_native_texture_ptr(source_native_texture_ptr) {
        Ok(source) => source,
        Err(err) => {
            UniencError::from_anyhow(err).apply_callback(callback, user_data);
            return false;
        }
    };

    (&*system)
        .new_blit_closure(
            source,
            BlitOptions {
                dst_width,
                dst_height,
                flip_vertically,
                is_gamma_workflow,
            },
        )
        .map_or_else(
            |err| {
                UniencError::from_anyhow(err).apply_callback(callback, user_data);
                false
            },
            |blit_closure| {
                let event_data: *mut EventClosure = Box::into_raw(Box::new(Box::new(move || {
                    let runtime = match runtime_weak.upgrade() {
                        Some(runtime) => runtime,
                        None => {
                            UniencError::from_anyhow(anyhow::anyhow!("Runtime has been dropped"))
                                .apply_callback(callback, user_data);
                            return;
                        }
                    };
                    let _guard = runtime.enter();
                    let f = blit_closure();

                    tokio::spawn(async move {
                        let res = f.await;
                        match res {
                            Ok(shared_texture) => {
                                callback(
                                    UniencBlitTargetData {
                                        data: shared_texture.into_raw(),
                                    },
                                    user_data.into(),
                                    UniencErrorNative::SUCCESS,
                                );
                            }
                            Err(err) => {
                                UniencError::from_anyhow(err).apply_callback(callback, user_data);
                            }
                        }
                    });
                })));

                unsafe {
                    *event_function_ptr_out = unienc_custom_graphics_event as *const c_void;
                    *event_id_out = EVENT_ID as u32;
                    *event_data_out = event_data as *mut _;
                }

                true
            },
        )
}

#[no_mangle]
pub unsafe extern "C" fn unienc_free_blit_target(blit_target_data: UniencBlitTargetData) {
    if blit_target_data.data.is_null() {
        return;
    }

    let _ = <BlitTarget as TryFromRaw>::try_from_raw(blit_target_data.data);
}

extern "C" fn unienc_custom_graphics_event(event: RenderingExtEventType, data: *mut EventClosure) {
    if event != EVENT_ID {
        return;
    }
    let closure: EventClosure = unsafe { *Box::from_raw(data) };
    closure();
}

#[repr(C)]
pub struct UniencBlitTargetData {
    pub data: *mut BlitTarget,
}

impl Default for UniencBlitTargetData {
    fn default() -> Self {
        UniencBlitTargetData {
            data: std::ptr::null_mut(),
        }
    }
}
