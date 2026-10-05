using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Text;
using System.Runtime.InteropServices.WindowsRuntime; // RESTORED: Required for .AsBuffer()

namespace MusicPower3.Models
{
    public class LocalFileAbstraction : TagLib.File.IFileAbstraction
    {
        public LocalFileAbstraction(string file) { Name = file; }
        public string Name { get; }
        public Stream ReadStream => new FileStream(Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        public Stream WriteStream => new FileStream(Name, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        public void CloseStream(Stream stream) => stream.Dispose();
    }

    public class AppSettings
    {
        public double GlobalScale { get; set; } = 1.0;
        public int Volume { get; set; } = 100;
        public bool ShowArtworkPanel { get; set; } = true;
        public int RepeatMode { get; set; } = 0; 
        public bool IsShuffle { get; set; } = false;
        public int ShuffleMemorySize { get; set; } = 50;
        public DateTimeOffset RandomStartDate { get; set; } = DateTimeOffset.Now.AddMonths(-1);
        public DateTimeOffset RandomEndDate { get; set; } = DateTimeOffset.Now;
        public string LastLibraryPath { get; set; } = string.Empty;

        // Empty means "no custom colour chosen yet". The Windows accent colour is used unless
        // UseSystemAccentColor is switched off AND a valid custom colour exists.
        public string AccentColorHex { get; set; } = string.Empty;
        public bool UseSystemAccentColor { get; set; } = true;

        // Online metadata lookup (Spotify / YouTube / SoundCloud oEmbed) can be disabled entirely.
        public bool EnableOnlineMetadata { get; set; } = true;
    }

    public sealed class Track : INotifyPropertyChanged
    {
        private const int ThumbnailDecodeWidth = 96;   // list thumbnails: small decode keeps RAM low
        private const int HighResDecodeWidth = 600;      // now-playing panel only (one image at a time)
        private const int ThumbnailCacheLimit = 80;

        private static readonly object _cacheLock = new();
        private static readonly Dictionary<string, BitmapImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> _imageCacheQueue = new();
        // Paths known to have no embedded artwork, so scrolling never re-reads those files.
        private static readonly HashSet<string> _noArtwork = new(StringComparer.OrdinalIgnoreCase);
        // At most two artwork reads in flight at once: keeps disk and CPU usage flat while scrolling.
        private static readonly SemaphoreSlim _artworkGate = new(2, 2);

        private bool _isPlaying;
        private bool _isPlayingState;
        private BitmapImage? _highResArtworkImage;
        private bool _imageLoadingStarted;
        private DateTime _dateModified;

        public Track() { FilePath = ""; Title = ""; Artist = ""; Album = ""; }

        public Track(string filePath, string title, string artist, string album, TimeSpan duration, DateTime dateAdded, DateTime dateModified)
        {
            FilePath = filePath; Title = title; Artist = artist; Album = album;
            Duration = duration; DateAdded = dateAdded; DateModified = dateModified;
        }

        private string _title = "";
        private string _artist = "";
        private string _album = "";

        public string FilePath { get; set; }
        public string Title { get => _title; set { if (_title != value) { _title = value; OnPropertyChanged(); } } }
        public string Artist { get => _artist; set { if (_artist != value) { _artist = value; OnPropertyChanged(); } } }
        public string Album { get => _album; set { if (_album != value) { _album = value; OnPropertyChanged(); } } }
        public TimeSpan Duration { get; set; }
        public DateTime DateAdded { get; set; }
        
        public DateTime DateModified 
        { 
            get => _dateModified; 
            set { _dateModified = value; OnPropertyChanged(); } 
        }
        
        [JsonIgnore] public string DisplayDuration => $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}";
        [JsonIgnore] public FontWeight TitleWeight => IsPlaying ? FontWeights.Bold : FontWeights.SemiBold;
        [JsonIgnore] public Visibility PlayingIndicatorVisibility => IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        [JsonIgnore] public string PlayPauseGlyph => IsPlayingState ? "\uE769" : "\uE768";

        [JsonIgnore]
        public bool IsPlayingState
        {
            get => _isPlayingState;
            set { _isPlayingState = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayPauseGlyph)); }
        }

        [JsonIgnore]
        public bool IsPlaying 
        { 
            get => _isPlaying; 
            set 
            { 
                _isPlaying = value; 
                if (_isPlaying && _highResArtworkImage == null) _ = LoadHighResImageAsync();
                else if (!_isPlaying) _highResArtworkImage = null; 
                
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(HighResArtworkImage));
                OnPropertyChanged(nameof(TitleWeight));
                OnPropertyChanged(nameof(PlayingIndicatorVisibility));
            } 
        }

        [JsonIgnore]
        public BitmapImage? ArtworkImage
        {
            get
            {
                lock (_cacheLock)
                {
                    if (_imageCache.TryGetValue(FilePath, out var cachedImg)) return cachedImg;
                    if (_noArtwork.Contains(FilePath)) return null;
                }
                if (!_imageLoadingStarted) { _imageLoadingStarted = true; _ = LoadImageAsync(); }
                return null;
            }
        }

        [JsonIgnore]
        public BitmapImage? HighResArtworkImage => _highResArtworkImage ?? ArtworkImage;

        /// <summary>Reads the first embedded picture of a file. Shared by the UI loaders and the SMTC thumbnail.</summary>
        public static async Task<byte[]?> ReadArtworkBytesAsync(string filePath)
        {
            await _artworkGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    using var file = TagLib.File.Create(new LocalFileAbstraction(filePath), TagLib.ReadStyle.PictureLazy);
                    return file.Tag.Pictures.Length > 0 ? file.Tag.Pictures[0].Data.Data : null;
                }).ConfigureAwait(false);
            }
            catch { return null; }
            finally { _artworkGate.Release(); }
        }

        private static async Task<BitmapImage?> DecodeAsync(byte[] data, int decodeWidth)
        {
            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(data.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
            catch { return null; }
        }

        private async Task LoadImageAsync()
        {
            string path = FilePath;
            try
            {
                byte[]? data = await ReadArtworkBytesAsync(path).ConfigureAwait(false);
                var dispatcher = MainWindow.MainDispatcher;
                if (dispatcher == null) return;

                if (data == null)
                {
                    lock (_cacheLock) { _noArtwork.Add(path); }
                    return;
                }

                dispatcher.TryEnqueue(async () =>
                {
                    var bitmap = await DecodeAsync(data, ThumbnailDecodeWidth);
                    if (bitmap == null) return;

                    lock (_cacheLock)
                    {
                        if (!_imageCache.ContainsKey(path))
                        {
                            while (_imageCacheQueue.Count >= ThumbnailCacheLimit) _imageCache.Remove(_imageCacheQueue.Dequeue());
                            _imageCacheQueue.Enqueue(path);
                        }
                        _imageCache[path] = bitmap;
                    }

                    OnPropertyChanged(nameof(ArtworkImage));
                    OnPropertyChanged(nameof(HighResArtworkImage));
                });
            }
            catch { }
            finally { _imageLoadingStarted = false; }
        }

        private async Task LoadHighResImageAsync()
        {
            try
            {
                byte[]? data = await ReadArtworkBytesAsync(FilePath).ConfigureAwait(false);
                var dispatcher = MainWindow.MainDispatcher;
                if (data == null || dispatcher == null) return;

                dispatcher.TryEnqueue(async () =>
                {
                    // The track may have stopped playing while the file was being read.
                    if (!_isPlaying) return;
                    var bitmap = await DecodeAsync(data, HighResDecodeWidth);
                    if (bitmap == null || !_isPlaying) return;

                    _highResArtworkImage = bitmap;
                    OnPropertyChanged(nameof(HighResArtworkImage));
                });
            }
            catch { }
        }

        public static void ClearArtworkCache(string filePath)
        {
            lock (_cacheLock)
            {
                _imageCache.Remove(filePath);
                _noArtwork.Remove(filePath);
            }
        }

        public void TriggerArtworkRefresh()
        {
            _imageLoadingStarted = false;
            _highResArtworkImage = null;
            OnPropertyChanged(nameof(ArtworkImage));
            OnPropertyChanged(nameof(HighResArtworkImage));
            if (_isPlaying) _ = LoadHighResImageAsync();
        }
        
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}