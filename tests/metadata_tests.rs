use music_power_3::metadata::{id3v1, id3v2, wav, AudioMetadata};

#[test]
fn test_id3v1_parser() {
    let mut data = vec![0u8; 128];
    data[0..3].copy_from_slice(b"TAG");
    // Title: "Track Title"
    data[3..14].copy_from_slice(b"Track Title");
    // Artist: "Artist Name"
    data[33..44].copy_from_slice(b"Artist Name");
    // Album: "Album Name"
    data[63..73].copy_from_slice(b"Album Name");
    // Year: "2024"
    data[93..97].copy_from_slice(b"2024");
    // Comment
    data[97..104].copy_from_slice(b"Comment");
    // Track number (ID3v1.1)
    data[125] = 0;
    data[126] = 7;

    let mut meta = AudioMetadata::default();
    let res = id3v1::parse_id3v1(&data, &mut meta);
    assert!(res);
    assert_eq!(meta.title, "Track Title");
    assert_eq!(meta.artist, "Artist Name");
    assert_eq!(meta.album, "Album Name");
    assert_eq!(meta.year, 2024);
    assert_eq!(meta.comment, "Comment");
    assert_eq!(meta.track_number, 7);
}

#[test]
fn test_id3v23_parser() {
    let mut data = Vec::new();
    // ID3 header: "ID3", version 3, revision 0, flags 0
    data.extend_from_slice(b"ID3");
    data.push(3);
    data.push(0);
    data.push(0);

    // Frame TIT2: "My Song" in UTF-8 (encoding 3)
    let mut tit2_data = vec![3]; // UTF-8 encoding
    tit2_data.extend_from_slice("My Song".as_bytes());

    let mut frames = Vec::new();
    frames.extend_from_slice(b"TIT2");
    frames.extend_from_slice(&(tit2_data.len() as u32).to_be_bytes());
    frames.extend_from_slice(&[0, 0]); // flags
    frames.extend_from_slice(&tit2_data);

    // Frame TPE1: "My Artist"
    let mut tpe1_data = vec![3];
    tpe1_data.extend_from_slice("My Artist".as_bytes());
    frames.extend_from_slice(b"TPE1");
    frames.extend_from_slice(&(tpe1_data.len() as u32).to_be_bytes());
    frames.extend_from_slice(&[0, 0]);
    frames.extend_from_slice(&tpe1_data);

    // Synchsafe size of frames
    let frames_len = frames.len();
    let synchsafe_size = [
        ((frames_len >> 21) & 0x7F) as u8,
        ((frames_len >> 14) & 0x7F) as u8,
        ((frames_len >> 7) & 0x7F) as u8,
        (frames_len & 0x7F) as u8,
    ];
    data.extend_from_slice(&synchsafe_size);
    data.extend_from_slice(&frames);

    let mut meta = AudioMetadata::default();
    let res = id3v2::parse_id3v2(&data, &mut meta, false);
    assert!(res);
    assert_eq!(meta.title, "My Song");
    assert_eq!(meta.artist, "My Artist");
}

#[test]
fn test_wav_riff_parser() {
    let mut data = Vec::new();
    data.extend_from_slice(b"RIFF");
    data.extend_from_slice(&0u32.to_le_bytes()); // chunk size placeholder
    data.extend_from_slice(b"WAVE");

    // fmt chunk: 16 bytes, byte_rate = 176400 (44100 * 2 channels * 16 bit / 8)
    data.extend_from_slice(b"fmt ");
    data.extend_from_slice(&16u32.to_le_bytes());
    data.extend_from_slice(&1u16.to_le_bytes()); // PCM
    data.extend_from_slice(&2u16.to_le_bytes()); // 2 channels
    data.extend_from_slice(&44100u32.to_le_bytes()); // sample rate
    data.extend_from_slice(&176400u32.to_le_bytes()); // byte rate
    data.extend_from_slice(&4u16.to_le_bytes()); // block align
    data.extend_from_slice(&16u16.to_le_bytes()); // bits per sample

    // data chunk: 352800 bytes (2 seconds of audio)
    data.extend_from_slice(b"data");
    data.extend_from_slice(&352800u32.to_le_bytes());

    let mut meta = AudioMetadata::default();
    let res = wav::parse_wav(&data, &mut meta, false);
    assert!(res);
    assert_eq!(meta.duration_ms, 2000);
}
