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
        public string AccentColorHex { get; set; } = "#FF0078D4";
    }

    public sealed class Track : INotifyPropertyChanged
    {
        private static readonly Dictionary<string, BitmapImage> _imageCache = new();
        private static readonly Queue<string> _imageCacheQueue = new();

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

        public string FilePath { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Album { get; set; }
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
                if (_isPlaying && _highResArtworkImage == null) LoadHighResImageAsync();
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
                if (_imageCache.TryGetValue(FilePath, out var cachedImg)) return cachedImg;
                if (!_imageLoadingStarted) { _imageLoadingStarted = true; LoadImageAsync(); }
                return null;
            }
        }

        [JsonIgnore]
        public BitmapImage? HighResArtworkImage => _highResArtworkImage ?? ArtworkImage;

        private async void LoadImageAsync()
        {
            try
            {
                byte[]? data = await Task.Run(() => {
                    using var file = TagLib.File.Create(new LocalFileAbstraction(FilePath));
                    return file.Tag.Pictures.Length > 0 ? file.Tag.Pictures[0].Data.Data : null;
                });

                if (data != null && MainWindow.MainDispatcher != null)
                {
                    MainWindow.MainDispatcher.TryEnqueue(async () =>
                    {
                        try 
                        {
                            var bitmap = new BitmapImage { DecodePixelWidth = 150 };
                            using var stream = new InMemoryRandomAccessStream();
                            await stream.WriteAsync(data.AsBuffer());
                            stream.Seek(0);
                            await bitmap.SetSourceAsync(stream);

                            if (_imageCacheQueue.Count >= 50) _imageCache.Remove(_imageCacheQueue.Dequeue());
                            _imageCache[FilePath] = bitmap;
                            _imageCacheQueue.Enqueue(FilePath);

                            OnPropertyChanged(nameof(ArtworkImage));
                            OnPropertyChanged(nameof(HighResArtworkImage));
                        } catch { }
                    });
                }
            }
            catch { }
            finally { _imageLoadingStarted = false; }
        }

        private async void LoadHighResImageAsync()
        {
            try
            {
                byte[]? data = await Task.Run(() => {
                    using var file = TagLib.File.Create(new LocalFileAbstraction(FilePath));
                    return file.Tag.Pictures.Length > 0 ? file.Tag.Pictures[0].Data.Data : null;
                });

                if (data != null && MainWindow.MainDispatcher != null)
                {
                    MainWindow.MainDispatcher.TryEnqueue(async () =>
                    {
                        try 
                        {
                            var bitmap = new BitmapImage { DecodePixelWidth = 800 }; 
                            using var stream = new InMemoryRandomAccessStream();
                            await stream.WriteAsync(data.AsBuffer());
                            stream.Seek(0);
                            await bitmap.SetSourceAsync(stream);
                            
                            _highResArtworkImage = bitmap;
                            OnPropertyChanged(nameof(HighResArtworkImage));
                        } catch { }
                    });
                }
            }
            catch { }
        }

        public static void ClearArtworkCache(string filePath)
        {
            _imageCache.Remove(filePath);
        }

        public void TriggerArtworkRefresh()
        {
            _imageLoadingStarted = false;
            _highResArtworkImage = null;
            OnPropertyChanged(nameof(ArtworkImage));
            OnPropertyChanged(nameof(HighResArtworkImage));
        }
        
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}