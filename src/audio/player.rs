use crate::models::Track;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use windows::core::HSTRING;
use windows::Foundation::{TimeSpan, Uri};
use windows::Media::Core::MediaSource;
use windows::Media::Playback::{MediaPlayer, MediaPlayerAudioCategory};

pub struct AudioPlayer {
    player: MediaPlayer,
    is_playing: Arc<AtomicBool>,
}

impl AudioPlayer {
    pub fn new() -> windows::core::Result<Self> {
        let player = MediaPlayer::new()?;
        player.SetAudioCategory(MediaPlayerAudioCategory::Media)?;

        let is_playing = Arc::new(AtomicBool::new(false));

        // Connect media ended event
        let is_playing_clone = is_playing.clone();
        player.MediaEnded(&windows::Foundation::TypedEventHandler::new(
            move |_sender, _args| {
                is_playing_clone.store(false, Ordering::Relaxed);
                Ok(())
            },
        ))?;

        Ok(Self { player, is_playing })
    }

    pub fn play_track(&self, track: &Track) -> windows::core::Result<()> {
        let path = &track.file_path;
        let file_uri = if path.starts_with("http://") || path.starts_with("https://") || path.starts_with("file://") {
            HSTRING::from(path.as_str())
        } else {
            // Convert Windows path C:\path\to\file into file:///C:/path/to/file
            let normalized = path.replace('\\', "/");
            HSTRING::from(format!("file:///{}", normalized))
        };

        let uri = Uri::CreateUri(&file_uri)?;
        let source = MediaSource::CreateFromUri(&uri)?;

        self.player.SetSource(&source)?;
        self.player.Play()?;
        self.is_playing.store(true, Ordering::Relaxed);

        Ok(())
    }

    pub fn pause(&self) -> windows::core::Result<()> {
        self.player.Pause()?;
        self.is_playing.store(false, Ordering::Relaxed);
        Ok(())
    }

    pub fn resume(&self) -> windows::core::Result<()> {
        self.player.Play()?;
        self.is_playing.store(true, Ordering::Relaxed);
        Ok(())
    }

    pub fn toggle_play_pause(&self) -> windows::core::Result<bool> {
        if self.is_playing() {
            self.pause()?;
            Ok(false)
        } else {
            self.resume()?;
            Ok(true)
        }
    }

    pub fn is_playing(&self) -> bool {
        self.is_playing.load(Ordering::Relaxed)
    }

    pub fn set_volume(&self, volume_percent: u32) -> windows::core::Result<()> {
        let vol = (volume_percent.min(100) as f64) / 100.0;
        self.player.SetVolume(vol)
    }

    pub fn get_position_ms(&self) -> u64 {
        if let Ok(session) = self.player.PlaybackSession() {
            if let Ok(pos) = session.Position() {
                return (pos.Duration / 10_000) as u64; // 100-nanosecond units to milliseconds
            }
        }
        0
    }

    pub fn get_duration_ms(&self) -> u64 {
        if let Ok(session) = self.player.PlaybackSession() {
            if let Ok(dur) = session.NaturalDuration() {
                return (dur.Duration / 10_000) as u64;
            }
        }
        0
    }

    pub fn seek_to_ms(&self, position_ms: u64) -> windows::core::Result<()> {
        if let Ok(session) = self.player.PlaybackSession() {
            if session.CanSeek()? {
                let span = TimeSpan {
                    Duration: (position_ms * 10_000) as i64,
                };
                session.SetPosition(span)?;
            }
        }
        Ok(())
    }

    pub fn get_media_player(&self) -> &MediaPlayer {
        &self.player
    }
}

