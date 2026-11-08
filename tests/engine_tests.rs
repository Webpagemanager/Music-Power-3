use music_power_3::library::{LibraryCache, SearchEngine};
use music_power_3::models::Track;
use music_power_3::platform::layout::{calculate_progress_layout, calculate_value_from_pointer};

#[test]
fn test_progress_bar_horizontal_layout() {
    let result = calculate_progress_layout(
        50.0,  // value
        0.0,   // min
        100.0, // max
        500.0, // width
        20.0,  // height
        0,     // horizontal
        true,  // hovered
        false, // scrubbing
        false, // pressed
        false, // always visible
    );

    assert_eq!(result.track_thickness, 6.0);
    assert_eq!(result.track_corner_radius, 3.0);
    assert_eq!(result.fill_length, 250.0);
    // 50% of 500 = 250. Half thumb (10) subtracted = 240
    assert_eq!(result.thumb_offset_x, 240.0);
    assert!(result.is_thumb_visible);
    assert_eq!(result.thumb_opacity, 1.0);
}

#[test]
fn test_progress_bar_pointer_value() {
    let val = calculate_value_from_pointer(
        250.0, // pointer X
        10.0,  // pointer Y
        500.0, // width
        20.0,  // height
        0,     // horizontal
        0.0,   // min
        100.0, // max
        1.0,
    );
    assert!((val - 50.0).abs() < f64::EPSILON);
}

#[test]
fn test_simd_search_filter() {
    let mut t1 = Track::new("C:/test1.mp3".to_string());
    t1.title = "Bohemian Rhapsody".to_string();
    t1.artist = "Queen".to_string();
    t1.album = "A Night at the Opera".to_string();

    let mut t2 = Track::new("C:/test2.flac".to_string());
    t2.title = "Stairway to Heaven".to_string();
    t2.artist = "Led Zeppelin".to_string();
    t2.album = "Led Zeppelin IV".to_string();

    let tracks = vec![t1, t2];

    let query_queen = SearchEngine::filter(&tracks, "queen");
    assert_eq!(query_queen.len(), 1);
    assert_eq!(query_queen[0].title, "Bohemian Rhapsody");

    let query_opera = SearchEngine::filter(&tracks, "OPERA");
    assert_eq!(query_opera.len(), 1);
    assert_eq!(query_opera[0].artist, "Queen");

    let query_heaven = SearchEngine::filter(&tracks, "stairway");
    assert_eq!(query_heaven.len(), 1);
    assert_eq!(query_heaven[0].artist, "Led Zeppelin");

    let query_none = SearchEngine::filter(&tracks, "NonExistentTrack");
    assert_eq!(query_none.len(), 0);
}

#[test]
fn test_rkyv_cache_roundtrip() {
    let mut t1 = Track::new("C:/Music/test.mp3".to_string());
    t1.title = "Sample Song".to_string();
    t1.artist = "Artist Name".to_string();
    t1.album = "Greatest Hits".to_string();
    t1.duration_ms = 215_000;
    t1.track_number = 1;
    t1.year = 2024;

    let original = vec![t1.clone()];

    // Save and load
    let res = LibraryCache::save(&original);
    assert!(res.is_ok());

    let loaded = LibraryCache::load();
    assert!(loaded.is_ok());
    let loaded_tracks = loaded.unwrap();
    assert_eq!(loaded_tracks.len(), 1);
    assert_eq!(loaded_tracks[0].title, "Sample Song");
    assert_eq!(loaded_tracks[0].duration_ms, 215_000);
}

