use rkyv::{Archive, Deserialize as RkyvDeserialize, Serialize as RkyvSerialize};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize, Archive, RkyvSerialize, RkyvDeserialize)]
pub struct Track {
    pub file_path: String,
    pub title: String,
    pub artist: String,
    pub album: String,
    pub album_artist: String,
    pub duration_ms: u64,
    pub track_number: u32,
    pub disc_number: u32,
    pub year: u32,
    pub genre: String,
    pub date_added: u64,
    pub date_modified: u64,
    pub has_artwork: bool,
}

impl Track {
    pub fn new(file_path: String) -> Self {
        Self {
            file_path,
            title: String::new(),
            artist: String::new(),
            album: String::new(),
            album_artist: String::new(),
            duration_ms: 0,
            track_number: 0,
            disc_number: 0,
            year: 0,
            genre: String::new(),
            date_added: 0,
            date_modified: 0,
            has_artwork: false,
        }
    }

    pub fn display_duration(&self) -> String {
        let total_seconds = self.duration_ms / 1000;
        let minutes = total_seconds / 60;
        let seconds = total_seconds % 60;
        format!("{}:{:02}", minutes, seconds)
    }

    pub fn display_title(&self) -> &str {
        if !self.title.is_empty() {
            &self.title
        } else {
            std::path::Path::new(&self.file_path)
                .file_stem()
                .and_then(|s| s.to_str())
                .unwrap_or("Unknown Title")
        }
    }

    pub fn display_artist(&self) -> &str {
        if !self.artist.is_empty() {
            &self.artist
        } else {
            "Unknown Artist"
        }
    }

    pub fn display_album(&self) -> &str {
        if !self.album.is_empty() {
            &self.album
        } else {
            "Unknown Album"
        }
    }
}
