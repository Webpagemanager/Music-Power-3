use crate::models::Track;
use std::fs;
use std::io;
use std::path::PathBuf;

pub struct LibraryCache;

impl LibraryCache {
    pub fn cache_file_path() -> PathBuf {
        let mut path = dirs_appdata();
        path.push("MusicPower3");
        let _ = fs::create_dir_all(&path);
        path.push("library_cache.bin");
        path
    }

    pub fn save(tracks: &[Track]) -> io::Result<()> {
        let path = Self::cache_file_path();
        let track_vec = tracks.to_vec();
        // Serialise using rkyv for zero-copy deserialisation
        let bytes = rkyv::to_bytes::<rkyv::rancor::Error>(&track_vec)
            .map_err(|e| io::Error::new(io::ErrorKind::Other, e.to_string()))?;

        let tmp_path = path.with_extension("tmp");
        fs::write(&tmp_path, bytes.as_slice())?;
        fs::rename(tmp_path, path)?;
        Ok(())
    }

    pub fn load() -> io::Result<Vec<Track>> {
        let path = Self::cache_file_path();
        if !path.exists() {
            return Ok(Vec::new());
        }

        let bytes = fs::read(&path)?;
        let tracks: Vec<Track> = rkyv::from_bytes::<Vec<Track>, rkyv::rancor::Error>(&bytes)
            .map_err(|e| io::Error::new(io::ErrorKind::Other, e.to_string()))?;

        Ok(tracks)
    }
}

fn dirs_appdata() -> PathBuf {
    if let Ok(appdata) = std::env::var("APPDATA") {
        PathBuf::from(appdata)
    } else {
        std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."))
    }
}
