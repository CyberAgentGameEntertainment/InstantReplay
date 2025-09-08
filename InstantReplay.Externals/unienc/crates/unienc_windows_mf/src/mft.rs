use tokio::sync::{mpsc, oneshot};
use windows::Win32::Media::MediaFoundation::{IMFSample, IMFTransform, MFT_OUTPUT_STREAM_INFO};

use crate::common::UnsafeSend;
use anyhow::{anyhow, Context, Result};
use std::cell::Cell;
use std::future::Future;
use std::mem::ManuallyDrop;
use std::ptr;
use windows::core::*;
use windows::Win32::Foundation::E_NOTIMPL;
use windows::Win32::Media::MediaFoundation::*;

pub trait MediaEventGeneratorCustom {
    fn get_event(&self) -> impl Future<Output = Result<UnsafeSend<IMFMediaEvent>>>;
    fn get_events(&self) -> mpsc::Receiver<Result<UnsafeSend<IMFMediaEvent>>>;
}

impl MediaEventGeneratorCustom for IMFMediaEventGenerator {
    fn get_event(&self) -> impl Future<Output = Result<UnsafeSend<IMFMediaEvent>>> {
        let (tx, rx) = oneshot::channel::<Result<UnsafeSend<IMFMediaEvent>>>();

        let result: std::result::Result<(), Error> = {
            let generator = UnsafeSend(self.clone());
            let callback: IMFAsyncCallback = AsyncCallback::new(move |result| {
                tx.send(unsafe {
                    generator
                        .EndGetEvent(result.unwrap())
                        .context("Failed to get media event")
                        .map(UnsafeSend::<IMFMediaEvent>::from)
                })
                .unwrap();
            })
            .into();

            unsafe { self.BeginGetEvent(&callback, Option::<&IUnknown>::None) }
        };

        async move {
            result?;
            match rx.await {
                Ok(event) => event,
                Err(_) => Err(anyhow!("Failed to receive media event")),
            }
        }
    }

    fn get_events(&self) -> mpsc::Receiver<Result<UnsafeSend<IMFMediaEvent>>> {
        let (tx, rx) = mpsc::channel::<Result<UnsafeSend<IMFMediaEvent>>>(32);

        let generator: UnsafeSend<IMFMediaEventGenerator> = UnsafeSend((*self).clone());

        tokio::spawn(async move {
            loop {
                match generator.get_event().await {
                    Ok(event) => {
                        if tx.send(Ok(event)).await.is_err() {
                            break; // Receiver dropped
                        }
                    }
                    Err(e) => {
                        let _ = tx.send(Err(e)).await;
                        break;
                    }
                }
            }
        });

        rx
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
        /*
         */
        Ok(())
    }
}

fn process_output(
    transform: &IMFTransform,
    output_info: &MFT_OUTPUT_STREAM_INFO,
    output_id: u32,
) -> Result<UnsafeSend<IMFSample>> {
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
    unsafe { transform.ProcessOutput(0, &mut buffers, &mut status) }?;

    let buffer = &mut buffers[0];

    let sample = buffer.pSample.take().context("Failed to get output")?;

    Ok(sample.into())
}

fn enum_mft(
    category: windows_core::GUID,
    input: MFT_REGISTER_TYPE_INFO,
    output: MFT_REGISTER_TYPE_INFO,
    flags: MFT_ENUM_FLAG,
) -> Result<Option<IMFActivate>> {
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

    if num_activate == 0 {
        return Ok(None);
    }

    Ok(unsafe { (*activate).take() })
}

pub struct Transform {
    pipeline: Pipeline,
    #[allow(dead_code)]
    input_type: UnsafeSend<IMFMediaType>,
    output_type: UnsafeSend<IMFMediaType>,
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
        input_type: impl FnOnce() -> Result<IMFMediaType>,
        output_type: impl FnOnce() -> Result<IMFMediaType>,
    ) -> Result<(Self, mpsc::Receiver<UnsafeSend<IMFSample>>)> {
        let (activate, is_async) = {
            if let Some(activate_hardware) = enum_mft(
                category,
                input,
                output,
                MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG_HARDWARE,
            )? {
                (activate_hardware, true)
            } else if let Some(activate_async) = enum_mft(
                category,
                input,
                output,
                MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG_ASYNCMFT,
            )? {
                (activate_async, true)
            } else if let Some(activate_sync) = enum_mft(
                category,
                input,
                output,
                MFT_ENUM_FLAG_SORTANDFILTER | MFT_ENUM_FLAG_SYNCMFT,
            )? {
                (activate_sync, false)
            } else {
                return Err(anyhow!("No suitable video encoder found"));
            }
        };

        let transform = unsafe { activate.ActivateObject::<IMFTransform>()? };

        if is_async {
            let attributes = unsafe { transform.GetAttributes()? };
            unsafe { attributes.SetUINT32(&MF_TRANSFORM_ASYNC_UNLOCK, 1)? };
        }

        let mut input_streams = 0;
        let mut output_streams = 0;
        unsafe { transform.GetStreamCount(&mut input_streams, &mut output_streams)? };

        if input_streams != 1 || output_streams != 1 {
            return Err(anyhow!(
                "Expected 1 input and 1 output stream for video encoder"
            ));
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

        let input_type = input_type()?;
        let output_type = output_type()?;

        unsafe { transform.SetOutputType(output_id, &output_type, 0)? };
        unsafe { transform.SetInputType(input_id, &input_type, 0)? };

        let mut input_info = MFT_INPUT_STREAM_INFO::default();

        unsafe { transform.GetInputStreamInfo(input_id, &mut input_info)? };
        let output_info = unsafe { transform.GetOutputStreamInfo(output_id)? };

        let (output_tx, output_rx) = mpsc::channel::<UnsafeSend<IMFSample>>(32);

        if is_async {
            let generator: UnsafeSend<IMFMediaEventGenerator> =
                transform.cast::<IMFMediaEventGenerator>()?.into();

            let mut rx = generator.get_events();

            unsafe { transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0)? };

            let (sample_tx, sample_rx) = mpsc::channel::<UnsafeSend<IMFSample>>(32);

            let transform = UnsafeSend(transform);

            // event loop
            tokio::spawn(async move {
                let mut sample_rx = sample_rx;
                while let Some(event) = rx.recv().await {
                    match event {
                        Ok(event) => {
                            let event_type: u32 = unsafe { event.GetType()? };
                            match MF_EVENT_TYPE(event_type as i32) {
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
                                METransformHaveOutput => {
                                    let data = process_output(&transform, &output_info, output_id)?;
                                    output_tx.send(data).await?;
                                }
                                METransformDrainComplete => {
                                    println!("Transform drain complete");
                                    // end
                                    break;
                                }
                                _ => {
                                    println!("Unhandled media event type: {:?}", event_type);
                                }
                            }
                        }
                        Err(e) => {
                            println!("Error receiving media event: {:?}", e);
                            break;
                        }
                    }
                }
                Result::<()>::Ok(())
            });

            Ok((
                Self {
                    pipeline: Pipeline::Async { sample_tx },
                    input_type: UnsafeSend(input_type),
                    output_type: UnsafeSend(output_type),
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
                    input_type: UnsafeSend(input_type),
                    output_type: UnsafeSend(output_type),
                },
                output_rx,
            ))
        }
    }

    pub async fn push(&mut self, sample: UnsafeSend<IMFSample>) -> Result<()> {
        match &mut self.pipeline {
            Pipeline::Async { sample_tx } => sample_tx
                .send(sample)
                .await
                .map_err(|e| anyhow!("Failed to send video sample: {}", e)),
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
                        Ok(data) => {
                            output_tx.send(data).await?;
                            continue;
                        }
                        Err(err) => {
                            if let Ok(err) = err.downcast::<windows_core::Error>() {
                                if err.code() == MF_E_TRANSFORM_NEED_MORE_INPUT {
                                    return Ok(());
                                } else {
                                    return Err(err.into());
                                }
                            }
                        }
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
            let output_info = *output_info;
            let output_id = *output_id;

            loop {
                match process_output(&transform, &output_info, output_id) {
                    Ok(data) => {
                        let Ok(_) = output_tx.try_send(data) else {
                            return; // channel is already closed
                        };
                        continue;
                    }
                    Err(err) => {
                        if let Ok(err) = err.downcast::<windows_core::Error>() {
                            if err.code() == MF_E_TRANSFORM_NEED_MORE_INPUT {
                                return;
                            } else {
                                panic!("{:?}", err)
                            }
                        }
                    }
                }
            }
        }
    }
}
