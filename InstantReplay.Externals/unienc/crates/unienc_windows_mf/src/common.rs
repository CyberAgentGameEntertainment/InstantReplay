use std::collections::HashMap;
use std::{fmt::Display, ops::Deref};

use crate::error::Result;
use bincode::{BorrowDecode, Decode, Encode};
use windows::core::GUID;
use windows::core::{Interface, BSTR};
use windows::Win32::Media::MediaFoundation::*;
use crate::WindowsError;

#[derive(Debug)]

pub struct UnsafeSend<T>(pub T);

unsafe impl<T> Send for UnsafeSend<T> {}
unsafe impl<T> Sync for UnsafeSend<T> {}

impl<T> Deref for UnsafeSend<T> {
    type Target = T;

    fn deref(&self) -> &Self::Target {
        &self.0
    }
}

impl<T> From<T> for UnsafeSend<T> {
    fn from(inner: T) -> Self {
        Self(inner)
    }
}

impl<T> Clone for UnsafeSend<T>
where
    T: Clone,
{
    fn clone(&self) -> Self {
        Self(self.0.clone())
    }
}

impl<T> Display for UnsafeSend<T>
where
    T: std::fmt::Debug,
{
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        self.0.fmt(f)
    }
}

#[derive(Clone)]
pub enum Payload {
    Sample(UnsafeSend<IMFSample>),
    Format(UnsafeSend<IMFMediaType>),
}

impl std::fmt::Debug for Payload {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let serializable: SerializablePayload = self
            .try_into()
            .map_err(|e: crate::error::WindowsError| bincode::error::EncodeError::OtherString(e.to_string())).unwrap();
        serializable.fmt(f)
    }
}

impl Encode for Payload {
    fn encode<E: bincode::enc::Encoder>(
        &self,
        encoder: &mut E,
    ) -> std::result::Result<(), bincode::error::EncodeError> {
        let serializable: SerializablePayload = self
            .try_into()
            .map_err(|e: crate::error::WindowsError| bincode::error::EncodeError::OtherString(e.to_string()))?;
        serializable.encode(encoder)
    }
}

impl<Context> Decode<Context> for Payload {
    fn decode<D: bincode::de::Decoder<Context = Context>>(
        decoder: &mut D,
    ) -> std::result::Result<Self, bincode::error::DecodeError> {
        let serializable = &SerializablePayload::decode(decoder)?;
        serializable
            .try_into()
            .map_err(|e: crate::error::WindowsError| bincode::error::DecodeError::OtherString(e.to_string()))
    }
}

impl<'de, Context> BorrowDecode<'de, Context> for Payload {
    fn borrow_decode<D: bincode::de::BorrowDecoder<'de, Context = Context>>(
        decoder: &mut D,
    ) -> std::result::Result<Self, bincode::error::DecodeError> {
        let serializable = &SerializablePayload::borrow_decode(decoder)?;
        serializable
            .try_into()
            .map_err(|e: crate::error::WindowsError| bincode::error::DecodeError::OtherString(e.to_string()))
    }
}

#[derive(Encode, Decode, Debug)]
enum SerializablePayload {
    Sample(SerializableMFSample),
    Format(SerializableMFAttributes),
}

impl TryFrom<&Payload> for SerializablePayload {
    type Error = crate::error::WindowsError;

    fn try_from(value: &Payload) -> std::result::Result<Self, Self::Error> {
        match value {
            Payload::Sample(sample) => Ok(SerializablePayload::Sample((&**sample).try_into()?)),
            Payload::Format(media_type) => Ok(SerializablePayload::Format(
                (&(**media_type).cast::<IMFAttributes>()?).try_into()?,
            )),
        }
    }
}

impl TryFrom<&SerializablePayload> for Payload {
    type Error = crate::error::WindowsError;

    fn try_from(value: &SerializablePayload) -> std::result::Result<Self, Self::Error> {
        match value {
            SerializablePayload::Sample(sample) => {
                Ok(Payload::Sample(UnsafeSend(sample.try_into()?)))
            }
            SerializablePayload::Format(attributes) => {
                let media_type = unsafe { MFCreateMediaType()? };
                attributes.apply(&media_type)?;

                Ok(Payload::Format(UnsafeSend(media_type)))
            }
        }
    }
}

#[derive(Encode, Decode, Debug)]
struct SerializableMFSample {
    attributes: SerializableMFAttributes,
    buffers: Vec<Vec<u8>>,
    time: i64,
    duration: i64,
    flags: u32,
}

#[derive(Encode, Decode, Debug)]
struct SerializableMFAttributes {
    attributes: HashMap<Guid, AttributeValue>,
}

#[repr(transparent)]
#[derive(Encode, Decode, PartialEq, Eq, PartialOrd, Ord, Hash)]
struct Guid(u128);

impl From<u128> for Guid {
    fn from(value: u128) -> Self {
        Guid(value)
    }
}

impl From<Guid> for u128 {
    fn from(value: Guid) -> Self {
        value.0
    }
}

impl std::fmt::Debug for Guid {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Guid({:x})", self.0)
    }
}

#[derive(Encode, Decode, Debug)]
enum AttributeValue {
    UInt32(u32),
    UInt64(u64),
    Double(f64),
    Guid(u128),
    String(String),
    Blob(Vec<u8>),
    IUnknown,
}

impl TryFrom<&IMFSample> for SerializableMFSample {
    type Error = crate::error::WindowsError;

    fn try_from(value: &IMFSample) -> std::result::Result<Self, Self::Error> {
        let attributes: SerializableMFAttributes = (&value.cast::<IMFAttributes>()?).try_into()?;
        let time = unsafe { value.GetSampleTime()? };
        let duration = unsafe { value.GetSampleDuration()? };
        let flags = unsafe { value.GetSampleFlags()? };
        let count = unsafe { value.GetBufferCount()? };
        let mut buffers = Vec::<Vec<u8>>::with_capacity(count as usize);
        for index in 0..count {
            let buffer = unsafe { value.GetBufferByIndex(index)? };
            let mut ptr: *mut u8 = std::ptr::null_mut();
            let mut length: u32 = 0;
            unsafe { buffer.Lock(&mut ptr, None, Some(&mut length))? };

            let data = unsafe { std::slice::from_raw_parts(ptr, length as usize) }.to_vec();
            unsafe { buffer.Unlock()? };
            buffers.push(data);
        }
        Ok(Self {
            attributes,
            buffers,
            time,
            duration,
            flags,
        })
    }
}

impl TryInto<IMFSample> for &SerializableMFSample {
    type Error = crate::error::WindowsError;

    fn try_into(self) -> std::result::Result<IMFSample, Self::Error> {
        let sample = unsafe { MFCreateSample()? };
        self.attributes
            .apply(&sample.cast::<IMFAttributes>()?)?;
        unsafe { sample.SetSampleTime(self.time)? };
        unsafe { sample.SetSampleDuration(self.duration)? };
        unsafe { sample.SetSampleFlags(self.flags)? };
        for data in &self.buffers {
            let buffer = unsafe { MFCreateMemoryBuffer(data.len() as u32)? };
            unsafe { buffer.SetCurrentLength(data.len() as u32)? };
            let mut ptr: *mut u8 = std::ptr::null_mut();
            let mut length: u32 = 0;
            unsafe { buffer.Lock(&mut ptr, None, Some(&mut length))? };

            unsafe { std::slice::from_raw_parts_mut(ptr, length as usize) }.copy_from_slice(data);
            unsafe { buffer.Unlock()? };
            unsafe { sample.AddBuffer(&buffer)? };
        }

        Ok(sample)
    }
}

impl TryFrom<&IMFAttributes> for SerializableMFAttributes {
    type Error = crate::error::WindowsError;

    fn try_from(from: &IMFAttributes) -> std::result::Result<Self, Self::Error> {
        let count = unsafe { from.GetCount()? };
        let mut map = HashMap::<Guid, AttributeValue>::new();
        for i in 0..count {
            let mut guid: GUID = GUID::default();

            unsafe { from.GetItemByIndex(i, &mut guid, None)? };

            let value = match unsafe { from.GetItemType(&guid)? } {
                MF_ATTRIBUTE_UINT32 => AttributeValue::UInt32(unsafe { from.GetUINT32(&guid)? }),
                MF_ATTRIBUTE_UINT64 => AttributeValue::UInt64(unsafe { from.GetUINT64(&guid)? }),
                MF_ATTRIBUTE_DOUBLE => AttributeValue::Double(unsafe { from.GetDouble(&guid)? }),
                MF_ATTRIBUTE_GUID => {
                    AttributeValue::Guid(unsafe { from.GetGUID(&guid)? }.to_u128())
                }
                MF_ATTRIBUTE_STRING => {
                    let mut length = unsafe { from.GetStringLength(&guid)?} + 1 /* NULL termination */;
                    let mut buffer: Vec<u16> = vec![0; length as usize];

                    unsafe { from.GetString(&guid, &mut buffer, Some(&mut length))? };

                    let value: String = BSTR::from_wide(&buffer[..length as usize]).try_into().map_err(|_| WindowsError::Utf16ToStringConversionFailed)?;
                    AttributeValue::String(value)
                }
                MF_ATTRIBUTE_BLOB => {
                    let mut length = unsafe { from.GetBlobSize(&guid)? };
                    let mut buffer = vec![0; length as usize];
                    unsafe { from.GetBlob(&guid, &mut buffer, Some(&mut length))? };
                    AttributeValue::Blob(buffer)
                }
                MF_ATTRIBUTE_IUNKNOWN => AttributeValue::IUnknown,
                _ => {
                    todo!()
                }
            };

            map.insert(guid.to_u128().into(), value);
        }

        Ok(Self { attributes: map })
    }
}

impl SerializableMFAttributes {
    pub fn apply(&self, target: &IMFAttributes) -> std::result::Result<(), crate::error::WindowsError> {
        for (guid, value) in &self.attributes {
            let guid = GUID::from_u128(guid.0);
            match value {
                AttributeValue::UInt32(v) => unsafe { target.SetUINT32(&guid, *v)? },
                AttributeValue::UInt64(v) => unsafe { target.SetUINT64(&guid, *v)? },
                AttributeValue::Double(v) => unsafe { target.SetDouble(&guid, *v)? },
                AttributeValue::Guid(v) => unsafe { target.SetGUID(&guid, &GUID::from_u128(*v))? },
                AttributeValue::String(v) => {
                    let value = BSTR::from(v);
                    unsafe { target.SetString(&guid, &value)? }
                }
                AttributeValue::Blob(v) => unsafe { target.SetBlob(&guid, v)? },
                AttributeValue::IUnknown => {}
            };
        }
        Ok(())
    }
}
