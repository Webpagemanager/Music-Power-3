using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using MusicPower3.Models;

namespace MusicPower3.Services
{
    public class AudioEngine : IDisposable
    {
        private readonly MediaPlayer _mediaPlayer;
        private readonly SystemMediaTransportControls _smtc;
        private MediaSource? _currentSource;
        private MediaPlaybackItem? _currentItem;
        private InMemoryRandomAccessStream? _thumbStream;
        private Track? _currentTrack;

        public event EventHandler? EndReached;
        public event EventHandler? PlayRequested;
        public event EventHandler? PauseRequested;
        public event EventHandler? NextRequested;
        public event EventHandler? PreviousRequested;
        public event Action<Track, TimeSpan>? DurationUpdated;

        public long Time => (long)_mediaPlayer.PlaybackSession.Position.TotalMilliseconds;
        public long Length => (long)_mediaPlayer.PlaybackSession.NaturalDuration.TotalMilliseconds;

        public AudioEngine()
        {
            // Created synchronously: MediaPlayer is a lightweight wrapper, and initialising it on the
            // calling thread removes the old race where Play() could arrive before the engine was ready.
            _mediaPlayer = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Media };
            _mediaPlayer.MediaEnded += (s, e) => EndReached?.Invoke(this, EventArgs.Empty);
            _mediaPlayer.PlaybackSession.NaturalDurationChanged += OnNaturalDurationChanged;

            _smtc = _mediaPlayer.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;

            var cm = _mediaPlayer.CommandManager;
            cm.IsEnabled = true;
            cm.PlayBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            cm.PauseBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            cm.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            cm.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;

            cm.PlayReceived += (c, e) => { e.Handled = true; PlayRequested?.Invoke(this, EventArgs.Empty); };
            cm.PauseReceived += (c, e) => { e.Handled = true; PauseRequested?.Invoke(this, EventArgs.Empty); };
            cm.NextReceived += (c, e) => { e.Handled = true; NextRequested?.Invoke(this, EventArgs.Empty); };
            cm.PreviousReceived += (c, e) => { e.Handled = true; PreviousRequested?.Invoke(this, EventArgs.Empty); };
        }

        private void OnNaturalDurationChanged(MediaPlaybackSession sender, object args)
        {
            var dur = sender.NaturalDuration;
            if (_currentTrack != null && dur > TimeSpan.Zero && Math.Abs((_currentTrack.Duration - dur).TotalMilliseconds) > 500)
            {
                _currentTrack.Duration = dur;
                DurationUpdated?.Invoke(_currentTrack, dur);
            }
        }

        public void Play(Track track)
        {
            if (track == null) return;
            _currentTrack = track;
            try
            {
                var source = MediaSource.CreateFromUri(new Uri(track.FilePath, UriKind.Absolute));
                var item = new MediaPlaybackItem(source);

                // With the CommandManager enabled, Windows reads the title/artist/artwork shown in the
                // volume flyout, lock screen and media keys from the playback item's display properties.
                var props = item.GetDisplayProperties();
                props.Type = MediaPlaybackType.Music;
                props.MusicProperties.Title = track.Title;
                props.MusicProperties.Artist = track.Artist;
                props.MusicProperties.AlbumTitle = track.Album;
                item.ApplyDisplayProperties(props);

                try
                {
                    _smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
                    _smtc.DisplayUpdater.AppMediaId = "MusicPower3";
                    _smtc.DisplayUpdater.MusicProperties.Title = track.Title;
                    _smtc.DisplayUpdater.MusicProperties.Artist = track.Artist;
                    _smtc.DisplayUpdater.MusicProperties.AlbumTitle = track.Album;
                    _smtc.DisplayUpdater.Update();
                }
                catch { }

                var previousSource = _currentSource;
                _currentSource = source;
                _currentItem = item;

                _mediaPlayer.Source = item;
                _mediaPlayer.Play();

                previousSource?.Dispose();
                _ = ApplyThumbnailAsync(item, track.FilePath);
            }
            catch (Exception ex) { Debug.WriteLine($"Play Failed: {ex.Message}"); }
        }

        private async Task ApplyThumbnailAsync(MediaPlaybackItem item, string filePath)
        {
            try
            {
                byte[]? data = await Track.ReadArtworkBytesAsync(filePath);
                if (data == null || !ReferenceEquals(item, _currentItem)) return;

                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(data.AsBuffer());
                stream.Seek(0);

                if (!ReferenceEquals(item, _currentItem)) { stream.Dispose(); return; }

                var thumbRef = RandomAccessStreamReference.CreateFromStream(stream);
                var props = item.GetDisplayProperties();
                props.Thumbnail = thumbRef;
                item.ApplyDisplayProperties(props);

                try
                {
                    _smtc.DisplayUpdater.Thumbnail = thumbRef;
                    _smtc.DisplayUpdater.Update();
                }
                catch { }

                var old = System.Threading.Interlocked.Exchange(ref _thumbStream, stream);
                old?.Dispose();
            }
            catch (Exception ex) { Debug.WriteLine($"Thumbnail Failed: {ex.Message}"); }
        }

        public void Pause() => _mediaPlayer.Pause();
        public void Resume() => _mediaPlayer.Play();
        public void Stop() => _mediaPlayer.Pause();
        public void SetVolume(int volume) => _mediaPlayer.Volume = Math.Clamp(volume / 100.0, 0.0, 1.0);

        public void SeekTo(float position)
        {
            if (_mediaPlayer.PlaybackSession.CanSeek)
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromMilliseconds(Length * position);
        }

        public void Dispose()
        {
            _mediaPlayer.Source = null;
            _mediaPlayer.Dispose();
            _currentSource?.Dispose();
            _thumbStream?.Dispose();
        }
    }

    internal static class AtomicFile
    {
        // Write to a sibling temp file and swap it in, so a crash or power loss mid-write can never
        // leave a half-written settings/cache file behind.
        public static void WriteAllText(string path, string content)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, true);
        }
    }

    public static class SettingsStore
    {
        private static readonly string SettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicPower3", "settings.json");
        private static readonly object _lock = new();
        private static readonly JsonSerializerOptions _options = new() { WriteIndented = true };

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
                lock (_lock)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
                    AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, _options));
                }
            }
            catch { }
        }
    }

    public static class LibraryCache
    {
        private static readonly string CachePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicPower3", "library_cache.json");
        private static readonly object _lock = new();

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
                lock (_lock)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath)!);
                    AtomicFile.WriteAllText(CachePath, JsonSerializer.Serialize(tracks));
                }
            }
            catch { }
        }
    }

    public static class ShellPropertyReader
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
        private static extern void SHGetPropertyStoreFromParsingName(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            uint flags,
            [System.Runtime.InteropServices.In] ref Guid riid,
            out IPropertyStore ppv);

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown), System.Runtime.InteropServices.Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        public interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, out PropVariant pv);
            int SetValue(ref PropertyKey key, ref PropVariant pv);
            int Commit();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
        public struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
            public PropertyKey(Guid g, uint p) { fmtid = g; pid = p; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        public struct PropVariant
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public ushort vt;
            [System.Runtime.InteropServices.FieldOffset(8)] public ulong uhVal;
        }

        private static readonly Guid IID_IPropertyStore = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        private static PropertyKey PKEY_Media_Duration = new PropertyKey(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);

        public static TimeSpan GetDuration(string filePath)
        {
            IPropertyStore? store = null;
            try
            {
                Guid iid = IID_IPropertyStore;
                SHGetPropertyStoreFromParsingName(filePath, IntPtr.Zero, 0, ref iid, out store);
                if (store != null)
                {
                    store.GetValue(ref PKEY_Media_Duration, out PropVariant pv);
                    if (pv.vt == 21 && pv.uhVal > 0)
                    {
                        return TimeSpan.FromTicks((long)pv.uhVal);
                    }
                }
            }
            catch { }
            finally
            {
                if (store != null)
                {
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(store); } catch { }
                }
            }
            return TimeSpan.Zero;
        }
    }

    public static class TrackMetadataReader
    {
        public static Track Read(string filePath) => Read(new FileInfo(filePath));

        // Takes a FileInfo so directory enumeration can hand over the timestamps it already fetched,
        // which avoids extra file-system calls for every file during a library scan.
        public static Track Read(FileInfo info)
        {
            string filePath = info.FullName;
            string title = System.IO.Path.GetFileNameWithoutExtension(filePath);
            string artist = "Unknown Artist"; string album = "Unknown Album";
            TimeSpan duration = ShellPropertyReader.GetDuration(filePath);

            try
            {
                // PictureLazy: embedded artwork is not pulled into memory just to read the text tags.
                using var file = TagLib.File.Create(new LocalFileAbstraction(filePath), TagLib.ReadStyle.Average | TagLib.ReadStyle.PictureLazy);
                if (!string.IsNullOrWhiteSpace(file.Tag.Title)) title = file.Tag.Title;
                if (file.Tag.Performers.Length > 0) artist = string.Join(", ", file.Tag.Performers);
                if (!string.IsNullOrWhiteSpace(file.Tag.Album)) album = file.Tag.Album;
                if (duration <= TimeSpan.Zero) duration = file.Properties.Duration;
            }
            catch { }

            return new Track(filePath, title, artist, album, duration, info.CreationTime, info.LastWriteTime);
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
            // Tags are written in place (TagLib# only rewrites the tag region and uses padding), which keeps
            // disk writes small and works while the file is open for playback. Copying the whole audio file
            // for an "atomic" swap would rewrite tens of megabytes per edit and fail on an in-use file.
            // If the file is strictly locked, TagLib# throws IOException; corrupted headers throw CorruptFileException.
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