pub fn calculate_mp3_duration(data: &[u8]) -> u64 {
    if data.len() < 32 {
        return 0;
    }

    let mut start_offset = 0;
    // Skip ID3v2 if present
    if data.len() >= 10 && &data[0..3] == b"ID3" {
        let tag_size = (((data[6] as usize) & 0x7F) << 21)
            | (((data[7] as usize) & 0x7F) << 14)
            | (((data[8] as usize) & 0x7F) << 7)
            | ((data[9] as usize) & 0x7F);
        start_offset = 10 + tag_size;
    }

    if start_offset + 32 > data.len() {
        return 0;
    }

    let scan_end = std::cmp::min(data.len(), start_offset + 16384);
    let slice = &data[start_offset..scan_end];

    for i in 0..slice.len().saturating_sub(32) {
        if slice[i] == 0xFF && (slice[i + 1] & 0xE0) == 0xE0 {
            let ver = (slice[i + 1] >> 3) & 0x03;
            let layer = (slice[i + 1] >> 1) & 0x03;
            if ver == 1 || layer == 0 {
                continue;
            }

            let sample_rate_idx = ((slice[i + 2] >> 2) & 0x03) as usize;
            if sample_rate_idx >= 3 {
                continue;
            }

            let sample_rate = if ver == 3 {
                [44100, 48000, 32000][sample_rate_idx]
            } else {
                [22050, 24000, 16000][sample_rate_idx]
            };

            let offsets = [i + 36, i + 21, i + 13];
            for &off in &offsets {
                if off + 16 <= slice.len() {
                    let tag = &slice[off..off + 4];
                    if tag == b"Xing" || tag == b"Info" {
                        let flags = u32::from_be_bytes(slice[off + 4..off + 8].try_into().unwrap_or([0; 4]));
                        if (flags & 0x01) != 0 && off + 12 <= slice.len() {
                            let frames = u32::from_be_bytes(slice[off + 8..off + 12].try_into().unwrap_or([0; 4])) as u64;
                            let samples_per_frame = if layer == 3 { 384 } else { 1152 };
                            if sample_rate > 0 {
                                return (frames * samples_per_frame * 1000) / (sample_rate as u64);
                            }
                        }
                    } else if tag == b"VBRI" && off + 26 <= slice.len() {
                        let frames = u32::from_be_bytes(slice[off + 14..off + 18].try_into().unwrap_or([0; 4])) as u64;
                        if sample_rate > 0 {
                            return (frames * 1152 * 1000) / (sample_rate as u64);
                        }
                    }
                }
            }

            // Estimate duration from CBR bitrate if no Xing header
            let bitrate_idx = ((slice[i + 2] >> 4) & 0x0F) as usize;
            let bitrates = [
                0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0,
            ];
            if bitrate_idx < 15 && bitrates[bitrate_idx] > 0 {
                let kbps = bitrates[bitrate_idx] as u64;
                let audio_bytes = (data.len() - start_offset) as u64;
                return (audio_bytes * 8) / kbps;
            }

            break;
        }
    }

    0
}

