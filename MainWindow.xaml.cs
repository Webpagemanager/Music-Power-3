using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;
using Color = Windows.UI.Color;
using MusicPower3.Models;
using MusicPower3.Services;

namespace MusicPower3
{
    public sealed partial class MainWindow : Window, System.ComponentModel.INotifyPropertyChanged
    {
        public static Microsoft.UI.Dispatching.DispatcherQueue? MainDispatcher { get; private set; }
        public AppSettings Settings { get; set; } = SettingsStore.Load();

        // ---- Online metadata: hosts that are allowed, and hard limits on what we will download ----
        private const int MaxJsonBytes = 256 * 1024;
        private const int MaxImageBytes = 10 * 1024 * 1024;
        private static readonly string[] AllowedLinkHosts =
        {
            "open.spotify.com", "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com",
            "youtu.be", "soundcloud.com", "www.soundcloud.com", "m.soundcloud.com"
        };
        private static readonly string[] AllowedImageHostSuffixes = { "ytimg.com", "scdn.co", "sndcdn.com" };
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            // No automatic redirects: a whitelisted host must not be able to bounce us to an internal address.
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicPower3/2.3");
            return client;
        }

        // ---- Playback state ----
        private bool _isPlaying = false;
        private bool _isScrubbing = false;
        private bool _isLoading = false;
        private bool _windowVisible = true;
        private bool _libraryDirty = false;

        private Track? _nowPlayingTrack;
        private List<Track> _fullCache = new List<Track>();
        private ObservableCollection<Track> _libraryTracks = new();
        public ObservableCollection<Track> LibraryTracks { get => _libraryTracks; set { _libraryTracks = value; OnPropertyChanged(); } }

        private List<Track> _playbackQueue = new List<Track>();
        private Stack<Track> _playbackHistory = new Stack<Track>();
        private List<string> _shufflePlayedHistory = new List<string>();
        private long _lastSecondsUpdated = -1;

        // Manual "Play next / Add to queue" list. It always plays before the normal list continues.
        public ObservableCollection<Track> UpcomingQueue { get; } = new();
        private bool _playingFromQueue = false;
        private Track? _contextAnchor;   // where the normal list resumes once the manual queue is empty

        public int QueueCount => UpcomingQueue.Count;
        public bool HasQueuedTracks => UpcomingQueue.Count > 0;
        public Visibility QueueBadgeVisibility => UpcomingQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility QueueEmptyVisibility => UpcomingQueue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- Theme state ----
        private readonly UISettings _uiSettings = new();
        private readonly HashSet<string> _appliedAccentKeys = new();
        private bool _isRefreshingAccentTheme = false;
        private bool _isInitializingUi = false;

        // ---- Timers (all low frequency; none runs while idle) ----
        private bool _isRenderingHooked = false;
        private readonly DispatcherTimer _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly DispatcherTimer _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private readonly DispatcherTimer _accentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

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
            this.Closed += MainWindow_Closed;
            this.VisibilityChanged += (s, e) => { _windowVisible = e.Visible; UpdateRenderingHook(); };

            SetWindowIcon();

            RootGrid.DataContext = this;

            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); ApplySortAndFilter(); };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                var snapshot = Settings;
                Task.Run(() => SettingsStore.Save(snapshot));
                if (_libraryDirty && _fullCache.Count > 0)
                {
                    var libSnapshot = _fullCache.ToList();
                    Task.Run(() => LibraryCache.Save(libSnapshot));
                    _libraryDirty = false;
                }
            };
            _accentTimer.Tick += (s, e) => { _accentTimer.Stop(); ApplyAccentTheme(true); RequestSaveSettings(); };

            UpcomingQueue.CollectionChanged += OnUpcomingQueueChanged;

            _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
            App.FileActivated += OnFileActivated;

            if (App.MusicEngine != null)
            {
                App.MusicEngine.EndReached += (s, e) => DispatcherQueue.TryEnqueue(() => PlayNext(false));
                App.MusicEngine.PlayRequested += (s, e) => DispatcherQueue.TryEnqueue(() => { if (!_isPlaying) OnPlayPauseClick(this, new RoutedEventArgs()); });
                App.MusicEngine.PauseRequested += (s, e) => DispatcherQueue.TryEnqueue(() => { if (_isPlaying) OnPlayPauseClick(this, new RoutedEventArgs()); });
                App.MusicEngine.NextRequested += (s, e) => DispatcherQueue.TryEnqueue(() => PlayNext(true));
                App.MusicEngine.PreviousRequested += (s, e) => DispatcherQueue.TryEnqueue(() => PlayPrevious());
                App.MusicEngine.DurationUpdated += (track, newDuration) => DispatcherQueue.TryEnqueue(() =>
                {
                    _libraryDirty = true;
                    RequestSaveSettings();
                });
            }

            RootGrid.ActualThemeChanged += (s, e) => { if (!_isRefreshingAccentTheme) ApplyAccentTheme(true); };
            RootGrid.Loaded += async (s, e) =>
            {
                ApplySettings();
                await LoadLibraryFromCacheAsync();
                if (App.LaunchFile != null) OpenExternalFile(App.LaunchFile);
            };
        }

        private void SetWindowIcon()
        {
            try
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                Program.SetWindowAppUserModelId(hWnd);

                string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
                if (File.Exists(icon)) AppWindow.SetIcon(icon);
            }
            catch { }
        }

        private static string FormatTime(long milliseconds)
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        #region Settings persistence

        // Settings are written at most once per burst of changes (sliders, colour picker) to spare the disk.
        private void RequestSaveSettings()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void ApplySettings()
        {
            _isInitializingUi = true;
            try
            {
                GlobalScaleSlider.Value = Settings.GlobalScale;
                ApplyScale(Settings.GlobalScale);
                VolumeSlider.Value = Settings.Volume;
                App.MusicEngine?.SetVolume(Settings.Volume);

                ToggleArtworkPanelSwitch.IsOn = Settings.ShowArtworkPanel;
                ArtworkPanel.Visibility = Settings.ShowArtworkPanel ? Visibility.Visible : Visibility.Collapsed;
                ShuffleMemoryBox.Value = Settings.ShuffleMemorySize;
                RandomStartPicker.Date = Settings.RandomStartDate;
                RandomEndPicker.Date = Settings.RandomEndDate;
                OnlineMetadataSwitch.IsOn = Settings.EnableOnlineMetadata;
                UseSystemAccentSwitch.IsOn = Settings.UseSystemAccentColor;
                CustomAccentExpander.IsEnabled = !Settings.UseSystemAccentColor;
                UpdateLibraryPathText();

                AppColorPicker.Color = TryParseHex(Settings.AccentColorHex, out var saved) ? saved : GetSystemAccent();
            }
            finally { _isInitializingUi = false; }

            ApplyAccentTheme(true);
            UpdateShuffleUI();
            UpdateRepeatUI();
        }

        private void UpdateLibraryPathText()
        {
            LibraryPathText.Text = string.IsNullOrWhiteSpace(Settings.LastLibraryPath) ? "No folder selected" : Settings.LastLibraryPath;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (_isRenderingHooked) { CompositionTarget.Rendering -= OnCompositionRendering; _isRenderingHooked = false; }
            _searchTimer.Stop(); _saveTimer.Stop(); _accentTimer.Stop();
            _uiSettings.ColorValuesChanged -= OnSystemColorValuesChanged;
            App.FileActivated -= OnFileActivated;

            App.MusicEngine?.Dispose();
            SettingsStore.Save(Settings);
            // The library cache is only rewritten when something actually changed.
            if (_libraryDirty && _fullCache.Count > 0) LibraryCache.Save(_fullCache.ToList());
        }

        #endregion

        #region Accent colour and theme

        private static bool TryParseHex(string? hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            try
            {
                if (hex.Length == 6)
                {
                    color = Color.FromArgb(255, Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
                    return true;
                }
                if (hex.Length == 8)
                {
                    color = Color.FromArgb(255, Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16), Convert.ToByte(hex.Substring(6, 2), 16));
                    return true;
                }
            }
            catch (FormatException) { }
            return false;
        }

        private static string ToHex(Color c) => $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";

        // The same shade Windows 11 itself uses for accent fills: lighter in dark mode, darker in light mode.
        private Color GetSystemAccent()
        {
            bool light = RootGrid.ActualTheme == ElementTheme.Light;
            return _uiSettings.GetColorValue(light ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
        }

        private Color EffectiveAccent =>
            !Settings.UseSystemAccentColor && TryParseHex(Settings.AccentColorHex, out var custom) ? custom : GetSystemAccent();

        private static Color Mix(Color from, Color to, double amount) => Color.FromArgb(255,
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));

        private static bool IsLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 160;

        private void OnSystemColorValuesChanged(UISettings sender, object args)
        {
            // Fires on a background thread whenever the user changes the Windows accent colour or theme.
            DispatcherQueue.TryEnqueue(() => { if (Settings.UseSystemAccentColor) ApplyAccentTheme(false); });
        }

        /// <summary>
        /// With the Windows accent colour selected nothing is overridden, so every control looks and updates exactly
        /// like a stock Windows 11 app. A custom colour overrides the accent resources that WinUI controls use.
        /// </summary>
        private void ApplyAccentTheme(bool refreshTheme)
        {
            if (RootGrid == null) return;

            var res = Application.Current.Resources;
            bool hadOverrides = _appliedAccentKeys.Count > 0;
            foreach (var key in _appliedAccentKeys) res.Remove(key);
            _appliedAccentKeys.Clear();

            Color accent = EffectiveAccent;

            if (!Settings.UseSystemAccentColor)
            {
                bool dark = RootGrid.ActualTheme != ElementTheme.Light;
                Color onAccent = IsLight(accent) ? Colors.Black : Colors.White;
                Color accentText = dark ? Mix(accent, Colors.White, 0.35) : Mix(accent, Colors.Black, 0.25);

                void Put(string key, object value) { res[key] = value; _appliedAccentKeys.Add(key); }
                SolidColorBrush Solid(Color c, double opacity = 1.0) => new SolidColorBrush(c) { Opacity = opacity };

                var fill = Solid(accent);
                var fillHover = Solid(accent, 0.9);
                var fillPressed = Solid(accent, 0.8);
                var text = Solid(accentText);
                var textOn = Solid(onAccent);
                var textOnSecondary = Solid(onAccent, 0.8);

                Put("SystemAccentColor", accent);
                Put("SystemAccentColorLight1", Mix(accent, Colors.White, 0.15));
                Put("SystemAccentColorLight2", Mix(accent, Colors.White, 0.30));
                Put("SystemAccentColorLight3", Mix(accent, Colors.White, 0.45));
                Put("SystemAccentColorDark1", Mix(accent, Colors.Black, 0.15));
                Put("SystemAccentColorDark2", Mix(accent, Colors.Black, 0.30));
                Put("SystemAccentColorDark3", Mix(accent, Colors.Black, 0.45));

                Put("AccentFillColorDefaultBrush", fill);
                Put("AccentFillColorSecondaryBrush", fillHover);
                Put("AccentFillColorTertiaryBrush", fillPressed);
                Put("AccentTextFillColorPrimaryBrush", text);
                Put("AccentTextFillColorSecondaryBrush", text);
                Put("AccentTextFillColorTertiaryBrush", text);
                Put("TextOnAccentFillColorPrimaryBrush", textOn);
                Put("TextOnAccentFillColorSecondaryBrush", textOnSecondary);

                Put("SystemControlForegroundAccentBrush", fill);
                Put("SystemControlBackgroundAccentBrush", fill);
                Put("SystemControlHighlightAccentBrush", fill);
                Put("SystemControlHyperlinkTextBrush", text);

                Put("AccentButtonBackground", fill);
                Put("AccentButtonBackgroundPointerOver", fillHover);
                Put("AccentButtonBackgroundPressed", fillPressed);
                Put("AccentButtonForeground", textOn);
                Put("AccentButtonForegroundPointerOver", textOn);
                Put("AccentButtonForegroundPressed", textOnSecondary);

                Put("ToggleSwitchFillOn", fill);
                Put("ToggleSwitchFillOnPointerOver", fillHover);
                Put("ToggleSwitchFillOnPressed", fillPressed);
                Put("ToggleSwitchStrokeOn", fill);
                Put("ToggleSwitchStrokeOnPointerOver", fillHover);
                Put("ToggleSwitchStrokeOnPressed", fillPressed);

                Put("ListViewItemSelectionIndicatorBrush", fill);
                Put("TextControlBorderBrushFocused", fill);
                Put("ToggleButtonBackgroundChecked", fill);
                Put("ToggleButtonBackgroundCheckedPointerOver", fillHover);
                Put("ToggleButtonBackgroundCheckedPressed", fillPressed);
                Put("ProgressBarForeground", fill);
                Put("ProgressRingForeground", fill);
            }

            // Custom controls draw their own accent.
            ProgressSlider.AccentColor = accent;
            VolumeSlider.AccentColor = accent;
            GlobalScaleSlider.AccentColor = accent;
            UpdateShuffleUI();
            UpdateRepeatUI();
            UpdateSelectionModeUI();

            // ThemeResource lookups only re-resolve on a theme change, so nudge the theme once and restore it.
            bool needsRefresh = hadOverrides || _appliedAccentKeys.Count > 0;
            if (refreshTheme && needsRefresh && !_isRefreshingAccentTheme)
            {
                _isRefreshingAccentTheme = true;
                var original = RootGrid.RequestedTheme;
                RootGrid.RequestedTheme = RootGrid.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
                {
                    RootGrid.RequestedTheme = original;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _isRefreshingAccentTheme = false);
                });
            }
        }

        private void OnAccentColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (_isInitializingUi || Settings.UseSystemAccentColor) return;
            Settings.AccentColorHex = ToHex(args.NewColor);
            // Dragging in the picker fires many events; apply once the user pauses.
            _accentTimer.Stop();
            _accentTimer.Start();
        }

        private void OnUseSystemAccentToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;

            Settings.UseSystemAccentColor = UseSystemAccentSwitch.IsOn;
            CustomAccentExpander.IsEnabled = !Settings.UseSystemAccentColor;

            if (!Settings.UseSystemAccentColor)
            {
                if (!TryParseHex(Settings.AccentColorHex, out var start))
                {
                    start = GetSystemAccent();
                    Settings.AccentColorHex = ToHex(start);
                }
                _isInitializingUi = true;
                AppColorPicker.Color = start;
                _isInitializingUi = false;
                CustomAccentExpander.IsExpanded = true;
            }
            else
            {
                CustomAccentExpander.IsExpanded = false;
            }

            ApplyAccentTheme(true);
            RequestSaveSettings();
        }

        private void OnOnlineMetadataToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi) return;
            Settings.EnableOnlineMetadata = OnlineMetadataSwitch.IsOn;
            RequestSaveSettings();
        }

        private Brush AccentBrush() => new SolidColorBrush(EffectiveAccent);

        #endregion

        #region Transport and progress

        private void OnCompositionRendering(object? sender, object e)
        {
            var engine = App.MusicEngine;
            if (_isScrubbing || !_windowVisible || engine == null) return;

            long length = engine.Length;
            if (length <= 0) return;
            long time = engine.Time;

            ProgressSlider.Value = (double)time / length * 100.0;

            long currentSeconds = time / 1000;
            if (currentSeconds != _lastSecondsUpdated)
            {
                CurrentTimeText.Text = FormatTime(time);
                TotalTimeText.Text = FormatTime(length);
                _lastSecondsUpdated = currentSeconds;
            }
        }

        private void SetPlayingState(bool playing)
        {
            _isPlaying = playing;
            UpdateRenderingHook();
        }

        private void UpdateRenderingHook()
        {
            bool shouldRender = _isPlaying && _windowVisible;
            if (shouldRender && !_isRenderingHooked)
            {
                CompositionTarget.Rendering += OnCompositionRendering;
                _isRenderingHooked = true;
            }
            else if (!shouldRender && _isRenderingHooked)
            {
                CompositionTarget.Rendering -= OnCompositionRendering;
                _isRenderingHooked = false;
            }
        }

        private void ProgressSlider_ScrubbingStarted(object sender, double value) { _isScrubbing = true; }

        // While dragging (or using the keyboard) only the time label previews the target position.
        private void ProgressSlider_ValueChanged(object sender, double value)
        {
            long length = App.MusicEngine?.Length ?? 0;
            if (length > 0) CurrentTimeText.Text = FormatTime((long)(length * (value / 100.0)));
        }

        private void ProgressSlider_ScrubbingEnded(object sender, double value)
        {
            _isScrubbing = false;
            App.MusicEngine?.SeekTo((float)(value / 100.0));
            _lastSecondsUpdated = -1;
        }

        private void ShuffleMemoryBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isInitializingUi || Settings == null || double.IsNaN(args.NewValue)) return;
            Settings.ShuffleMemorySize = Math.Max(0, (int)args.NewValue);
            RequestSaveSettings();
        }

        private void ApplyScale(double scale)
        {
            if (ContentTransform != null) { ContentTransform.ScaleX = scale; ContentTransform.ScaleY = scale; }
            if (TransportTransform != null) { TransportTransform.ScaleX = scale; TransportTransform.ScaleY = scale; }
            if (GlobalScaleText != null) GlobalScaleText.Text = $"{scale:P0}";
        }

        private void GlobalScaleSlider_ValueChanged(object sender, double newVal)
        {
            if (Settings == null) return;
            Settings.GlobalScale = newVal;
            ApplyScale(newVal);
        }
        private void GlobalScaleSlider_ScrubbingEnded(object sender, double newVal) => RequestSaveSettings();

        private void VolumeSlider_ValueChanged(object sender, double newVal)
        {
            if (Settings == null) return;
            Settings.Volume = (int)newVal;
            App.MusicEngine?.SetVolume((int)newVal);
        }
        private void VolumeSlider_ScrubbingEnded(object sender, double newVal) => RequestSaveSettings();

        #endregion

        #region Library loading, search and sorting

        private async Task LoadLibraryFromCacheAsync()
        {
            if (string.IsNullOrWhiteSpace(Settings.LastLibraryPath) || !Directory.Exists(Settings.LastLibraryPath)) return;
            IsLoading = true;
            var cachedTracks = await Task.Run(() => LibraryCache.Load());
            if (cachedTracks != null && cachedTracks.Count > 0)
            {
                _fullCache = cachedTracks;
                ApplySortAndFilter();
            }
            IsLoading = false;
        }

        private static List<Track> ScanFolder(string folderPath, List<Track> existing, bool refreshDurations = false)
        {
            // Streaming enumeration: FileInfo objects carry timestamps from the directory listing itself,
            // so no per-file stat calls are needed and nothing is read for unchanged files.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System
            };

            var cached = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in existing) cached[t.FilePath] = t;

            var result = new ConcurrentBag<Track>();
            var files = new DirectoryInfo(folderPath).EnumerateFiles("*", options).Where(f => AudioFormats.Extensions.Contains(f.Extension));

            // Two readers at most: plenty for tag parsing while keeping disk and CPU load light.
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 2 }, fi =>
            {
                if (cached.TryGetValue(fi.FullName, out var old) && Math.Abs((fi.LastWriteTime - old.DateModified).TotalSeconds) < 1)
                {
                    if (refreshDurations || old.Duration <= TimeSpan.Zero)
                    {
                        var dur = ShellPropertyReader.GetDuration(fi.FullName);
                        if (dur > TimeSpan.Zero) old.Duration = dur;
                    }
                    result.Add(old);   // unchanged file: reuse the cached entry, no file access at all
                    return;
                }
                result.Add(TrackMetadataReader.Read(fi));
            });

            // Files that disappeared are simply absent from the result, so no File.Exists sweep is needed.
            return result.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private async Task ProcessAudioFilesAsync(string folderPath, bool isIncrementalScan, bool refreshDurations = false)
        {
            if (IsLoading) return;
            IsLoading = true;

            var existing = isIncrementalScan ? _fullCache.ToList() : new List<Track>();
            try
            {
                _fullCache = await Task.Run(() => ScanFolder(folderPath, existing, refreshDurations));
                _libraryDirty = true;
                ApplySortAndFilter();

                var snapshot = _fullCache.ToList();
                _ = Task.Run(() => LibraryCache.Save(snapshot));
                _libraryDirty = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning audio files: {ex.Message}");
            }
            finally { IsLoading = false; }
        }

        private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // Filter after the user pauses typing instead of on every keystroke.
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void OnSortChanged(object sender, SelectionChangedEventArgs e) => ApplySortAndFilter();

        private void ApplySortAndFilter()
        {
            if (_fullCache.Count == 0 || SortBox.SelectedItem is not ComboBoxItem item) return;

            var cmp = StringComparer.OrdinalIgnoreCase;
            IEnumerable<Track> sorted = item.Content?.ToString() switch
            {
                "Title (A-Z)" => _fullCache.OrderBy(t => t.Title, cmp),
                "Title (Z-A)" => _fullCache.OrderByDescending(t => t.Title, cmp),
                "Artist (A-Z)" => _fullCache.OrderBy(t => t.Artist, cmp).ThenBy(t => t.Title, cmp),
                "Artist (Z-A)" => _fullCache.OrderByDescending(t => t.Artist, cmp).ThenBy(t => t.Title, cmp),
                "Album (A-Z)" => _fullCache.OrderBy(t => t.Album, cmp).ThenBy(t => t.Title, cmp),
                "Album (Z-A)" => _fullCache.OrderByDescending(t => t.Album, cmp).ThenBy(t => t.Title, cmp),
                "Date Added (Newest)" => _fullCache.OrderByDescending(t => t.DateAdded),
                "Date Added (Oldest)" => _fullCache.OrderBy(t => t.DateAdded),
                "Date Modified (Newest)" => _fullCache.OrderByDescending(t => t.DateModified),
                "Date Modified (Oldest)" => _fullCache.OrderBy(t => t.DateModified),
                "Duration (Longest)" => _fullCache.OrderByDescending(t => t.Duration),
                "Duration (Shortest)" => _fullCache.OrderBy(t => t.Duration),
                _ => _fullCache
            };
            _fullCache = sorted.ToList();

            string query = SearchBox.Text?.Trim() ?? string.Empty;
            List<Track> view = query.Length == 0
                ? _fullCache
                : _fullCache.Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                                     || t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase)
                                     || t.Album.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            LibraryTracks = new ObservableCollection<Track>(view);
            SearchCountText.Text = query.Length == 0 ? "" : $"{view.Count} found";
        }

        #endregion

        #region Playback queue

        private void OnUpcomingQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(QueueCount));
            OnPropertyChanged(nameof(HasQueuedTracks));
            OnPropertyChanged(nameof(QueueBadgeVisibility));
            OnPropertyChanged(nameof(QueueEmptyVisibility));
        }

        // A context action applies to every selected track when the right-clicked track is part of a multi-selection.
        private List<Track> GetActionTargets(object sender)
        {
            if ((sender as FrameworkElement)?.DataContext is not Track clicked) return new List<Track>();

            if (LibraryList.SelectionMode == ListViewSelectionMode.Multiple && LibraryList.SelectedItems.Count > 1)
            {
                var selected = LibraryList.SelectedItems.OfType<Track>().ToHashSet();
                if (selected.Contains(clicked)) return LibraryTracks.Where(selected.Contains).ToList();
            }
            return new List<Track> { clicked };
        }

        private void QueueNext(IReadOnlyList<Track> tracks)
        {
            if (tracks.Count == 0) return;
            if (NowPlayingTrack == null)
            {
                // Nothing is playing yet: start the first one and line up the rest.
                for (int i = 1; i < tracks.Count; i++) UpcomingQueue.Insert(i - 1, tracks[i]);
                StartFromLibrary(tracks[0]);
                return;
            }
            for (int i = 0; i < tracks.Count; i++) UpcomingQueue.Insert(i, tracks[i]);
        }

        private void QueueLast(IReadOnlyList<Track> tracks)
        {
            foreach (var t in tracks) UpcomingQueue.Add(t);
        }

        private void OnQueueNextClick(object sender, RoutedEventArgs e) => QueueNext(GetActionTargets(sender));
        private void OnQueueLastClick(object sender, RoutedEventArgs e) => QueueLast(GetActionTargets(sender));

        private void OnBatchQueueNextClick(object sender, RoutedEventArgs e)
        {
            var targets = GetSelectedInLibraryOrder();
            ExitSelectionMode();
            QueueNext(targets);
        }

        private void OnBatchQueueLastClick(object sender, RoutedEventArgs e)
        {
            var targets = GetSelectedInLibraryOrder();
            ExitSelectionMode();
            QueueLast(targets);
        }

        private List<Track> GetSelectedInLibraryOrder()
        {
            var selected = LibraryList.SelectedItems.OfType<Track>().ToHashSet();
            return LibraryTracks.Where(selected.Contains).ToList();
        }

        private void OnClearQueueClick(object sender, RoutedEventArgs e) => UpcomingQueue.Clear();

        private void OnRemoveQueueItemClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is Track track) UpcomingQueue.Remove(track);
        }

        #endregion

        #region Playback control

        private void StartFromLibrary(Track track)
        {
            _playingFromQueue = false;
            _contextAnchor = null;
            _playbackQueue = LibraryTracks.ToList();
            PlayTrack(track);
        }

        private void OnTrackItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Track selectedTrack) StartFromLibrary(selectedTrack);
        }

        private void OnPlayNowClick(object sender, RoutedEventArgs e)
        {
            var targets = GetActionTargets(sender);
            if (targets.Count == 0) return;
            for (int i = 1; i < targets.Count; i++) UpcomingQueue.Insert(i - 1, targets[i]);
            StartFromLibrary(targets[0]);
        }

        private void PushHistory(Track track)
        {
            _playbackHistory.Push(track);
            if (_playbackHistory.Count > 200)
                _playbackHistory = new Stack<Track>(_playbackHistory.Take(100).Reverse());
        }

        private void PlayTrack(Track? track, bool recordHistory = true)
        {
            if (track == null) return;

            if (NowPlayingTrack != null)
            {
                NowPlayingTrack.IsPlaying = false;
                NowPlayingTrack.IsPlayingState = false;
                if (recordHistory) PushHistory(NowPlayingTrack);
            }

            NowPlayingTrack = track;
            NowPlayingTrack.IsPlaying = true;
            NowPlayingTrack.IsPlayingState = true;
            LibraryList.ScrollIntoView(NowPlayingTrack);

            if (!_shufflePlayedHistory.Contains(track.FilePath)) _shufflePlayedHistory.Add(track.FilePath);
            if (Settings.ShuffleMemorySize > 0 && _shufflePlayedHistory.Count > Settings.ShuffleMemorySize) _shufflePlayedHistory.RemoveAt(0);

            _lastSecondsUpdated = -1;
            App.MusicEngine?.Play(NowPlayingTrack);
            SetPlayingState(true);
            PlayPauseIcon.Glyph = "\uE769";
        }

        private void StopAtEnd()
        {
            if (NowPlayingTrack != null) NowPlayingTrack.IsPlayingState = false;
            SetPlayingState(false);
            PlayPauseIcon.Glyph = "\uE768";
        }

        /// <param name="userInitiated">True for the Next button / media keys, false when a track simply finished.</param>
        private void PlayNext(bool userInitiated)
        {
            // Repeat-one only applies when a track ends by itself; pressing Next always moves on.
            if (!userInitiated && Settings.RepeatMode == 2 && NowPlayingTrack != null) { PlayTrack(NowPlayingTrack, false); return; }

            if (UpcomingQueue.Count > 0)
            {
                if (!_playingFromQueue) { _contextAnchor = NowPlayingTrack; _playingFromQueue = true; }
                var next = UpcomingQueue[0];
                UpcomingQueue.RemoveAt(0);
                PlayTrack(next);
                return;
            }

            if (_fullCache.Count == 0) return;

            // The normal list resumes from where it was before the manual queue took over.
            Track? basis = _playingFromQueue && _contextAnchor != null ? _contextAnchor : NowPlayingTrack;
            _playingFromQueue = false;
            _contextAnchor = null;

            if (Settings.IsShuffle)
            {
                var pool = _fullCache.Where(t => t.DateModified >= Settings.RandomStartDate && t.DateModified <= Settings.RandomEndDate).ToList();
                if (Settings.ShuffleMemorySize > 0)
                {
                    var fresh = pool.Where(t => !_shufflePlayedHistory.Contains(t.FilePath)).ToList();
                    if (fresh.Count > 0) pool = fresh;
                }
                if (pool.Count > 0) { PlayTrack(pool[Random.Shared.Next(pool.Count)]); return; }
            }

            if (_playbackQueue.Count == 0 || (basis != null && !_playbackQueue.Contains(basis))) _playbackQueue = _fullCache.ToList();

            int index = (basis == null ? -1 : _playbackQueue.IndexOf(basis)) + 1;
            if (index >= _playbackQueue.Count)
            {
                if (Settings.RepeatMode == 1) index = 0;
                else { if (!userInitiated) StopAtEnd(); return; }
            }
            PlayTrack(_playbackQueue[index]);
        }

        private void PlayPrevious()
        {
            if (App.MusicEngine != null && App.MusicEngine.Time > 3000 && NowPlayingTrack != null) { PlayTrack(NowPlayingTrack, false); return; }
            if (_playbackHistory.Count > 0)
            {
                var prevTrack = _playbackHistory.Pop();
                PlayTrack(prevTrack, false);
                LibraryList.ScrollIntoView(prevTrack);
            }
        }

        private void OnPreviousClick(object sender, RoutedEventArgs e) => PlayPrevious();
        private void OnNextClick(object sender, RoutedEventArgs e) => PlayNext(true);

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (NowPlayingTrack == null) { PlayNext(true); return; }
            if (!_isPlaying) { App.MusicEngine?.Resume(); NowPlayingTrack.IsPlayingState = true; PlayPauseIcon.Glyph = "\uE769"; SetPlayingState(true); }
            else { App.MusicEngine?.Pause(); NowPlayingTrack.IsPlayingState = false; PlayPauseIcon.Glyph = "\uE768"; SetPlayingState(false); }
        }

        private void RepeatBtn_Click(object sender, RoutedEventArgs e) { Settings.RepeatMode = (Settings.RepeatMode + 1) % 3; UpdateRepeatUI(); RequestSaveSettings(); }

        private void UpdateRepeatUI()
        {
            if (RepeatIcon == null) return;
            RepeatIcon.Text = Settings.RepeatMode == 2 ? "\uE8ED" : "\uE8EE";
            // Inactive icons inherit the button's own theme-aware foreground.
            if (Settings.RepeatMode == 0) RepeatIcon.ClearValue(Microsoft.UI.Xaml.Controls.TextBlock.ForegroundProperty);
            else RepeatIcon.Foreground = AccentBrush();
        }

        private void ShuffleBtn_Click(object sender, RoutedEventArgs e)
        {
            Settings.IsShuffle = !Settings.IsShuffle;
            UpdateShuffleUI();
            RequestSaveSettings();
        }

        private void UpdateShuffleUI()
        {
            if (ShuffleIcon == null) return;
            if (Settings.IsShuffle) ShuffleIcon.Foreground = AccentBrush();
            else ShuffleIcon.ClearValue(Microsoft.UI.Xaml.Controls.TextBlock.ForegroundProperty);
        }

        private void RandomDates_Changed(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            if (_isInitializingUi) return;
            if (RandomStartPicker?.Date != null) Settings.RandomStartDate = RandomStartPicker.Date.Value;
            if (RandomEndPicker?.Date != null) Settings.RandomEndDate = RandomEndPicker.Date.Value;
            RequestSaveSettings();
        }

        #endregion

        #region Opening files from Windows

        private void OnFileActivated(string? path)
        {
            try
            {
                if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.Restore();
                this.Activate();
            }
            catch { }
            if (path != null) OpenExternalFile(path);
        }

        private void OpenExternalFile(string path)
        {
            if (!AudioFormats.IsSupported(path) || !File.Exists(path)) return;

            var track = _fullCache.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase))
                        ?? TrackMetadataReader.Read(path);

            _playingFromQueue = false;
            _contextAnchor = null;
            _playbackQueue = new List<Track> { track };
            PlayTrack(track);
        }

        #endregion

        #region Settings overlay and library folder

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = SettingsOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (SettingsOverlay.Visibility == Visibility.Visible) UpdateLibraryPathText();
        }

        private void OnViewToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingUi || ArtworkPanel == null) return;
            ArtworkPanel.Visibility = ToggleArtworkPanelSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
            Settings.ShowArtworkPanel = ToggleArtworkPanelSwitch.IsOn;
            RequestSaveSettings();
        }

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
                UpdateLibraryPathText();
                RequestSaveSettings();
                await ProcessAudioFilesAsync(folder.Path, false);
            }
        }

        private async void OnScanLibraryClick(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlay != null) SettingsOverlay.Visibility = Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(Settings.LastLibraryPath) || !Directory.Exists(Settings.LastLibraryPath))
            {
                OnAddFolderClick(sender, e);
                return;
            }
            await ProcessAudioFilesAsync(Settings.LastLibraryPath, true, refreshDurations: true);
        }

        #endregion

        #region Selection mode and batch edit

        private bool _isBatchEditMode = false;
        private List<Track> _tracksToEdit = new();
        private byte[]? _newArtworkBytes = null;

        private bool IsSelectionMode => LibraryList.SelectionMode == ListViewSelectionMode.Multiple;

        private void UpdateSelectionModeUI()
        {
            if (SelectionModeToggleBtn == null) return;
            if (IsSelectionMode) SelectionModeToggleBtn.Foreground = AccentBrush();
            else SelectionModeToggleBtn.ClearValue(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty);
        }

        private void ExitSelectionMode()
        {
            LibraryList.SelectionMode = ListViewSelectionMode.Single;
            LibraryList.IsItemClickEnabled = true;
            BatchEditExecuteBtn.Visibility = Visibility.Collapsed;
            BatchQueueNextBtn.Visibility = Visibility.Collapsed;
            BatchQueueLastBtn.Visibility = Visibility.Collapsed;
            UpdateSelectionModeUI();
        }

        private void OnSelectionModeToggleClick(object sender, RoutedEventArgs e)
        {
            if (!IsSelectionMode)
            {
                LibraryList.SelectionMode = ListViewSelectionMode.Multiple;
                LibraryList.IsItemClickEnabled = false;
                BatchEditExecuteBtn.Visibility = Visibility.Visible;
                BatchQueueNextBtn.Visibility = Visibility.Visible;
                BatchQueueLastBtn.Visibility = Visibility.Visible;
                UpdateSelectionModeUI();
            }
            else ExitSelectionMode();
        }

        private void OnBatchEditExecuteClick(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedInLibraryOrder();
            ExitSelectionMode();
            if (selected.Count > 0) OpenEditOverlay(selected);
        }

        private void OnEditTrackMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.DataContext is Track track) OpenEditOverlay(new List<Track> { track });
        }

        private void ShowEditError(string title, string message)
        {
            EditErrorBar.Title = title;
            EditErrorBar.Message = message;
            EditErrorBar.IsOpen = true;
        }

        private void HideEditError() => EditErrorBar.IsOpen = false;

        private void OpenEditOverlay(List<Track> tracks)
        {
            if (SettingsOverlay != null) SettingsOverlay.Visibility = Visibility.Collapsed;

            _tracksToEdit = tracks;
            _isBatchEditMode = tracks.Count > 1;
            _newArtworkBytes = null;
            HideEditError();
            EditOverlayTitle.Text = _isBatchEditMode ? $"Batch edit ({tracks.Count} tracks)" : "Edit track info";
            MetadataFetchPanel.Visibility = Settings.EnableOnlineMetadata ? Visibility.Visible : Visibility.Collapsed;
            MetadataUrlBox.Text = "";

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
                    using var file = TagLib.File.Create(new LocalFileAbstraction(t.FilePath), TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
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

        #endregion

        #region Online metadata (hardened)

        private static bool IsAllowedImageHost(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (IPAddress.TryParse(uri.IdnHost, out _)) return false;   // never fetch from a raw IP address
            string host = uri.IdnHost.ToLowerInvariant();
            return AllowedImageHostSuffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));
        }

        private static bool TryBuildOEmbedUrl(string input, out Uri oEmbed, out bool isYouTube)
        {
            oEmbed = null!;
            isYouTube = false;

            if (!Uri.TryCreate(input, UriKind.Absolute, out var link)) return false;
            if (link.Scheme != Uri.UriSchemeHttps || !link.IsDefaultPort || !string.IsNullOrEmpty(link.UserInfo)) return false;

            string host = link.IdnHost.ToLowerInvariant();
            if (!AllowedLinkHosts.Contains(host)) return false;

            string escaped = Uri.EscapeDataString(link.AbsoluteUri);
            if (host.EndsWith("spotify.com", StringComparison.Ordinal))
                oEmbed = new Uri($"https://open.spotify.com/oembed?url={escaped}");
            else if (host.Contains("soundcloud.com", StringComparison.Ordinal))
                oEmbed = new Uri($"https://soundcloud.com/oembed?format=json&url={escaped}");
            else
            {
                oEmbed = new Uri($"https://www.youtube.com/oembed?url={escaped}&format=json");
                isYouTube = true;
            }
            return true;
        }

        private static async Task<byte[]> DownloadLimitedAsync(Uri uri, int maxBytes, bool requireImage, CancellationToken token)
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            if (requireImage && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidDataException("The server did not return an image.");
            if (response.Content.Headers.ContentLength > maxBytes)
                throw new InvalidDataException("The response is larger than allowed.");

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, token)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new InvalidDataException("The response is larger than allowed.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        private async void OnFetchMetadataClick(object sender, RoutedEventArgs e)
        {
            if (!Settings.EnableOnlineMetadata) return;
            string url = MetadataUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url)) return;

            HideEditError();
            if (!TryBuildOEmbedUrl(url, out var oEmbedUrl, out bool isYouTube))
            {
                ShowEditError("Link not supported", "Use a secure (https) link from Spotify, YouTube or SoundCloud.");
                return;
            }

            FetchMetadataBtn.IsEnabled = false;
            FetchProgressRing.IsActive = true;
            FetchProgressRing.Visibility = Visibility.Visible;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                byte[] jsonBytes = await DownloadLimitedAsync(oEmbedUrl, MaxJsonBytes, false, cts.Token);
                var data = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(jsonBytes));
                if (data == null) throw new InvalidDataException("The service returned no data.");

                string fetchedTitle = data["title"]?.ToString() ?? "";
                string fetchedArtist = data["author_name"]?.ToString() ?? "";
                string thumbnailUrl = data["thumbnail_url"]?.ToString() ?? "";

                if (isYouTube)
                {
                    fetchedArtist = fetchedArtist.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Trim();
                    int dash = fetchedTitle.IndexOf(" - ", StringComparison.Ordinal);
                    if (dash > 0)
                    {
                        fetchedArtist = fetchedTitle.Substring(0, dash).Trim();
                        fetchedTitle = fetchedTitle.Substring(dash + 3).Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(fetchedTitle) && !_isBatchEditMode) EditTitleBox.Text = fetchedTitle;
                if (!string.IsNullOrWhiteSpace(fetchedArtist)) EditArtistBox.Text = fetchedArtist;

                if (Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var thumbUri) && IsAllowedImageHost(thumbUri))
                {
                    byte[] imageBytes = await DownloadLimitedAsync(thumbUri, MaxImageBytes, true, cts.Token);

                    var bitmap = new BitmapImage { DecodePixelWidth = 320 };
                    using var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(imageBytes.AsBuffer());
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);   // throws if the bytes are not a valid image

                    _newArtworkBytes = imageBytes;
                    EditArtworkPreview.Source = bitmap;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
            {
                ShowEditError("Could not fetch metadata", ex is TaskCanceledException ? "The request timed out." : ex.Message);
            }
            catch (Exception ex)
            {
                ShowEditError("Could not fetch metadata", ex.Message);
            }
            finally
            {
                FetchMetadataBtn.IsEnabled = true;
                FetchProgressRing.IsActive = false;
                FetchProgressRing.Visibility = Visibility.Collapsed;
            }
        }

        #endregion

        #region Editing artwork and saving tags

        private async void OnEditArtworkClick(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            HideEditError();

            try
            {
                using var stream = await file.OpenReadAsync();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);

                if (decoder.PixelWidth < 300 || decoder.PixelHeight < 300)
                {
                    ShowEditError("Artwork rejected", $"The image is {decoder.PixelWidth}x{decoder.PixelHeight}. The minimum is 300x300.");
                    return;
                }

                using var memStream = new InMemoryRandomAccessStream();
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, memStream);

                var softwareBitmap = await decoder.GetSoftwareBitmapAsync();
                encoder.SetSoftwareBitmap(softwareBitmap);
                await encoder.FlushAsync();

                _newArtworkBytes = new byte[memStream.Size];
                await memStream.ReadAsync(_newArtworkBytes.AsBuffer(), (uint)memStream.Size, InputStreamOptions.None);

                var bmp = new BitmapImage { DecodePixelWidth = 320 };
                memStream.Seek(0);
                await bmp.SetSourceAsync(memStream);
                EditArtworkPreview.Source = bmp;
            }
            catch (Exception ex)
            {
                ShowEditError("Could not load image", ex.Message);
            }
        }

        private void OnClearArtworkClick(object sender, RoutedEventArgs e)
        {
            _newArtworkBytes = Array.Empty<byte>();
            EditArtworkPreview.Source = null;
        }

        private void OnCancelEditClick(object sender, RoutedEventArgs e) => EditOverlay.Visibility = Visibility.Collapsed;

        private async void OnSaveEditClick(object sender, RoutedEventArgs e)
        {
            HideEditError();
            var update = new TrackMetadataUpdate();

            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditTitleBox.Text)) update.Title = EditTitleBox.Text;
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditArtistBox.Text)) update.Performers = EditArtistBox.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditAlbumBox.Text)) update.Album = EditAlbumBox.Text;
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditAlbumArtistBox.Text)) update.AlbumArtists = EditAlbumArtistBox.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (!_isBatchEditMode || !double.IsNaN(EditTrackBox.Value)) update.Track = (uint)Math.Max(0, double.IsNaN(EditTrackBox.Value) ? 0 : EditTrackBox.Value);
            if (!_isBatchEditMode || !double.IsNaN(EditDiscBox.Value)) update.Disc = (uint)Math.Max(0, double.IsNaN(EditDiscBox.Value) ? 0 : EditDiscBox.Value);
            if (!_isBatchEditMode || !double.IsNaN(EditYearBox.Value)) update.Year = (uint)Math.Max(0, double.IsNaN(EditYearBox.Value) ? 0 : EditYearBox.Value);

            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditGenreBox.Text)) update.Genres = EditGenreBox.Text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!_isBatchEditMode || !string.IsNullOrWhiteSpace(EditCommentBox.Text)) update.Comment = EditCommentBox.Text;

            update.ArtworkData = _newArtworkBytes;

            bool isBatch = _isBatchEditMode;
            bool successAll = true;
            EditOverlay.IsHitTestVisible = false;

            try
            {
                foreach (var t in _tracksToEdit)
                {
                    try
                    {
                        string path = t.FilePath;
                        // Tag writing runs off the UI thread so a large batch never freezes the window.
                        await Task.Run(() => MetadataWriter.SaveMetadata(path, update, isBatch));

                        if (update.Title != null) t.Title = update.Title;
                        if (update.Performers != null) t.Artist = string.Join(", ", update.Performers);
                        if (update.Album != null) t.Album = update.Album;

                        t.DateModified = File.GetLastWriteTime(path);

                        if (update.ArtworkData != null)
                        {
                            Track.ClearArtworkCache(path);
                            t.TriggerArtworkRefresh();
                        }
                    }
                    catch (Exception ex)
                    {
                        string message = ex is IOException ? "Close the file in other programs and try again."
                                       : ex is TagLib.CorruptFileException ? "The file's metadata is corrupted."
                                       : ex.Message;
                        ShowEditError("Could not save", message);
                        successAll = false;
                        break;
                    }
                }
            }
            finally { EditOverlay.IsHitTestVisible = true; }

            if (successAll)
            {
                EditOverlay.Visibility = Visibility.Collapsed;
                _libraryDirty = true;
                var snapshot = _fullCache.ToList();
                _ = Task.Run(() => LibraryCache.Save(snapshot));
                _libraryDirty = false;
                ApplySortAndFilter();
            }
        }

        #endregion

        private void RootGrid_DragOver(object sender, DragEventArgs e) { }
        private void RootGrid_Drop(object sender, DragEventArgs e) { }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}