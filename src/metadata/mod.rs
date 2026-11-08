pub mod flac;
pub mod id3v1;
pub mod id3v2;
pub mod mmap_reader;
pub mod mp3_duration;
pub mod mp4;
pub mod ogg;
pub mod wav;

use mmap_reader::MmapReader;
use std::path::Path;

#[derive(Debug, Clone, Default)]
pub struct AudioMetadata {
    pub title: String,
    pub artist: String,
    pub album: String,
    pub album_artist: String,
    pub track_number: u32,
    pub track_total: u32,
    pub disc_number: u32,
    pub disc_total: u32,
    pub year: u32,
    pub genre: String,
    pub comment: String,
    pub duration_ms: u64,
    pub artwork_data: Option<Vec<u8>>,
    pub artwork_mime_type: String,
}

pub fn read_metadata<P: AsRef<Path>>(path: P, read_artwork: bool) -> std::io::Result<AudioMetadata> {
    let path = path.as_ref();
    let reader = MmapReader::open(path)?;
    let data = reader.data();

    let mut meta = AudioMetadata::default();
    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();

    match ext.as_str() {
        "flac" => {
            flac::parse_flac(data, &mut meta, read_artwork);
        }
        "m4a" | "aac" => {
            mp4::parse_mp4(data, &mut meta, read_artwork);
        }
        "wav" => {
            wav::parse_wav(data, &mut meta, read_artwork);
        }
        "ogg" | "opus" => {
            ogg::parse_ogg(data, &mut meta);
        }
        _ => {
            // Default to ID3 (MP3, etc.)
            let has_id3v2 = id3v2::parse_id3v2(data, &mut meta, read_artwork);
            if !has_id3v2 {
                id3v1::parse_id3v1(data, &mut meta);
            }
            if meta.duration_ms == 0 {
                meta.duration_ms = mp3_duration::calculate_mp3_duration(data);
            }
        }
    }

    Ok(meta)
}

pub fn read_artwork_bytes<P: AsRef<Path>>(path: P) -> Option<Vec<u8>> {
    let path = path.as_ref();
    let reader = MmapReader::open(path).ok()?;
    let data = reader.data();

    let ext = path
        .extension()
        .and_then(|e| e.to_str())
        .unwrap_or("")
        .to_lowercase();

    let mut meta = AudioMetadata::default();
    match ext.as_str() {
        "flac" => {
            flac::parse_flac(data, &mut meta, true);
        }
        "m4a" | "aac" => {
            mp4::parse_mp4(data, &mut meta, true);
        }
        "wav" => {
            wav::parse_wav(data, &mut meta, true);
        }
        _ => {
            id3v2::parse_id3v2(data, &mut meta, true);
        }
    }

    meta.artwork_data
}

