use serde::{Deserialize, Serialize};
use std::fs;
use std::path::PathBuf;

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AppSettings {
    pub global_scale: f64,
    pub volume: u32,
    pub show_artwork_panel: bool,
    pub repeat_mode: u32,
    pub is_shuffle: bool,
    pub shuffle_memory_size: u32,
    pub last_library_path: String,
    pub accent_color_hex: String,
    pub use_system_accent_color: bool,
    pub enable_online_metadata: bool,
    pub library_folders: Vec<String>,
}

impl Default for AppSettings {
    fn default() -> Self {
        Self {
            global_scale: 1.0,
            volume: 100,
            show_artwork_panel: true,
            repeat_mode: 0,
            is_shuffle: false,
            shuffle_memory_size: 50,
            last_library_path: String::new(),
            accent_color_hex: String::new(),
            use_system_accent_color: true,
            enable_online_metadata: true,
            library_folders: Vec::new(),
        }
    }
}

impl AppSettings {
    pub fn settings_file_path() -> PathBuf {
        let mut path = dirs_appdata();
        path.push("MusicPower3");
        let _ = fs::create_dir_all(&path);
        path.push("settings.json");
        path
    }

    pub fn load() -> Self {
        let path = Self::settings_file_path();
        if path.exists() {
            if let Ok(content) = fs::read_to_string(&path) {
                if let Ok(settings) = serde_json::from_str::<AppSettings>(&content) {
                    return settings;
                }
            }
        }
        Self::default()
    }

    pub fn save(&self) -> Result<(), std::io::Error> {
        let path = Self::settings_file_path();
        let content = serde_json::to_string_pretty(self)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::Other, e))?;

        // Atomic write via temporary sibling file
        let tmp_path = path.with_extension("tmp");
        fs::write(&tmp_path, content)?;
        fs::rename(tmp_path, path)?;
        Ok(())
    }
}

fn dirs_appdata() -> PathBuf {
    if let Ok(appdata) = std::env::var("APPDATA") {
        PathBuf::from(appdata)
    } else {
        std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."))
    }
}

