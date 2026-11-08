use crate::audio::{AudioPlayer, SmtcController};
use crate::library::{LibraryCache, LibraryScanner, SearchEngine};
use crate::models::{AppSettings, Track};
use crate::platform::{pick_folder, set_app_user_model_id};
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};
use windows_reactor::*;

const APP_ICON_PNG_32: &[u8] = include_bytes!("../../Assets/icon_32.png");
const APP_ICON_PNG_256: &[u8] = include_bytes!("../../Assets/icon.png");

fn parse_hex_color(hex: &str) -> Option<Color> {
    let clean = hex.trim().trim_start_matches('#');
    if clean.len() == 6 {
        let r = u8::from_str_radix(&clean[0..2], 16).ok()?;
        let g = u8::from_str_radix(&clean[2..4], 16).ok()?;
        let b = u8::from_str_radix(&clean[4..6], 16).ok()?;
        Some(Color::argb(255, r, g, b))
    } else if clean.len() == 8 {
        let a = u8::from_str_radix(&clean[0..2], 16).ok()?;
        let r = u8::from_str_radix(&clean[2..4], 16).ok()?;
        let g = u8::from_str_radix(&clean[4..6], 16).ok()?;
        let b = u8::from_str_radix(&clean[6..8], 16).ok()?;
        Some(Color::argb(a, r, g, b))
    } else {
        None
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ActiveView {
    Library,
    Queue,
    Settings,
    EditTrack(usize),
}

#[derive(Debug, Clone, Default)]
pub struct TrackEditForm {
    pub title: String,
    pub artist: String,
    pub album: String,
    pub album_artist: String,
    pub track_number: String,
    pub disc_number: String,
    pub year: String,
    pub genre: String,
    pub comment: String,
    pub tag_encoding: usize,
}

pub struct MainApp {
    tracks: Vec<Track>,
    filtered_tracks: Vec<Track>,
    search_query: String,
    sort_index: usize,
    current_track: Option<Track>,
    is_playing: bool,
    is_shuffle: bool,
    is_repeat: bool,
    volume: u32,
    position_ms: u64,
    duration_ms: u64,
    active_view: ActiveView,
    queue: Vec<Track>,
    is_scanning: bool,
    edit_form: TrackEditForm,
    settings: AppSettings,
    player: Option<Arc<AudioPlayer>>,
    smtc: Option<Arc<SmtcController>>,
    global_scale: f64,
    show_artwork_panel: bool,
    current_artwork_bytes: Option<Arc<[u8]>>,
    thumbnail_cache: Arc<Mutex<HashMap<String, Option<Arc<[u8]>>>>>,
    is_scrubbing: bool,
}

#[derive(Debug)]
pub enum AppMessage {
    PlaybackTick,
    SearchChanged(String),
    SortChanged(Option<usize>),
    PlayTrack(usize),
    PlayPauseToggle,
    NextTrack,
    PreviousTrack,
    ToggleShuffle,
    ToggleRepeat,
    VolumeChanged(f64),
    Seek(f64),
    OpenSettings,
    CloseSettings,
    OpenQueue,
    CloseQueue,
    QueueNextTrack(usize),
    QueueLastTrack(usize),
    RemoveFromQueue(usize),
    ClearQueue,
    StartEditTrack(usize),
    CancelEdit,
    SaveEdit,
    EditFieldChanged(EditField, String),
    ScanLibrary,
    ChooseMusicFolder,
    TracksDiscovered(Vec<Track>),
    SetGlobalScale(f64),
    ToggleArtworkPanel,
    ToggleUseSystemAccent,
    SetAccentColor(Color),
    SetShuffleMemory(f64),
    ToggleOnlineMetadata,
}

#[derive(Debug, Clone, Copy)]
pub enum EditField {
    Title,
    Artist,
    Album,
    AlbumArtist,
    TrackNumber,
    DiscNumber,
    Year,
    Genre,
    Comment,
}

impl Component for MainApp {
    type Input = ();
    type Message = AppMessage;

    fn create(_input: &Self::Input, _context: &ComponentContext<Self>) -> Self {
        set_app_user_model_id();

        let settings = AppSettings::load();
        let cached = LibraryCache::load().unwrap_or_default();
        let volume = settings.volume;

        let player = AudioPlayer::new().ok().map(Arc::new);
        let smtc = player.as_ref().and_then(|p| {
            SmtcController::new(p.get_media_player()).ok().map(Arc::new)
        });

        if let Some(ref p) = player {
            let _ = p.set_volume(volume);
        }

        let filtered_tracks = cached.clone();
        let global_scale = if settings.global_scale < 0.75 || settings.global_scale > 1.5 {
            1.0
        } else {
            settings.global_scale
        };
        let show_artwork_panel = settings.show_artwork_panel;

        Self {
            tracks: cached,
            filtered_tracks,
            search_query: String::new(),
            sort_index: 0,
            current_track: None,
            is_playing: false,
            is_shuffle: false,
            is_repeat: false,
            volume,
            position_ms: 0,
            duration_ms: 0,
            active_view: ActiveView::Library,
            queue: Vec::new(),
            is_scanning: false,
            edit_form: TrackEditForm::default(),
            settings,
            player,
            smtc,
            global_scale,
            show_artwork_panel,
            current_artwork_bytes: None,
            thumbnail_cache: Arc::new(Mutex::new(HashMap::new())),
            is_scrubbing: false,
        }
    }

    fn update(&mut self, message: Self::Message, _context: &ComponentContext<Self>) {
        match message {
            AppMessage::PlaybackTick => {
                if self.is_playing && !self.is_scrubbing {
                    if let Some(ref player) = self.player {
                        self.position_ms = player.get_position_ms();
                        if self.duration_ms == 0 {
                            self.duration_ms = player.get_duration_ms();
                        }
                        if self.duration_ms > 0 && self.position_ms >= self.duration_ms.saturating_sub(250) {
                            self.update(AppMessage::NextTrack, _context);
                        }
                    }
                    _context.spawn_background(|_| {
                        std::thread::sleep(std::time::Duration::from_millis(100));
                        AppMessage::PlaybackTick
                    });
                }
            }
            AppMessage::SearchChanged(query) => {
                self.search_query = query;
                self.apply_filter_and_sort();
            }
            AppMessage::SortChanged(opt_idx) => {
                if let Some(idx) = opt_idx {
                    self.sort_index = idx;
                    self.apply_filter_and_sort();
                }
            }
            AppMessage::PlayTrack(idx) => {
                if let Some(track) = self.filtered_tracks.get(idx).cloned() {
                    self.play_track_internal(track, _context);
                }
            }
            AppMessage::PlayPauseToggle => {
                if let Some(ref player) = self.player {
                    if self.is_playing {
                        let _ = player.pause();
                        self.is_playing = false;
                    } else if self.current_track.is_some() {
                        let _ = player.resume();
                        self.is_playing = true;
                        _context.spawn_background(|_| {
                            std::thread::sleep(std::time::Duration::from_millis(100));
                            AppMessage::PlaybackTick
                        });
                    } else if !self.filtered_tracks.is_empty() {
                        let track = self.filtered_tracks[0].clone();
                        self.play_track_internal(track, _context);
                    }
                }
            }
            AppMessage::NextTrack => {
                if !self.queue.is_empty() {
                    let next = self.queue.remove(0);
                    self.play_track_internal(next, _context);
                } else if let Some(ref current) = self.current_track {
                    if let Some(pos) = self.filtered_tracks.iter().position(|t| t.file_path == current.file_path) {
                        let next_idx = (pos + 1) % self.filtered_tracks.len().max(1);
                        if let Some(next) = self.filtered_tracks.get(next_idx).cloned() {
                            self.play_track_internal(next, _context);
                        }
                    }
                }
            }
            AppMessage::PreviousTrack => {
                if let Some(ref current) = self.current_track {
                    if let Some(pos) = self.filtered_tracks.iter().position(|t| t.file_path == current.file_path) {
                        let prev_idx = if pos == 0 {
                            self.filtered_tracks.len().saturating_sub(1)
                        } else {
                            pos - 1
                        };
                        if let Some(prev) = self.filtered_tracks.get(prev_idx).cloned() {
                            self.play_track_internal(prev, _context);
                        }
                    }
                }
            }
            AppMessage::ToggleShuffle => {
                self.is_shuffle = !self.is_shuffle;
            }
            AppMessage::ToggleRepeat => {
                self.is_repeat = !self.is_repeat;
            }
            AppMessage::VolumeChanged(vol) => {
                self.volume = vol.round() as u32;
                self.settings.volume = self.volume;
                let _ = self.settings.save();
                if let Some(ref player) = self.player {
                    let _ = player.set_volume(self.volume);
                }
            }
            AppMessage::Seek(percent) => {
                if self.duration_ms > 0 {
                    let target_ms = ((percent / 100.0) * self.duration_ms as f64) as u64;
                    let diff = (target_ms as i64 - self.position_ms as i64).abs();
                    if diff > 1000 {
                        self.position_ms = target_ms;
                        if let Some(ref player) = self.player {
                            let _ = player.seek_to_ms(target_ms);
                        }
                    }
                }
            }
            AppMessage::OpenSettings => {
                self.active_view = ActiveView::Settings;
            }
            AppMessage::CloseSettings => {
                self.active_view = ActiveView::Library;
            }
            AppMessage::OpenQueue => {
                self.active_view = ActiveView::Queue;
            }
            AppMessage::CloseQueue => {
                self.active_view = ActiveView::Library;
            }
            AppMessage::QueueNextTrack(idx) => {
                if let Some(track) = self.filtered_tracks.get(idx).cloned() {
                    self.queue.insert(0, track);
                }
            }
            AppMessage::QueueLastTrack(idx) => {
                if let Some(track) = self.filtered_tracks.get(idx).cloned() {
                    self.queue.push(track);
                }
            }
            AppMessage::RemoveFromQueue(idx) => {
                if idx < self.queue.len() {
                    self.queue.remove(idx);
                }
            }
            AppMessage::ClearQueue => {
                self.queue.clear();
            }
            AppMessage::StartEditTrack(idx) => {
                if let Some(track) = self.filtered_tracks.get(idx) {
                    self.edit_form = TrackEditForm {
                        title: track.title.clone(),
                        artist: track.artist.clone(),
                        album: track.album.clone(),
                        album_artist: track.album_artist.clone(),
                        track_number: if track.track_number > 0 { track.track_number.to_string() } else { String::new() },
                        disc_number: if track.disc_number > 0 { track.disc_number.to_string() } else { String::new() },
                        year: if track.year > 0 { track.year.to_string() } else { String::new() },
                        genre: track.genre.clone(),
                        comment: String::new(),
                        tag_encoding: 0,
                    };
                    self.active_view = ActiveView::EditTrack(idx);
                }
            }
            AppMessage::CancelEdit => {
                self.active_view = ActiveView::Library;
            }
            AppMessage::SaveEdit => {
                if let ActiveView::EditTrack(idx) = self.active_view {
                    if let Some(track) = self.filtered_tracks.get_mut(idx) {
                        track.title = self.edit_form.title.clone();
                        track.artist = self.edit_form.artist.clone();
                        track.album = self.edit_form.album.clone();
                        track.album_artist = self.edit_form.album_artist.clone();
                        track.track_number = self.edit_form.track_number.parse().unwrap_or(0);
                        track.disc_number = self.edit_form.disc_number.parse().unwrap_or(0);
                        track.year = self.edit_form.year.parse().unwrap_or(0);
                        track.genre = self.edit_form.genre.clone();
                    }
                    let _ = LibraryCache::save(&self.tracks);
                }
                self.active_view = ActiveView::Library;
            }
            AppMessage::EditFieldChanged(field, val) => {
                match field {
                    EditField::Title => self.edit_form.title = val,
                    EditField::Artist => self.edit_form.artist = val,
                    EditField::Album => self.edit_form.album = val,
                    EditField::AlbumArtist => self.edit_form.album_artist = val,
                    EditField::TrackNumber => self.edit_form.track_number = val,
                    EditField::DiscNumber => self.edit_form.disc_number = val,
                    EditField::Year => self.edit_form.year = val,
                    EditField::Genre => self.edit_form.genre = val,
                    EditField::Comment => self.edit_form.comment = val,
                }
            }
            AppMessage::ScanLibrary => {
                if self.is_scanning {
                    return;
                }
                let mut folders: Vec<PathBuf> = self
                    .settings
                    .library_folders
                    .iter()
                    .map(PathBuf::from)
                    .collect();

                if folders.is_empty() && !self.settings.last_library_path.is_empty() {
                    folders.push(PathBuf::from(&self.settings.last_library_path));
                }

                if folders.is_empty() {
                    // Prompt with folder picker if no library path is configured
                    if let Some(folder) = pick_folder() {
                        let folder_str = folder.to_string_lossy().to_string();
                        self.settings.last_library_path = folder_str.clone();
                        self.settings.library_folders.push(folder_str);
                        let _ = self.settings.save();
                        folders.push(folder);
                    } else {
                        return;
                    }
                }

                self.is_scanning = true;
                let cancel = Arc::new(AtomicBool::new(false));
                let rx = LibraryScanner::scan_directories(folders, &self.tracks, cancel);

                let mut all_scanned = Vec::new();
                while let Ok(batch) = rx.recv() {
                    all_scanned.extend(batch);
                }

                if !all_scanned.is_empty() {
                    self.tracks = all_scanned;
                    self.thumbnail_cache.lock().unwrap().clear();
                    let _ = LibraryCache::save(&self.tracks);
                    self.apply_filter_and_sort();
                }
                self.is_scanning = false;
            }
            AppMessage::ChooseMusicFolder => {
                if let Some(folder) = pick_folder() {
                    let folder_str = folder.to_string_lossy().to_string();
                    self.settings.last_library_path = folder_str.clone();
                    if !self.settings.library_folders.contains(&folder_str) {
                        self.settings.library_folders.push(folder_str);
                    }
                    let _ = self.settings.save();

                    self.is_scanning = true;
                    let cancel = Arc::new(AtomicBool::new(false));
                    let rx = LibraryScanner::scan_directories(vec![folder], &[], cancel);

                    let mut all_scanned = Vec::new();
                    while let Ok(batch) = rx.recv() {
                        all_scanned.extend(batch);
                    }

                    if !all_scanned.is_empty() {
                        self.tracks = all_scanned;
                        self.thumbnail_cache.lock().unwrap().clear();
                        let _ = LibraryCache::save(&self.tracks);
                        self.apply_filter_and_sort();
                    }
                    self.is_scanning = false;
                }
            }
            AppMessage::TracksDiscovered(batch) => {
                self.tracks.extend(batch);
                self.thumbnail_cache.lock().unwrap().clear();
                let _ = LibraryCache::save(&self.tracks);
                self.apply_filter_and_sort();
            }
            AppMessage::SetGlobalScale(scale) => {
                let clamped = (scale.clamp(0.75, 1.5) * 20.0).round() / 20.0;
                self.global_scale = clamped;
                self.settings.global_scale = clamped;
                let _ = self.settings.save();
            }
            AppMessage::ToggleArtworkPanel => {
                self.show_artwork_panel = !self.show_artwork_panel;
                self.settings.show_artwork_panel = self.show_artwork_panel;
                let _ = self.settings.save();
            }
            AppMessage::ToggleUseSystemAccent => {
                self.settings.use_system_accent_color = !self.settings.use_system_accent_color;
                let _ = self.settings.save();
            }
            AppMessage::SetAccentColor(color) => {
                self.settings.accent_color_hex = format!("#{:02X}{:02X}{:02X}", color.r, color.g, color.b);
                let _ = self.settings.save();
            }
            AppMessage::SetShuffleMemory(val) => {
                self.settings.shuffle_memory_size = val.clamp(0.0, 100.0) as u32;
                let _ = self.settings.save();
            }
            AppMessage::ToggleOnlineMetadata => {
                self.settings.enable_online_metadata = !self.settings.enable_online_metadata;
                let _ = self.settings.save();
            }
        }
    }

    fn view(&self, _input: &Self::Input, context: &mut ViewContext<Self>) -> View {
        context.window_title("Music Power 3");
        context.window_visuals(
            WindowVisuals::new()
                .backdrop(WindowBackdrop::Mica)
                .icon("Assets\\icon.ico")
                .client_size(1080.0, 720.0),
        );

        let scale = self.global_scale.clamp(0.75, 1.5);

        let active_title = self
            .current_track
            .as_ref()
            .map(|t| t.display_title().to_string())
            .unwrap_or_else(|| "Music Power 3".to_string());

        let active_artist = self
            .current_track
            .as_ref()
            .map(|t| t.display_artist().to_string())
            .unwrap_or_else(|| "No track selected".to_string());

        let total_time_str = if self.duration_ms > 0 {
            format_time_ms(self.duration_ms)
        } else {
            "-:--".to_string()
        };

        let current_time_str = format_time_ms(self.position_ms);

        let seek_percent = if self.duration_ms > 0 {
            (self.position_ms as f64 / self.duration_ms as f64) * 100.0
        } else {
            0.0
        };

        let play_pause_symbol = if self.is_playing {
            Symbol::Pause
        } else {
            Symbol::Play
        };

        let queue_label = if !self.queue.is_empty() {
            format!(" ({})", self.queue.len())
        } else {
            String::new()
        };

        let shuffle_style = if self.is_shuffle {
            ButtonStyle::Accent
        } else {
            ButtonStyle::Subtle
        };

        let repeat_style = if self.is_repeat {
            ButtonStyle::Accent
        } else {
            ButtonStyle::Subtle
        };

        Grid::new()
            .rows([
                GridLength::Auto,
                GridLength::STAR,
                GridLength::Auto,
            ])
            .children((
                // Row 0: AppTitleBar (Height ~32) with Custom App Icon & "Music Power 3" Caption
                StackPanel::new()
                    .grid_row(0)
                    .orientation(Orientation::Horizontal)
                    .vertical_alignment(VerticalAlignment::Center)
                    .horizontal_alignment(HorizontalAlignment::Left)
                    .margin(Thickness::new(16.0, 8.0, 0.0, 4.0))
                    .spacing(8.0)
                    .children((
                        Image::new()
                            .source_data(EncodedImage::from_static(APP_ICON_PNG_32))
                            .width(18.0)
                            .height(18.0)
                            .vertical_alignment(VerticalAlignment::Center),
                        TextBlock::new()
                            .text("Music Power 3")
                            .font_size(12.0)
                            .font_weight(FontWeight::SEMI_BOLD)
                            .opacity(0.85)
                            .vertical_alignment(VerticalAlignment::Center),
                    )),

                // Row 1: Content Area (Library / Overlays) inside Windows 11 Card Border
                Border::new()
                    .grid_row(1)
                    .margin(Thickness::new(16.0, 4.0, 16.0, 12.0))
                    .background(Brush::Theme(ThemeBrush::CardBackground))
                    .border_brush(Brush::Theme(ThemeBrush::CardStroke))
                    .border_thickness(Thickness::uniform(1.0))
                    .corner_radius(CornerRadius::uniform(8.0))
                    .content(match self.active_view {
                        ActiveView::Library => self.render_library_main(context),
                        ActiveView::Settings => self.render_settings_overlay(context),
                        ActiveView::Queue => self.render_queue_overlay(context),
                        ActiveView::EditTrack(idx) => self.render_edit_overlay(context, idx),
                    }),

                // Row 2: Bottom Floating Transport Bar
                Border::new()
                    .grid_row(2)
                    .margin(Thickness::new(16.0, 0.0, 16.0, 16.0))
                    .padding(Thickness::new(24.0, 16.0, 24.0, 16.0))
                    .background(Brush::Theme(ThemeBrush::CardBackground))
                    .border_brush(Brush::Theme(ThemeBrush::CardStroke))
                    .border_thickness(Thickness::uniform(1.0))
                    .corner_radius(CornerRadius::uniform(8.0))
                    .content(
                        Grid::new()
                            .rows([
                                GridLength::Auto,
                                GridLength::Auto,
                            ])
                            .children((
                                // Row 0: Seek bar (Current time, Slider, Total time)
                                Grid::new()
                                    .grid_row(0)
                                    .columns([
                                        GridLength::Auto,
                                        GridLength::STAR,
                                        GridLength::Auto,
                                    ])
                                    .children((
                                        TextBlock::new()
                                            .grid_column(0)
                                            .text(&current_time_str)
                                            .font_size((12.0 * scale).round())
                                            .opacity(0.7)
                                            .vertical_alignment(VerticalAlignment::Center),
                                        Slider::new()
                                            .grid_column(1)
                                            .minimum(0.0)
                                            .maximum(100.0)
                                            .value(seek_percent)
                                            .margin(Thickness::new(12.0, 0.0, 12.0, 0.0))
                                            .vertical_alignment(VerticalAlignment::Center)
                                            .on_value_changed(context.callback(|v| AppMessage::Seek(v))),
                                        TextBlock::new()
                                            .grid_column(2)
                                            .text(&total_time_str)
                                            .font_size((12.0 * scale).round())
                                            .opacity(0.7)
                                            .vertical_alignment(VerticalAlignment::Center),
                                    )),

                                // Row 1: Now Playing Info, Transport Controls, Extra controls
                                Grid::new()
                                    .grid_row(1)
                                    .margin(Thickness::new(0.0, 8.0, 0.0, 0.0))
                                    .columns([
                                        GridLength::STAR,
                                        GridLength::Auto,
                                        GridLength::STAR,
                                    ])
                                    .children((
                                        // Left: Now Playing Info
                                        StackPanel::new()
                                            .grid_column(0)
                                            .vertical_alignment(VerticalAlignment::Center)
                                            .horizontal_alignment(HorizontalAlignment::Left)
                                            .spacing(2.0)
                                            .children((
                                                TextBlock::new()
                                                    .text(active_title)
                                                    .font_size((14.0 * scale).round())
                                                    .font_weight(FontWeight::SEMI_BOLD),
                                                TextBlock::new()
                                                    .text(active_artist)
                                                    .font_size((12.0 * scale).round())
                                                    .opacity(0.7),
                                            )),

                                        // Center: Transport buttons (Shuffle, Previous, Play/Pause Accent, Next, Repeat)
                                        StackPanel::new()
                                            .grid_column(1)
                                            .orientation(Orientation::Horizontal)
                                            .horizontal_alignment(HorizontalAlignment::Center)
                                            .vertical_alignment(VerticalAlignment::Center)
                                            .spacing(8.0)
                                            .children((
                                                Button::new()
                                                    .style(shuffle_style)
                                                    .on_click(context.callback(|_| AppMessage::ToggleShuffle))
                                                    .content(SymbolIcon::new().symbol(Symbol::Shuffle)),
                                                Button::new()
                                                    .style(ButtonStyle::Subtle)
                                                    .on_click(context.callback(|_| AppMessage::PreviousTrack))
                                                    .content(SymbolIcon::new().symbol(Symbol::Previous)),
                                                Button::new()
                                                    .style(ButtonStyle::Accent)
                                                    .width((56.0 * scale).round())
                                                    .height((38.0 * scale).round())
                                                    .on_click(context.callback(|_| AppMessage::PlayPauseToggle))
                                                    .content(SymbolIcon::new().symbol(play_pause_symbol)),
                                                Button::new()
                                                    .style(ButtonStyle::Subtle)
                                                    .on_click(context.callback(|_| AppMessage::NextTrack))
                                                    .content(SymbolIcon::new().symbol(Symbol::Next)),
                                                Button::new()
                                                    .style(repeat_style)
                                                    .on_click(context.callback(|_| AppMessage::ToggleRepeat))
                                                    .content(SymbolIcon::new().symbol(Symbol::RepeatAll)),
                                            )),

                                        // Right: Queue button & Volume slider
                                        StackPanel::new()
                                            .grid_column(2)
                                            .orientation(Orientation::Horizontal)
                                            .horizontal_alignment(HorizontalAlignment::Right)
                                            .vertical_alignment(VerticalAlignment::Center)
                                            .spacing(8.0)
                                            .children((
                                                Button::new()
                                                    .style(ButtonStyle::Subtle)
                                                    .on_click(context.callback(|_| AppMessage::OpenQueue))
                                                    .content(
                                                        StackPanel::new()
                                                            .orientation(Orientation::Horizontal)
                                                            .spacing(4.0)
                                                            .children((
                                                                SymbolIcon::new().symbol(Symbol::List),
                                                                TextBlock::new().text(queue_label),
                                                            ))
                                                    ),
                                                SymbolIcon::new().symbol(Symbol::Volume),
                                                Slider::new()
                                                    .minimum(0.0)
                                                    .maximum(100.0)
                                                    .value(self.volume as f64)
                                                    .width((100.0 * scale).round())
                                                    .vertical_alignment(VerticalAlignment::Center)
                                                    .on_value_changed(context.callback(|v| AppMessage::VolumeChanged(v))),
                                            )),
                                    )),
                            ))
                    ),
            ))
    }
}

impl MainApp {
    fn apply_filter_and_sort(&mut self) {
        let mut matches = SearchEngine::filter(&self.tracks, &self.search_query)
            .into_iter()
            .cloned()
            .collect::<Vec<_>>();

        match self.sort_index {
            0 => matches.sort_by(|a, b| a.display_title().to_lowercase().cmp(&b.display_title().to_lowercase())),
            1 => matches.sort_by(|a, b| b.display_title().to_lowercase().cmp(&a.display_title().to_lowercase())),
            2 => matches.sort_by(|a, b| a.display_artist().to_lowercase().cmp(&b.display_artist().to_lowercase())),
            3 => matches.sort_by(|a, b| b.display_artist().to_lowercase().cmp(&a.display_artist().to_lowercase())),
            4 => matches.sort_by(|a, b| a.album.to_lowercase().cmp(&b.album.to_lowercase())),
            5 => matches.sort_by(|a, b| b.album.to_lowercase().cmp(&a.album.to_lowercase())),
            6 => matches.sort_by_key(|t| t.duration_ms),
            7 => matches.sort_by(|a, b| b.duration_ms.cmp(&a.duration_ms)),
            _ => {}
        }

        self.filtered_tracks = matches;
    }

    fn play_track_internal(&mut self, track: Track, context: &ComponentContext<Self>) {
        if let Some(ref player) = self.player {
            if player.play_track(&track).is_ok() {
                self.is_playing = true;
                self.duration_ms = track.duration_ms;
                self.position_ms = 0;
                if let Some(ref smtc) = self.smtc {
                    let _ = smtc.update_track(&track);
                }
                context.spawn_background(|_| {
                    std::thread::sleep(std::time::Duration::from_millis(100));
                    AppMessage::PlaybackTick
                });
            }
        }
        self.current_artwork_bytes = crate::metadata::read_artwork_bytes(&track.file_path).map(Arc::from);
        self.current_track = Some(track);
    }

    fn render_library_main(&self, context: &mut ViewContext<Self>) -> View {
        let scale = self.global_scale.clamp(0.75, 1.5);
        let search_count_text = if !self.search_query.is_empty() {
            format!("{} results", self.filtered_tracks.len())
        } else {
            String::new()
        };

        let title_font_size = (28.0 * scale).round();
        let track_title_size = (14.0 * scale).round();
        let track_artist_size = (12.0 * scale).round();
        let duration_size = (12.0 * scale).round();
        let thumb_size = (48.0 * scale).round();
        let artwork_max_dim = 600.0 * scale;

        Grid::new()
            .columns([
                GridLength::STAR,
                GridLength::Auto,
            ])
            .children((
                // Column 0: Library Container
                Grid::new()
                    .grid_column(0)
                    .margin(Thickness::uniform(20.0))
                    .rows([
                        GridLength::Auto,
                        GridLength::Auto,
                        GridLength::STAR,
                    ])
                    .children((
                        // Row 0: Title and Toolbar Buttons
                        Grid::new()
                            .grid_row(0)
                            .margin(Thickness::new(0.0, 0.0, 0.0, 16.0))
                            .columns([
                                GridLength::STAR,
                                GridLength::Auto,
                            ])
                            .children((
                                TextBlock::new()
                                    .grid_column(0)
                                    .text("Library")
                                    .font_size(title_font_size)
                                    .font_weight(FontWeight::BOLD)
                                    .vertical_alignment(VerticalAlignment::Center),

                                StackPanel::new()
                                    .grid_column(1)
                                    .orientation(Orientation::Horizontal)
                                    .spacing(6.0)
                                    .children((
                                        ProgressRing::new()
                                            .is_active(self.is_scanning)
                                            .width(20.0)
                                            .height(20.0)
                                            .vertical_alignment(VerticalAlignment::Center),
                                        Button::new()
                                            .style(ButtonStyle::Subtle)
                                            .content(FontIcon::new().glyph("\u{E762}")),
                                        Button::new()
                                            .style(ButtonStyle::Subtle)
                                            .on_click(context.callback(|_| AppMessage::ScanLibrary))
                                            .content(SymbolIcon::new().symbol(Symbol::Refresh)),
                                        Button::new()
                                            .style(ButtonStyle::Subtle)
                                            .on_click(context.callback(|_| AppMessage::OpenSettings))
                                            .content(SymbolIcon::new().symbol(Symbol::Setting)),
                                    )),
                            )),

                        // Row 1: Search and Sort Row
                        Grid::new()
                            .grid_row(1)
                            .margin(Thickness::new(0.0, 0.0, 0.0, 12.0))
                            .columns([
                                GridLength::STAR,
                                GridLength::Auto,
                                GridLength::Auto,
                            ])
                            .children((
                                AutoSuggestBox::new()
                                    .grid_column(0)
                                    .placeholder_text("Search tracks")
                                    .on_text_changed(context.callback(|e| AppMessage::SearchChanged(e))),

                                TextBlock::new()
                                    .grid_column(1)
                                    .text(&search_count_text)
                                    .font_size(12.0)
                                    .opacity(0.7)
                                    .margin(Thickness::new(12.0, 0.0, 12.0, 0.0))
                                    .vertical_alignment(VerticalAlignment::Center),

                                ComboBox::new()
                                    .grid_column(2)
                                    .width(180.0)
                                    .items_source([
                                        "Title (A-Z)",
                                        "Title (Z-A)",
                                        "Artist (A-Z)",
                                        "Artist (Z-A)",
                                        "Album (A-Z)",
                                        "Album (Z-A)",
                                        "Date Added (Newest)",
                                        "Date Added (Oldest)",
                                        "Date Modified (Newest)",
                                        "Date Modified (Oldest)",
                                        "Duration (Longest)",
                                        "Duration (Shortest)",
                                    ])
                                    .selected_index(self.sort_index)
                                    .on_selection_changed(context.callback(|idx| AppMessage::SortChanged(idx))),
                            )),

                        // Row 2: Track List
                        ScrollViewer::new()
                            .grid_row(2)
                            .vertical_scroll_bar_visibility(ScrollBarVisibility::Auto)
                            .content(
                                StackPanel::new()
                                    .spacing(2.0)
                                    .keyed_children(
                                        self.filtered_tracks
                                            .iter()
                                            .enumerate()
                                            .take(200)
                                            .map(|(idx, track)| {
                                                let is_current = self
                                                    .current_track
                                                    .as_ref()
                                                    .map(|c| c.file_path == track.file_path)
                                                    .unwrap_or(false);

                                                let duration_str = track.display_duration();

                                                let thumb_data: Option<Arc<[u8]>> = {
                                                    let mut cache = self.thumbnail_cache.lock().unwrap();
                                                    if let Some(cached) = cache.get(&track.file_path) {
                                                        cached.clone()
                                                    } else {
                                                        let loaded = crate::metadata::read_artwork_bytes(&track.file_path)
                                                            .map(|bytes| Arc::from(bytes.into_boxed_slice()));
                                                        cache.insert(track.file_path.clone(), loaded.clone());
                                                        loaded
                                                    }
                                                };

                                                let playing_overlay = if is_current {
                                                    View::from(
                                                        Border::new()
                                                            .background(Color::argb(136, 0, 0, 0))
                                                            .corner_radius(CornerRadius::uniform(4.0))
                                                            .content(
                                                                SymbolIcon::new()
                                                                    .symbol(if self.is_playing {
                                                                        Symbol::Pause
                                                                    } else {
                                                                        Symbol::Play
                                                                    })
                                                                    .horizontal_alignment(HorizontalAlignment::Center)
                                                                    .vertical_alignment(VerticalAlignment::Center),
                                                            )
                                                    )
                                                } else {
                                                    View::from(Border::new().width(0.0).height(0.0))
                                                };

                                                let thumbnail_content = if let Some(ref bytes) = thumb_data {
                                                    Grid::new().children((
                                                        Image::new()
                                                            .source_data(EncodedImage::new(bytes.clone()))
                                                            .stretch(Stretch::UniformToFill),
                                                        playing_overlay,
                                                    ))
                                                } else {
                                                    Grid::new().children((
                                                        SymbolIcon::new()
                                                            .symbol(Symbol::MusicInfo)
                                                            .opacity(0.4)
                                                            .horizontal_alignment(HorizontalAlignment::Center)
                                                            .vertical_alignment(VerticalAlignment::Center),
                                                        playing_overlay,
                                                    ))
                                                };

                                                let row_grid = Grid::new()
                                                    .columns([
                                                        GridLength::Auto,
                                                        GridLength::Auto,
                                                        GridLength::STAR,
                                                        GridLength::Auto,
                                                    ])
                                                    .children((
                                                        if is_current {
                                                            Border::new()
                                                                .grid_column(0)
                                                                .width(3.0)
                                                                .height(24.0)
                                                                .background(Brush::Theme(ThemeBrush::Accent))
                                                                .corner_radius(CornerRadius::uniform(1.5))
                                                                .margin(Thickness::new(0.0, 0.0, 8.0, 0.0))
                                                                .vertical_alignment(VerticalAlignment::Center)
                                                        } else {
                                                            Border::new().grid_column(0).width(0.0).height(0.0)
                                                        },

                                                        // 48x48 rounded thumbnail (scaled)
                                                        Border::new()
                                                            .grid_column(1)
                                                            .width(thumb_size)
                                                            .height(thumb_size)
                                                            .corner_radius(CornerRadius::uniform(4.0))
                                                            .background(Brush::Theme(ThemeBrush::CardStroke))
                                                            .content(thumbnail_content),

                                                        // Title and Artist
                                                        StackPanel::new()
                                                            .grid_column(2)
                                                            .margin(Thickness::new(12.0, 0.0, 0.0, 0.0))
                                                            .vertical_alignment(VerticalAlignment::Center)
                                                            .spacing(2.0)
                                                            .children((
                                                                TextBlock::new()
                                                                    .text(track.display_title())
                                                                    .font_size(track_title_size)
                                                                    .font_weight(if is_current {
                                                                        FontWeight::BOLD
                                                                    } else {
                                                                        FontWeight::SEMI_BOLD
                                                                    }),
                                                                TextBlock::new()
                                                                    .text(track.display_artist())
                                                                    .font_size(track_artist_size)
                                                                    .opacity(0.7),
                                                            )),

                                                        // Duration
                                                        TextBlock::new()
                                                            .grid_column(3)
                                                            .text(&duration_str)
                                                            .font_size(duration_size)
                                                            .opacity(0.7)
                                                            .margin(Thickness::new(0.0, 0.0, 12.0, 0.0))
                                                            .vertical_alignment(VerticalAlignment::Center),
                                                    ));

                                                let item_border = Border::new()
                                                    .padding(Thickness::new(8.0, 4.0, 8.0, 4.0))
                                                    .corner_radius(CornerRadius::uniform(4.0))
                                                    .background(if is_current {
                                                        Brush::Theme(ThemeBrush::CardBackground)
                                                    } else {
                                                        Brush::Solid(Color::argb(0, 0, 0, 0))
                                                    })
                                                    .border_brush(if is_current {
                                                        Brush::Theme(ThemeBrush::CardStroke)
                                                    } else {
                                                        Brush::Solid(Color::argb(0, 0, 0, 0))
                                                    })
                                                    .border_thickness(Thickness::uniform(1.0))
                                                    .content(row_grid);

                                                let item_view = Button::new()
                                                    .style(ButtonStyle::Subtle)
                                                    .horizontal_alignment(HorizontalAlignment::Stretch)
                                                    .horizontal_content_alignment(HorizontalAlignment::Stretch)
                                                    .on_click(context.callback(move |_| AppMessage::PlayTrack(idx)))
                                                    .content(item_border);

                                                KeyedView::new(idx as u64, item_view)
                                            }),
                                    )
                            ),
                    )),

                // Column 1: ArtworkPanel (Right side panel with full resolution artwork)
                if self.show_artwork_panel {
                    Border::new()
                        .grid_column(1)
                        .corner_radius(CornerRadius::new(0.0, 8.0, 8.0, 0.0))
                        .vertical_alignment(VerticalAlignment::Stretch)
                        .horizontal_alignment(HorizontalAlignment::Center)
                        .content(
                            if let Some(ref bytes) = self.current_artwork_bytes {
                                View::from(
                                    Image::new()
                                        .source_data(EncodedImage::new(bytes.clone()))
                                        .stretch(Stretch::Uniform)
                                        .max_width(artwork_max_dim)
                                        .max_height(artwork_max_dim)
                                        .horizontal_alignment(HorizontalAlignment::Center)
                                        .vertical_alignment(VerticalAlignment::Center)
                                )
                            } else {
                                View::from(
                                    TextBlock::new()
                                        .text("\u{E8D6}")
                                        .font_size((120.0 * scale).round())
                                        .opacity(0.4)
                                        .horizontal_alignment(HorizontalAlignment::Center)
                                        .vertical_alignment(VerticalAlignment::Center)
                                )
                            }
                        )
                } else {
                    View::from(Border::new().grid_column(1).width(0.0).height(0.0))
                },
            ))
    }

    fn render_settings_overlay(&self, context: &mut ViewContext<Self>) -> View {
        let accent_color = parse_hex_color(&self.settings.accent_color_hex)
            .unwrap_or_else(|| Color::argb(255, 0, 120, 212));

        let current_path_display = if self.settings.last_library_path.is_empty() {
            "No folder selected".to_string()
        } else {
            self.settings.last_library_path.clone()
        };

        Grid::new()
            .margin(Thickness::uniform(24.0))
            .rows([
                GridLength::Auto,
                GridLength::STAR,
            ])
            .children((
                // Header: Title and Close Button
                Grid::new()
                    .grid_row(0)
                    .margin(Thickness::new(0.0, 0.0, 0.0, 16.0))
                    .columns([
                        GridLength::STAR,
                        GridLength::Auto,
                    ])
                    .children((
                        TextBlock::new()
                            .grid_column(0)
                            .text("Settings")
                            .font_size(24.0)
                            .font_weight(FontWeight::BOLD)
                            .vertical_alignment(VerticalAlignment::Center),
                        Button::new()
                            .grid_column(1)
                            .style(ButtonStyle::Subtle)
                            .on_click(context.callback(|_| AppMessage::CloseSettings))
                            .content(SymbolIcon::new().symbol(Symbol::Cancel)),
                    )),

                // Scrollable Settings Cards
                ScrollViewer::new()
                    .grid_row(1)
                    .vertical_scroll_bar_visibility(ScrollBarVisibility::Auto)
                    .content(
                        StackPanel::new()
                            .spacing(16.0)
                            .children((
                                // 1. Appearance Card
                                Border::new()
                                    .padding(Thickness::uniform(16.0))
                                    .background(Brush::Theme(ThemeBrush::CardStroke))
                                    .corner_radius(CornerRadius::uniform(8.0))
                                    .content(
                                        StackPanel::new()
                                            .spacing(14.0)
                                            .children((
                                                TextBlock::new()
                                                    .text("Appearance")
                                                    .font_size(18.0)
                                                    .font_weight(FontWeight::SEMI_BOLD),

                                                // Use Windows accent color switch
                                                StackPanel::new()
                                                    .orientation(Orientation::Horizontal)
                                                    .spacing(10.0)
                                                    .vertical_alignment(VerticalAlignment::Center)
                                                    .children((
                                                        ToggleSwitch::new()
                                                            .is_on(self.settings.use_system_accent_color)
                                                            .on_toggled(context.callback(|_| AppMessage::ToggleUseSystemAccent)),
                                                        TextBlock::new()
                                                            .text("Use Windows accent color")
                                                            .font_size(14.0)
                                                            .vertical_alignment(VerticalAlignment::Center),
                                                    )),

                                                // Custom Accent Section
                                                if !self.settings.use_system_accent_color {
                                                    StackPanel::new()
                                                        .spacing(8.0)
                                                        .children((
                                                            TextBlock::new()
                                                                .text("Custom accent color")
                                                                .font_size(14.0)
                                                                .font_weight(FontWeight::SEMI_BOLD),
                                                            TextBlock::new()
                                                                .text("Applied to buttons, sliders, switches, selection and highlights across the app.")
                                                                .font_size(12.0)
                                                                .opacity(0.7),
                                                            ColorPicker::new()
                                                                .color(accent_color)
                                                                .is_hex_input_visible(true)
                                                                .is_alpha_enabled(false)
                                                                .is_color_slider_visible(false)
                                                                .on_color_changed(context.callback(|c| AppMessage::SetAccentColor(c))),
                                                        ))
                                                } else {
                                                    View::from(StackPanel::new())
                                                },

                                                // Interface Scale
                                                StackPanel::new()
                                                    .spacing(4.0)
                                                    .children((
                                                        Grid::new()
                                                            .columns([GridLength::STAR, GridLength::Auto])
                                                            .children((
                                                                TextBlock::new()
                                                                    .grid_column(0)
                                                                    .text("Interface scale")
                                                                    .font_size(14.0),
                                                                TextBlock::new()
                                                                    .grid_column(1)
                                                                    .text(format!("{:.0}%", self.global_scale * 100.0))
                                                                    .font_size(12.0)
                                                                    .opacity(0.7),
                                                            )),
                                                        Slider::new()
                                                            .minimum(0.75)
                                                            .maximum(1.5)
                                                            .step_frequency(0.05)
                                                            .value(self.global_scale)
                                                            .on_value_changed(context.callback(|v| AppMessage::SetGlobalScale(v))),
                                                    )),

                                                // Show large artwork panel toggle
                                                StackPanel::new()
                                                    .orientation(Orientation::Horizontal)
                                                    .spacing(10.0)
                                                    .vertical_alignment(VerticalAlignment::Center)
                                                    .children((
                                                        ToggleSwitch::new()
                                                            .is_on(self.show_artwork_panel)
                                                            .on_toggled(context.callback(|_| AppMessage::ToggleArtworkPanel)),
                                                        TextBlock::new()
                                                            .text("Show large artwork panel")
                                                            .font_size(14.0)
                                                            .vertical_alignment(VerticalAlignment::Center),
                                                    )),
                                            ))
                                    ),

                                // 2. Playback Card
                                Border::new()
                                    .padding(Thickness::uniform(16.0))
                                    .background(Brush::Theme(ThemeBrush::CardStroke))
                                    .corner_radius(CornerRadius::uniform(8.0))
                                    .content(
                                        StackPanel::new()
                                            .spacing(14.0)
                                            .children((
                                                TextBlock::new()
                                                    .text("Playback")
                                                    .font_size(18.0)
                                                    .font_weight(FontWeight::SEMI_BOLD),

                                                // Shuffle memory
                                                StackPanel::new()
                                                    .spacing(4.0)
                                                    .children((
                                                        Grid::new()
                                                            .columns([GridLength::STAR, GridLength::Auto])
                                                            .children((
                                                                TextBlock::new()
                                                                    .grid_column(0)
                                                                    .text("Shuffle memory (tracks played before repeat allowed)")
                                                                    .font_size(14.0),
                                                                TextBlock::new()
                                                                    .grid_column(1)
                                                                    .text(format!("{} tracks", self.settings.shuffle_memory_size))
                                                                    .font_size(12.0)
                                                                    .opacity(0.7),
                                                            )),
                                                        Slider::new()
                                                            .minimum(0.0)
                                                            .maximum(100.0)
                                                            .step_frequency(1.0)
                                                            .value(self.settings.shuffle_memory_size as f64)
                                                            .on_value_changed(context.callback(|v| AppMessage::SetShuffleMemory(v))),
                                                    )),
                                            ))
                                    ),

                                // 3. Library Card
                                Border::new()
                                    .padding(Thickness::uniform(16.0))
                                    .background(Brush::Theme(ThemeBrush::CardStroke))
                                    .corner_radius(CornerRadius::uniform(8.0))
                                    .content(
                                        StackPanel::new()
                                            .spacing(14.0)
                                            .children((
                                                TextBlock::new()
                                                    .text("Library")
                                                    .font_size(18.0)
                                                    .font_weight(FontWeight::SEMI_BOLD),

                                                // Music folder display
                                                StackPanel::new()
                                                    .spacing(2.0)
                                                    .children((
                                                        TextBlock::new()
                                                            .text("Music folder")
                                                            .font_size(14.0),
                                                        TextBlock::new()
                                                            .text(current_path_display)
                                                            .font_size(12.0)
                                                            .opacity(0.7),
                                                    )),

                                                // Folder action buttons
                                                Button::new()
                                                    .style(ButtonStyle::Accent)
                                                    .on_click(context.callback(|_| AppMessage::ChooseMusicFolder))
                                                    .content("Choose music folder"),

                                                Button::new()
                                                    .style(ButtonStyle::Subtle)
                                                    .on_click(context.callback(|_| AppMessage::ScanLibrary))
                                                    .content("Scan folder for new tracks"),

                                                // Online metadata lookup switch
                                                StackPanel::new()
                                                    .spacing(4.0)
                                                    .children((
                                                        StackPanel::new()
                                                            .orientation(Orientation::Horizontal)
                                                            .spacing(10.0)
                                                            .vertical_alignment(VerticalAlignment::Center)
                                                            .children((
                                                                ToggleSwitch::new()
                                                                    .is_on(self.settings.enable_online_metadata)
                                                                    .on_toggled(context.callback(|_| AppMessage::ToggleOnlineMetadata)),
                                                                TextBlock::new()
                                                                    .text("Online metadata lookup")
                                                                    .font_size(14.0)
                                                                    .vertical_alignment(VerticalAlignment::Center),
                                                            )),
                                                        TextBlock::new()
                                                            .text("Lets the track editor fetch the title and artwork from a Spotify, YouTube or SoundCloud link. Nothing is sent unless you press Fetch.")
                                                            .font_size(12.0)
                                                            .opacity(0.6),
                                                    )),
                                            ))
                                    ),
                            ))
                    ),
            ))
    }

    fn render_queue_overlay(&self, context: &mut ViewContext<Self>) -> View {
        let queue_empty_view = TextBlock::new()
            .text("Queue is empty. Use 'Add to queue' on any track.")
            .margin(Thickness::uniform(20.0))
            .opacity(0.6);

        let queue_content: View = if self.queue.is_empty() {
            queue_empty_view.into()
        } else {
            StackPanel::new()
                .spacing(4.0)
                .keyed_children(
                    self.queue.iter().enumerate().map(|(idx, track)| {
                        let item = Border::new()
                            .padding(Thickness::uniform(8.0))
                            .corner_radius(CornerRadius::uniform(4.0))
                            .content(
                                Grid::new()
                                    .columns([
                                        GridLength::STAR,
                                        GridLength::Auto,
                                    ])
                                    .children((
                                        StackPanel::new()
                                            .grid_column(0)
                                            .children((
                                                TextBlock::new()
                                                    .text(track.display_title())
                                                    .font_weight(FontWeight::SEMI_BOLD),
                                                TextBlock::new()
                                                    .text(track.display_artist())
                                                    .font_size(12.0)
                                                    .opacity(0.7),
                                            )),
                                        Button::new()
                                            .grid_column(1)
                                            .style(ButtonStyle::Subtle)
                                            .on_click(context.callback(move |_| AppMessage::RemoveFromQueue(idx)))
                                            .content(SymbolIcon::new().symbol(Symbol::Delete)),
                                    ))
                            );
                        KeyedView::new(idx as u64, item)
                    })
                )
        };

        Grid::new()
            .margin(Thickness::uniform(24.0))
            .rows([
                GridLength::Auto,
                GridLength::STAR,
            ])
            .children((
                Grid::new()
                    .grid_row(0)
                    .margin(Thickness::new(0.0, 0.0, 0.0, 16.0))
                    .columns([
                        GridLength::STAR,
                        GridLength::Auto,
                        GridLength::Auto,
                    ])
                    .children((
                        TextBlock::new()
                            .grid_column(0)
                            .text("Up next")
                            .font_size(24.0)
                            .font_weight(FontWeight::BOLD)
                            .vertical_alignment(VerticalAlignment::Center),
                        Button::new()
                            .grid_column(1)
                            .margin(Thickness::new(0.0, 0.0, 8.0, 0.0))
                            .on_click(context.callback(|_| AppMessage::ClearQueue))
                            .content("Clear queue"),
                        Button::new()
                            .grid_column(2)
                            .style(ButtonStyle::Subtle)
                            .on_click(context.callback(|_| AppMessage::CloseQueue))
                            .content(SymbolIcon::new().symbol(Symbol::Cancel)),
                    )),

                ScrollViewer::new()
                    .grid_row(1)
                    .vertical_scroll_bar_visibility(ScrollBarVisibility::Auto)
                    .content(queue_content),
            ))
    }

    fn render_edit_overlay(&self, context: &mut ViewContext<Self>, _idx: usize) -> View {
        Grid::new()
            .margin(Thickness::uniform(24.0))
            .rows([
                GridLength::Auto,
                GridLength::STAR,
                GridLength::Auto,
            ])
            .children((
                TextBlock::new()
                    .grid_row(0)
                    .text("Edit track info")
                    .font_size(24.0)
                    .font_weight(FontWeight::BOLD)
                    .margin(Thickness::new(0.0, 0.0, 0.0, 16.0)),

                ScrollViewer::new()
                    .grid_row(1)
                    .vertical_scroll_bar_visibility(ScrollBarVisibility::Auto)
                    .content(
                        StackPanel::new()
                            .spacing(16.0)
                            .max_width(720.0)
                            .children((
                                Grid::new()
                                    .columns([
                                        GridLength::Auto,
                                        GridLength::STAR,
                                    ])
                                    .children((
                                        // Left artwork preview
                                        StackPanel::new()
                                            .grid_column(0)
                                            .width(160.0)
                                            .spacing(8.0)
                                            .children((
                                                Border::new()
                                                    .width(160.0)
                                                    .height(160.0)
                                                    .corner_radius(CornerRadius::uniform(4.0))
                                                    .background(Brush::Theme(ThemeBrush::CardStroke))
                                                    .content(
                                                        Image::new()
                                                            .source_data(EncodedImage::from_static(APP_ICON_PNG_256))
                                                            .width(120.0)
                                                            .height(120.0)
                                                            .horizontal_alignment(HorizontalAlignment::Center)
                                                            .vertical_alignment(VerticalAlignment::Center)
                                                    ),
                                                Button::new().content("Choose image..."),
                                                Button::new().content("Clear artwork"),
                                            )),

                                        // Right metadata inputs
                                        StackPanel::new()
                                            .grid_column(1)
                                            .margin(Thickness::new(16.0, 0.0, 0.0, 0.0))
                                            .spacing(12.0)
                                            .children((
                                                TextBox::new()
                                                    .placeholder_text("Title")
                                                    .text(&self.edit_form.title)
                                                    .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::Title, t))),
                                                TextBox::new()
                                                    .placeholder_text("Artist (separate performers with ;)")
                                                    .text(&self.edit_form.artist)
                                                    .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::Artist, t))),
                                                TextBox::new()
                                                    .placeholder_text("Album")
                                                    .text(&self.edit_form.album)
                                                    .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::Album, t))),
                                                TextBox::new()
                                                    .placeholder_text("Album artist (separate with ;)")
                                                    .text(&self.edit_form.album_artist)
                                                    .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::AlbumArtist, t))),
                                            )),
                                    )),

                                Grid::new()
                                    .columns([
                                        GridLength::STAR,
                                        GridLength::STAR,
                                        GridLength::STAR,
                                    ])
                                    .children((
                                        TextBox::new()
                                            .grid_column(0)
                                            .placeholder_text("Track #")
                                            .text(&self.edit_form.track_number)
                                            .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::TrackNumber, t))),
                                        TextBox::new()
                                            .grid_column(1)
                                            .placeholder_text("Disc #")
                                            .text(&self.edit_form.disc_number)
                                            .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::DiscNumber, t))),
                                        TextBox::new()
                                            .grid_column(2)
                                            .placeholder_text("Year")
                                            .text(&self.edit_form.year)
                                            .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::Year, t))),
                                    )),

                                TextBox::new()
                                    .placeholder_text("Genre (separate with ;)")
                                    .text(&self.edit_form.genre)
                                    .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::Genre, t))),

                                TextBox::new()
                                    .placeholder_text("Comment")
                                    .text(&self.edit_form.comment)
                                    .accepts_return(true)
                                    .on_text_changed(context.callback(|t| AppMessage::EditFieldChanged(EditField::Comment, t))),

                                ComboBox::new()
                                    .items_source([
                                        "ID3v2.3 (UTF-16) — Recommended (Windows, Apple Music, Android)",
                                        "ID3v2.3 (ISO-8859-1) — Legacy compatibility",
                                        "ID3v2.4 (UTF-8) — Modern open standard",
                                    ])
                                    .selected_index(self.edit_form.tag_encoding),
                            ))
                    ),

                StackPanel::new()
                    .grid_row(2)
                    .orientation(Orientation::Horizontal)
                    .horizontal_alignment(HorizontalAlignment::Right)
                    .spacing(8.0)
                    .margin(Thickness::new(0.0, 16.0, 0.0, 0.0))
                    .children((
                        Button::new()
                            .on_click(context.callback(|_| AppMessage::CancelEdit))
                            .content("Cancel"),
                        Button::new()
                            .style(ButtonStyle::Accent)
                            .on_click(context.callback(|_| AppMessage::SaveEdit))
                            .content("Save"),
                    )),
            ))
    }
}

fn format_time_ms(ms: u64) -> String {
    let total_secs = ms / 1000;
    let mins = total_secs / 60;
    let secs = total_secs % 60;
    format!("{}:{:02}", mins, secs)
}
