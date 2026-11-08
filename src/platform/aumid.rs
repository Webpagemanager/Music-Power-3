use std::path::PathBuf;
use windows::core::w;
use windows::Win32::System::Registry::{
    RegCreateKeyExW, RegSetValueExW, HKEY, HKEY_CURRENT_USER, KEY_SET_VALUE, REG_OPTION_NON_VOLATILE, REG_SZ,
};
use windows::Win32::UI::Shell::SetCurrentProcessExplicitAppUserModelID;

pub fn set_app_user_model_id() {
    unsafe {
        // 1. Set explicit process AUMID for Windows 11 taskbar, volume flyouts, and SMTC
        let _ = SetCurrentProcessExplicitAppUserModelID(w!("MusicPower3"));

        // 2. Register AUMID and Icon in HKCU\Software\Classes\AppUserModelId\MusicPower3
        // so that Windows Shell taskbar and window manager associate the custom icon with the app.
        register_shell_icon();
    }
}

fn register_shell_icon() {
    let icon_path = find_icon_path();
    if let Some(path) = icon_path {
        unsafe {
            let subkey = w!("Software\\Classes\\AppUserModelId\\MusicPower3");
            let mut hkey = HKEY::default();
            if RegCreateKeyExW(
                HKEY_CURRENT_USER,
                subkey,
                None,
                None,
                REG_OPTION_NON_VOLATILE,
                KEY_SET_VALUE,
                None,
                &mut hkey,
                None,
            )
            .is_ok()
            {
                // Set DisplayName = "Music Power 3"
                let name: Vec<u16> = "Music Power 3\0".encode_utf16().collect();
                let _ = RegSetValueExW(
                    hkey,
                    w!("DisplayName"),
                    None,
                    REG_SZ,
                    Some(std::slice::from_raw_parts(
                        name.as_ptr() as *const u8,
                        name.len() * 2,
                    )),
                );

                // Set IconUri = absolute path to icon.ico
                let icon_str = format!("{}\0", path.to_string_lossy());
                let icon_utf16: Vec<u16> = icon_str.encode_utf16().collect();
                let _ = RegSetValueExW(
                    hkey,
                    w!("IconUri"),
                    None,
                    REG_SZ,
                    Some(std::slice::from_raw_parts(
                        icon_utf16.as_ptr() as *const u8,
                        icon_utf16.len() * 2,
                    )),
                );
            }
        }
    }
}

fn find_icon_path() -> Option<PathBuf> {
    if let Ok(exe_path) = std::env::current_exe() {
        if let Some(parent) = exe_path.parent() {
            let candidate = parent.join("Assets").join("icon.ico");
            if candidate.exists() {
                return Some(candidate);
            }
        }
    }

    let candidate = PathBuf::from("Assets/icon.ico");
    if candidate.exists() {
        if let Ok(canon) = std::fs::canonicalize(&candidate) {
            return Some(canon);
        }
    }

    let favicon = PathBuf::from("favicon.ico");
    if favicon.exists() {
        if let Ok(canon) = std::fs::canonicalize(&favicon) {
            return Some(canon);
        }
    }

    None
}
