use super::AudioMetadata;

pub fn parse_ogg(data: &[u8], meta: &mut AudioMetadata) -> bool {
    if data.len() < 27 || &data[0..4] != b"OggS" {
        return false;
    }

    // Fast scan of initial header chunk (up to 64KB) for Vorbis comments
    let scan_len = std::cmp::min(data.len(), 65536);
    let slice = &data[..scan_len];

    let text = String::from_utf8_lossy(slice);
    for line in text.split(|c| c == '\0' || c == '\n') {
        if let Some((k, v)) = line.split_once('=') {
            let key = k.trim().to_uppercase();
            let val = v.trim().to_string();
            match key.as_str() {
                "TITLE" if meta.title.is_empty() => meta.title = val,
                "ARTIST" if meta.artist.is_empty() => meta.artist = val,
                "ALBUM" if meta.album.is_empty() => meta.album = val,
                "ALBUMARTIST" | "ALBUM ARTIST" if meta.album_artist.is_empty() => {
                    meta.album_artist = val
                }
                "GENRE" if meta.genre.is_empty() => meta.genre = val,
                "COMMENT" | "DESCRIPTION" if meta.comment.is_empty() => meta.comment = val,
                _ => {}
            }
        }
    }

    true
}

