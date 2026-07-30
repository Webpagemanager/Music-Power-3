using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using MusicPower3.Models;
using MusicPower3.Services;
using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MusicPower3
{
    public sealed partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
    {
        public static Microsoft.UI.Dispatching.DispatcherQueue? MainDispatcher { get; private set; }
        public AppSettings Settings { get; set; } = SettingsStore.Load();

        private static readonly HttpClient _httpClient = new HttpClient();

        private bool _isPlaying = false;
        private bool _isScrubbing = false;
        private bool _isUpdatingFromEngine = false;
        private bool _isLoading = false;
        
        private Track? _nowPlayingTrack;
        private List<Track> _fullCache = new List<Track>();
        private ObservableCollection<Track> _libraryTracks = new();
        public ObservableCollection<Track> LibraryTracks { get => _libraryTracks; set { _libraryTracks = value; OnPropertyChanged(); } }

        private List<Track> _playbackQueue = new List<Track>();
        private Stack<Track> _playbackHistory = new Stack<Track>();
        private List<string> _shufflePlayedHistory = new List<string>();
        private int _currentQueueIndex = -1;
        private long _lastSecondsUpdated = -1;
        private bool _isRefreshingAccentTheme = false;
        private readonly Microsoft.UI.Xaml.DispatcherTimer _progressTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        private double _progressThumbOpacity = 0.0;
        public double ProgressThumbOpacity { get => _progressThumbOpacity; set { _progressThumbOpacity = value; OnPropertyChanged(); } }

        public Track? NowPlayingTrack { get => _nowPlayingTrack; set { _nowPlayingTrack = value; OnPropertyChanged(); } }
        public bool IsLoading { get => _isLoading; set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingVisibility)); } }
        public Visibility LoadingVisibility => IsLoading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GetFallbackVisibility(Track? track) => track == null ? Visibility.Visible : Visibility.Collapsed;

        public MainWindow()
        {
            this.InitializeComponent();
            MainDispatcher = this.DispatcherQueue;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            this.Closed += MainWindow_Closed;
            this.AppWindow.SetIcon("Assets\\icon.ico");

            RootGrid.DataContext = this;

            _progressTimer.Tick += ProgressTimer_Tick;

            if (App.MusicEngine != null)
            {
                App.MusicEngine.EndReached += (s, e) => DispatcherQueue.TryEnqueue(() => PlayNext());
                App.MusicEngine.PlayRequested += (s, e) => DispatcherQueue.TryEnqueue(() => { if (!_isPlaying) OnPlayPauseClick(this, new RoutedEventArgs()); });
                App.MusicEngine.PauseRequested += (s, e) => DispatcherQueue.TryEnqueue(() => { if (_isPlaying) OnPlayPauseClick(this, new RoutedEventArgs()); });
                App.MusicEngine.NextRequested += (s, e) => DispatcherQueue.TryEnqueue(() => PlayNext());
                App.MusicEngine.PreviousRequested += (s, e) => DispatcherQueue.TryEnqueue(() => PlayPrevious());
            }

            RootGrid.Loaded += async (s, e) => { ApplySettings(); await LoadLibraryFromCacheAsync(); };
        }

        private void ProgressTimer_Tick(object? sender, object e)
        {
            if (_isScrubbing || App.MusicEngine == null || App.MusicEngine.Length <= 0) return;

            _isUpdatingFromEngine = true;
            ProgressSlider.Value = ((double)App.MusicEngine.Time / App.MusicEngine.Length) * 100.0;
            _isUpdatingFromEngine = false;

            long currentSeconds = App.MusicEngine.Time / 1000;
            if (currentSeconds != _lastSecondsUpdated)
            {
                CurrentTimeText.Text = TimeSpan.FromMilliseconds(App.MusicEngine.Time).ToString(@"m\:ss");
                TotalTimeText.Text = TimeSpan.FromMilliseconds(App.MusicEngine.Length).ToString(@"m\:ss");
                _lastSecondsUpdated = currentSeconds;
            }
        }

        private void SetPlayingState(bool playing)
        {
            _isPlaying = playing;
            if (playing) _progressTimer.Start(); else _progressTimer.Stop();
        }

        private void ProgressSlider_Loaded(object sender, RoutedEventArgs e) { ProgressThumbOpacity = 0.0; }
        private void ProgressSlider_PointerEntered(object sender, PointerRoutedEventArgs e) { ProgressThumbOpacity = 1.0; }
        private void ProgressSlider_PointerExited(object sender, PointerRoutedEventArgs e) { if (!_isScrubbing) ProgressThumbOpacity = 0.0; }
        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e) { _isScrubbing = true; ProgressThumbOpacity = 1.0; }
        
        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isScrubbing = false;
            App.MusicEngine?.SeekTo((float)(ProgressSlider.Value / 100.0));
            ProgressThumbOpacity = 0.0;
        }

        private void ProgressSlider_ValueChanged(object sender, double newVal) { if (!_isUpdatingFromEngine && !_isScrubbing && App.MusicEngine != null) App.MusicEngine.SeekTo((float)(newVal / 100.0)); }

        private void OnAccentColorChanged(Microsoft.UI.Xaml.Controls.ColorPicker sender, Microsoft.UI.Xaml.Controls.ColorChangedEventArgs args)
        {
            ApplyGlobalAccentColor(args.NewColor);
            
            Settings.AccentColorHex = $"#{args.NewColor.A:X2}{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
            SettingsStore.Save(Settings);
        }

        private void ApplyGlobalAccentColor(Windows.UI.Color rawColor)
        {
            Windows.UI.Color solidColor = Windows.UI.Color.FromArgb(255, rawColor.R, rawColor.G, rawColor.B);
            var solidBrush = new SolidColorBrush(solidColor);

            var appRes = Application.Current.Resources;
            string[] brushKeys = new string[] {
                "SystemControlForegroundAccentBrush", "SystemControlBackgroundAccentBrush",
                "SystemControlHighlightAccentBrush", "SystemControlHyperlinkTextBrush",
                "AccentButtonBackground", "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed",
                "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed"
            };

            appRes["SystemAccentColor"] = solidColor;
            appRes["SystemAccentColorPrimary"] = solidColor;
            appRes["SystemAccentColorLight1"] = solidColor;
            appRes["SystemAccentColorLight2"] = solidColor;
            appRes["SystemAccentColorDark1"] = solidColor;
            appRes["SystemAccentColorDark2"] = solidColor;

            foreach (var key in brushKeys) { appRes[key] = solidBrush; }

            if (RootGrid?.Resources != null)
            {
                RootGrid.Resources["SystemAccentColor"] = solidColor;
                foreach (var key in brushKeys) { RootGrid.Resources[key] = solidBrush; }
            }

            if (ProgressSlider != null) ProgressSlider.AccentColor = solidColor;
            if (VolumeSlider != null) VolumeSlider.AccentColor = solidColor;
            if (GlobalScaleSlider != null) GlobalScaleSlider.AccentColor = solidColor;
            
            UpdateShuffleUI();
            UpdateRepeatUI();

            if (RootGrid != null && !_isRefreshingAccentTheme)
            {
                _isRefreshingAccentTheme = true;
                var currentTheme = RootGrid.ActualTheme == ElementTheme.Default ? ElementTheme.Dark : RootGrid.ActualTheme;
                ElementTheme oppositeTheme = currentTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;

                RootGrid.RequestedTheme = oppositeTheme;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => {
                    RootGrid.RequestedTheme = currentTheme;
                    _isRefreshingAccentTheme = false;
                });
            }
        }

        private void ShuffleMemoryBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (Settings == null || double.IsNaN(args.NewValue)) return;
            Settings.ShuffleMemorySize = Math.Max(0, (int)args.NewValue);
            SettingsStore.Save(Settings);
        }

        private void GlobalScaleSlider_ValueChanged(object sender, double newVal)
        {
            if (Settings == null) return;
            Settings.GlobalScale = newVal;
            if (LibraryTransform != null) { LibraryTransform.ScaleX = newVal; LibraryTransform.ScaleY = newVal; }
            if (TransportTransform != null) { TransportTransform.ScaleX = newVal; TransportTransform.ScaleY = newVal; }
        }
        private void GlobalScaleSlider_ScrubbingEnded(object sender, double newVal) { if (Settings != null) SettingsStore.Save(Settings); }

        private void VolumeSlider_ValueChanged(object sender, double newVal) { if (Settings != null) { Settings.Volume = (int)newVal; App.MusicEngine?.SetVolume((int)newVal); } }
        private void VolumeSlider_ScrubbingEnded(object sender, double newVal) { if (Settings != null) SettingsStore.Save(Settings); }

        private async Task LoadLibraryFromCacheAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.LastLibraryPath) || !System.IO.Directory.Exists(Settings.LastLibraryPath)) return;
            IsLoading = true;
            var cachedTracks = await Task.Run(() => LibraryCache.Load());
            if (cachedTracks != null && cachedTracks.Count > 0)
            {
                _fullCache = cachedTracks.ToList();
                LibraryTracks = new ObservableCollection<Track>(_fullCache);
            }
            IsLoading = false;
        }

        private void ApplySettings()
        {
            if (GlobalScaleSlider != null) GlobalScaleSlider.Value = Settings.GlobalScale;
            if (VolumeSlider != null) VolumeSlider.Value = Settings.Volume;
            if (ToggleArtworkPanelSwitch != null) ToggleArtworkPanelSwitch.IsOn = Settings.ShowArtworkPanel;
            if (ShuffleMemoryBox != null) ShuffleMemoryBox.Value = Settings.ShuffleMemorySize;
            if (RandomStartPicker != null) RandomStartPicker.Date = Settings.RandomStartDate;
            if (RandomEndPicker != null) RandomEndPicker.Date = Settings.RandomEndDate;
            
            UpdateShuffleUI();
            UpdateRepeatUI();

            if (!string.IsNullOrEmpty(Settings.AccentColorHex))
            {
                try
                {
                    var hex = Settings.AccentColorHex.TrimStart('#');
                    byte a = 255;
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    if (hex.Length == 8)
                    {
                        a = Convert.ToByte(hex.Substring(0, 2), 16);
                        r = Convert.ToByte(hex.Substring(2, 2), 16);
                        g = Convert.ToByte(hex.Substring(4, 2), 16);
                        b = Convert.ToByte(hex.Substring(6, 2), 16);
                    }
                    Windows.UI.Color savedColor = Windows.UI.Color.FromArgb(a, r, g, b);
                    
                    ApplyGlobalAccentColor(savedColor);
                    if (AppColorPicker != null) AppColorPicker.Color = savedColor;
                }
                catch { }
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            App.MusicEngine?.Dispose();
            SettingsStore.Save(Settings); 
            if (_fullCache != null && _fullCache.Count > 0) LibraryCache.Save(_fullCache);
        }

        private void OnTrackItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Track selectedTrack)
            {
                _playbackQueue = LibraryTracks.ToList();
                _currentQueueIndex = _playbackQueue.IndexOf(selectedTrack);
                PlayTrack(selectedTrack);
            }
        }

        private void OnQueueNextClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is Track trackToQueue)
            {
                if (NowPlayingTrack == null)
                {
                    _playbackQueue = _fullCache.ToList();
                    PlayTrack(trackToQueue);
                    return;
                }

                if (_playbackQueue.Count == 0 || !_playbackQueue.Contains(NowPlayingTrack))
                {
                    _playbackQueue = _fullCache.ToList();
                }

                int currentIndex = _playbackQueue.IndexOf(NowPlayingTrack);
                int existingIndex = _playbackQueue.IndexOf(trackToQueue);
                
                if (existingIndex > currentIndex) _playbackQueue.RemoveAt(existingIndex);
                else if (existingIndex != -1 && existingIndex <= currentIndex) { _playbackQueue.RemoveAt(existingIndex); currentIndex--; }

                _playbackQueue.Insert(currentIndex + 1, trackToQueue);
            }
        }

        private void OnPlayNowClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is Track track)
            {
                _playbackQueue = LibraryTracks.ToList();
                _currentQueueIndex = _playbackQueue.IndexOf(track);
                PlayTrack(track);
            }
        }

        private void PlayTrack(Track? track)
        {
            if (track == null) return;

            if (NowPlayingTrack != null) 
            {
                NowPlayingTrack.IsPlaying = false;
                NowPlayingTrack.IsPlayingState = false;
                _playbackHistory.Push(NowPlayingTrack);
            }
            
            NowPlayingTrack = track;
            NowPlayingTrack.IsPlaying = true; 
            NowPlayingTrack.IsPlayingState = true;
            LibraryList.ScrollIntoView(NowPlayingTrack);

            if (!_shufflePlayedHistory.Contains(track.FilePath)) _shufflePlayedHistory.Add(track.FilePath);
            if (Settings.ShuffleMemorySize > 0 && _shufflePlayedHistory.Count > Settings.ShuffleMemorySize) _shufflePlayedHistory.RemoveAt(0);

            App.MusicEngine?.Play(NowPlayingTrack);
            SetPlayingState(true);
            PlayPauseIcon.Glyph = "\uE769";
        }

        private void PlayNext()
        {
            if (_fullCache.Count == 0) return;
            if (Settings.RepeatMode == 2 && NowPlayingTrack != null) { PlayTrack(NowPlayingTrack); return; }

            if (Settings.IsShuffle)
            {
                var filteredPool = _fullCache.Where(t => t.DateModified >= Settings.RandomStartDate && t.DateModified <= Settings.RandomEndDate).ToList();
                if (Settings.ShuffleMemorySize > 0)
                {
                    var freshPool = filteredPool.Where(t => !_shufflePlayedHistory.Contains(t.FilePath)).ToList();
                    if (freshPool.Count > 0) filteredPool = freshPool;
                }
                if (filteredPool.Count > 0) { PlayTrack(filteredPool[new Random().Next(filteredPool.Count)]); return; }
            }
            else 
            {
                if (NowPlayingTrack != null && !_playbackQueue.Contains(NowPlayingTrack)) _playbackQueue = _fullCache.ToList();
                _currentQueueIndex = NowPlayingTrack == null ? -1 : _playbackQueue.IndexOf(NowPlayingTrack);
                _currentQueueIndex++;
                if (_currentQueueIndex >= _playbackQueue.Count) { if (Settings.RepeatMode == 1) _currentQueueIndex = 0; else return; }
                PlayTrack(_playbackQueue[_currentQueueIndex]);
            }
        }

        private void PlayPrevious()
        {
            if (App.MusicEngine != null && App.MusicEngine.Time > 3000 && NowPlayingTrack != null) { PlayTrack(NowPlayingTrack); return; }
            if (_playbackHistory.Count > 0)
            {
                var prevTrack = _playbackHistory.Pop();
                if (NowPlayingTrack != null) { NowPlayingTrack.IsPlaying = false; NowPlayingTrack.IsPlayingState = false; }
                NowPlayingTrack = prevTrack;
                NowPlayingTrack.IsPlaying = true; NowPlayingTrack.IsPlayingState = true;
                LibraryList.ScrollIntoView(NowPlayingTrack);
                App.MusicEngine?.Play(NowPlayingTrack);
                SetPlayingState(true); PlayPauseIcon.Glyph = "\uE769";
            }
        }

        private void OnPreviousClick(object sender, RoutedEventArgs e) => PlayPrevious();
        private void OnNextClick(object sender, RoutedEventArgs e) => PlayNext();

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (NowPlayingTrack == null) return;
            if (!_isPlaying) { App.MusicEngine?.Resume(); NowPlayingTrack.IsPlayingState = true; PlayPauseIcon.Glyph = "\uE769"; SetPlayingState(true); }
            else { App.MusicEngine?.Pause(); NowPlayingTrack.IsPlayingState = false; PlayPauseIcon.Glyph = "\uE768"; SetPlayingState(false); }
        }

        private void RepeatBtn_Click(object sender, RoutedEventArgs e) { Settings.RepeatMode = (Settings.RepeatMode + 1) % 3; UpdateRepeatUI(); SettingsStore.Save(Settings); }

        private void UpdateRepeatUI()
        {
            if (RepeatIcon == null) return;
            
            var normal = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            Brush activeBrush = normal;
            
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object res) && res is Windows.UI.Color accentColor)
            {
                activeBrush = new SolidColorBrush(accentColor);
            }

            switch (Settings.RepeatMode)
            {
                case 0: RepeatIcon.Text = "\uE8EE"; RepeatIcon.Foreground = normal; break;
                case 1: RepeatIcon.Text = "\uE8EE"; RepeatIcon.Foreground = activeBrush; break;
                case 2: RepeatIcon.Text = "\uE8ED"; RepeatIcon.Foreground = activeBrush; break;
            }
        }

        private void ShuffleBtn_Click(object sender, RoutedEventArgs e)
        {
            Settings.IsShuffle = !Settings.IsShuffle;
            UpdateShuffleUI();
            SettingsStore.Save(Settings);
        }

        private void UpdateShuffleUI()
        {
            if (ShuffleIcon == null) return;
            
            var normal = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            Brush activeBrush = normal;
            
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out object res) && res is Windows.UI.Color accentColor)
            {
                activeBrush = new SolidColorBrush(accentColor);
            }

            ShuffleIcon.Foreground = Settings.IsShuffle ? activeBrush : normal;
        }

        private void RandomDates_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) { if (RandomStartPicker?.Date != null) Settings.RandomStartDate = RandomStartPicker.Date.Value; if (RandomEndPicker?.Date != null) Settings.RandomEndDate = RandomEndPicker.Date.Value; SettingsStore.Save(Settings); }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = SettingsOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (SettingsOverlay.Visibility == Visibility.Visible) UpdateSettingsLayoutMode();
        }

        private void SettingsScrollHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSettingsLayoutMode();

        private void UpdateSettingsLayoutMode()
        {
            const double smallScreenThreshold = 720.0;
            double availableHeight = SettingsScrollHost.ActualHeight;
            if (availableHeight <= 0) return;

            if (availableHeight <= smallScreenThreshold)
            {
                SettingsViewbox.Width = double.NaN;
                SettingsViewbox.Height = double.NaN;
            }
            else
            {
                SettingsViewbox.Width = SettingsScrollHost.ActualWidth;
                SettingsViewbox.Height = availableHeight;
            }
        }
        private void OnViewToggled(object sender, RoutedEventArgs e) { if (ToggleArtworkPanelSwitch != null && ColNowPlaying != null) { ColNowPlaying.Width = ToggleArtworkPanelSwitch.IsOn ? GridLength.Auto : new GridLength(0); if (Settings != null) { Settings.ShowArtworkPanel = ToggleArtworkPanelSwitch.IsOn; SettingsStore.Save(Settings); } } }

        private async void OnAddFolderClick(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlay != null) SettingsOverlay.Visibility = Visibility.Collapsed;
            var folderPicker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            folderPicker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            folderPicker.FileTypeFilter.Add("*");

            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                Settings.LastLibraryPath = folder.Path;
                SettingsStore.Save(Settings);
                await ProcessAudioFilesAsync(folder.Path, false);
            }
        }

        private async void OnScanLibraryClick(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlay != null) SettingsOverlay.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(Settings.LastLibraryPath) || !System.IO.Directory.Exists(Settings.LastLibraryPath))
            {
                OnAddFolderClick(sender, e);
                return;
            }
            await ProcessAudioFilesAsync(Settings.LastLibraryPath, true);
        }

        private async Task ProcessAudioFilesAsync(string folderPath, bool isIncrementalScan)
        {
            IsLoading = true;
            if (!isIncrementalScan) _fullCache.Clear();

            await Task.Run(async () => {
                try
                {
                    var allFiles = System.IO.Directory.GetFiles(folderPath, "*.*", System.IO.SearchOption.AllDirectories)
                        .Where(f => {
                            string ext = System.IO.Path.GetExtension(f).ToLower();
                            return ext == ".mp3" || ext == ".flac" || ext == ".wav" || ext == ".m4a" || ext == ".aac" || ext == ".ogg" || ext == ".wma";
                        }).ToList();

                    // FIX: Create a dictionary map of cached items to cross-reference timestamps
                    var existingCache = _fullCache.ToDictionary(t => t.FilePath, t => t, StringComparer.OrdinalIgnoreCase);
                    var newTracks = new ConcurrentBag<Track>();
                    var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 4 };

                    await Parallel.ForEachAsync(allFiles, parallelOptions, async (filePath, token) => {
                        try 
                        {
                            DateTime currentWriteTime = System.IO.File.GetLastWriteTime(filePath);
                            
                            // FIX: Check if the file's OS timestamp is newer than our cached entry.
                            // If they are less than a second apart, it means it hasn't changed, so skip it safely.
                            if (isIncrementalScan && existingCache.TryGetValue(filePath, out var cachedTrack))
                            {
                                if (Math.Abs((currentWriteTime - cachedTrack.DateModified).TotalSeconds) < 1) 
                                    return; 
                            }

                            string title = System.IO.Path.GetFileNameWithoutExtension(filePath);
                            string artist = "Unknown Artist"; string album = "Unknown Album";
                            TimeSpan duration = TimeSpan.Zero;
                            
                            using var tlFile = TagLib.File.Create(new LocalFileAbstraction(filePath));
                            if (!string.IsNullOrWhiteSpace(tlFile.Tag.Title)) title = tlFile.Tag.Title;
                            if (tlFile.Tag.Performers.Length > 0) artist = string.Join(", ", tlFile.Tag.Performers);
                            if (!string.IsNullOrWhiteSpace(tlFile.Tag.Album)) album = tlFile.Tag.Album;
                            duration = tlFile.Properties.Duration;

                            var track = new Track(filePath, title, artist, album, duration, System.IO.File.GetCreationTime(filePath), currentWriteTime);
                            newTracks.Add(track);
                        } 
                        catch 
                        { 
                            newTracks.Add(new Track(filePath, System.IO.Path.GetFileName(filePath), "Unknown Artist", "Unknown Album", TimeSpan.Zero, System.IO.File.GetCreationTime(filePath), System.IO.File.GetLastWriteTime(filePath))); 
                        }
                    });

                    // FIX: Reconstruct cache without duplicates by filtering out old versions of re-scanned tracks
                    var newTrackPaths = new HashSet<string>(newTracks.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);
                    var validCache = new List<Track>();
                    
                    foreach (var t in _fullCache)
                    {
                        if (System.IO.File.Exists(t.FilePath) && !newTrackPaths.Contains(t.FilePath)) 
                        {
                            validCache.Add(t);
                        }
                    }
                    validCache.AddRange(newTracks);

                    DispatcherQueue.TryEnqueue(() => {
                        _fullCache = validCache.OrderBy(t => t.Title).ToList();
                        LibraryTracks = new ObservableCollection<Track>(_fullCache);
                        IsLoading = false;
                        Task.Run(() => LibraryCache.Save(_fullCache));
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error scanning audio files: {ex.Message}");
                    DispatcherQueue.TryEnqueue(() => { IsLoading = false; });
                }
            });
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            string query = SearchBox.Text.ToLower();
            var filtered = string.IsNullOrWhiteSpace(query) ? _fullCache : _fullCache.Where(t => t.Title.ToLower().Contains(query) || t.Artist.ToLower().Contains(query) || t.Album.ToLower().Contains(query)).ToList();
            LibraryTracks = new ObservableCollection<Track>(filtered);
            
            if (SearchCountText != null)
            {
                SearchCountText.Text = string.IsNullOrWhiteSpace(query) ? "" : $"{filtered.Count} items found";
            }
        }

        private bool _isBatchEditMode = false;
        private List<Track> _tracksToEdit = new();
        private byte[]? _newArtworkBytes = null;

        private void OnSelectionModeToggleClick(object sender, RoutedEventArgs e)
        {
            if (LibraryList.SelectionMode == ListViewSelectionMode.Single)
            {
                LibraryList.SelectionMode = ListViewSelectionMode.Multiple;
                LibraryList.IsItemClickEnabled = false;
                SelectionModeToggleBtn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DeepSkyBlue);
                BatchEditExecuteBtn.Visibility = Visibility.Visible;
            }
            else
            {
                LibraryList.SelectionMode = ListViewSelectionMode.Single;
                LibraryList.IsItemClickEnabled = true;
                SelectionModeToggleBtn.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
                BatchEditExecuteBtn.Visibility = Visibility.Collapsed;
            }
        }

        private void OnBatchEditExecuteClick(object sender, RoutedEventArgs e)
        {
            var selected = LibraryList.SelectedItems.Cast<Track>().ToList();
            
            LibraryList.SelectionMode = ListViewSelectionMode.Single;
            LibraryList.IsItemClickEnabled = true;
            SelectionModeToggleBtn.Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            BatchEditExecuteBtn.Visibility = Visibility.Collapsed;

            if (selected.Count > 0)
            {
                OpenEditOverlay(selected);
            }
        }

        private void OnEditTrackMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is Track track)
            {
                OpenEditOverlay(new List<Track> { track });
            }
        }

        private void OpenEditOverlay(List<Track> tracks)
        {
            if (SettingsOverlay != null) SettingsOverlay.Visibility = Visibility.Collapsed;

            _tracksToEdit = tracks;
            _isBatchEditMode = tracks.Count > 1;
            _newArtworkBytes = null;
            EditErrorText.Visibility = Visibility.Collapsed;
            EditOverlayTitle.Text = _isBatchEditMode ? $"Batch Edit ({tracks.Count} Tracks)" : "Edit Track Info";

            EditTitleBox.Visibility = _isBatchEditMode ? Visibility.Collapsed : Visibility.Visible;
            EditTrackBox.Visibility = _isBatchEditMode ? Visibility.Collapsed : Visibility.Visible;
            EditDiscBox.Visibility = _isBatchEditMode ? Visibility.Collapsed : Visibility.Visible;

            EditTitleBox.Text = "";
            EditArtistBox.Text = "";
            EditAlbumBox.Text = "";
            EditAlbumArtistBox.Text = "";
            EditTrackBox.Value = double.NaN; EditTrackBox.Text = "";
            EditDiscBox.Value = double.NaN; EditDiscBox.Text = "";
            EditYearBox.Value = double.NaN; EditYearBox.Text = "";
            EditGenreBox.Text = "";
            EditCommentBox.Text = "";
            EditArtworkPreview.Source = null;

            if (!_isBatchEditMode)
            {
                var t = tracks[0];
                try
                {
                    using var file = TagLib.File.Create(new LocalFileAbstraction(t.FilePath));
                    EditTitleBox.Text = file.Tag.Title ?? "";
                    EditArtistBox.Text = string.Join("; ", file.Tag.Performers ?? Array.Empty<string>());
                    EditAlbumBox.Text = file.Tag.Album ?? "";
                    EditAlbumArtistBox.Text = string.Join("; ", file.Tag.AlbumArtists ?? Array.Empty<string>());
                    if (file.Tag.Track > 0) EditTrackBox.Value = file.Tag.Track;
                    if (file.Tag.Disc > 0) EditDiscBox.Value = file.Tag.Disc;
                    if (file.Tag.Year > 0) EditYearBox.Value = file.Tag.Year;
                    EditGenreBox.Text = string.Join("; ", file.Tag.Genres ?? Array.Empty<string>());
                    EditCommentBox.Text = file.Tag.Comment ?? "";
                }
                catch { }
                EditArtworkPreview.Source = t.HighResArtworkImage;
            }

            EditOverlay.Visibility = Visibility.Visible;
        }

        private async void OnFetchMetadataClick(object sender, RoutedEventArgs e)
        {
            string url = MetadataUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;

            FetchMetadataBtn.IsEnabled = false;
            FetchProgressRing.IsActive = true;
            FetchProgressRing.Visibility = Visibility.Visible;
            EditErrorText.Visibility = Visibility.Collapsed;

            try
            {
                string oEmbedUrl = string.Empty;
                bool isYouTube = false;

                if (url.Contains("spotify.com"))
                {
                    oEmbedUrl = $"https://open.spotify.com/oembed?url={Uri.EscapeDataString(url)}";
                }
                else if (url.Contains("youtube.com") || url.Contains("youtu.be"))
                {
                    oEmbedUrl = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(url)}&format=json";
                    isYouTube = true;
                }
                else if (url.Contains("soundcloud.com"))
                {
                    oEmbedUrl = $"https://soundcloud.com/oembed?format=json&url={Uri.EscapeDataString(url)}";
                }
                else
                {
                    throw new Exception("Unsupported URL. Please use Spotify, YouTube, or SoundCloud.");
                }

                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MusicPower3/2.1");
                string jsonResponse = await _httpClient.GetStringAsync(oEmbedUrl);
                var data = JsonNode.Parse(jsonResponse);

                if (data != null)
                {
                    string fetchedTitle = data["title"]?.ToString() ?? "";
                    string fetchedArtist = data["author_name"]?.ToString() ?? "";
                    string thumbnailUrl = data["thumbnail_url"]?.ToString() ?? "";

                    if (isYouTube && fetchedTitle.Contains("-"))
                    {
                        var parts = fetchedTitle.Split(new[] { '-' }, 2);
                        fetchedArtist = parts[0].Trim();
                        fetchedTitle = parts[1].Trim();
                        fetchedArtist = fetchedArtist.Replace(" - Topic", ""); 
                    }

                    if (!string.IsNullOrWhiteSpace(fetchedTitle)) EditTitleBox.Text = fetchedTitle;
                    if (!string.IsNullOrWhiteSpace(fetchedArtist)) EditArtistBox.Text = fetchedArtist;

                    if (!string.IsNullOrWhiteSpace(thumbnailUrl))
                    {
                        byte[] imageBytes = await _httpClient.GetByteArrayAsync(thumbnailUrl);
                        _newArtworkBytes = imageBytes;

                        var bitmap = new BitmapImage();
                        using var stream = new InMemoryRandomAccessStream();
                        await stream.WriteAsync(imageBytes.AsBuffer());
                        stream.Seek(0);
                        await bitmap.SetSourceAsync(stream);
                        EditArtworkPreview.Source = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                EditErrorText.Text = $"Could not fetch metadata: {ex.Message}";
                EditErrorText.Visibility = Visibility.Visible;
            }
            finally
            {
                FetchMetadataBtn.IsEnabled = true;
                FetchProgressRing.IsActive = false;
                FetchProgressRing.Visibility = Visibility.Collapsed;
            }
        }

        private async void OnEditArtworkClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            EditErrorText.Visibility = Visibility.Collapsed;

            using var stream = await file.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);

            if (decoder.PixelWidth < 300 || decoder.PixelHeight < 300)
            {
                EditErrorText.Text = $"Artwork rejected: Image is {decoder.PixelWidth}x{decoder.PixelHeight}. Minimum required is 300x300.";
                EditErrorText.Visibility = Visibility.Visible;
                return;
            }

            using var memStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, memStream);
            
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            _newArtworkBytes = new byte[memStream.Size];
            await memStream.ReadAsync(_newArtworkBytes.AsBuffer(), (uint)memStream.Size, Windows.Storage.Streams.InputStreamOptions.None);

            var bmp = new BitmapImage();
            memStream.Seek(0);
            await bmp.SetSourceAsync(memStream);
            EditArtworkPreview.Source = bmp;
        }

        private void OnClearArtworkClick(object sender, RoutedEventArgs e)
        {
            _newArtworkBytes = Array.Empty<byte>();
            EditArtworkPreview.Source = null;
        }

        private void OnCancelEditClick(object sender, RoutedEventArgs e)
        {
            EditOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnSaveEditClick(object sender, RoutedEventArgs e)
        {
            EditErrorText.Visibility = Visibility.Collapsed;
            var update = new TrackMetadataUpdate();
            
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditTitleBox.Text)) update.Title = EditTitleBox.Text;
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditArtistBox.Text)) update.Performers = EditArtistBox.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditAlbumBox.Text)) update.Album = EditAlbumBox.Text;
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditAlbumArtistBox.Text)) update.AlbumArtists = EditAlbumArtistBox.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            
            if (!_isBatchEditMode || !double.IsNaN(EditTrackBox.Value)) update.Track = (uint)Math.Max(0, EditTrackBox.Value);
            if (!_isBatchEditMode || !double.IsNaN(EditDiscBox.Value)) update.Disc = (uint)Math.Max(0, EditDiscBox.Value);
            if (!_isBatchEditMode || !double.IsNaN(EditYearBox.Value)) update.Year = (uint)Math.Max(0, EditYearBox.Value);
            
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditGenreBox.Text)) update.Genres = EditGenreBox.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditCommentBox.Text)) update.Comment = EditCommentBox.Text;
            
            update.ArtworkData = _newArtworkBytes;

            bool successAll = true;

            foreach(var t in _tracksToEdit)
            {
                try
                {
                    MetadataWriter.SaveMetadata(t.FilePath, update, _isBatchEditMode);
                    
                    if (update.Title != null) t.Title = update.Title;
                    if (update.Performers != null) t.Artist = string.Join(", ", update.Performers);
                    if (update.Album != null) t.Album = update.Album;
                    
                    t.DateModified = System.IO.File.GetLastWriteTime(t.FilePath);
                    
                    if (update.ArtworkData != null)
                    {
                        Track.ClearArtworkCache(t.FilePath);
                        t.TriggerArtworkRefresh();
                    }
                }
                catch (Exception ex)
                {
                    if (ex is IOException) EditErrorText.Text = "Couldn't save — close the file elsewhere and try again.";
                    else if (ex is TagLib.CorruptFileException) EditErrorText.Text = "Couldn't save — the file metadata is corrupted.";
                    else EditErrorText.Text = $"Error: {ex.Message}";
                    
                    EditErrorText.Visibility = Visibility.Visible;
                    successAll = false;
                    break;
                }
            }

            if (successAll)
            {
                EditOverlay.Visibility = Visibility.Collapsed;
                _ = Task.Run(() => LibraryCache.Save(_fullCache));
                OnSortChanged(SortBox, null!); 
            }
        }

        private void OnSortChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_fullCache.Count == 0 || SortBox.SelectedItem is not ComboBoxItem item) return;
            var sorted = item.Content.ToString() switch
            {
                "Title (A-Z)" => _fullCache.OrderBy(t => t.Title),
                "Title (Z-A)" => _fullCache.OrderByDescending(t => t.Title),
                "Artist (A-Z)" => _fullCache.OrderBy(t => t.Artist).ThenBy(t => t.Title),
                "Artist (Z-A)" => _fullCache.OrderByDescending(t => t.Artist).ThenBy(t => t.Title),
                "Album (A-Z)" => _fullCache.OrderBy(t => t.Album).ThenBy(t => t.Title),
                "Album (Z-A)" => _fullCache.OrderByDescending(t => t.Album).ThenBy(t => t.Title),
                "Date Added (Newest)" => _fullCache.OrderByDescending(t => t.DateAdded),
                "Date Added (Oldest)" => _fullCache.OrderBy(t => t.DateAdded),
                "Date Modified (Newest)" => _fullCache.OrderByDescending(t => t.DateModified),
                "Date Modified (Oldest)" => _fullCache.OrderBy(t => t.DateModified),
                "Duration (Longest)" => _fullCache.OrderByDescending(t => t.Duration),
                "Duration (Shortest)" => _fullCache.OrderBy(t => t.Duration),
                _ => _fullCache.AsEnumerable()
            };
            _fullCache = sorted.ToList();
            LibraryTracks = new ObservableCollection<Track>(_fullCache);
        }

        private void RootGrid_DragOver(object sender, DragEventArgs e) { }
        private void RootGrid_Drop(object sender, DragEventArgs e) { }
        private void LibraryList_DragItemsStarting(object sender, DragItemsStartingEventArgs e) { }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}