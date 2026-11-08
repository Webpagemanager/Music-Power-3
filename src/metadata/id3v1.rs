use super::AudioMetadata;

pub fn parse_id3v1(data: &[u8], meta: &mut AudioMetadata) -> bool {
    if data.len() < 128 {
        return false;
    }

    let tail = &data[data.len() - 128..];
    if &tail[0..3] != b"TAG" {
        return false;
    }

    if meta.title.is_empty() {
        meta.title = decode_latin1_trimmed(&tail[3..33]);
    }
    if meta.artist.is_empty() {
        meta.artist = decode_latin1_trimmed(&tail[33..63]);
    }
    if meta.album.is_empty() {
        meta.album = decode_latin1_trimmed(&tail[63..93]);
    }
    if meta.year == 0 {
        if let Ok(yr_str) = std::str::from_utf8(&tail[93..97]) {
            if let Ok(yr) = yr_str.trim().parse::<u32>() {
                meta.year = yr;
            }
        }
    }
    if meta.comment.is_empty() {
        if tail[125] == 0 && tail[126] != 0 {
            // ID3v1.1 with track number
            meta.comment = decode_latin1_trimmed(&tail[97..125]);
            if meta.track_number == 0 {
                meta.track_number = tail[126] as u32;
            }
        } else {
            meta.comment = decode_latin1_trimmed(&tail[97..127]);
        }
    }

    true
}

fn decode_latin1_trimmed(slice: &[u8]) -> String {
    // Find null terminator or trim trailing whitespaces
    let end = slice.iter().position(|&b| b == 0).unwrap_or(slice.len());
    let s: String = slice[..end].iter().map(|&b| b as char).collect();
    s.trim().to_string()
}

