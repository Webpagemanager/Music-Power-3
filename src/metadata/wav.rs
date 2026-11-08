use super::id3v2;
use super::AudioMetadata;

pub fn parse_wav(data: &[u8], meta: &mut AudioMetadata, read_artwork: bool) -> bool {
    if data.len() < 12 || &data[0..4] != b"RIFF" || &data[8..12] != b"WAVE" {
        return false;
    }

    let mut cursor = 12;
    let mut byte_rate = 0u32;

    while cursor + 8 <= data.len() {
        let chunk_id = &data[cursor..cursor + 4];
        let chunk_size = u32::from_le_bytes(data[cursor + 4..cursor + 8].try_into().unwrap_or([0; 4])) as usize;
        cursor += 8;

        if chunk_id == b"data" {
            if byte_rate > 0 && meta.duration_ms == 0 {
                meta.duration_ms = ((chunk_size as u64) * 1000) / (byte_rate as u64);
            }
            cursor = cursor.saturating_add(chunk_size + (chunk_size % 2));
            continue;
        }

        let chunk_end = cursor + chunk_size;
        if chunk_end > data.len() {
            break;
        }

        let chunk_data = &data[cursor..chunk_end];

        match chunk_id {
            b"fmt " if chunk_size >= 16 => {
                byte_rate = u32::from_le_bytes(chunk_data[8..12].try_into().unwrap_or([0; 4]));
            }
            b"id3 " => {
                id3v2::parse_id3v2(chunk_data, meta, read_artwork);
            }
            b"LIST" if chunk_size >= 4 && &chunk_data[0..4] == b"INFO" => {
                parse_riff_info(&chunk_data[4..], meta);
            }
            _ => {}
        }

        cursor = chunk_end + (chunk_size % 2); // WAV chunks are word-aligned
    }

    true
}

fn parse_riff_info(data: &[u8], meta: &mut AudioMetadata) {
    let mut cursor = 0;
    while cursor + 8 <= data.len() {
        let id = &data[cursor..cursor + 4];
        let size = u32::from_le_bytes(data[cursor + 4..cursor + 8].try_into().unwrap_or([0; 4])) as usize;
        cursor += 8;

        let end = cursor + size;
        if end > data.len() {
            break;
        }

        let val = String::from_utf8_lossy(&data[cursor..end])
            .trim_matches(|c: char| c == '\0' || c.is_whitespace())
            .to_string();

        match id {
            b"INAM" if meta.title.is_empty() => meta.title = val,
            b"IART" if meta.artist.is_empty() => meta.artist = val,
            b"IPRD" if meta.album.is_empty() => meta.album = val,
            b"IGNR" if meta.genre.is_empty() => meta.genre = val,
            b"ICMT" if meta.comment.is_empty() => meta.comment = val,
            b"ITRK" if meta.track_number == 0 => meta.track_number = val.parse().unwrap_or(0),
            _ => {}
        }

        cursor = end + (size % 2);
    }
}
