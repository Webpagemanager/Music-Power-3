use super::AudioMetadata;

pub fn parse_flac(data: &[u8], meta: &mut AudioMetadata, read_artwork: bool) -> bool {
    if data.len() < 8 || &data[0..4] != b"fLaC" {
        return false;
    }

    let mut offset = 4;
    let mut is_last = false;

    while !is_last && offset + 4 <= data.len() {
        let header = &data[offset..offset + 4];
        is_last = (header[0] & 0x80) != 0;
        let block_type = header[0] & 0x7F;
        let block_len =
            ((header[1] as usize) << 16) | ((header[2] as usize) << 8) | (header[3] as usize);

        offset += 4;
        let block_end = offset + block_len;
        if block_end > data.len() {
            break;
        }

        let block_data = &data[offset..block_end];

        match block_type {
            0 if block_len >= 34 => {
                // STREAMINFO: sample rate (20 bits), channels (3 bits), bits per sample (5 bits), total samples (36 bits)
                let sample_rate = (((block_data[10] as u32) << 12)
                    | ((block_data[11] as u32) << 4)
                    | ((block_data[12] as u32) >> 4))
                    & 0xFFFFF;
                let total_samples = (((block_data[13] as u64) & 0x0F) << 32)
                    | ((block_data[14] as u64) << 24)
                    | ((block_data[15] as u64) << 16)
                    | ((block_data[16] as u64) << 8)
                    | (block_data[17] as u64);

                if sample_rate > 0 && total_samples > 0 {
                    meta.duration_ms = (total_samples * 1000) / (sample_rate as u64);
                }
            }
            4 => {
                // VORBIS_COMMENT
                parse_vorbis_comment(block_data, meta);
            }
            6 if read_artwork && meta.artwork_data.is_none() => {
                // PICTURE
                parse_flac_picture(block_data, meta);
            }
            _ => {}
        }

        offset = block_end;
    }

    true
}

fn parse_vorbis_comment(data: &[u8], meta: &mut AudioMetadata) {
    if data.len() < 8 {
        return;
    }

    let mut cursor = 0;
    let vendor_len = u32::from_le_bytes(data[cursor..cursor + 4].try_into().unwrap_or([0; 4])) as usize;
    cursor += 4 + vendor_len;

    if cursor + 4 > data.len() {
        return;
    }

    let comment_count =
        u32::from_le_bytes(data[cursor..cursor + 4].try_into().unwrap_or([0; 4])) as usize;
    cursor += 4;

    for _ in 0..comment_count {
        if cursor + 4 > data.len() {
            break;
        }
        let len =
            u32::from_le_bytes(data[cursor..cursor + 4].try_into().unwrap_or([0; 4])) as usize;
        cursor += 4;

        if cursor + len > data.len() {
            break;
        }

        let entry = String::from_utf8_lossy(&data[cursor..cursor + len]);
        cursor += len;

        if let Some((key, val)) = entry.split_once('=') {
            let key = key.trim().to_uppercase();
            let val = val.trim().to_string();

            match key.as_str() {
                "TITLE" => meta.title = val,
                "ARTIST" => meta.artist = val,
                "ALBUM" => meta.album = val,
                "ALBUMARTIST" | "ALBUM ARTIST" => meta.album_artist = val,
                "GENRE" => meta.genre = val,
                "COMMENT" | "DESCRIPTION" => meta.comment = val,
                "DATE" | "YEAR" => {
                    if val.len() >= 4 {
                        if let Ok(y) = val[0..4].parse::<u32>() {
                            meta.year = y;
                        }
                    }
                }
                "TRACKNUMBER" => {
                    if let Some((n, tot)) = val.split_once('/') {
                        meta.track_number = n.trim().parse().unwrap_or(0);
                        meta.track_total = tot.trim().parse().unwrap_or(0);
                    } else {
                        meta.track_number = val.parse().unwrap_or(0);
                    }
                }
                "DISCNUMBER" => {
                    if let Some((n, tot)) = val.split_once('/') {
                        meta.disc_number = n.trim().parse().unwrap_or(0);
                        meta.disc_total = tot.trim().parse().unwrap_or(0);
                    } else {
                        meta.disc_number = val.parse().unwrap_or(0);
                    }
                }
                _ => {}
            }
        }
    }
}

fn parse_flac_picture(data: &[u8], meta: &mut AudioMetadata) {
    if data.len() < 32 {
        return;
    }
    let mut cursor = 4; // skip picture type (4 bytes)

    // MIME type length
    let mime_len =
        u32::from_be_bytes(data[cursor..cursor + 4].try_into().unwrap_or([0; 4])) as usize;
    cursor += 4;
    if cursor + mime_len > data.len() {
        return;
    }
    let mime = String::from_utf8_lossy(&data[cursor..cursor + mime_len]).to_string();
    cursor += mime_len;

    // Description length
    if cursor + 4 > data.len() {
        return;
    }
    let desc_len =
        u32::from_be_bytes(data[cursor..cursor + 4].try_into().unwrap_or([0; 4])) as usize;
    cursor += 4 + desc_len;

    // Width(4), Height(4), ColorDepth(4), Colors(4)
    cursor += 16;
    if cursor + 4 > data.len() {
        return;
    }

    let data_len =
        u32::from_be_bytes(data[cursor..cursor + 4].try_into().unwrap_or([0; 4])) as usize;
    cursor += 4;
    if cursor + data_len > data.len() {
        return;
    }

    meta.artwork_data = Some(data[cursor..cursor + data_len].to_vec());
    meta.artwork_mime_type = if mime.is_empty() {
        "image/jpeg".to_string()
    } else {
        mime
    };
}

