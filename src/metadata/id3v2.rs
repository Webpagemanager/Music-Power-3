use super::AudioMetadata;

pub fn parse_id3v2(data: &[u8], meta: &mut AudioMetadata, read_artwork: bool) -> bool {
    if data.len() < 10 || &data[0..3] != b"ID3" {
        return false;
    }

    let major = data[3]; // 2 = v2.2, 3 = v2.3, 4 = v2.4
    let flags = data[5];
    let tag_size = decode_synchsafe(&data[6..10]);
    if tag_size == 0 || 10 + tag_size > data.len() {
        return false;
    }

    let tag_slice = &data[10..10 + tag_size];

    if major == 2 {
        parse_id3v22(tag_slice, meta, read_artwork);
    } else if major == 3 || major == 4 {
        // If un-synchronization flag set on tag header
        if (flags & 0x80) != 0 {
            let clean = de_unsynchronize(tag_slice);
            parse_id3v23_or_24(&clean, major, flags & !0x80, meta, read_artwork);
        } else {
            parse_id3v23_or_24(tag_slice, major, flags, meta, read_artwork);
        }
    }

    true
}

fn decode_synchsafe(slice: &[u8]) -> usize {
    if slice.len() < 4 {
        return 0;
    }
    (((slice[0] as usize) & 0x7F) << 21)
        | (((slice[1] as usize) & 0x7F) << 14)
        | (((slice[2] as usize) & 0x7F) << 7)
        | ((slice[3] as usize) & 0x7F)
}

fn de_unsynchronize(input: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(input.len());
    let mut i = 0;
    while i < input.len() {
        out.push(input[i]);
        if input[i] == 0xFF && i + 1 < input.len() && input[i + 1] == 0x00 {
            i += 2;
        } else {
            i += 1;
        }
    }
    out
}

fn parse_id3v23_or_24(
    slice: &[u8],
    major: u8,
    flags: u8,
    meta: &mut AudioMetadata,
    read_artwork: bool,
) {
    let mut offset = 0;

    // Extended header
    if (flags & 0x40) != 0 && offset + 4 <= slice.len() {
        let ext_size = if major == 4 {
            decode_synchsafe(&slice[offset..offset + 4])
        } else {
            u32::from_be_bytes(slice[offset..offset + 4].try_into().unwrap_or([0; 4])) as usize
        };
        offset += ext_size;
    }

    while offset + 10 <= slice.len() {
        let header = &slice[offset..offset + 10];
        if header[0..4] == [0, 0, 0, 0] {
            // Reached padding
            break;
        }

        if !is_valid_frame_id(&header[0..4]) {
            break;
        }

        let frame_id = std::str::from_utf8(&header[0..4]).unwrap_or("");
        let frame_size = if major == 4 {
            let sync_size = decode_synchsafe(&header[4..8]);
            if offset + 10 + sync_size <= slice.len() {
                sync_size
            } else {
                u32::from_be_bytes(header[4..8].try_into().unwrap_or([0; 4])) as usize
            }
        } else {
            u32::from_be_bytes(header[4..8].try_into().unwrap_or([0; 4])) as usize
        };

        let frame_data_start = offset + 10;
        let frame_data_end = frame_data_start + frame_size;
        if frame_data_end > slice.len() {
            break;
        }

        let frame_bytes = &slice[frame_data_start..frame_data_end];
        offset = frame_data_end;

        process_frame(frame_id, frame_bytes, meta, read_artwork);
    }
}

fn parse_id3v22(slice: &[u8], meta: &mut AudioMetadata, read_artwork: bool) {
    let mut offset = 0;
    while offset + 6 <= slice.len() {
        let header = &slice[offset..offset + 6];
        if header[0..3] == [0, 0, 0] {
            break;
        }

        let frame_id = std::str::from_utf8(&header[0..3]).unwrap_or("");
        let frame_size =
            ((header[3] as usize) << 16) | ((header[4] as usize) << 8) | (header[5] as usize);

        let frame_data_start = offset + 6;
        let frame_data_end = frame_data_start + frame_size;
        if frame_data_end > slice.len() {
            break;
        }

        let frame_bytes = &slice[frame_data_start..frame_data_end];
        offset = frame_data_end;

        match frame_id {
            "TT2" => meta.title = decode_text(frame_bytes),
            "TP1" => meta.artist = decode_text(frame_bytes),
            "TAL" => meta.album = decode_text(frame_bytes),
            "TP2" => meta.album_artist = decode_text(frame_bytes),
            "TCO" => meta.genre = decode_text(frame_bytes),
            "TRK" => {
                let (track, total) = parse_number_and_total(&decode_text(frame_bytes));
                meta.track_number = track;
                meta.track_total = total;
            }
            "TYE" => {
                let yr_str = decode_text(frame_bytes);
                if yr_str.len() >= 4 {
                    if let Ok(y) = yr_str[0..4].parse::<u32>() {
                        meta.year = y;
                    }
                }
            }
            "PIC" if read_artwork && meta.artwork_data.is_none() => {
                decode_id3v22_pic(frame_bytes, meta);
            }
            _ => {}
        }
    }
}

fn is_valid_frame_id(bytes: &[u8]) -> bool {
    bytes
        .iter()
        .all(|&b| (b'A'..=b'Z').contains(&b) || (b'0'..=b'9').contains(&b))
}

fn process_frame(id: &str, data: &[u8], meta: &mut AudioMetadata, read_artwork: bool) {
    if data.is_empty() {
        return;
    }

    match id {
        "TIT2" => meta.title = decode_text(data),
        "TPE1" => meta.artist = decode_text(data),
        "TALB" => meta.album = decode_text(data),
        "TPE2" => meta.album_artist = decode_text(data),
        "TCON" => meta.genre = decode_text(data),
        "TRCK" => {
            let (track, total) = parse_number_and_total(&decode_text(data));
            meta.track_number = track;
            meta.track_total = total;
        }
        "TPOS" => {
            let (disc, total) = parse_number_and_total(&decode_text(data));
            meta.disc_number = disc;
            meta.disc_total = total;
        }
        "TYER" | "TDRC" => {
            let yr_str = decode_text(data);
            if yr_str.len() >= 4 {
                if let Ok(y) = yr_str[0..4].parse::<u32>() {
                    meta.year = y;
                }
            }
        }
        "COMM" => meta.comment = decode_comment(data),
        "APIC" if read_artwork && meta.artwork_data.is_none() => {
            decode_apic(data, meta);
        }
        _ => {}
    }
}

pub fn decode_text(data: &[u8]) -> String {
    if data.len() <= 1 {
        return String::new();
    }
    let encoding = data[0];
    let payload = &data[1..];

    let raw = match encoding {
        1 => {
            // UTF-16 with BOM
            if payload.len() >= 2 && payload[0] == 0xFE && payload[1] == 0xFF {
                decode_utf16_be(&payload[2..])
            } else if payload.len() >= 2 && payload[0] == 0xFF && payload[1] == 0xFE {
                decode_utf16_le(&payload[2..])
            } else {
                decode_utf16_le(payload)
            }
        }
        2 => {
            // UTF-16BE without BOM
            decode_utf16_be(payload)
        }
        3 => {
            // UTF-8
            String::from_utf8_lossy(payload).to_string()
        }
        _ => {
            // Encoding 0: ISO-8859-1 / Windows-1252 / mislabeled UTF-8
            if is_valid_utf8(payload) {
                String::from_utf8_lossy(payload).to_string()
            } else {
                decode_windows1252(payload)
            }
        }
    };

    let trimmed = raw.trim_matches(|c: char| c == '\u{FEFF}' || c == '\0' || c.is_whitespace());
    if trimmed.contains('\0') {
        trimmed
            .split('\0')
            .filter(|s| !s.trim().is_empty())
            .collect::<Vec<_>>()
            .join(", ")
    } else {
        trimmed.to_string()
    }
}

fn decode_comment(data: &[u8]) -> String {
    if data.len() < 5 {
        return String::new();
    }
    let encoding = data[0];
    let mut idx = 4; // Skip encoding (1) + language (3)

    if encoding == 1 || encoding == 2 {
        while idx + 1 < data.len() && !(data[idx] == 0 && data[idx + 1] == 0) {
            idx += 2;
        }
        idx += 2;
    } else {
        while idx < data.len() && data[idx] != 0 {
            idx += 1;
        }
        idx += 1;
    }

    if idx >= data.len() {
        return String::new();
    }

    let mut buf = Vec::with_capacity(data.len() - idx + 1);
    buf.push(encoding);
    buf.extend_from_slice(&data[idx..]);
    decode_text(&buf)
}

fn decode_apic(data: &[u8], meta: &mut AudioMetadata) {
    if data.len() < 10 {
        return;
    }
    let encoding = data[0];
    let mut idx = 1;

    // MIME type (ASCII / Latin-1 null-terminated)
    let mime_start = idx;
    while idx < data.len() && data[idx] != 0 {
        idx += 1;
    }
    let mime = String::from_utf8_lossy(&data[mime_start..idx]).to_string();
    idx += 1; // skip null

    if idx >= data.len() {
        return;
    }
    let _pic_type = data[idx];
    idx += 1;

    // Skip description
    if encoding == 1 || encoding == 2 {
        while idx + 1 < data.len() && !(data[idx] == 0 && data[idx + 1] == 0) {
            idx += 2;
        }
        idx += 2;
    } else {
        while idx < data.len() && data[idx] != 0 {
            idx += 1;
        }
        idx += 1;
    }

    if idx >= data.len() {
        return;
    }

    let pic_bytes = data[idx..].to_vec();
    meta.artwork_mime_type = if mime.is_empty() {
        "image/jpeg".to_string()
    } else {
        mime
    };
    meta.artwork_data = Some(pic_bytes);
}

fn decode_id3v22_pic(data: &[u8], meta: &mut AudioMetadata) {
    if data.len() < 6 {
        return;
    }
    let encoding = data[0];
    let format = String::from_utf8_lossy(&data[1..4]).to_uppercase();
    let mut idx = 5;

    if encoding == 1 || encoding == 2 {
        while idx + 1 < data.len() && !(data[idx] == 0 && data[idx + 1] == 0) {
            idx += 2;
        }
        idx += 2;
    } else {
        while idx < data.len() && data[idx] != 0 {
            idx += 1;
        }
        idx += 1;
    }

    if idx >= data.len() {
        return;
    }

    let pic_bytes = data[idx..].to_vec();
    meta.artwork_mime_type = if format == "PNG" {
        "image/png".to_string()
    } else {
        "image/jpeg".to_string()
    };
    meta.artwork_data = Some(pic_bytes);
}

fn parse_number_and_total(s: &str) -> (u32, u32) {
    let s = s.trim();
    if s.is_empty() {
        return (0, 0);
    }
    if let Some((num_str, total_str)) = s.split_once('/') {
        let n = num_str.trim().parse::<u32>().unwrap_or(0);
        let tot = total_str.trim().parse::<u32>().unwrap_or(0);
        (n, tot)
    } else {
        (s.parse::<u32>().unwrap_or(0), 0)
    }
}

fn decode_utf16_le(bytes: &[u8]) -> String {
    let u16s: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    String::from_utf16_lossy(&u16s)
}

fn decode_utf16_be(bytes: &[u8]) -> String {
    let u16s: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|c| u16::from_be_bytes([c[0], c[1]]))
        .collect();
    String::from_utf16_lossy(&u16s)
}

fn decode_windows1252(bytes: &[u8]) -> String {
    bytes.iter().map(|&b| b as char).collect()
}

fn is_valid_utf8(bytes: &[u8]) -> bool {
    std::str::from_utf8(bytes).is_ok()
}

