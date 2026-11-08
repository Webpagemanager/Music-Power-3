use crate::metadata;
use crate::models::Track;
use crossbeam_channel::{bounded, Receiver, Sender};
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::UNIX_EPOCH;

pub struct LibraryScanner;

impl LibraryScanner {
    pub const SUPPORTED_EXTENSIONS: &'static [&'static str] =
        &["mp3", "flac", "wav", "m4a", "aac", "ogg", "wma", "opus"];

    pub fn is_supported(path: &Path) -> bool {
        path.extension()
            .and_then(|e| e.to_str())
            .map(|ext| {
                let lower = ext.to_lowercase();
                Self::SUPPORTED_EXTENSIONS.contains(&lower.as_str())
            })
            .unwrap_or(false)
    }

    /// Spawns a storage-gentle background scanner.
    /// - Only reads directory metadata first (timestamp check).
    /// - Caps concurrent disk reads to 2 worker threads.
    /// - Emits discovered/updated tracks in batches over the returned receiver.
    pub fn scan_directories(
        directories: Vec<PathBuf>,
        cached_tracks: &[Track],
        cancel_token: Arc<AtomicBool>,
    ) -> Receiver<Vec<Track>> {
        let (batch_tx, batch_rx) = bounded::<Vec<Track>>(16);

        // Build timestamp lookup for cached tracks to bypass unchanged files
        let mut cache_map: HashMap<String, Track> = HashMap::with_capacity(cached_tracks.len());
        for track in cached_tracks {
            cache_map.insert(track.file_path.clone(), track.clone());
        }

        thread::spawn(move || {
            let (file_tx, file_rx) = bounded::<PathBuf>(1024);

            // Channel for parsed tracks from the 2 workers
            let (track_tx, track_rx) = bounded::<Track>(512);

            // Spawn 2 bounded worker threads for metadata parsing
            let mut worker_handles = Vec::with_capacity(2);
            for _ in 0..2 {
                let rx = file_rx.clone();
                let tx = track_tx.clone();
                let cancel = cancel_token.clone();

                let handle = thread::spawn(move || {
                    while let Ok(path) = rx.recv() {
                        if cancel.load(Ordering::Relaxed) {
                            break;
                        }
                        if let Ok(track) = Self::parse_file(&path) {
                            let _ = tx.send(track);
                        }
                    }
                });
                worker_handles.push(handle);
            }
            drop(track_tx); // Close original sender so track_rx knows when workers finish

            // Thread to batch tracks and send to the UI receiver
            let cancel_agg = cancel_token.clone();
            let batch_tx_agg = batch_tx.clone();
            let aggregator_handle = thread::spawn(move || {
                let mut batch = Vec::with_capacity(100);
                while let Ok(track) = track_rx.recv() {
                    if cancel_agg.load(Ordering::Relaxed) {
                        break;
                    }
                    batch.push(track);
                    if batch.len() >= 100 {
                        let _ = batch_tx_agg
                            .send(std::mem::replace(&mut batch, Vec::with_capacity(100)));
                    }
                }
                if !batch.is_empty() {
                    let _ = batch_tx_agg.send(batch);
                }
            });

            // Directory walker (finds candidate audio files)
            for dir in directories {
                if cancel_token.load(Ordering::Relaxed) {
                    break;
                }
                Self::walk_directory(&dir, &cache_map, &file_tx, &batch_tx, &cancel_token);
            }

            drop(file_tx); // Signal workers to complete

            for handle in worker_handles {
                let _ = handle.join();
            }
            let _ = aggregator_handle.join();
        });

        batch_rx
    }

    fn walk_directory(
        dir: &Path,
        cache: &HashMap<String, Track>,
        file_tx: &Sender<PathBuf>,
        batch_tx: &Sender<Vec<Track>>,
        cancel: &Arc<AtomicBool>,
    ) {
        let entries = match fs::read_dir(dir) {
            Ok(e) => e,
            Err(_) => return,
        };

        let mut cached_matches = Vec::new();

        for entry in entries.flatten() {
            if cancel.load(Ordering::Relaxed) {
                return;
            }

            let path = entry.path();
            if path.is_dir() {
                Self::walk_directory(&path, cache, file_tx, batch_tx, cancel);
            } else if Self::is_supported(&path) {
                let path_str = path.to_string_lossy().to_string();

                // Timestamp check
                let file_mtime = entry
                    .metadata()
                    .ok()
                    .and_then(|m| m.modified().ok())
                    .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
                    .map(|d| d.as_secs())
                    .unwrap_or(0);

                if let Some(cached) = cache.get(&path_str) {
                    if cached.date_modified == file_mtime && file_mtime > 0 {
                        // File unmodified: directly use cached version without opening file
                        cached_matches.push(cached.clone());
                        if cached_matches.len() >= 100 {
                            let _ = batch_tx.send(std::mem::replace(
                                &mut cached_matches,
                                Vec::with_capacity(100),
                            ));
                        }
                        continue;
                    }
                }

                // File is new or modified: queue for metadata worker
                let _ = file_tx.send(path);
            }
        }

        if !cached_matches.is_empty() {
            let _ = batch_tx.send(cached_matches);
        }
    }

    fn parse_file(path: &Path) -> std::io::Result<Track> {
        let meta = metadata::read_metadata(path, false)?;
        let path_str = path.to_string_lossy().to_string();

        let (date_added, date_modified) = fs::metadata(path)
            .and_then(|m| {
                let mtime = m
                    .modified()
                    .ok()
                    .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
                    .map(|d| d.as_secs())
                    .unwrap_or(0);
                let ctime = m
                    .created()
                    .ok()
                    .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
                    .map(|d| d.as_secs())
                    .unwrap_or(mtime);
                Ok((ctime, mtime))
            })
            .unwrap_or((0, 0));

        let mut track = Track::new(path_str);
        track.title = meta.title;
        track.artist = meta.artist;
        track.album = meta.album;
        track.album_artist = meta.album_artist;
        track.track_number = meta.track_number;
        track.disc_number = meta.disc_number;
        track.year = meta.year;
        track.genre = meta.genre;
        track.duration_ms = meta.duration_ms;
        track.date_added = date_added;
        track.date_modified = date_modified;
        track.has_artwork = meta.artwork_data.is_some();

        Ok(track)
    }
}
