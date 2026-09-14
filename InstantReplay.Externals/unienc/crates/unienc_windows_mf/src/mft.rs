use tokio::sync::{mpsc, oneshot};
use windows::Win32::Media::MediaFoundation::{IMFSample, IMFTransform, MFT_OUTPUT_STREAM_INFO};
use windows::Win32::System::Com::CoTaskMemFree;

use crate::common::{ErrorSlot, UnsafeSend};
use crate::error::{Result, WindowsError};
use std::cell::Cell;
use std::future::Future;
use std::mem::ManuallyDrop;
use std::ptr;
use unienc_common::{Runtime, SpawnExt};
use windows::Win32::Foundation::E_NOTIMPL;
use windows::Win32::Media::MediaFoundation::*;
use windows::core::*;

pub trait MediaEventGeneratorCustom {
    fn get_event(&self) -> impl Future<Output = Result<UnsafeSend<IMFMediaEvent>>>;
}

impl MediaEventGeneratorCustom for IMFMediaEventGenerator {
    fn get_event(&self) -> impl Future<Output = Result<UnsafeSend<IMFMediaEvent>>> {
        let (tx, rx) = oneshot::channel::<Result<UnsafeSend<IMFMediaEvent>>>();

        let result: std::result::Result<(), Error> = {
            let generator = UnsafeSend(self.clone());
            let callback: IMFAsyncCallback = AsyncCallback::new(move |result| {
                // Media Foundation invokes this on one of its own work queue
                // threads, and it may do so after the awaiting task is already
                // gone. Nobody is left to hear the result then, and panicking
                // here would abort the host process from a thread we do not
                // own, so the closed receiver is not an error.
                let _ = tx.send(unsafe {
                    generator
                        .EndGetEvent(result.unwrap())
                        .map(UnsafeSend::<IMFMediaEvent>::from)
                        .map_err(WindowsError::from)
                });
            })
            .into();

            unsafe { self.BeginGetEvent(&callback, Option::<&IUnknown>::None) }
        };

        async move {
            result?;
            match rx.await {
                Ok(event) => event,
                Err(_) => Err(WindowsError::MediaEventReceiveFailed),
            }
        }
    }
}

#[implement(IMFAsyncCallback)]
pub struct AsyncCallback<F>
where
    F: Send + FnOnce(windows_core::Ref<'_, IMFAsyncResult>) + 'static,
{
    // generator: IMFMediaEventGenerator,
    on_invoke: Cell<Option<F>>,
    // tx: Cell<Option<oneshot::Sender<Result<UnsafeSend<IMFMediaEvent>>>>>,
}

impl<F> AsyncCallback<F>
where
    F: Send + FnOnce(windows_core::Ref<'_, IMFAsyncResult>) + 'static,
{
    pub fn new(on_invoke: F) -> Self {
        Self {
            on_invoke: Cell::new(Some(on_invoke)),
            // tx: Cell::new(None),
        }
    }
}

impl<F> IMFAsyncCallback_Impl for AsyncCallback_Impl<F>
where
    F: Send + FnOnce(windows_core::Ref<'_, IMFAsyncResult>),
{
    fn GetParameters(&self, _pdwflags: *mut u32, _pdwqueue: *mut u32) -> windows_core::Result<()> {
        Err(windows_core::Error::from_hresult(E_NOTIMPL))
    }
    fn Invoke(&self, result: windows_core::Ref<'_, IMFAsyncResult>) -> windows_core::Result<()> {
        if let Some(on_invoke) = self.on_invoke.take() {
            on_invoke(result);
        }
        Ok(())
    }
}

/// What one `ProcessOutput` call yielded.
enum Output {
    Sample(UnsafeSend<IMFSample>),
    /// The MFT renegotiated its output type. The new type has already been
    /// accepted; the caller has to refresh its cached stream info and ask for
    /// output again.
    FormatChanged,
}

/// Accepts the output type an MFT switched to.
///
/// An encoder that reports `MF_E_TRANSFORM_STREAM_CHANGE` produces nothing at
/// all until the new type is set — Intel's Quick Sync H.264 encoder does this on
/// its very first output, before any sample — so this is not an optional
/// nicety, it is what keeps the encoder running on those machines.
fn accept_new_output_type(transform: &IMFTransform, output_id: u32) -> Result<()> {
    let media_type = unsafe { transform.GetOutputAvailableType(output_id, 0)? };
    unsafe { transform.SetOutputType(output_id, &media_type, 0)? };
    Ok(())
}

fn process_output(
    transform: &IMFTransform,
    output_info: &MFT_OUTPUT_STREAM_INFO,
    output_id: u32,
) -> Result<Output> {
    let mut buffers = [MFT_OUTPUT_DATA_BUFFER::default(); 1];
    {
        let buffer = &mut buffers[0];
        buffer.dwStreamID = output_id;

        let need_provide_output_sample =
            (output_info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.0 as u32) == 0
                && (output_info.dwFlags & MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES.0 as u32) == 0;

        if need_provide_output_sample {
            let sample = unsafe { MFCreateSample()? };

            if output_info.cbSize > 0 {
                let buffer = if output_info.cbAlignment > 0 {
                    unsafe {
                        MFCreateAlignedMemoryBuffer(output_info.cbSize, output_info.cbAlignment)?
                    }
                } else {
                    unsafe { MFCreateMemoryBuffer(output_info.cbSize)? }
                };

                unsafe { sample.AddBuffer(&buffer)? };
            }

            buffer.pSample = ManuallyDrop::new(Some(sample));
        } else {
            buffer.pSample = ManuallyDrop::new(None);
        }
    }

    let mut status = 0;
    let result = unsafe { transform.ProcessOutput(0, &mut buffers, &mut status) };

    let buffer = &mut buffers[0];

    let sample = unsafe { ManuallyDrop::take(&mut buffer.pSample) };
    drop(unsafe { ManuallyDrop::take(&mut buffer.pEvents) });

    if let Err(err) = &result
        && err.code() == MF_E_TRANSFORM_STREAM_CHANGE
    {
        log::info!("Encoder MFT renegotiated its output type");
        accept_new_output_type(transform, output_id)?;
        return Ok(Output::FormatChanged);
    }

    result?;

    let sample = sample.ok_or(WindowsError::OutputGetFailed)?;

    Ok(Output::Sample(sample.into()))
}

struct MftIter {
    category: windows_core::GUID,
    input: MFT_REGISTER_TYPE_INFO,
    output: MFT_REGISTER_TYPE_INFO,
    flags: Vec<MFT_ENUM_FLAG>,
    current: Vec<IMFActivate>,
}
impl MftIter {
    fn new(
        category: windows_core::GUID,
        input: MFT_REGISTER_TYPE_INFO,
        output: MFT_REGISTER_TYPE_INFO,
    ) -> Self {
        Self {
            category,
            input,
            output,
            flags: vec![
                MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG_SYNCMFT,
                MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG_ASYNCMFT,
                MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG_HARDWARE,
            ],
            current: vec![],
        }
    }
}
impl Iterator for MftIter {
    type Item = IMFActivate;

    fn next(&mut self) -> Option<Self::Item> {
        if let Some(activate) = self.current.pop() {
            return Some(activate);
        }

        if let Some(flag) = self.flags.pop() {
            if let Ok(mut activates) = enum_mft(self.category, self.input, self.output, flag) {
                activates.reverse();
                self.current = activates;
            }
            return self.next();
        }

        None
    }
}

fn enum_mft(
    category: windows_core::GUID,
    input: MFT_REGISTER_TYPE_INFO,
    output: MFT_REGISTER_TYPE_INFO,
    flags: MFT_ENUM_FLAG,
) -> Result<Vec<IMFActivate>> {
    let mut activate: *mut Option<IMFActivate> = ptr::null_mut();
    let mut num_activate: u32 = 0;

    unsafe {
        MFTEnumEx(
            category,
            flags,
            Some(&input),
            Some(&output),
            &mut activate as *mut _,
            &mut num_activate,
        )?
    };

    let activates = if num_activate > 0 {
        let activates = unsafe {
            std::slice::from_raw_parts_mut(activate, num_activate as usize)
                .iter_mut()
                .filter_map(Option::take)
                .collect::<Vec<_>>()
        };
        activates
    } else {
        vec![]
    };

    if !activate.is_null() {
        unsafe { CoTaskMemFree(Some(activate as *const _)) };
    }

    Ok(activates)
}

pub struct Transform {
    pipeline: Pipeline,
    #[allow(dead_code)]
    input_type: UnsafeSend<IMFMediaType>,
    output_type: UnsafeSend<IMFMediaType>,
    errors: ErrorSlot,
}
enum Pipeline {
    Async {
        sample_tx: mpsc::Sender<UnsafeSend<IMFSample>>,
    },
    Sync {
        output_tx: mpsc::Sender<UnsafeSend<IMFSample>>,
        transform: UnsafeSend<IMFTransform>,
        input_id: u32,
        output_id: u32,
        output_info: MFT_OUTPUT_STREAM_INFO,
    },
}

impl Transform {
    pub fn new(
        category: windows_core::GUID,
        input: MFT_REGISTER_TYPE_INFO,
        output: MFT_REGISTER_TYPE_INFO,
        input_type: IMFMediaType,
        output_type: IMFMediaType,
        runtime: &impl Runtime,
    ) -> Result<(Self, mpsc::Receiver<UnsafeSend<IMFSample>>)> {
        let mfts = MftIter::new(category, input, output);

        let mut input_type = Some(input_type);
        let mut output_type = Some(output_type);

        let mut result = None;

        for activate in mfts {
            let name = Self::get_name(&activate)?;
            if let Some(_r) = &result {
                log::debug!("Skipping MFT: {name}");
                continue;
            }
            match Self::try_activate(activate, &mut input_type, &mut output_type, runtime) {
                Ok(r) => {
                    // Which encoder the machine picked is the first thing worth
                    // knowing when an export misbehaves on one machine only:
                    // hardware MFTs differ from each other and from software.
                    log::info!("Using MFT: {name}");
                    result = Some(r);
                }
                Err(err) => {
                    log::warn!("Failed to activate MFT {name}: {err:?}");
                }
            };
        }

        result.ok_or(WindowsError::NoSuitableMft)
    }

    fn get_name(activate: &IMFActivate) -> Result<String> {
        let mut length = unsafe { activate.GetStringLength(&MFT_FRIENDLY_NAME_Attribute)?} + 1 /* NULL termination */;
        let mut buffer: Vec<u16> = vec![0; length as usize];

        unsafe {
            activate.GetString(&MFT_FRIENDLY_NAME_Attribute, &mut buffer, Some(&mut length))?
        };

        let value: String = BSTR::from_wide(&buffer[..length as usize])
            .try_into()
            .map_err(|_| WindowsError::Utf16ToStringConversionFailed)?;
        Ok(value)
    }

    fn try_activate(
        activate: IMFActivate,
        input_type: &mut Option<IMFMediaType>,
        output_type: &mut Option<IMFMediaType>,
        runtime: &impl Runtime,
    ) -> Result<(Self, mpsc::Receiver<UnsafeSend<IMFSample>>)> {
        log::debug!("Trying MFT: {}", Self::get_name(&activate)?);

        let is_async = unsafe { activate.GetUINT32(&MF_TRANSFORM_ASYNC) }.unwrap_or(0) != 0;
        let transform = unsafe { activate.ActivateObject::<IMFTransform>()? };

        if is_async {
            let attributes = unsafe { transform.GetAttributes()? };
            unsafe { attributes.SetUINT32(&MF_TRANSFORM_ASYNC_UNLOCK, 1)? };
        }

        let mut input_streams = 0;
        let mut output_streams = 0;
        unsafe { transform.GetStreamCount(&mut input_streams, &mut output_streams)? };

        if input_streams != 1 || output_streams != 1 {
            return Err(WindowsError::InvalidStreamCount);
        }

        let mut input_ids = [0; 1];
        let mut output_ids = [0; 1];
        if let Err(err) = unsafe { transform.GetStreamIDs(&mut input_ids, &mut output_ids) } {
            if err.code() == E_NOTIMPL {
                // zero
            } else {
                Err(err)?;
            }
        }

        let input_id = input_ids[0];
        let output_id = output_ids[0];

        {
            let Some(input_type) = &input_type else {
                return Err(WindowsError::InputTypeNone);
            };

            let Some(output_type) = &output_type else {
                return Err(WindowsError::OutputTypeNone);
            };

            unsafe { transform.SetOutputType(output_id, output_type, 0)? };
            unsafe { transform.SetInputType(input_id, input_type, 0)? };
        }

        let mut input_info = MFT_INPUT_STREAM_INFO::default();

        unsafe { transform.GetInputStreamInfo(input_id, &mut input_info)? };
        let output_info = unsafe { transform.GetOutputStreamInfo(output_id)? };

        let (output_tx, output_rx) = mpsc::channel::<UnsafeSend<IMFSample>>(32);
        let errors = ErrorSlot::default();

        if is_async {
            let generator: UnsafeSend<IMFMediaEventGenerator> =
                transform.cast::<IMFMediaEventGenerator>()?.into();

            unsafe { transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0)? };

            let (sample_tx, sample_rx) = mpsc::channel::<UnsafeSend<IMFSample>>(32);

            let transform = UnsafeSend(transform);
            let loop_errors = errors.clone();

            runtime.spawn_ret(async move {
                let mut sample_rx = sample_rx;
                let mut output_info = output_info;
                let result: Result<()> = async {
                    loop {
                        match generator.get_event().await {
                            Ok(event) => {
                                let event_type: u32 = unsafe { event.GetType()? };
                                match MF_EVENT_TYPE(event_type as i32) {
                                    #[allow(non_upper_case_globals)]
                                    METransformNeedInput => {
                                        let Some(sample) = sample_rx.recv().await else {
                                            unsafe {
                                                transform.ProcessMessage(
                                                    MFT_MESSAGE_NOTIFY_END_OF_STREAM,
                                                    0,
                                                )?
                                            };
                                            unsafe {
                                                transform
                                                    .ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0)?
                                            };
                                            continue;
                                        };
                                        unsafe {
                                            transform.ProcessInput(input_id, &*sample, 0)?;
                                        };
                                    }
                                    #[allow(non_upper_case_globals)]
                                    METransformHaveOutput => {
                                        match process_output(&transform, &output_info, output_id)? {
                                            Output::Sample(data) => output_tx.send(data).await?,
                                            Output::FormatChanged => {
                                                // The new type can change the
                                                // buffer the MFT expects us to
                                                // supply, so the cached stream
                                                // info is stale from here on.
                                                output_info = unsafe {
                                                    transform.GetOutputStreamInfo(output_id)?
                                                };
                                            }
                                        }
                                    }
                                    #[allow(non_upper_case_globals)]
                                    METransformDrainComplete => {
                                        log::debug!("Transform drain complete");
                                        // end - generator and transform are dropped here
                                        break;
                                    }
                                    _ => {
                                        log::debug!("Unhandled media event type: {:?}", event_type);
                                    }
                                }
                            }
                            Err(e) => {
                                log::error!("Error receiving media event: {:?}", e);
                                break;
                            }
                        }
                    }
                    Ok(())
                }
                .await;

                // Leave the reason behind before the channels close: to everyone
                // else this loop's death looks like nothing but a closed channel.
                if let Err(e) = &result {
                    log::error!("Transform event loop failed: {:?}", e);
                    loop_errors.set(e.clone());
                }

                result
            });

            Ok((
                Self {
                    pipeline: Pipeline::Async { sample_tx },
                    input_type: UnsafeSend(input_type.take().ok_or(WindowsError::InputTypeNone)?),
                    output_type: UnsafeSend(
                        output_type.take().ok_or(WindowsError::OutputTypeNone)?,
                    ),
                    errors,
                },
                output_rx,
            ))
        } else {
            unsafe { transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0)? };

            Ok((
                Self {
                    pipeline: Pipeline::Sync {
                        output_tx,
                        transform: UnsafeSend(transform),
                        input_id,
                        output_id,
                        output_info,
                    },
                    input_type: UnsafeSend(input_type.take().ok_or(WindowsError::InputTypeNone)?),
                    output_type: UnsafeSend(
                        output_type.take().ok_or(WindowsError::OutputTypeNone)?,
                    ),
                    errors,
                },
                output_rx,
            ))
        }
    }

    /// The errors the transform's own background loop leaves behind, so a
    /// consumer that only sees a closed channel can still report the cause.
    pub fn errors(&self) -> ErrorSlot {
        self.errors.clone()
    }

    pub async fn push(&mut self, sample: UnsafeSend<IMFSample>) -> Result<()> {
        match &mut self.pipeline {
            Pipeline::Async { sample_tx } => sample_tx.send(sample).await.map_err(|e| {
                // The receiving loop is gone. Its own error, if it recorded one,
                // is the real failure; this send is only where it became visible.
                self.errors
                    .get_or(WindowsError::SampleSendFailed(e.to_string()))
            }),
            Pipeline::Sync {
                output_tx,
                transform,
                input_id,
                output_id,
                output_info,
            } => {
                unsafe { transform.ProcessInput(*input_id, &*sample, 0)? };
                loop {
                    match process_output(transform, output_info, *output_id) {
                        Ok(Output::Sample(data)) => {
                            output_tx.send(data).await?;
                            continue;
                        }
                        Ok(Output::FormatChanged) => {
                            *output_info = unsafe { transform.GetOutputStreamInfo(*output_id)? };
                            continue;
                        }
                        Err(WindowsError::Windows(err))
                            if err.code() == MF_E_TRANSFORM_NEED_MORE_INPUT =>
                        {
                            return Ok(());
                        }
                        // Anything else ends the call. Falling through to
                        // another iteration here would spin on the same failing
                        // ProcessOutput forever.
                        Err(err) => return Err(err),
                    }
                }
            }
        }
    }

    #[allow(dead_code)]
    pub fn input_type(&self) -> Result<&IMFMediaType> {
        Ok(&*self.input_type)
    }

    pub fn output_type(&self) -> Result<&IMFMediaType> {
        Ok(&*self.output_type)
    }
}

impl Drop for Transform {
    fn drop(&mut self) {
        if let Pipeline::Sync {
            output_tx,
            transform,
            input_id: _,
            output_id,
            output_info,
        } = &mut self.pipeline
        {
            unsafe {
                transform
                    .ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0)
                    .unwrap()
            };
            unsafe {
                transform
                    .ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0)
                    .unwrap()
            };

            let transform = UnsafeSend(transform.clone());
            let output_tx = output_tx.clone();
            let mut output_info = *output_info;
            let output_id = *output_id;

            loop {
                match process_output(&transform, &output_info, output_id) {
                    Ok(Output::Sample(data)) => {
                        let Ok(_) = output_tx.try_send(data) else {
                            return; // channel is already closed
                        };
                        continue;
                    }
                    Ok(Output::FormatChanged) => {
                        match unsafe { transform.GetOutputStreamInfo(output_id) } {
                            Ok(info) => output_info = info,
                            // Drop has nobody to return an error to.
                            Err(err) => panic!("{:?}", err),
                        }
                        continue;
                    }
                    Err(WindowsError::Windows(err))
                        if err.code() == MF_E_TRANSFORM_NEED_MORE_INPUT =>
                    {
                        return;
                    }
                    Err(err) => panic!("{:?}", err),
                }
            }
        }
    }
}
