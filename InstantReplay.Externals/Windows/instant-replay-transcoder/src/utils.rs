// --------------------------------------------------------------
// Copyright 2025 CyberAgent, Inc.
// --------------------------------------------------------------

use std::error::Error;

use windows::Win32::{Foundation::S_FALSE, Media::MediaFoundation::*, System::Com::*};

pub struct MediaFoundationLifetime {
    _com_lifetime: Option<ComLifetime>,
}

struct ComLifetime;

impl ComLifetime {
    pub fn new() -> Result<Option<ComLifetime>, Box<dyn Error>> {
        unsafe {
            let hr = CoInitializeEx(None, COINIT_MULTITHREADED);
            hr.map(|| {
                Ok(if hr == S_FALSE {
                    None // COM was already initialized
                } else {
                    Some(ComLifetime {})
                })
            })?
        }
    }
}

impl Drop for ComLifetime {
    fn drop(&mut self) {
        unsafe {
            CoUninitialize();
        }
    }
}

impl MediaFoundationLifetime {
    pub fn new() -> Result<MediaFoundationLifetime, Box<dyn Error>> {
        unsafe {
            let com = ComLifetime::new()?;
            MFStartup(MF_VERSION, MFSTARTUP_FULL)?;

            Ok(MediaFoundationLifetime { _com_lifetime: com })
        }
    }
}

impl Drop for MediaFoundationLifetime {
    fn drop(&mut self) {
        unsafe {
            MFShutdown().expect("Failed to shutdown Media Foundation");
        }
    }
}
