using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using MusicPower3.Models;

namespace MusicPower3.Services
{
    public class AudioEngine : IDisposable
    {
        private MediaPlayer? _mediaPlayer;
        private SystemMediaTransportControls? _smtc;
        private bool _isReady = false;

        public event EventHandler? EndReached;
        public event EventHandler? PlayRequested;
        public event EventHandler? PauseRequested;
        public event EventHandler? NextRequested;
        public event EventHandler? PreviousRequested;

        public long Time => (long)(_mediaPlayer?.PlaybackSession.Position.TotalMilliseconds ?? 0);
        public long Length => (long)(_mediaPlayer?.PlaybackSession.NaturalDuration.TotalMilliseconds ?? 0);

        public AudioEngine()
        {
            Task.Run(() => 
            {
                try 
                {
                    _mediaPlayer = new MediaPlayer();
                    _mediaPlayer.AudioCategory = MediaPlayerAudioCategory.Media;
                    _mediaPlayer.MediaEnded += (s, e) => EndReached?.Invoke(this, EventArgs.Empty);
                    
                    _smtc = _mediaPlayer.SystemMediaTransportControls;
                    _smtc.IsEnabled = true;
                    _smtc.IsPlayEnabled = true;
                    _smtc.IsPauseEnabled = true;
                    _smtc.IsNextEnabled = true;
                    _smtc.IsPreviousEnabled = true;

                    _mediaPlayer.CommandManager.IsEnabled = true;
                    _mediaPlayer.CommandManager.PlayBehavior.EnablingRule = MediaCommandEnablingRule.Always;
                    _mediaPlayer.CommandManager.PauseBehavior.EnablingRule = MediaCommandEnablingRule.Always;
                    _mediaPlayer.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
                    _mediaPlayer.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;

                    _mediaPlayer.CommandManager.PlayReceived += (cm, e) => { e.Handled = true; PlayRequested?.Invoke(this, EventArgs.Empty); };
                    _mediaPlayer.CommandManager.PauseReceived += (cm, e) => { e.Handled = true; PauseRequested?.Invoke(this, EventArgs.Empty); };
                    _mediaPlayer.CommandManager.NextReceived += (cm, e) => { e.Handled = true; NextRequested?.Invoke(this, EventArgs.Empty); };
                    _mediaPlayer.CommandManager.PreviousReceived += (cm, e) => { e.Handled = true; PreviousRequested?.Invoke(this, EventArgs.Empty); };

                    _isReady = true;
                }
                catch (Exception ex) { Debug.WriteLine($"Audio Init Failed: {ex.Message}"); }
            });
        }

        public void Play(Track track)
        {
            if (!_isReady || track == null || _mediaPlayer == null || _smtc == null) return;
            _mediaPlayer.Pause();
            try
            {
                _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(track.FilePath, UriKind.Absolute));
                var updater = _smtc.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = track.Title;
                updater.MusicProperties.Artist = track.Artist;
                updater.Update();
                _mediaPlayer.Play();
            }
            catch (Exception ex) { Debug.WriteLine($"Play Failed: {ex.Message}"); }
        }

        public void Pause() => _mediaPlayer?.Pause();
        public void Resume() => _mediaPlayer?.Play();
        public void Stop() => _mediaPlayer?.Pause();
        public void SetVolume(int volume) { if (_mediaPlayer != null) _mediaPlayer.Volume = Math.Clamp(volume / 100.0, 0.0, 1.0); }
        public void SeekTo(float position) { if (_mediaPlayer != null && _mediaPlayer.PlaybackSession.CanSeek) _mediaPlayer.PlaybackSession.Position = TimeSpan.FromMilliseconds(Length * position); }
        public void Dispose() => _mediaPlayer?.Dispose();
    }

    public static class SettingsStore
    {
        private static readonly string SettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicPower3", "settings.json");

        public static MusicPower3.Models.AppSettings Load()
        {
            try
            {
                if (System.IO.File.Exists(SettingsPath))
                {
                    string json = System.IO.File.ReadAllText(SettingsPath);
                    return System.Text.Json.JsonSerializer.Deserialize<MusicPower3.Models.AppSettings>(json) ?? new MusicPower3.Models.AppSettings();
                }
            }
            catch { }
            return new MusicPower3.Models.AppSettings();
        }

        public static void Save(MusicPower3.Models.AppSettings settings)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
                string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }

    public static class LibraryCache
    {
        private static readonly string CachePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicPower3", "library_cache.json");

        public static System.Collections.Generic.List<MusicPower3.Models.Track> Load()
        {
            try
            {
                if (System.IO.File.Exists(CachePath))
                {
                    string json = System.IO.File.ReadAllText(CachePath);
                    return System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<MusicPower3.Models.Track>>(json) ?? new System.Collections.Generic.List<MusicPower3.Models.Track>();
                }
            }
            catch { }
            return new System.Collections.Generic.List<MusicPower3.Models.Track>();
        }

        public static void Save(System.Collections.Generic.List<MusicPower3.Models.Track> tracks)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath)!);
                string json = System.Text.Json.JsonSerializer.Serialize(tracks);
                System.IO.File.WriteAllText(CachePath, json);
            }
            catch { }
        }
    }

    public static class TrackMetadataReader
    {
        public static Track Read(string filePath)
        {
            string title = System.IO.Path.GetFileNameWithoutExtension(filePath);
            string artist = "Unknown Artist"; string album = "Unknown Album";
            TimeSpan duration = TimeSpan.Zero;
            DateTime dateAdded = File.GetCreationTime(filePath);
            DateTime dateModified = File.GetLastWriteTime(filePath);

            try
            {
                using var file = TagLib.File.Create(new LocalFileAbstraction(filePath));
                if (!string.IsNullOrWhiteSpace(file.Tag.Title)) title = file.Tag.Title;
                if (file.Tag.Performers.Length > 0) artist = string.Join(", ", file.Tag.Performers);
                if (!string.IsNullOrWhiteSpace(file.Tag.Album)) album = file.Tag.Album;
                duration = file.Properties.Duration;
            }
            catch { }

            return new Track(filePath, title, artist, album, duration, dateAdded, dateModified);
        }
    }
    public class TrackMetadataUpdate
    {
        // Nullable fields allow us to skip writing them during a Batch Edit
        public string? Title { get; set; }
        public string[]? Performers { get; set; }
        public string? Album { get; set; }
        public string[]? AlbumArtists { get; set; }
        public uint? Track { get; set; }
        public uint? Disc { get; set; }
        public uint? Year { get; set; }
        public string[]? Genres { get; set; }
        public string? Comment { get; set; }
        
        // Null = don't touch. Empty array (Length == 0) = delete artwork. 
        public byte[]? ArtworkData { get; set; }
    }

    public static class MetadataWriter
    {
        static MetadataWriter()
        {
            // Force ID3v2.3 instead of ID3v2.4. Windows Explorer and many car stereos 
            // cannot read v2.4 tags properly. This guarantees maximum compatibility.
            TagLib.Id3v2.Tag.DefaultVersion = 3;
            TagLib.Id3v2.Tag.ForceDefaultVersion = true;
        }

        public static void SaveMetadata(string filePath, TrackMetadataUpdate data, bool isBatch)
        {
            // We use the existing LocalFileAbstraction[cite: 12] which opens with FileShare.ReadWrite.
            // If the file is strictly locked by playback, TagLib# will throw an IOException here.
            // If the file headers are corrupted, it throws CorruptFileException.
            using var file = TagLib.File.Create(new MusicPower3.Models.LocalFileAbstraction(filePath));

            // Single track mode overwrites everything (even with empty strings).
            // Batch mode ONLY overwrites fields that were explicitly provided (not null).
            if (!isBatch || data.Title != null) file.Tag.Title = data.Title ?? string.Empty;
            if (!isBatch || data.Performers != null) file.Tag.Performers = data.Performers ?? Array.Empty<string>();
            if (!isBatch || data.Album != null) file.Tag.Album = data.Album ?? string.Empty;
            if (!isBatch || data.AlbumArtists != null) file.Tag.AlbumArtists = data.AlbumArtists ?? Array.Empty<string>();
            
            if (!isBatch || data.Track.HasValue) file.Tag.Track = data.Track ?? 0;
            if (!isBatch || data.Disc.HasValue) file.Tag.Disc = data.Disc ?? 0;
            if (!isBatch || data.Year.HasValue) file.Tag.Year = data.Year ?? 0;
            
            if (!isBatch || data.Genres != null) file.Tag.Genres = data.Genres ?? Array.Empty<string>();
            if (!isBatch || data.Comment != null) file.Tag.Comment = data.Comment ?? string.Empty;

            // Artwork Handling
            if (data.ArtworkData != null)
            {
                if (data.ArtworkData.Length == 0)
                {
                    // User explicitly cleared the artwork
                    file.Tag.Pictures = Array.Empty<TagLib.IPicture>();
                }
                else
                {
                    // Completely replace existing pictures to prevent accumulating duplicates
                    file.Tag.Pictures = new TagLib.IPicture[] 
                    { 
                        new TagLib.Picture(new TagLib.ByteVector(data.ArtworkData)) 
                    };
                }
            }

            file.Save();
        }
    }
}