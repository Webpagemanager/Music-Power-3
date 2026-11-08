use windows::core::w;
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::System::Threading::CreateMutexW;

pub struct SingleInstanceGuard {
    handle: HANDLE,
    is_primary: bool,
}

impl SingleInstanceGuard {
    pub fn try_acquire(name: &str) -> Option<Self> {
        let _ = name; // Guard name
        unsafe {
            let handle = CreateMutexW(None, true, w!("Local\\MusicPower3_Instance_Mutex")).ok()?;
            let last_error = windows::Win32::Foundation::GetLastError();
            if last_error == windows::Win32::Foundation::ERROR_ALREADY_EXISTS {
                let _ = CloseHandle(handle);
                None
            } else {
                Some(Self {
                    handle,
                    is_primary: true,
                })
            }
        }
    }

    pub fn is_primary(&self) -> bool {
        self.is_primary
    }
}

impl Drop for SingleInstanceGuard {
    fn drop(&mut self) {
        if !self.handle.is_invalid() {
            unsafe {
                let _ = CloseHandle(self.handle);
            }
        }
    }
}

