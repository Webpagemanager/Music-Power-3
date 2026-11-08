use super::AudioMetadata;

pub fn parse_mp4(data: &[u8], meta: &mut AudioMetadata, read_artwork: bool) -> bool {
    scan_atoms(data, 0, data.len(), meta, read_artwork);
    !meta.title.is_empty() || meta.duration_ms > 0
}

fn scan_atoms(
    data: &[u8],
    start: usize,
    end: usize,
    meta: &mut AudioMetadata,
    read_artwork: bool,
) {
    let mut cursor = start;
    while cursor + 8 <= end {
        let size_raw = u32::from_be_bytes(data[cursor..cursor + 4].try_into().unwrap_or([0; 4])) as usize;
        let name = &data[cursor + 4..cursor + 8];

        let (atom_size, hdr_len) = if size_raw == 1 {
            if cursor + 16 > end {
                break;
            }
            let size64 = u64::from_be_bytes(data[cursor + 8..cursor + 16].try_into().unwrap_or([0; 8])) as usize;
            (size64, 16)
        } else if size_raw == 0 {
            (end - cursor, 8)
        } else {
            (size_raw, 8)
        };

        if atom_size < hdr_len || cursor + atom_size > end {
            break;
        }

        let atom_data_start = cursor + hdr_len;
        let atom_data_end = cursor + atom_size;

        match name {
            b"moov" | b"udta" | b"trak" | b"mdia" => {
                scan_atoms(data, atom_data_start, atom_data_end, meta, read_artwork);
            }
            b"mvhd" if meta.duration_ms == 0 => {
                let payload = &data[atom_data_start..atom_data_end];
                if !payload.is_empty() {
                    let version = payload[0];
                    if version == 0 && payload.len() >= 20 {
                        let timescale = u32::from_be_bytes(payload[12..16].try_into().unwrap_or([0; 4])) as u64;
                        let duration = u32::from_be_bytes(payload[16..20].try_into().unwrap_or([0; 4])) as u64;
                        if timescale > 0 {
                            meta.duration_ms = (duration * 1000) / timescale;
                        }
                    } else if version == 1 && payload.len() >= 28 {
                        let timescale = u32::from_be_bytes(payload[20..24].try_into().unwrap_or([0; 4])) as u64;
                        let duration = u64::from_be_bytes(payload[24..32].try_into().unwrap_or([0; 8]));
                        if timescale > 0 {
                            meta.duration_ms = (duration * 1000) / timescale;
                        }
                    }
                }
            }
            b"meta" => {
                // Meta has a 4-byte version/flags prefix
                if atom_data_start + 4 <= atom_data_end {
                    scan_atoms(data, atom_data_start + 4, atom_data_end, meta, read_artwork);
                }
            }
            b"ilst" => {
                read_ilst(data, atom_data_start, atom_data_end, meta, read_artwork);
            }
            _ => {}
        }

        cursor += atom_size;
    }
}

fn read_ilst(
    data: &[u8],
    start: usize,
    end: usize,
    meta: &mut AudioMetadata,
    read_artwork: bool,
) {
    let mut cursor = start;
    while cursor + 8 <= end {
        let item_size = u32::from_be_bytes(data[cursor..cursor + 4].try_into().unwrap_or([0; 4])) as usize;
        let item_name = &data[cursor + 4..cursor + 8];

        if item_size < 8 || cursor + item_size > end {
            break;
        }

        let item_end = cursor + item_size;
        let mut sub_cursor = cursor + 8;

        // An ilst item contains a 'data' atom
        while sub_cursor + 8 <= item_end {
            let data_size = u32::from_be_bytes(data[sub_cursor..sub_cursor + 4].try_into().unwrap_or([0; 4])) as usize;
            let data_name = &data[sub_cursor + 4..sub_cursor + 8];

            if data_size < 16 || sub_cursor + data_size > item_end {
                break;
            }

            if data_name == b"data" {
                // type_flags: 4 bytes type, 4 bytes locale
                let payload = &data[sub_cursor + 16..sub_cursor + data_size];

                match item_name {
                    b"\xA9nam" => meta.title = String::from_utf8_lossy(payload).trim().to_string(),
                    b"\xA9ART" => meta.artist = String::from_utf8_lossy(payload).trim().to_string(),
                    b"\xA9alb" => meta.album = String::from_utf8_lossy(payload).trim().to_string(),
                    b"aART" => meta.album_artist = String::from_utf8_lossy(payload).trim().to_string(),
                    b"\xA9gen" => meta.genre = String::from_utf8_lossy(payload).trim().to_string(),
                    b"\xA9cmt" => meta.comment = String::from_utf8_lossy(payload).trim().to_string(),
                    b"\xA9day" => {
                        let yr_str = String::from_utf8_lossy(payload);
                        if yr_str.len() >= 4 {
                            if let Ok(y) = yr_str[0..4].parse::<u32>() {
                                meta.year = y;
                            }
                        }
                    }
                    b"trkn" if payload.len() >= 4 => {
                        meta.track_number = u16::from_be_bytes(payload[2..4].try_into().unwrap_or([0; 2])) as u32;
                        if payload.len() >= 6 {
                            meta.track_total = u16::from_be_bytes(payload[4..6].try_into().unwrap_or([0; 2])) as u32;
                        }
                    }
                    b"disk" if payload.len() >= 4 => {
                        meta.disc_number = u16::from_be_bytes(payload[2..4].try_into().unwrap_or([0; 2])) as u32;
                        if payload.len() >= 6 {
                            meta.disc_total = u16::from_be_bytes(payload[4..6].try_into().unwrap_or([0; 2])) as u32;
                        }
                    }
                    b"covr" if read_artwork && meta.artwork_data.is_none() => {
                        meta.artwork_data = Some(payload.to_vec());
                        meta.artwork_mime_type = if payload.len() >= 8 && payload[0..8] == [0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A] {
                            "image/png".to_string()
                        } else {
                            "image/jpeg".to_string()
                        };
                    }
                    _ => {}
                }
            }

            sub_cursor += data_size;
        }

        cursor = item_end;
    }
}

