use crate::models::Track;
use memchr::memmem;

pub struct SearchEngine;

impl SearchEngine {
    /// Fast case-insensitive substring search over track library using SIMD memchr
    pub fn filter<'a>(tracks: &'a [Track], query: &str) -> Vec<&'a Track> {
        let q = query.trim().to_lowercase();
        if q.is_empty() {
            return tracks.iter().collect();
        }

        let finder = memmem::Finder::new(q.as_bytes());

        tracks
            .iter()
            .filter(|track| {
                Self::matches_field(&finder, &track.title)
                    || Self::matches_field(&finder, &track.artist)
                    || Self::matches_field(&finder, &track.album)
                    || Self::matches_field(&finder, &track.album_artist)
                    || Self::matches_field(&finder, &track.genre)
            })
            .collect()
    }

    #[inline]
    fn matches_field(finder: &memmem::Finder, field: &str) -> bool {
        if field.is_empty() {
            return false;
        }
        let lower = field.to_lowercase();
        finder.find(lower.as_bytes()).is_some()
    }
}

