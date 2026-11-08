using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPower3.Metadata
{
    public enum TagEncodingProfile
    {
        Id3v23Utf16 = 0,
        Id3v23Iso88591 = 1,
        Id3v24Utf8 = 2
    }

    public sealed class AudioMetadata
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string AlbumArtist { get; set; } = string.Empty;
        public uint TrackNumber { get; set; }
        public uint TrackTotal { get; set; }
        public uint DiscNumber { get; set; }
        public uint DiscTotal { get; set; }
        public uint Year { get; set; }
        public string Genre { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
        public byte[]? ArtworkData { get; set; }
        public string? ArtworkMimeType { get; set; }
    }

    public static class MetadataEngine
    {
        public static AudioMetadata Read(string filePath)
        {
            var meta = new AudioMetadata();
            if (!File.Exists(filePath)) return meta;

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
                if (ext == ".flac")
                {
                    FlacHandler.Read(fs, meta, readArtwork: true);
                }
                else if (ext is ".m4a" or ".aac")
                {
                    Mp4Handler.Read(fs, meta, readArtwork: true);
                }
                else if (ext == ".wav")
                {
                    WavHandler.Read(fs, meta, readArtwork: true);
                }
                else if (ext is ".ogg" or ".opus")
                {
                    OggHandler.Read(fs, meta, readArtwork: true);
                }
                else
                {
                    // Default to ID3 (MP3, etc.)
                    Id3Handler.Read(fs, meta, readArtwork: true);
                    if (meta.Duration <= TimeSpan.Zero)
                    {
                        meta.Duration = Mp3DurationCalculator.Calculate(fs);
                    }
                }
            }
            catch { }

            return meta;
        }

        public static byte[]? ReadArtworkBytes(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
                var meta = new AudioMetadata();
                if (ext == ".flac") FlacHandler.Read(fs, meta, readArtwork: true);
                else if (ext is ".m4a" or ".aac") Mp4Handler.Read(fs, meta, readArtwork: true);
                else if (ext == ".wav") WavHandler.Read(fs, meta, readArtwork: true);
                else if (ext is ".ogg" or ".opus") OggHandler.Read(fs, meta, readArtwork: true);
                else Id3Handler.Read(fs, meta, readArtwork: true);
                return meta.ArtworkData;
            }
            catch { return null; }
        }

        public static void Write(string filePath, Services.TrackMetadataUpdate update, TagEncodingProfile profile = TagEncodingProfile.Id3v23Utf16)
        {
            if (!File.Exists(filePath)) return;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".flac")
            {
                FlacHandler.Write(filePath, update);
            }
            else if (ext is ".m4a" or ".aac")
            {
                Mp4Handler.Write(filePath, update);
            }
            else
            {
                // MP3 and standard audio containers with ID3
                Id3Handler.Write(filePath, update, profile);
            }
        }
    }

    #region ID3v2 & ID3v1 Implementation

    internal static class Id3Handler
    {
        private static readonly char[] Win1252Table = new char[32]
        {
            '\u20AC', '\u003F', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
            '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u003F', '\u017D', '\u003F',
            '\u003F', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
            '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u003F', '\u017E', '\u0178'
        };

        public static void Read(Stream stream, AudioMetadata meta, bool readArtwork)
        {
            stream.Position = 0;
            byte[] header = new byte[10];
            if (stream.Read(header, 0, 10) < 10) return;

            if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
            {
                byte majorVersion = header[3]; // 2 = v2.2, 3 = v2.3, 4 = v2.4
                byte flags = header[5];
                int tagSize = DecodeSynchsafe(header, 6);

                if (majorVersion == 2)
                {
                    ReadId3v22(stream, tagSize, meta, readArtwork);
                }
                else if (majorVersion is 3 or 4)
                {
                    // If tag has unsynchronization flag set (bit 7 = 0x80), de-unsynchronize payload
                    if ((flags & 0x80) != 0 && tagSize > 0 && stream.Position + tagSize <= stream.Length)
                    {
                        byte[] rawPayload = new byte[tagSize];
                        int read = stream.Read(rawPayload, 0, tagSize);
                        byte[] cleanPayload = DeUnsynchronize(rawPayload.AsSpan(0, read));
                        using var memStream = new MemoryStream(cleanPayload);
                        ReadId3v23Or24(memStream, cleanPayload.Length, majorVersion, (byte)(flags & ~0x80), meta, readArtwork);
                    }
                    else
                    {
                        ReadId3v23Or24(stream, tagSize, majorVersion, flags, meta, readArtwork);
                    }
                }
            }
            else
            {
                ReadId3v1(stream, meta);
            }
        }

        private static byte[] DeUnsynchronize(ReadOnlySpan<byte> input)
        {
            int count = 0;
            for (int i = 0; i < input.Length - 1; i++)
            {
                if (input[i] == 0xFF && input[i + 1] == 0x00)
                {
                    count++;
                    i++;
                }
            }
            if (count == 0) return input.ToArray();

            byte[] result = new byte[input.Length - count];
            int dst = 0;
            for (int src = 0; src < input.Length; src++)
            {
                result[dst++] = input[src];
                if (input[src] == 0xFF && src + 1 < input.Length && input[src + 1] == 0x00)
                {
                    src++; // Skip the zero byte inserted by unsynchronization
                }
            }
            return result;
        }

        private static void ReadId3v23Or24(Stream stream, int tagSize, byte majorVersion, byte flags, AudioMetadata meta, bool readArtwork)
        {
            long tagEnd = Math.Min(stream.Length, stream.Position + (long)tagSize);
            // Handle extended header if present (bit 6 = 0x40)
            if ((flags & 0x40) != 0 && stream.Position < tagEnd)
            {
                byte[] extHeaderBuf = new byte[4];
                if (stream.Read(extHeaderBuf, 0, 4) == 4)
                {
                    int extSize = majorVersion == 4 ? DecodeSynchsafe(extHeaderBuf, 0) : BinaryPrimitives.ReadInt32BigEndian(extHeaderBuf);
                    if (extSize > 4 && stream.Position + extSize - 4 <= tagEnd)
                    {
                        stream.Seek(extSize - (majorVersion == 4 ? 4 : 0), SeekOrigin.Current);
                    }
                }
            }

            byte[] frameHeader = new byte[10];
            while (stream.Position + 10 <= tagEnd)
            {
                if (stream.Read(frameHeader, 0, 10) < 10) break;
                if (frameHeader[0] == 0 && frameHeader[1] == 0 && frameHeader[2] == 0 && frameHeader[3] == 0)
                {
                    // Hit padding
                    break;
                }

                // Check for invalid frame ID characters (must be A-Z, 0-9)
                if (!IsValidFrameId(frameHeader.AsSpan(0, 4))) break;

                string frameId = Encoding.ASCII.GetString(frameHeader, 0, 4);
                int frameSize;
                if (majorVersion == 4)
                {
                    frameSize = DecodeSynchsafe(frameHeader, 4);
                    // Fallback: check if frame was written with standard 32-bit int instead of synchsafe
                    if (frameSize <= 0 || stream.Position + frameSize > tagEnd)
                    {
                        int altSize = BinaryPrimitives.ReadInt32BigEndian(frameHeader.AsSpan(4, 4));
                        if (altSize > 0 && stream.Position + altSize <= tagEnd) frameSize = altSize;
                    }
                }
                else
                {
                    frameSize = BinaryPrimitives.ReadInt32BigEndian(frameHeader.AsSpan(4, 4));
                }

                if (frameSize <= 0 || stream.Position + frameSize > stream.Length) break;

                byte[] frameData = new byte[frameSize];
                int read = stream.Read(frameData, 0, frameSize);
                if (read < frameSize) break;

                // Frame-level unsynchronization (ID3v2.4 frame flag 1 bit 1 = 0x0002)
                if (majorVersion == 4 && (frameHeader[9] & 0x02) != 0)
                {
                    frameData = DeUnsynchronize(frameData);
                }

                ProcessId3v23Frame(frameId, frameData, meta, readArtwork);
            }
        }

        private static bool IsValidFrameId(ReadOnlySpan<byte> span)
        {
            for (int i = 0; i < 4; i++)
            {
                byte b = span[i];
                if (!((b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'0' && b <= (byte)'9'))) return false;
            }
            return true;
        }

        private static void ProcessId3v23Frame(string id, byte[] data, AudioMetadata meta, bool readArtwork)
        {
            if (data.Length == 0) return;

            switch (id)
            {
                case "TIT2": meta.Title = DecodeText(data); break;
                case "TPE1": meta.Artist = DecodeText(data); break;
                case "TALB": meta.Album = DecodeText(data); break;
                case "TPE2": meta.AlbumArtist = DecodeText(data); break;
                case "TCON": meta.Genre = DecodeText(data); break;
                case "TRCK":
                    var (track, total) = ParseNumberAndTotal(DecodeText(data));
                    meta.TrackNumber = track;
                    meta.TrackTotal = total;
                    break;
                case "TPOS":
                    var (disc, dTotal) = ParseNumberAndTotal(DecodeText(data));
                    meta.DiscNumber = disc;
                    meta.DiscTotal = dTotal;
                    break;
                case "TYER":
                case "TDRC":
                    string yearStr = DecodeText(data);
                    if (yearStr.Length >= 4 && uint.TryParse(yearStr.Substring(0, 4), out var yr)) meta.Year = yr;
                    break;
                case "COMM":
                    meta.Comment = DecodeComment(data);
                    break;
                case "APIC" when readArtwork && meta.ArtworkData == null:
                    DecodeApic(data, meta);
                    break;
            }
        }

        private static void ReadId3v22(Stream stream, int tagSize, AudioMetadata meta, bool readArtwork)
        {
            long tagEnd = Math.Min(stream.Length, 10 + (long)tagSize);
            byte[] frameHeader = new byte[6];

            while (stream.Position + 6 <= tagEnd)
            {
                if (stream.Read(frameHeader, 0, 6) < 6) break;
                if (frameHeader[0] == 0 && frameHeader[1] == 0 && frameHeader[2] == 0) break;

                string frameId = Encoding.ASCII.GetString(frameHeader, 0, 3);
                int frameSize = (frameHeader[3] << 16) | (frameHeader[4] << 8) | frameHeader[5];
                if (frameSize <= 0 || stream.Position + frameSize > stream.Length) break;

                byte[] frameData = new byte[frameSize];
                if (stream.Read(frameData, 0, frameSize) < frameSize) break;

                switch (frameId)
                {
                    case "TT2": meta.Title = DecodeText(frameData); break;
                    case "TP1": meta.Artist = DecodeText(frameData); break;
                    case "TAL": meta.Album = DecodeText(frameData); break;
                    case "TP2": meta.AlbumArtist = DecodeText(frameData); break;
                    case "TCO": meta.Genre = DecodeText(frameData); break;
                    case "TRK":
                        var (tr, trTot) = ParseNumberAndTotal(DecodeText(frameData));
                        meta.TrackNumber = tr; meta.TrackTotal = trTot;
                        break;
                    case "TYE":
                        string ys = DecodeText(frameData);
                        if (ys.Length >= 4 && uint.TryParse(ys.Substring(0, 4), out var y)) meta.Year = y;
                        break;
                    case "PIC" when readArtwork && meta.ArtworkData == null:
                        DecodeId3v22Pic(frameData, meta);
                        break;
                }
            }
        }

        private static void ReadId3v1(Stream stream, AudioMetadata meta)
        {
            if (stream.Length < 128) return;
            stream.Seek(-128, SeekOrigin.End);
            byte[] buf = new byte[128];
            if (stream.Read(buf, 0, 128) < 128) return;

            if (buf[0] == 'T' && buf[1] == 'A' && buf[2] == 'G')
            {
                if (string.IsNullOrWhiteSpace(meta.Title)) meta.Title = DecodeWindows1252(buf, 3, 30).Trim('\0', ' ');
                if (string.IsNullOrWhiteSpace(meta.Artist)) meta.Artist = DecodeWindows1252(buf, 33, 30).Trim('\0', ' ');
                if (string.IsNullOrWhiteSpace(meta.Album)) meta.Album = DecodeWindows1252(buf, 63, 30).Trim('\0', ' ');
                if (meta.Year == 0 && uint.TryParse(Encoding.ASCII.GetString(buf, 93, 4).Trim('\0', ' '), out var yr)) meta.Year = yr;
                if (string.IsNullOrWhiteSpace(meta.Comment)) meta.Comment = DecodeWindows1252(buf, 97, 28).Trim('\0', ' ');
                if (meta.TrackNumber == 0 && buf[125] == 0 && buf[126] > 0) meta.TrackNumber = buf[126];
            }
        }

        private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
        {
            bool hasMultiByte = false;
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b <= 0x7F) continue;
                if ((b & 0xE0) == 0xC0)
                {
                    if (i + 1 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80) return false;
                    hasMultiByte = true;
                    i += 1;
                }
                else if ((b & 0xF0) == 0xE0)
                {
                    if (i + 2 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80) return false;
                    hasMultiByte = true;
                    i += 2;
                }
                else if ((b & 0xF8) == 0xF0)
                {
                    if (i + 3 >= bytes.Length || (bytes[i + 1] & 0xC0) != 0x80 || (bytes[i + 2] & 0xC0) != 0x80 || (bytes[i + 3] & 0xC0) != 0x80) return false;
                    hasMultiByte = true;
                    i += 3;
                }
                else return false;
            }
            return hasMultiByte;
        }

        private static string DecodeWindows1252(byte[] data, int offset, int length)
        {
            if (length <= 0) return string.Empty;
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                byte b = data[offset + i];
                if (b >= 0x80 && b <= 0x9F) chars[i] = Win1252Table[b - 0x80];
                else chars[i] = (char)b;
            }
            return new string(chars);
        }

        private static string DecodeText(byte[] data)
        {
            if (data.Length <= 1) return string.Empty;
            byte encoding = data[0];
            int offset = 1;
            int length = data.Length - 1;

            string result;
            if (encoding == 1) // UTF-16 with BOM
            {
                if (length >= 2 && data[offset] == 0xFE && data[offset + 1] == 0xFF)
                    result = Encoding.BigEndianUnicode.GetString(data, offset + 2, length - 2);
                else if (length >= 2 && data[offset] == 0xFF && data[offset + 1] == 0xFE)
                    result = Encoding.Unicode.GetString(data, offset + 2, length - 2);
                else
                    result = Encoding.Unicode.GetString(data, offset, length);
            }
            else if (encoding == 2) // UTF-16BE without BOM
            {
                result = Encoding.BigEndianUnicode.GetString(data, offset, length);
            }
            else if (encoding == 3) // UTF-8
            {
                result = Encoding.UTF8.GetString(data, offset, length);
            }
            else // Encoding 0: ISO-8859-1 or mislabeled UTF-8 / Windows-1252
            {
                ReadOnlySpan<byte> span = data.AsSpan(offset, length);
                if (IsValidUtf8(span))
                    result = Encoding.UTF8.GetString(data, offset, length);
                else
                    result = DecodeWindows1252(data, offset, length);
            }

            result = result.Trim('\uFEFF', '\0', ' ');
            if (result.Contains('\0'))
            {
                result = string.Join(", ", result.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            return result;
        }

        private static string DecodeComment(byte[] data)
        {
            if (data.Length < 5) return string.Empty;
            byte encoding = data[0];
            int idx = 4; // Skip encoding (1) + language (3)

            // Find end of description (null-terminated)
            if (encoding is 1 or 2)
            {
                while (idx + 1 < data.Length && !(data[idx] == 0 && data[idx + 1] == 0)) idx += 2;
                idx += 2;
            }
            else
            {
                while (idx < data.Length && data[idx] != 0) idx++;
                idx++;
            }

            if (idx >= data.Length) return string.Empty;
            int length = data.Length - idx;
            byte[] textPayload = new byte[length + 1];
            textPayload[0] = encoding;
            Buffer.BlockCopy(data, idx, textPayload, 1, length);
            return DecodeText(textPayload);
        }

        private static void DecodeApic(byte[] data, AudioMetadata meta)
        {
            if (data.Length < 10) return;
            byte encoding = data[0];
            int idx = 1;

            // Read MIME type (Latin-1 / ASCII, null-terminated)
            int mimeStart = idx;
            while (idx < data.Length && data[idx] != 0) idx++;
            string mime = Encoding.ASCII.GetString(data, mimeStart, idx - mimeStart);
            idx++; // skip null

            if (idx >= data.Length) return;
            byte picType = data[idx++]; // picture type (3 = Cover Front)

            // Skip description
            if (encoding is 1 or 2)
            {
                while (idx + 1 < data.Length && !(data[idx] == 0 && data[idx + 1] == 0)) idx += 2;
                idx += 2;
            }
            else
            {
                while (idx < data.Length && data[idx] != 0) idx++;
                idx++;
            }

            if (idx >= data.Length) return;

            int picBytesLength = data.Length - idx;
            byte[] imgBytes = new byte[picBytesLength];
            Buffer.BlockCopy(data, idx, imgBytes, 0, picBytesLength);
            meta.ArtworkData = imgBytes;
            meta.ArtworkMimeType = string.IsNullOrWhiteSpace(mime) ? "image/jpeg" : mime;
        }

        private static void DecodeId3v22Pic(byte[] data, AudioMetadata meta)
        {
            if (data.Length < 6) return;
            byte encoding = data[0];
            string format = Encoding.ASCII.GetString(data, 1, 3).ToUpperInvariant();
            byte picType = data[4];
            int idx = 5;

            if (encoding is 1 or 2)
            {
                while (idx + 1 < data.Length && !(data[idx] == 0 && data[idx + 1] == 0)) idx += 2;
                idx += 2;
            }
            else
            {
                while (idx < data.Length && data[idx] != 0) idx++;
                idx++;
            }

            if (idx >= data.Length) return;
            int len = data.Length - idx;
            byte[] pic = new byte[len];
            Buffer.BlockCopy(data, idx, pic, 0, len);
            meta.ArtworkData = pic;
            meta.ArtworkMimeType = format == "PNG" ? "image/png" : "image/jpeg";
        }

        private static (uint num, uint total) ParseNumberAndTotal(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (0, 0);
            int slash = s.IndexOf('/');
            if (slash >= 0)
            {
                uint.TryParse(s.Substring(0, slash).Trim(), out var n);
                uint.TryParse(s.Substring(slash + 1).Trim(), out var tot);
                return (n, tot);
            }
            uint.TryParse(s.Trim(), out var single);
            return (single, 0);
        }

        public static void Write(string filePath, Services.TrackMetadataUpdate update, TagEncodingProfile profile)
        {
            // First read existing metadata to merge fields not touched by the update
            var existing = new AudioMetadata();
            long audioDataStartOffset = 0;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] hdr = new byte[10];
                if (fs.Read(hdr, 0, 10) == 10 && hdr[0] == 'I' && hdr[1] == 'D' && hdr[2] == '3')
                {
                    int existingTagSize = DecodeSynchsafe(hdr, 6);
                    audioDataStartOffset = 10 + existingTagSize;
                    if ((hdr[5] & 0x10) != 0) audioDataStartOffset += 10; // Footer present
                    fs.Position = 0;
                    Read(fs, existing, readArtwork: true);
                }
                else
                {
                    fs.Position = 0;
                    Read(fs, existing, readArtwork: true);
                }
            }

            // Apply updates
            string title = update.Title ?? existing.Title;
            string artist = update.Performers != null ? string.Join(", ", update.Performers) : existing.Artist;
            string album = update.Album ?? existing.Album;
            string albumArtist = update.AlbumArtists != null ? string.Join(", ", update.AlbumArtists) : existing.AlbumArtist;
            uint track = update.Track ?? existing.TrackNumber;
            uint disc = update.Disc ?? existing.DiscNumber;
            uint year = update.Year ?? existing.Year;
            string genre = update.Genres != null ? string.Join(", ", update.Genres) : existing.Genre;
            string comment = update.Comment ?? existing.Comment;

            byte[]? artwork = update.ArtworkData != null 
                ? (update.ArtworkData.Length == 0 ? null : update.ArtworkData) 
                : existing.ArtworkData;

            // Build ID3v2 frames
            byte textEncodingByte = profile switch
            {
                TagEncodingProfile.Id3v23Iso88591 => 0,
                TagEncodingProfile.Id3v24Utf8 => 3,
                _ => 1 // UTF-16 with BOM
            };

            var frames = new List<byte[]>();
            if (!string.IsNullOrEmpty(title)) frames.Add(BuildTextFrame("TIT2", title, textEncodingByte, profile));
            if (!string.IsNullOrEmpty(artist)) frames.Add(BuildTextFrame("TPE1", artist, textEncodingByte, profile));
            if (!string.IsNullOrEmpty(album)) frames.Add(BuildTextFrame("TALB", album, textEncodingByte, profile));
            if (!string.IsNullOrEmpty(albumArtist)) frames.Add(BuildTextFrame("TPE2", albumArtist, textEncodingByte, profile));
            if (track > 0) frames.Add(BuildTextFrame("TRCK", track.ToString(), textEncodingByte, profile));
            if (disc > 0) frames.Add(BuildTextFrame("TPOS", disc.ToString(), textEncodingByte, profile));
            if (year > 0) frames.Add(BuildTextFrame(profile == TagEncodingProfile.Id3v24Utf8 ? "TDRC" : "TYER", year.ToString(), textEncodingByte, profile));
            if (!string.IsNullOrEmpty(genre)) frames.Add(BuildTextFrame("TCON", genre, textEncodingByte, profile));
            if (!string.IsNullOrEmpty(comment)) frames.Add(BuildCommentFrame(comment, textEncodingByte, profile));
            if (artwork != null && artwork.Length > 0) frames.Add(BuildApicFrame(artwork, profile));

            int totalFramesSize = frames.Sum(f => f.Length);
            int desiredPadding = 2048; // 2 KB of padding for fast subsequent in-place edits
            int newTagSize = totalFramesSize + desiredPadding;

            // Check if existing tag can be overwritten in-place
            if (audioDataStartOffset > 10 && newTagSize <= audioDataStartOffset - 10)
            {
                // Can overwrite in-place!
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.Read);
                WriteTagToStream(fs, frames, (int)(audioDataStartOffset - 10), profile);
                return;
            }

            // Otherwise, write to a temp file and replace atomically
            string tempFile = filePath + ".mp3tmp";
            using (var outFs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
            {
                WriteTagToStream(outFs, frames, newTagSize, profile);

                // Copy audio data from original file
                using var inFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024);
                inFs.Position = audioDataStartOffset;
                inFs.CopyTo(outFs);

                // Also write ID3v1 tag at the end of the stream for universal compatibility
                WriteId3v1Tag(outFs, title, artist, album, year, comment, track);
            }

            File.Move(tempFile, filePath, overwrite: true);
        }

        private static void WriteId3v1Tag(Stream s, string title, string artist, string album, uint year, string comment, uint track)
        {
            byte[] v1 = new byte[128];
            v1[0] = (byte)'T'; v1[1] = (byte)'A'; v1[2] = (byte)'G';
            WriteFixedString(v1, 3, 30, title);
            WriteFixedString(v1, 33, 30, artist);
            WriteFixedString(v1, 63, 30, album);
            if (year > 0) WriteFixedString(v1, 93, 4, year.ToString());
            WriteFixedString(v1, 97, 28, comment);
            v1[125] = 0; // ID3v1.1 marker
            v1[126] = (byte)(track & 0xFF);
            v1[127] = 255; // Unknown genre byte
            s.Write(v1, 0, 128);
        }

        private static void WriteFixedString(byte[] buf, int offset, int maxLen, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            int copyLen = Math.Min(bytes.Length, maxLen);
            Buffer.BlockCopy(bytes, 0, buf, offset, copyLen);
        }

        private static void WriteTagToStream(Stream s, List<byte[]> frames, int tagPayloadSize, TagEncodingProfile profile)
        {
            byte majorVer = profile == TagEncodingProfile.Id3v24Utf8 ? (byte)4 : (byte)3;
            byte[] header = new byte[10];
            header[0] = (byte)'I'; header[1] = (byte)'D'; header[2] = (byte)'3';
            header[3] = majorVer; header[4] = 0; // version 2.3 or 2.4, revision 0
            header[5] = 0; // flags
            EncodeSynchsafe(tagPayloadSize, header, 6);
            s.Write(header, 0, 10);

            int written = 0;
            foreach (var frame in frames)
            {
                s.Write(frame, 0, frame.Length);
                written += frame.Length;
            }

            // Fill remaining tag payload with 0x00 padding
            int padding = Math.Max(0, tagPayloadSize - written);
            if (padding > 0)
            {
                byte[] padBuf = new byte[Math.Min(padding, 4096)];
                while (padding > 0)
                {
                    int chunk = Math.Min(padding, padBuf.Length);
                    s.Write(padBuf, 0, chunk);
                    padding -= chunk;
                }
            }
        }

        private static byte[] BuildTextFrame(string id, string text, byte encodingByte, TagEncodingProfile profile)
        {
            byte[] encodedText = EncodeString(text, encodingByte);
            int frameDataLen = 1 + encodedText.Length;
            byte[] frame = new byte[10 + frameDataLen];

            Encoding.ASCII.GetBytes(id, 0, 4, frame, 0);
            if (profile == TagEncodingProfile.Id3v24Utf8) EncodeSynchsafe(frameDataLen, frame, 4);
            else BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), frameDataLen);

            frame[8] = 0; frame[9] = 0; // flags
            frame[10] = encodingByte;
            Buffer.BlockCopy(encodedText, 0, frame, 11, encodedText.Length);
            return frame;
        }

        private static byte[] BuildCommentFrame(string comment, byte encodingByte, TagEncodingProfile profile)
        {
            byte[] encodedText = EncodeString(comment, encodingByte);
            // 1 (encoding) + 3 (lang "eng") + (1 or 2 null for empty desc) + comment bytes
            int descNullLen = encodingByte == 1 ? 2 : 1;
            int frameDataLen = 1 + 3 + descNullLen + encodedText.Length;
            byte[] frame = new byte[10 + frameDataLen];

            Encoding.ASCII.GetBytes("COMM", 0, 4, frame, 0);
            if (profile == TagEncodingProfile.Id3v24Utf8) EncodeSynchsafe(frameDataLen, frame, 4);
            else BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), frameDataLen);

            frame[8] = 0; frame[9] = 0;
            frame[10] = encodingByte;
            Encoding.ASCII.GetBytes("eng", 0, 3, frame, 11);
            int idx = 14;
            for (int i = 0; i < descNullLen; i++) frame[idx++] = 0;
            Buffer.BlockCopy(encodedText, 0, frame, idx, encodedText.Length);
            return frame;
        }

        private static byte[] BuildApicFrame(byte[] imgData, TagEncodingProfile profile)
        {
            string mime = (imgData.Length > 8 && imgData[0] == 0x89 && imgData[1] == 0x50 && imgData[2] == 0x4E && imgData[3] == 0x47)
                ? "image/png"
                : "image/jpeg";

            byte[] mimeBytes = Encoding.ASCII.GetBytes(mime);
            int frameDataLen = 1 + mimeBytes.Length + 1 + 1 + 1 + imgData.Length; // enc(1), mime, null(1), type(1), desc_null(1), data
            byte[] frame = new byte[10 + frameDataLen];

            Encoding.ASCII.GetBytes("APIC", 0, 4, frame, 0);
            if (profile == TagEncodingProfile.Id3v24Utf8) EncodeSynchsafe(frameDataLen, frame, 4);
            else BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), frameDataLen);

            frame[8] = 0; frame[9] = 0;

            int idx = 10;
            frame[idx++] = 0; // ISO-8859-1 for MIME and description
            Buffer.BlockCopy(mimeBytes, 0, frame, idx, mimeBytes.Length);
            idx += mimeBytes.Length;
            frame[idx++] = 0; // null terminate MIME
            frame[idx++] = 3; // Front cover
            frame[idx++] = 0; // empty description null
            Buffer.BlockCopy(imgData, 0, frame, idx, imgData.Length);
            return frame;
        }

        private static byte[] EncodeString(string s, byte encodingByte)
        {
            s = s.Trim('\uFEFF');
            return encodingByte switch
            {
                1 => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(s)).ToArray(),
                3 => Encoding.UTF8.GetBytes(s),
                _ => Encoding.Latin1.GetBytes(s)
            };
        }

        private static int DecodeSynchsafe(byte[] buf, int offset)
        {
            return ((buf[offset] & 0x7F) << 21) |
                   ((buf[offset + 1] & 0x7F) << 14) |
                   ((buf[offset + 2] & 0x7F) << 7) |
                   (buf[offset + 3] & 0x7F);
        }

        private static void EncodeSynchsafe(int value, byte[] buf, int offset)
        {
            buf[offset] = (byte)((value >> 21) & 0x7F);
            buf[offset + 1] = (byte)((value >> 14) & 0x7F);
            buf[offset + 2] = (byte)((value >> 7) & 0x7F);
            buf[offset + 3] = (byte)(value & 0x7F);
        }
    }

    #endregion

    #region MP3 VBR & Frame Duration Calculator

    internal static class Mp3DurationCalculator
    {
        public static TimeSpan Calculate(Stream stream)
        {
            try
            {
                long length = stream.Length;
                stream.Position = 0;

                // Skip ID3v2 if present
                byte[] hdr = new byte[10];
                if (stream.Read(hdr, 0, 10) == 10 && hdr[0] == 'I' && hdr[1] == 'D' && hdr[2] == '3')
                {
                    int id3Size = ((hdr[6] & 0x7F) << 21) | ((hdr[7] & 0x7F) << 14) | ((hdr[8] & 0x7F) << 7) | (hdr[9] & 0x7F);
                    stream.Position = 10 + id3Size;
                }
                else
                {
                    stream.Position = 0;
                }

                byte[] buf = new byte[8192];
                int read = stream.Read(buf, 0, buf.Length);
                if (read < 32) return TimeSpan.Zero;

                for (int i = 0; i < read - 32; i++)
                {
                    if (buf[i] == 0xFF && (buf[i + 1] & 0xE0) == 0xE0)
                    {
                        // Potential MPEG audio frame
                        byte ver = (byte)((buf[i + 1] >> 3) & 0x03);
                        byte layer = (byte)((buf[i + 1] >> 1) & 0x03);
                        if (ver == 1 || layer == 0) continue; // reserved

                        int sampleRateIdx = (buf[i + 2] >> 2) & 0x03;
                        int[] rates = ver == 3 ? new[] { 44100, 48000, 32000 } : new[] { 22050, 24000, 16000 };
                        if (sampleRateIdx >= 3) continue;
                        int sampleRate = rates[sampleRateIdx];

                        // Xing header can appear at offset +36 (MPEG-1 stereo) or +21 (MPEG-1 mono)
                        int[] offsets = { i + 36, i + 21, i + 13 };
                        foreach (int off in offsets)
                        {
                            if (off + 16 <= read)
                            {
                                string tag = Encoding.ASCII.GetString(buf, off, 4);
                                if (tag is "Xing" or "Info")
                                {
                                    int flags = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(off + 4, 4));
                                    if ((flags & 0x01) != 0) // Frames flag present
                                    {
                                        int frames = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(off + 8, 4));
                                        int samplesPerFrame = layer == 3 ? 384 : 1152; // Layer I = 384, Layer II/III = 1152
                                        double secs = (double)frames * samplesPerFrame / sampleRate;
                                        if (secs > 0) return TimeSpan.FromSeconds(secs);
                                    }
                                }
                                else if (tag == "VBRI" && off + 26 <= read)
                                {
                                    int frames = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(off + 14, 4));
                                    double secs = (double)frames * 1152 / sampleRate;
                                    if (secs > 0) return TimeSpan.FromSeconds(secs);
                                }
                            }
                        }
                        break;
                    }
                }
            }
            catch { }
            return TimeSpan.Zero;
        }
    }

    #endregion

    #region FLAC Implementation

    internal static class FlacHandler
    {
        public static void Read(Stream stream, AudioMetadata meta, bool readArtwork)
        {
            stream.Position = 0;
            byte[] magic = new byte[4];
            if (stream.Read(magic, 0, 4) < 4 || magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C') return;

            bool isLast = false;
            byte[] blockHdr = new byte[4];

            while (!isLast && stream.Read(blockHdr, 0, 4) == 4)
            {
                isLast = (blockHdr[0] & 0x80) != 0;
                int blockType = blockHdr[0] & 0x7F;
                int blockLength = (blockHdr[1] << 16) | (blockHdr[2] << 8) | blockHdr[3];

                if (blockLength < 0 || stream.Position + blockLength > stream.Length) break;

                long blockEnd = stream.Position + blockLength;

                if (blockType == 0 && blockLength >= 34) // STREAMINFO
                {
                    byte[] si = new byte[34];
                    if (stream.Read(si, 0, 34) == 34)
                    {
                        int sampleRate = ((si[10] << 12) | (si[11] << 4) | (si[12] >> 4)) & 0xFFFFF;
                        long totalSamples = (((long)(si[13] & 0x0F)) << 32) |
                                            (((long)si[14]) << 24) |
                                            (((long)si[15]) << 16) |
                                            (((long)si[16]) << 8) |
                                            si[17];
                        if (sampleRate > 0 && totalSamples > 0)
                        {
                            meta.Duration = TimeSpan.FromSeconds((double)totalSamples / sampleRate);
                        }
                    }
                }
                else if (blockType == 4) // VORBIS_COMMENT
                {
                    ReadVorbisComment(stream, blockLength, meta);
                }
                else if (blockType == 6 && readArtwork && meta.ArtworkData == null) // PICTURE
                {
                    ReadFlacPicture(stream, blockLength, meta);
                }

                stream.Position = blockEnd;
            }
        }

        private static void ReadVorbisComment(Stream s, int length, AudioMetadata meta)
        {
            byte[] lenBuf = new byte[4];
            if (s.Read(lenBuf, 0, 4) < 4) return;
            int vendorLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (vendorLen > 0 && s.Position + vendorLen <= s.Length) s.Seek(vendorLen, SeekOrigin.Current);

            if (s.Read(lenBuf, 0, 4) < 4) return;
            int count = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

            for (int i = 0; i < count; i++)
            {
                if (s.Read(lenBuf, 0, 4) < 4) break;
                int commentLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (commentLen <= 0 || s.Position + commentLen > s.Length) break;

                byte[] cBuf = new byte[commentLen];
                if (s.Read(cBuf, 0, commentLen) < commentLen) break;
                string comment = Encoding.UTF8.GetString(cBuf);

                int eq = comment.IndexOf('=');
                if (eq > 0)
                {
                    string key = comment.Substring(0, eq).Trim().ToUpperInvariant();
                    string val = comment.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "TITLE": meta.Title = val; break;
                        case "ARTIST": meta.Artist = val; break;
                        case "ALBUM": meta.Album = val; break;
                        case "ALBUMARTIST": meta.AlbumArtist = val; break;
                        case "GENRE": meta.Genre = val; break;
                        case "COMMENT": meta.Comment = val; break;
                        case "DATE" when meta.Year == 0 && val.Length >= 4 && uint.TryParse(val.Substring(0, 4), out var yr):
                            meta.Year = yr;
                            break;
                        case "TRACKNUMBER" when uint.TryParse(val, out var trk):
                            meta.TrackNumber = trk;
                            break;
                        case "DISCNUMBER" when uint.TryParse(val, out var dsc):
                            meta.DiscNumber = dsc;
                            break;
                    }
                }
            }
        }

        private static void ReadFlacPicture(Stream s, int length, AudioMetadata meta)
        {
            byte[] buf4 = new byte[4];
            if (s.Read(buf4, 0, 4) < 4) return;
            int picType = BinaryPrimitives.ReadInt32BigEndian(buf4);

            if (s.Read(buf4, 0, 4) < 4) return;
            int mimeLen = BinaryPrimitives.ReadInt32BigEndian(buf4);
            if (mimeLen < 0 || s.Position + mimeLen > s.Length) return;
            byte[] mimeBytes = new byte[mimeLen];
            s.ReadExactly(mimeBytes, 0, mimeLen);
            string mime = Encoding.ASCII.GetString(mimeBytes);

            if (s.Read(buf4, 0, 4) < 4) return;
            int descLen = BinaryPrimitives.ReadInt32BigEndian(buf4);
            if (descLen > 0 && s.Position + descLen <= s.Length) s.Seek(descLen, SeekOrigin.Current);

            // Skip 4x4 width, height, depth, colors
            s.Seek(16, SeekOrigin.Current);

            if (s.Read(buf4, 0, 4) < 4) return;
            int dataLen = BinaryPrimitives.ReadInt32BigEndian(buf4);
            if (dataLen <= 0 || s.Position + dataLen > s.Length) return;

            byte[] picData = new byte[dataLen];
            s.ReadExactly(picData, 0, dataLen);
            meta.ArtworkData = picData;
            meta.ArtworkMimeType = mime;
        }

        public static void Write(string filePath, Services.TrackMetadataUpdate update)
        {
            var existing = new AudioMetadata();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Read(fs, existing, readArtwork: true);
            }

            string title = update.Title ?? existing.Title;
            string artist = update.Performers != null ? string.Join(", ", update.Performers) : existing.Artist;
            string album = update.Album ?? existing.Album;
            string albumArtist = update.AlbumArtists != null ? string.Join(", ", update.AlbumArtists) : existing.AlbumArtist;
            uint track = update.Track ?? existing.TrackNumber;
            uint disc = update.Disc ?? existing.DiscNumber;
            uint year = update.Year ?? existing.Year;
            string genre = update.Genres != null ? string.Join(", ", update.Genres) : existing.Genre;
            string comment = update.Comment ?? existing.Comment;

            byte[]? artwork = update.ArtworkData != null 
                ? (update.ArtworkData.Length == 0 ? null : update.ArtworkData) 
                : existing.ArtworkData;

            // Build new Vorbis Comment block
            var comments = new List<string>();
            if (!string.IsNullOrEmpty(title)) comments.Add($"TITLE={title}");
            if (!string.IsNullOrEmpty(artist)) comments.Add($"ARTIST={artist}");
            if (!string.IsNullOrEmpty(album)) comments.Add($"ALBUM={album}");
            if (!string.IsNullOrEmpty(albumArtist)) comments.Add($"ALBUMARTIST={albumArtist}");
            if (track > 0) comments.Add($"TRACKNUMBER={track}");
            if (disc > 0) comments.Add($"DISCNUMBER={disc}");
            if (year > 0) comments.Add($"DATE={year}");
            if (!string.IsNullOrEmpty(genre)) comments.Add($"GENRE={genre}");
            if (!string.IsNullOrEmpty(comment)) comments.Add($"COMMENT={comment}");

            byte[] vorbisBlock = BuildVorbisBlock(comments);
            byte[]? pictureBlock = artwork != null ? BuildPictureBlock(artwork) : null;

            // Rewrite FLAC file with new blocks
            string tempFile = filePath + ".flactmp";
            using (var outFs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
            using (var inFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024))
            {
                byte[] magic = new byte[4];
                inFs.ReadExactly(magic, 0, 4);
                outFs.Write(magic, 0, 4);

                bool isLast = false;
                byte[] blockHdr = new byte[4];

                while (!isLast && inFs.Read(blockHdr, 0, 4) == 4)
                {
                    isLast = (blockHdr[0] & 0x80) != 0;
                    int blockType = blockHdr[0] & 0x7F;
                    int blockLength = (blockHdr[1] << 16) | (blockHdr[2] << 8) | blockHdr[3];

                    if (blockType == 0) // STREAMINFO - preserve as not last
                    {
                        byte[] si = new byte[blockLength];
                        inFs.ReadExactly(si, 0, blockLength);
                        blockHdr[0] = 0; // not last
                        outFs.Write(blockHdr, 0, 4);
                        outFs.Write(si, 0, blockLength);
                    }
                    else if (blockType is 4 or 6)
                    {
                        // Skip old comments or pictures
                        inFs.Seek(blockLength, SeekOrigin.Current);
                    }
                    else
                    {
                        // Copy other blocks as not last
                        byte[] other = new byte[blockLength];
                        inFs.ReadExactly(other, 0, blockLength);
                        blockHdr[0] = (byte)blockType; // preserve not last
                        outFs.Write(blockHdr, 0, 4);
                        outFs.Write(other, 0, blockLength);
                    }
                }

                // Append new Vorbis Comment
                byte vcFlag = (byte)(pictureBlock == null ? 0x84 : 0x04); // 0x84 if last, 0x04 if followed by picture
                byte[] vcHdr = new byte[4]
                {
                    vcFlag,
                    (byte)((vorbisBlock.Length >> 16) & 0xFF),
                    (byte)((vorbisBlock.Length >> 8) & 0xFF),
                    (byte)(vorbisBlock.Length & 0xFF)
                };
                outFs.Write(vcHdr, 0, 4);
                outFs.Write(vorbisBlock, 0, vorbisBlock.Length);

                // Append picture if present
                if (pictureBlock != null)
                {
                    byte picFlag = 0x86; // blockType 6, last block
                    byte[] picHdr = new byte[4]
                    {
                        picFlag,
                        (byte)((pictureBlock.Length >> 16) & 0xFF),
                        (byte)((pictureBlock.Length >> 8) & 0xFF),
                        (byte)(pictureBlock.Length & 0xFF)
                    };
                    outFs.Write(picHdr, 0, 4);
                    outFs.Write(pictureBlock, 0, pictureBlock.Length);
                }

                // Copy remaining audio stream
                inFs.CopyTo(outFs);
            }

            Services.AtomicFile.WriteAllBytes(filePath, File.ReadAllBytes(tempFile));
            try { File.Delete(tempFile); } catch { }
        }

        private static byte[] BuildVorbisBlock(List<string> comments)
        {
            using var ms = new MemoryStream();
            byte[] vendor = Encoding.UTF8.GetBytes("MusicPower3");
            byte[] len4 = new byte[4];

            BinaryPrimitives.WriteInt32LittleEndian(len4, vendor.Length);
            ms.Write(len4, 0, 4);
            ms.Write(vendor, 0, vendor.Length);

            BinaryPrimitives.WriteInt32LittleEndian(len4, comments.Count);
            ms.Write(len4, 0, 4);

            foreach (var c in comments)
            {
                byte[] cb = Encoding.UTF8.GetBytes(c);
                BinaryPrimitives.WriteInt32LittleEndian(len4, cb.Length);
                ms.Write(len4, 0, 4);
                ms.Write(cb, 0, cb.Length);
            }
            return ms.ToArray();
        }

        private static byte[] BuildPictureBlock(byte[] picData)
        {
            using var ms = new MemoryStream();
            byte[] len4 = new byte[4];

            BinaryPrimitives.WriteInt32BigEndian(len4, 3); // type 3 = Cover Front
            ms.Write(len4, 0, 4);

            byte[] mime = Encoding.ASCII.GetBytes("image/jpeg");
            BinaryPrimitives.WriteInt32BigEndian(len4, mime.Length);
            ms.Write(len4, 0, 4);
            ms.Write(mime, 0, mime.Length);

            BinaryPrimitives.WriteInt32BigEndian(len4, 0); // empty description
            ms.Write(len4, 0, 4);

            byte[] dims = new byte[16]; // width, height, depth, colors
            ms.Write(dims, 0, 16);

            BinaryPrimitives.WriteInt32BigEndian(len4, picData.Length);
            ms.Write(len4, 0, 4);
            ms.Write(picData, 0, picData.Length);

            return ms.ToArray();
        }
    }

    #endregion

    #region Apple MP4 / M4A Implementation

    internal static class Mp4Handler
    {
        public static void Read(Stream stream, AudioMetadata meta, bool readArtwork)
        {
            stream.Position = 0;
            ScanAtoms(stream, stream.Length, meta, readArtwork);
        }

        private static void ScanAtoms(Stream s, long maxEnd, AudioMetadata meta, bool readArtwork)
        {
            byte[] hdr = new byte[8];
            while (s.Position + 8 <= maxEnd)
            {
                if (s.Read(hdr, 0, 8) < 8) break;
                long size = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
                string name = Encoding.ASCII.GetString(hdr, 4, 4);

                if (size == 1) // 64-bit size
                {
                    byte[] ext = new byte[8];
                    if (s.Read(ext, 0, 8) < 8) break;
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                }
                else if (size == 0)
                {
                    size = maxEnd - s.Position + 8;
                }

                if (size < 8) break;
                long atomDataStart = s.Position;
                long atomEnd = atomDataStart + size - 8;

                if (name == "moov" || name == "udta" || name == "trak" || name == "mdia")
                {
                    ScanAtoms(s, atomEnd, meta, readArtwork);
                }
                else if (name == "mvhd" && meta.Duration <= TimeSpan.Zero)
                {
                    // Media duration
                    byte[] mvhd = new byte[Math.Min(24, size - 8)];
                    if (s.Read(mvhd, 0, mvhd.Length) == mvhd.Length)
                    {
                        byte version = mvhd[0];
                        if (version == 0 && mvhd.Length >= 20)
                        {
                            uint timescale = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(12, 4));
                            uint duration = BinaryPrimitives.ReadUInt32BigEndian(mvhd.AsSpan(16, 4));
                            if (timescale > 0 && duration > 0) meta.Duration = TimeSpan.FromSeconds((double)duration / timescale);
                        }
                    }
                }
                else if (name == "meta")
                {
                    // Meta has a 4-byte version/flags prefix
                    s.Seek(4, SeekOrigin.Current);
                    ScanAtoms(s, atomEnd, meta, readArtwork);
                }
                else if (name == "ilst")
                {
                    ReadIlst(s, atomEnd, meta, readArtwork);
                }

                s.Position = atomEnd;
            }
        }

        private static void ReadIlst(Stream s, long ilstEnd, AudioMetadata meta, bool readArtwork)
        {
            byte[] atomHdr = new byte[8];
            while (s.Position + 8 <= ilstEnd)
            {
                if (s.Read(atomHdr, 0, 8) < 8) break;
                int size = BinaryPrimitives.ReadInt32BigEndian(atomHdr.AsSpan(0, 4));
                string name = Encoding.Latin1.GetString(atomHdr, 4, 4);

                if (size <= 8 || s.Position + size - 8 > ilstEnd) break;
                long itemEnd = s.Position + size - 8;

                // ilst items contain one or more 'data' atoms
                byte[] dataHdr = new byte[8];
                if (s.Read(dataHdr, 0, 8) == 8)
                {
                    int dataSize = BinaryPrimitives.ReadInt32BigEndian(dataHdr.AsSpan(0, 4));
                    string dataName = Encoding.ASCII.GetString(dataHdr, 4, 4);

                    if (dataName == "data" && dataSize >= 16)
                    {
                        byte[] typeFlags = new byte[8]; // 4-byte type, 4-byte locale
                        s.ReadExactly(typeFlags, 0, 8);
                        int payloadSize = dataSize - 16;
                        byte[] payload = new byte[payloadSize];
                        s.ReadExactly(payload, 0, payloadSize);

                        switch (name)
                        {
                            case "\u00a9nam": meta.Title = Encoding.UTF8.GetString(payload); break;
                            case "\u00a9ART": meta.Artist = Encoding.UTF8.GetString(payload); break;
                            case "\u00a9alb": meta.Album = Encoding.UTF8.GetString(payload); break;
                            case "aART": meta.AlbumArtist = Encoding.UTF8.GetString(payload); break;
                            case "\u00a9gen": meta.Genre = Encoding.UTF8.GetString(payload); break;
                            case "\u00a9cmt": meta.Comment = Encoding.UTF8.GetString(payload); break;
                            case "\u00a9day" when payload.Length >= 4 && uint.TryParse(Encoding.UTF8.GetString(payload, 0, 4), out var yr):
                                meta.Year = yr;
                                break;
                            case "trkn" when payload.Length >= 6:
                                meta.TrackNumber = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2));
                                meta.TrackTotal = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2));
                                break;
                            case "disk" when payload.Length >= 6:
                                meta.DiscNumber = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2));
                                meta.DiscTotal = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4, 2));
                                break;
                            case "covr" when readArtwork && meta.ArtworkData == null:
                                meta.ArtworkData = payload;
                                meta.ArtworkMimeType = (payload.Length > 8 && payload[0] == 0x89) ? "image/png" : "image/jpeg";
                                break;
                        }
                    }
                }

                s.Position = itemEnd;
            }
        }

        public static void Write(string filePath, Services.TrackMetadataUpdate update)
        {
            var existing = new AudioMetadata();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Read(fs, existing, readArtwork: true);
            }

            string title = update.Title ?? existing.Title;
            string artist = update.Performers != null ? string.Join(", ", update.Performers) : existing.Artist;
            string album = update.Album ?? existing.Album;
            string albumArtist = update.AlbumArtists != null ? string.Join(", ", update.AlbumArtists) : existing.AlbumArtist;
            uint track = update.Track ?? existing.TrackNumber;
            uint disc = update.Disc ?? existing.DiscNumber;
            uint year = update.Year ?? existing.Year;
            string genre = update.Genres != null ? string.Join(", ", update.Genres) : existing.Genre;
            string comment = update.Comment ?? existing.Comment;

            byte[]? artwork = update.ArtworkData != null 
                ? (update.ArtworkData.Length == 0 ? null : update.ArtworkData) 
                : existing.ArtworkData;

            byte[] newIlstBytes = BuildIlstAtom(title, artist, album, albumArtist, track, disc, year, genre, comment, artwork);
            byte[] newUdtaBytes = BuildUdtaAtom(newIlstBytes);

            RewriteMp4WithNewUdta(filePath, newUdtaBytes);
        }

        private static byte[] BuildIlstAtom(string title, string artist, string album, string albumArtist, uint track, uint disc, uint year, string genre, string comment, byte[]? artwork)
        {
            using var ms = new MemoryStream();
            if (!string.IsNullOrEmpty(title)) WriteTextItem(ms, "\u00a9nam", title);
            if (!string.IsNullOrEmpty(artist)) WriteTextItem(ms, "\u00a9ART", artist);
            if (!string.IsNullOrEmpty(album)) WriteTextItem(ms, "\u00a9alb", album);
            if (!string.IsNullOrEmpty(albumArtist)) WriteTextItem(ms, "aART", albumArtist);
            if (year > 0) WriteTextItem(ms, "\u00a9day", year.ToString());
            if (!string.IsNullOrEmpty(genre)) WriteTextItem(ms, "\u00a9gen", genre);
            if (!string.IsNullOrEmpty(comment)) WriteTextItem(ms, "\u00a9cmt", comment);
            if (track > 0) WriteTrknItem(ms, (ushort)track, 0);
            if (disc > 0) WriteDiskItem(ms, (ushort)disc, 0);
            if (artwork != null && artwork.Length > 0) WriteCoverItem(ms, artwork);

            byte[] payload = ms.ToArray();
            return WrapAtom("ilst", payload);
        }

        private static void WriteTextItem(Stream s, string name, string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            byte[] dataPayload = new byte[8 + utf8.Length];
            dataPayload[3] = 1; // type 1 = UTF-8 text
            Buffer.BlockCopy(utf8, 0, dataPayload, 8, utf8.Length);

            byte[] dataAtom = WrapAtom("data", dataPayload);
            byte[] itemAtom = WrapAtom(name, dataAtom);
            s.Write(itemAtom, 0, itemAtom.Length);
        }

        private static void WriteTrknItem(Stream s, ushort trackNum, ushort trackTotal)
        {
            byte[] dataPayload = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(dataPayload.AsSpan(10, 2), trackNum);
            BinaryPrimitives.WriteUInt16BigEndian(dataPayload.AsSpan(12, 2), trackTotal);

            byte[] dataAtom = WrapAtom("data", dataPayload);
            byte[] itemAtom = WrapAtom("trkn", dataAtom);
            s.Write(itemAtom, 0, itemAtom.Length);
        }

        private static void WriteDiskItem(Stream s, ushort discNum, ushort discTotal)
        {
            byte[] dataPayload = new byte[14];
            BinaryPrimitives.WriteUInt16BigEndian(dataPayload.AsSpan(10, 2), discNum);
            BinaryPrimitives.WriteUInt16BigEndian(dataPayload.AsSpan(12, 2), discTotal);

            byte[] dataAtom = WrapAtom("data", dataPayload);
            byte[] itemAtom = WrapAtom("disk", dataAtom);
            s.Write(itemAtom, 0, itemAtom.Length);
        }

        private static void WriteCoverItem(Stream s, byte[] imgBytes)
        {
            bool isPng = imgBytes.Length > 8 && imgBytes[0] == 0x89 && imgBytes[1] == 0x50 && imgBytes[2] == 0x4E && imgBytes[3] == 0x47;
            byte[] dataPayload = new byte[8 + imgBytes.Length];
            dataPayload[3] = (byte)(isPng ? 0x0E : 0x0D); // 13 = JPEG, 14 = PNG
            Buffer.BlockCopy(imgBytes, 0, dataPayload, 8, imgBytes.Length);

            byte[] dataAtom = WrapAtom("data", dataPayload);
            byte[] itemAtom = WrapAtom("covr", dataAtom);
            s.Write(itemAtom, 0, itemAtom.Length);
        }

        private static byte[] WrapAtom(string name, byte[] payload)
        {
            byte[] atom = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(atom.AsSpan(0, 4), (uint)atom.Length);
            Encoding.Latin1.GetBytes(name, 0, 4, atom, 4);
            Buffer.BlockCopy(payload, 0, atom, 8, payload.Length);
            return atom;
        }

        private static byte[] BuildUdtaAtom(byte[] ilstAtom)
        {
            byte[] hdlrPayload = new byte[25];
            Encoding.ASCII.GetBytes("mdir", 0, 4, hdlrPayload, 8);
            Encoding.ASCII.GetBytes("appl", 0, 4, hdlrPayload, 12);
            byte[] hdlrAtom = WrapAtom("hdlr", hdlrPayload);

            byte[] metaPayload = new byte[4 + hdlrAtom.Length + ilstAtom.Length];
            Buffer.BlockCopy(hdlrAtom, 0, metaPayload, 4, hdlrAtom.Length);
            Buffer.BlockCopy(ilstAtom, 0, metaPayload, 4 + hdlrAtom.Length, ilstAtom.Length);

            byte[] metaAtom = WrapAtom("meta", metaPayload);
            return WrapAtom("udta", metaAtom);
        }

        private static void RewriteMp4WithNewUdta(string filePath, byte[] newUdtaBytes)
        {
            string tempFile = filePath + ".m4atmp";
            using (var inFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024))
            using (var outFs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
            {
                long fileLen = inFs.Length;
                byte[] hdr = new byte[8];

                long moovPos = -1;
                long moovSize = -1;
                long mdatPos = -1;

                while (inFs.Position + 8 <= fileLen)
                {
                    long atomStart = inFs.Position;
                    if (inFs.Read(hdr, 0, 8) < 8) break;
                    long atomSize = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0, 4));
                    string atomName = Encoding.ASCII.GetString(hdr, 4, 4);

                    if (atomSize == 1)
                    {
                        byte[] ext = new byte[8];
                        if (inFs.Read(ext, 0, 8) < 8) break;
                        atomSize = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                    }
                    else if (atomSize == 0)
                    {
                        atomSize = fileLen - atomStart;
                    }

                    if (atomSize < 8) break;

                    if (atomName == "moov")
                    {
                        moovPos = atomStart;
                        moovSize = atomSize;
                    }
                    else if (atomName == "mdat" && mdatPos < 0)
                    {
                        mdatPos = atomStart;
                    }

                    inFs.Position = atomStart + atomSize;
                }

                if (moovPos < 0 || moovSize < 8) return;

                inFs.Position = moovPos;
                byte[] oldMoov = new byte[moovSize];
                inFs.ReadExactly(oldMoov, 0, (int)moovSize);

                byte[] newMoov = UpdateMoovWithUdta(oldMoov, newUdtaBytes);
                long delta = (long)newMoov.Length - moovSize;

                if (delta != 0 && mdatPos > moovPos)
                {
                    AdjustChunkOffsets(newMoov, delta);
                }

                inFs.Position = 0;
                while (inFs.Position < fileLen)
                {
                    long pos = inFs.Position;
                    if (pos == moovPos)
                    {
                        outFs.Write(newMoov, 0, newMoov.Length);
                        inFs.Position = moovPos + moovSize;
                    }
                    else
                    {
                        long copyChunk = moovPos > pos ? Math.Min(fileLen - pos, moovPos - pos) : (fileLen - pos);
                        byte[] buf = new byte[Math.Min(copyChunk, 128 * 1024)];
                        while (copyChunk > 0)
                        {
                            int r = inFs.Read(buf, 0, (int)Math.Min(buf.Length, copyChunk));
                            if (r <= 0) break;
                            outFs.Write(buf, 0, r);
                            copyChunk -= r;
                        }
                    }
                }
            }

            File.Move(tempFile, filePath, overwrite: true);
        }

        private static byte[] UpdateMoovWithUdta(byte[] moovBytes, byte[] newUdta)
        {
            using var ms = new MemoryStream();
            ms.Position = 8;

            int offset = 8;
            int len = moovBytes.Length;

            while (offset + 8 <= len)
            {
                uint atomSize = BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(offset, 4));
                string name = Encoding.ASCII.GetString(moovBytes, offset + 4, 4);

                if (atomSize < 8 || offset + atomSize > len) break;

                if (name != "udta")
                {
                    ms.Write(moovBytes, offset, (int)atomSize);
                }

                offset += (int)atomSize;
            }

            ms.Write(newUdta, 0, newUdta.Length);

            byte[] result = ms.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)result.Length);
            Encoding.ASCII.GetBytes("moov", 0, 4, result, 4);
            return result;
        }

        private static void AdjustChunkOffsets(byte[] moovBytes, long delta)
        {
            int offset = 0;
            int len = moovBytes.Length;
            while (offset + 8 <= len)
            {
                string name = Encoding.ASCII.GetString(moovBytes, offset + 4, 4);
                if (name == "stco" && offset + 16 <= len)
                {
                    uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(offset + 12, 4));
                    int tableOffset = offset + 16;
                    for (int i = 0; i < entryCount && tableOffset + 4 <= len; i++)
                    {
                        uint currentOffset = BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(tableOffset, 4));
                        uint updated = (uint)(currentOffset + delta);
                        BinaryPrimitives.WriteUInt32BigEndian(moovBytes.AsSpan(tableOffset, 4), updated);
                        tableOffset += 4;
                    }
                }
                else if (name == "co64" && offset + 16 <= len)
                {
                    uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(moovBytes.AsSpan(offset + 12, 4));
                    int tableOffset = offset + 16;
                    for (int i = 0; i < entryCount && tableOffset + 8 <= len; i++)
                    {
                        ulong currentOffset = BinaryPrimitives.ReadUInt64BigEndian(moovBytes.AsSpan(tableOffset, 8));
                        ulong updated = (ulong)((long)currentOffset + delta);
                        BinaryPrimitives.WriteUInt64BigEndian(moovBytes.AsSpan(tableOffset, 8), updated);
                        tableOffset += 8;
                    }
                }
                offset++;
            }
        }
    }

    #endregion

    #region WAV Implementation

    internal static class WavHandler
    {
        public static void Read(Stream stream, AudioMetadata meta, bool readArtwork)
        {
            stream.Position = 0;
            byte[] riffHdr = new byte[12];
            if (stream.Read(riffHdr, 0, 12) < 12) return;

            if (Encoding.ASCII.GetString(riffHdr, 0, 4) != "RIFF" || Encoding.ASCII.GetString(riffHdr, 8, 4) != "WAVE") return;

            byte[] chunkHdr = new byte[8];
            int byteRate = 0;

            while (stream.Position + 8 <= stream.Length)
            {
                if (stream.Read(chunkHdr, 0, 8) < 8) break;
                string chunkId = Encoding.ASCII.GetString(chunkHdr, 0, 4);
                uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr.AsSpan(4, 4));
                long chunkEnd = stream.Position + chunkSize;

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    byte[] fmt = new byte[16];
                    if (stream.Read(fmt, 0, 16) == 16)
                    {
                        byteRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(8, 4));
                    }
                }
                else if (chunkId == "data" && byteRate > 0 && meta.Duration <= TimeSpan.Zero)
                {
                    meta.Duration = TimeSpan.FromSeconds((double)chunkSize / byteRate);
                }
                else if (chunkId == "id3 ")
                {
                    Id3Handler.Read(stream, meta, readArtwork);
                }

                stream.Position = chunkEnd + (chunkSize % 2); // WAV chunks are word-aligned
            }
        }
    }

    #endregion

    #region OGG / Opus Implementation

    internal static class OggHandler
    {
        public static void Read(Stream stream, AudioMetadata meta, bool readArtwork)
        {
            stream.Position = 0;
            byte[] pageHdr = new byte[27];
            if (stream.Read(pageHdr, 0, 27) < 27) return;
            if (Encoding.ASCII.GetString(pageHdr, 0, 4) != "OggS") return;

            // Stream Vorbis / Opus comment packet
            byte[] buf = new byte[Math.Min(stream.Length, 65536)];
            stream.Position = 0;
            int read = stream.Read(buf, 0, buf.Length);

            // Search for "TITLE=" in buffer
            string text = Encoding.UTF8.GetString(buf, 0, read);
            foreach (var line in text.Split('\0', '\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string k = line.Substring(0, eq).Trim().ToUpperInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "TITLE": meta.Title = v; break;
                        case "ARTIST": meta.Artist = v; break;
                        case "ALBUM": meta.Album = v; break;
                        case "ALBUMARTIST": meta.AlbumArtist = v; break;
                        case "GENRE": meta.Genre = v; break;
                    }
                }
            }
        }
    }

    #endregion

    #region Online Metadata Service (Apple Music, MusicBrainz, Spotify, YouTube, SoundCloud)

    public static class OnlineMetadataService
    {
        private static readonly HttpClient _httpClient = CreateClient();

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 MusicPower3/2.3");
            return client;
        }

        public static async Task<AudioMetadata?> FetchMetadataAsync(string queryOrUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(queryOrUrl)) return null;
            queryOrUrl = queryOrUrl.Trim();

            // 1. Direct link detection
            if (Uri.TryCreate(queryOrUrl, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
            {
                string host = uri.Host.ToLowerInvariant();
                try
                {
                    if (host.Contains("apple.com"))
                    {
                        var appleResult = await FetchFromAppleMusicUrlAsync(uri, ct);
                        if (appleResult != null) return appleResult;
                    }
                    else if (host.Contains("spotify.com"))
                    {
                        var spotifyResult = await FetchFromSpotifyUrlAsync(uri, ct);
                        if (spotifyResult != null) return spotifyResult;
                    }
                    else if (host.Contains("youtube.com") || host.Contains("youtu.be"))
                    {
                        var ytUrlResult = await FetchFromYouTubeUrlAsync(uri, ct);
                        if (ytUrlResult != null) return ytUrlResult;
                    }
                    else if (host.Contains("soundcloud.com"))
                    {
                        var scResult = await FetchFromSoundCloudUrlAsync(uri, ct);
                        if (scResult != null) return scResult;
                    }
                }
                catch { }
            }

            bool wantsVariant = ContainsVariantKeyword(queryOrUrl);

            // 2. Priority 1: MusicBrainz Open Database (clean studio recordings and original album releases)
            var mbResult = await SearchMusicBrainzAsync(queryOrUrl, ct, wantsVariant);
            if (mbResult != null && !string.IsNullOrWhiteSpace(mbResult.Title) && !string.IsNullOrWhiteSpace(mbResult.Artist))
            {
                return mbResult;
            }

            // 3. Priority 2: YouTube Search (official release and topic video resolution)
            var ytResult = await SearchYouTubeAsync(queryOrUrl, wantsVariant, ct);
            if (ytResult != null && !string.IsNullOrWhiteSpace(ytResult.Title) && !string.IsNullOrWhiteSpace(ytResult.Artist))
            {
                return ytResult;
            }

            // 4. Priority 3: Apple Music / iTunes Search API with relevance scoring
            var itunesResult = await SearchItunesApiAsync(queryOrUrl, ct, wantsVariant: wantsVariant);
            if (itunesResult != null)
            {
                return itunesResult;
            }

            return null;
        }

        private static readonly string[] VariantKeywords = new[]
        {
            "live", "remix", "remaster", "remastered", "re-record", "rerecorded",
            "re-recorded", "acoustic", "session", "deluxe", "anniversary", "demo",
            "alternate", "unplugged", "orchestral", "instrumental", "tribute", "karaoke"
        };

        public static bool ContainsVariantKeyword(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string lower = text.ToLowerInvariant();
            return VariantKeywords.Any(k => lower.Contains(k));
        }

        private static async Task<AudioMetadata?> SearchYouTubeAsync(string query, bool wantsVariant, CancellationToken ct)
        {
            try
            {
                string cleaned = CleanQueryForSearch(query);
                string term = string.IsNullOrWhiteSpace(cleaned) ? query : cleaned;

                string searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(term)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;

                string html = await response.Content.ReadAsStringAsync(ct);
                var matches = System.Text.RegularExpressions.Regex.Matches(html, @"""videoId"":""([A-Za-z0-9_-]{11})""");
                if (matches.Count == 0) return null;

                var candidateIds = matches.Cast<System.Text.RegularExpressions.Match>()
                                          .Select(m => m.Groups[1].Value)
                                          .Distinct()
                                          .Take(3)
                                          .ToList();

                foreach (var vid in candidateIds)
                {
                    ct.ThrowIfCancellationRequested();
                    var meta = await FetchFromYouTubeUrlAsync(new Uri($"https://www.youtube.com/watch?v={vid}"), ct);
                    if (meta != null && !string.IsNullOrWhiteSpace(meta.Title))
                    {
                        if (!wantsVariant)
                        {
                            if (ContainsVariantKeyword(meta.Title) || ContainsVariantKeyword(meta.Album))
                                continue;
                        }
                        return meta;
                    }
                }
            }
            catch { }
            return null;
        }

        public static string CleanQueryForSearch(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = raw.Trim();

            // Strip audio file extension if filename was passed
            if (s.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".aac", StringComparison.OrdinalIgnoreCase) ||
                s.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                s = Path.GetFileNameWithoutExtension(s);
            }

            // Strip YouTube 11-char video ID suffix (e.g. _62Vjm0UGfv8 or -62Vjm0UGfv8)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"[_\-][A-Za-z0-9_-]{11}$", "");

            // Strip leading track numbers: (01), [01], 01., 01 - , 01_
            s = System.Text.RegularExpressions.Regex.Replace(s, @"^[\(\[]?\d{1,3}[\)\]]?[\.\-_\s]+", "");

            // Strip rip and audio quality tags
            string[] noisePatterns = {
                @"\[HQ\]", @"\[HD\]", @"\[4K\]", @"\[AMV\]", @"\[Official Video\]", @"\(Official Video\)",
                @"\(Official Music Video\)", @"\[Official Music Video\]", @"\(Official Audio\)", @"\[Official Audio\]",
                @"\(Audio\)", @"\[Audio\]", @"\(Lyric Video\)", @"\[Lyric Video\]", @"\(Lyrics\)", @"\[Lyrics\]",
                @"\(Visualizer\)", @"\[Visualizer\]", @"\(Clip Officiel\)", @"HIGH QUALITY AUDIO",
                @"\(Remastered.*?\)", @"\[Remastered.*?\]", @"\(Deluxe.*?\)", @"\[Deluxe.*?\]"
            };
            foreach (var pattern in noisePatterns)
            {
                s = System.Text.RegularExpressions.Regex.Replace(s, pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            // Strip soundtrack tags formatted like "- from the ... Soundtrack"
            s = System.Text.RegularExpressions.Regex.Replace(s, @"-\s*from\s+the\s+.*?Soundtrack.*?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            // Strip trailing bracketed tags like [Gta San Andreas-Radio X] or [320kbps]
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\[[^\]]+\]\s*$", "");

            // Replace underscores with space and trim quotes/brackets
            s = s.Replace('_', ' ').Trim(' ', '-', '"', '\'', '[', ']');
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ");

            return s;
        }

        private static async Task<AudioMetadata?> SearchItunesApiAsync(string query, CancellationToken ct, string? explicitTitle = null, string? explicitArtist = null, bool wantsVariant = false)
        {
            try
            {
                string cleaned = CleanQueryForSearch(query);
                string termToSearch = string.IsNullOrWhiteSpace(cleaned) ? query : cleaned;

                // Query iTunes API with limit=10 for candidate ranking
                string url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(termToSearch)}&entity=song&limit=10";
                string json = await _httpClient.GetStringAsync(url, ct);
                var doc = JsonNode.Parse(json);
                var results = doc?["results"]?.AsArray();

                if (results == null || results.Count == 0)
                {
                    // Try stripping bracketed terms like [Gta San Andreas] or [AMV]
                    string unbracketed = System.Text.RegularExpressions.Regex.Replace(termToSearch, @"\[.*?\]", " ").Trim();
                    unbracketed = System.Text.RegularExpressions.Regex.Replace(unbracketed, @"\s{2,}", " ");
                    if (!string.IsNullOrWhiteSpace(unbracketed) && unbracketed != termToSearch)
                    {
                        url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(unbracketed)}&entity=song&limit=10";
                        json = await _httpClient.GetStringAsync(url, ct);
                        doc = JsonNode.Parse(json);
                        results = doc?["results"]?.AsArray();
                    }
                }

                if (results == null || results.Count == 0)
                {
                    // If combined query failed and original contained separator, try searching title only
                    int dash = termToSearch.IndexOf(" - ", StringComparison.Ordinal);
                    if (dash > 0)
                    {
                        string subTitle = termToSearch.Substring(dash + 3).Trim();
                        subTitle = System.Text.RegularExpressions.Regex.Replace(subTitle, @"\[.*?\]", " ").Trim();
                        url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(subTitle)}&entity=song&limit=10";
                        json = await _httpClient.GetStringAsync(url, ct);
                        doc = JsonNode.Parse(json);
                        results = doc?["results"]?.AsArray();
                    }
                }

                if (results != null && results.Count > 0)
                {
                    JsonNode? bestMatch = SelectBestItunesMatch(results, termToSearch, explicitTitle, explicitArtist, wantsVariant);
                    if (bestMatch != null)
                    {
                        var meta = new AudioMetadata
                        {
                            Title = bestMatch["trackName"]?.ToString() ?? "",
                            Artist = bestMatch["artistName"]?.ToString() ?? "",
                            Album = bestMatch["collectionName"]?.ToString() ?? "",
                            AlbumArtist = bestMatch["artistName"]?.ToString() ?? "",
                            Genre = bestMatch["primaryGenreName"]?.ToString() ?? ""
                        };

                        if (uint.TryParse(bestMatch["trackNumber"]?.ToString(), out var trk)) meta.TrackNumber = trk;
                        if (uint.TryParse(bestMatch["discNumber"]?.ToString(), out var dsc)) meta.DiscNumber = dsc;

                        string? releaseDate = bestMatch["releaseDate"]?.ToString();
                        if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && uint.TryParse(releaseDate.Substring(0, 4), out var yr))
                        {
                            meta.Year = yr;
                        }

                        // Strictly NO artwork download as requested
                        return meta;
                    }
                }
            }
            catch { }
            return null;
        }

        private static JsonNode? SelectBestItunesMatch(JsonArray results, string query, string? explicitTitle = null, string? explicitArtist = null, bool wantsVariant = false)
        {
            if (results == null || results.Count == 0) return null;

            string targetTitle = explicitTitle ?? "";
            string targetArtist = explicitArtist ?? "";

            if (string.IsNullOrEmpty(targetTitle))
            {
                int dash = query.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0)
                {
                    targetArtist = query.Substring(0, dash).Trim();
                    targetTitle = query.Substring(dash + 3).Trim();
                }
                else
                {
                    targetTitle = query;
                }
            }

            string cleanTargetTitle = System.Text.RegularExpressions.Regex.Replace(targetTitle.ToLowerInvariant(), @"[^\w\s]", " ").Trim();
            string[] targetTitleWords = cleanTargetTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                      .Where(w => w.Length >= 2).ToArray();

            string cleanTargetArtist = System.Text.RegularExpressions.Regex.Replace(targetArtist.ToLowerInvariant(), @"[^\w\s]", " ").Trim();
            string[] targetArtistWords = cleanTargetArtist.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                         .Where(w => w.Length >= 2).ToArray();

            JsonNode? best = null;
            int bestScore = int.MinValue;

            foreach (var item in results)
            {
                if (item == null) continue;
                string trackName = (item["trackName"]?.ToString() ?? "").ToLowerInvariant();
                string artistName = (item["artistName"]?.ToString() ?? "").ToLowerInvariant();
                string collectionName = (item["collectionName"]?.ToString() ?? "").ToLowerInvariant();

                string cleanTrack = System.Text.RegularExpressions.Regex.Replace(trackName, @"[^\w\s]", " ");
                string[] trackWords = cleanTrack.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                string cleanArtist = System.Text.RegularExpressions.Regex.Replace(artistName, @"[^\w\s]", " ");
                string[] artistWords = cleanArtist.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                // 1. Verify Title Match:
                // Either candidate track title contains the target title, or candidate words contain all target title words
                bool titleVerified = cleanTrack.Contains(cleanTargetTitle) || cleanTargetTitle.Contains(cleanTrack);
                if (!titleVerified && targetTitleWords.Length > 0)
                {
                    int matchedTitleWords = targetTitleWords.Count(tw => trackWords.Contains(tw));
                    if (matchedTitleWords >= (targetTitleWords.Length + 1) / 2)
                        titleVerified = true;
                }

                if (!titleVerified) continue; // REJECT: Title doesn't match

                // 2. Verify Artist Match:
                bool artistVerified = string.IsNullOrEmpty(cleanTargetArtist);
                if (!artistVerified && targetArtistWords.Length > 0)
                {
                    artistVerified = cleanArtist.Contains(cleanTargetArtist) || cleanTargetArtist.Contains(cleanArtist);
                    if (!artistVerified)
                    {
                        int matchedArtistWords = targetArtistWords.Count(aw => artistWords.Contains(aw));
                        if (matchedArtistWords >= 1 && (matchedArtistWords >= (targetArtistWords.Length + 1) / 2 || targetArtistWords.Length <= 2))
                            artistVerified = true;
                    }
                }

                if (!artistVerified) continue; // REJECT: Artist doesn't match

                int score = 100;
                if (cleanTrack == cleanTargetTitle) score += 50;
                if (cleanArtist == cleanTargetArtist) score += 50;

                // Penalize variant keywords (live, remix, remaster, deluxe, etc.) when user did not ask for variant
                if (!wantsVariant)
                {
                    if (ContainsVariantKeyword(trackName) || ContainsVariantKeyword(collectionName))
                    {
                        score -= 80;
                    }
                }

                // Penalize unwanted karaoke, backing tracks, or instrumental covers unless requested
                string queryLower = query.ToLowerInvariant();
                bool queryWantsKaraoke = queryLower.Contains("karaoke") || queryLower.Contains("tribute") || queryLower.Contains("instrumental");
                if (!queryWantsKaraoke)
                {
                    if (trackName.Contains("karaoke") || artistName.Contains("karaoke")) score -= 30;
                    if (trackName.Contains("tribute") || artistName.Contains("tribute")) score -= 25;
                    if (trackName.Contains("instrumental") || trackName.Contains("backing track")) score -= 25;
                    if (trackName.Contains("originally performed by")) score -= 30;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }

            return best;
        }

        private static async Task<AudioMetadata?> SearchMusicBrainzAsync(string query, CancellationToken ct, bool wantsVariant = false)
        {
            try
            {
                int dash = query.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0)
                {
                    string artistPart = query.Substring(0, dash).Trim();
                    string titlePart = query.Substring(dash + 3).Trim();
                    var structured = await SearchMusicBrainzStructuredAsync(artistPart, titlePart, ct, wantsVariant);
                    if (structured != null) return structured;
                }

                string cleaned = CleanQueryForSearch(query);
                string term = string.IsNullOrWhiteSpace(cleaned) ? query : cleaned;

                string url = $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(term)}&fmt=json&limit=10";
                string json = await _httpClient.GetStringAsync(url, ct);
                var doc = JsonNode.Parse(json);
                var recordings = doc?["recordings"]?.AsArray();
                if (recordings != null && recordings.Count > 0)
                {
                    foreach (var rec in recordings)
                    {
                        if (rec == null) continue;
                        string recTitle = rec["title"]?.ToString() ?? "";
                        string disambiguation = rec["disambiguation"]?.ToString() ?? "";

                        if (!wantsVariant)
                        {
                            if (ContainsVariantKeyword(disambiguation) || ContainsVariantKeyword(recTitle))
                                continue;
                        }

                        var artistCredit = rec["artist-credit"]?.AsArray();
                        string artist = artistCredit != null && artistCredit.Count > 0 ? (artistCredit[0]?["name"]?.ToString() ?? "") : "";

                        var releases = rec["releases"]?.AsArray();
                        string albumName = "";
                        uint releaseYear = 0;

                        if (releases != null && releases.Count > 0)
                        {
                            var validReleases = new List<(string Title, uint Year, bool IsStudio)>();
                            foreach (var rel in releases)
                            {
                                if (rel == null) continue;
                                string relTitle = rel["title"]?.ToString() ?? "";
                                if (!wantsVariant && ContainsVariantKeyword(relTitle))
                                    continue;

                                string? relDate = rel["date"]?.ToString();
                                uint y = 0;
                                if (!string.IsNullOrEmpty(relDate) && relDate.Length >= 4 && uint.TryParse(relDate.Substring(0, 4), out var parsedY))
                                {
                                    y = parsedY;
                                }

                                var rg = rel["release-group"];
                                string primaryType = rg?["primary-type"]?.ToString() ?? "";
                                var secTypes = rg?["secondary-types"]?.AsArray();
                                bool hasVariantSecondary = false;
                                if (secTypes != null && !wantsVariant)
                                {
                                    foreach (var st in secTypes)
                                    {
                                        if (ContainsVariantKeyword(st?.ToString()))
                                        {
                                            hasVariantSecondary = true;
                                            break;
                                        }
                                    }
                                }
                                if (hasVariantSecondary) continue;

                                bool isStudio = string.Equals(primaryType, "Album", StringComparison.OrdinalIgnoreCase);
                                validReleases.Add((relTitle, y, isStudio));
                            }

                            if (validReleases.Count > 0)
                            {
                                var bestRel = validReleases
                                    .OrderByDescending(r => r.IsStudio)
                                    .ThenBy(r => r.Year == 0 ? uint.MaxValue : r.Year)
                                    .First();

                                albumName = bestRel.Title;
                                releaseYear = bestRel.Year;
                            }
                            else if (releases[0] != null)
                            {
                                albumName = releases[0]?["title"]?.ToString() ?? "";
                            }
                        }

                        if (releaseYear == 0)
                        {
                            string? fDate = rec["first-release-date"]?.ToString();
                            if (!string.IsNullOrEmpty(fDate) && fDate.Length >= 4 && uint.TryParse(fDate.Substring(0, 4), out var fy))
                            {
                                releaseYear = fy;
                            }
                        }

                        return new AudioMetadata
                        {
                            Title = recTitle,
                            Artist = artist,
                            AlbumArtist = artist,
                            Album = albumName,
                            Year = releaseYear
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task<AudioMetadata?> SearchMusicBrainzStructuredAsync(string artist, string title, CancellationToken ct, bool wantsVariant = false)
        {
            try
            {
                string term = $"artist:\"{artist}\" AND recording:\"{title}\"";
                string url = $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(term)}&fmt=json&limit=10";
                string json = await _httpClient.GetStringAsync(url, ct);
                var doc = JsonNode.Parse(json);
                var recordings = doc?["recordings"]?.AsArray();
                if (recordings != null && recordings.Count > 0)
                {
                    foreach (var rec in recordings)
                    {
                        if (rec == null) continue;
                        string recTitle = rec["title"]?.ToString() ?? "";
                        string disambiguation = rec["disambiguation"]?.ToString() ?? "";

                        if (!wantsVariant)
                        {
                            if (ContainsVariantKeyword(disambiguation) || ContainsVariantKeyword(recTitle))
                                continue;
                        }

                        var artistCredit = rec["artist-credit"]?.AsArray();
                        string recArtist = artistCredit != null && artistCredit.Count > 0 ? (artistCredit[0]?["name"]?.ToString() ?? artist) : artist;

                        var releases = rec["releases"]?.AsArray();
                        string albumName = "";
                        uint releaseYear = 0;

                        if (releases != null && releases.Count > 0)
                        {
                            var validReleases = new List<(string Title, uint Year, bool IsStudio)>();
                            foreach (var rel in releases)
                            {
                                if (rel == null) continue;
                                string relTitle = rel["title"]?.ToString() ?? "";
                                if (!wantsVariant && ContainsVariantKeyword(relTitle))
                                    continue;

                                string? relDate = rel["date"]?.ToString();
                                uint y = 0;
                                if (!string.IsNullOrEmpty(relDate) && relDate.Length >= 4 && uint.TryParse(relDate.Substring(0, 4), out var parsedY))
                                {
                                    y = parsedY;
                                }

                                var rg = rel["release-group"];
                                string primaryType = rg?["primary-type"]?.ToString() ?? "";
                                var secTypes = rg?["secondary-types"]?.AsArray();
                                bool hasVariantSecondary = false;
                                if (secTypes != null && !wantsVariant)
                                {
                                    foreach (var st in secTypes)
                                    {
                                        if (ContainsVariantKeyword(st?.ToString()))
                                        {
                                            hasVariantSecondary = true;
                                            break;
                                        }
                                    }
                                }
                                if (hasVariantSecondary) continue;

                                bool isStudio = string.Equals(primaryType, "Album", StringComparison.OrdinalIgnoreCase);
                                validReleases.Add((relTitle, y, isStudio));
                            }

                            if (validReleases.Count > 0)
                            {
                                var bestRel = validReleases
                                    .OrderByDescending(r => r.IsStudio)
                                    .ThenBy(r => r.Year == 0 ? uint.MaxValue : r.Year)
                                    .First();

                                albumName = bestRel.Title;
                                releaseYear = bestRel.Year;
                            }
                            else if (releases[0] != null)
                            {
                                albumName = releases[0]?["title"]?.ToString() ?? "";
                            }
                        }

                        if (releaseYear == 0)
                        {
                            string? fDate = rec["first-release-date"]?.ToString();
                            if (!string.IsNullOrEmpty(fDate) && fDate.Length >= 4 && uint.TryParse(fDate.Substring(0, 4), out var fy))
                            {
                                releaseYear = fy;
                            }
                        }

                        return new AudioMetadata
                        {
                            Title = string.IsNullOrWhiteSpace(title) ? recTitle : title,
                            Artist = string.IsNullOrWhiteSpace(artist) ? recArtist : artist,
                            AlbumArtist = string.IsNullOrWhiteSpace(artist) ? recArtist : artist,
                            Album = albumName,
                            Year = releaseYear
                        };
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task<AudioMetadata?> FetchFromAppleMusicUrlAsync(Uri uri, CancellationToken ct)
        {
            string path = uri.AbsolutePath;
            string? trackId = null;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (!string.IsNullOrEmpty(query["i"])) trackId = query["i"];

            if (string.IsNullOrEmpty(trackId))
            {
                int lastSlash = path.LastIndexOf('/');
                if (lastSlash >= 0) trackId = path.Substring(lastSlash + 1);
            }

            // Detect regional storefront from URL (e.g., /us/, /fr/, /gb/, /jp/, /de/)
            string country = "us";
            var segments = uri.Segments;
            if (segments.Length > 1 && segments[1].Length == 3 && segments[1].EndsWith("/"))
            {
                country = segments[1].Trim('/').ToLowerInvariant();
            }

            if (!string.IsNullOrEmpty(trackId) && long.TryParse(trackId, out _))
            {
                string lookupUrl = $"https://itunes.apple.com/lookup?id={trackId}&country={country}";
                try
                {
                    string json = await _httpClient.GetStringAsync(lookupUrl, ct);
                    var doc = JsonNode.Parse(json);
                    var results = doc?["results"]?.AsArray();
                    if (results != null && results.Count > 0)
                    {
                        // Prefer item with wrapperType == "track"
                        var item = results.FirstOrDefault(r => r?["wrapperType"]?.ToString() == "track") ?? results[0];
                        if (item != null)
                        {
                            var meta = new AudioMetadata
                            {
                                Title = item["trackName"]?.ToString() ?? item["collectionName"]?.ToString() ?? "",
                                Artist = item["artistName"]?.ToString() ?? "",
                                Album = item["collectionName"]?.ToString() ?? "",
                                AlbumArtist = item["artistName"]?.ToString() ?? "",
                                Genre = item["primaryGenreName"]?.ToString() ?? ""
                            };
                            if (uint.TryParse(item["trackNumber"]?.ToString(), out var trk)) meta.TrackNumber = trk;
                            if (uint.TryParse(item["discNumber"]?.ToString(), out var dsc)) meta.DiscNumber = dsc;
                            string? releaseDate = item["releaseDate"]?.ToString();
                            if (!string.IsNullOrEmpty(releaseDate) && releaseDate.Length >= 4 && uint.TryParse(releaseDate.Substring(0, 4), out var yr))
                            {
                                meta.Year = yr;
                            }
                            return meta;
                        }
                    }
                }
                catch { }
            }

            // Fallback: extract album/song title slug from path and search
            string slug = path.Replace('/', ' ').Trim();
            return await SearchItunesApiAsync(slug, ct);
        }

        private static async Task<AudioMetadata?> FetchFromSpotifyUrlAsync(Uri uri, CancellationToken ct)
        {
            // Normalize Spotify URL: strip query parameters and regional /intl-*/ prefix
            string cleanUrl = uri.GetLeftPart(UriPartial.Path);
            cleanUrl = System.Text.RegularExpressions.Regex.Replace(cleanUrl, @"/intl-[a-z]{2}/", "/");

            string? title = null;
            string? artist = null;
            string? album = null;
            uint year = 0;

            try
            {
                string html = await _httpClient.GetStringAsync(cleanUrl, ct);

                // Try og:title
                var ogTitleMatch = System.Text.RegularExpressions.Regex.Match(html, @"<meta\s+property=""og:title""\s+content=""(.*?)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (ogTitleMatch.Success) title = System.Net.WebUtility.HtmlDecode(ogTitleMatch.Groups[1].Value);

                // Try og:description: "Artist · Album · Song · Year"
                var ogDescMatch = System.Text.RegularExpressions.Regex.Match(html, @"<meta\s+property=""og:description""\s+content=""(.*?)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (ogDescMatch.Success)
                {
                    string desc = System.Net.WebUtility.HtmlDecode(ogDescMatch.Groups[1].Value);
                    string[] parts = desc.Split(new[] { "·", "•" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1) artist = parts[0];
                    if (parts.Length >= 2 && parts[1] != "Song") album = parts[1];
                    if (parts.Length >= 4 && uint.TryParse(parts[3], out var yr)) year = yr;
                }

                // Fallback to title tag: "Title - song and lyrics by Artist | Spotify"
                if (string.IsNullOrWhiteSpace(artist))
                {
                    var titleTagMatch = System.Text.RegularExpressions.Regex.Match(html, @"<title>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (titleTagMatch.Success)
                    {
                        string titleTag = System.Net.WebUtility.HtmlDecode(titleTagMatch.Groups[1].Value);
                        int byIdx = titleTag.IndexOf(" - song and lyrics by ", StringComparison.OrdinalIgnoreCase);
                        if (byIdx > 0)
                        {
                            title = titleTag.Substring(0, byIdx).Trim();
                            string remainder = titleTag.Substring(byIdx + 22);
                            int pipe = remainder.IndexOf('|');
                            artist = (pipe > 0 ? remainder.Substring(0, pipe) : remainder).Trim();
                        }
                    }
                }
            }
            catch { }

            // If scraping was insufficient, try Spotify oEmbed
            if (string.IsNullOrEmpty(title))
            {
                try
                {
                    string oEmbedUrl = $"https://open.spotify.com/oembed?url={Uri.EscapeDataString(cleanUrl)}";
                    string json = await _httpClient.GetStringAsync(oEmbedUrl, ct);
                    var doc = JsonNode.Parse(json);
                    if (doc != null)
                    {
                        title = doc["title"]?.ToString();
                        string? author = doc["author_name"]?.ToString();
                        if (!string.IsNullOrEmpty(author)) artist = author;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(title))
            {
                // Enrich using iTunes API to get complete album, year, and track number
                string searchQuery = !string.IsNullOrEmpty(artist) ? $"{artist} {title}" : title;
                var enriched = await SearchItunesApiAsync(searchQuery, ct);
                if (enriched != null) return enriched;

                return new AudioMetadata
                {
                    Title = title,
                    Artist = artist ?? "",
                    Album = album ?? "",
                    AlbumArtist = artist ?? "",
                    Year = year
                };
            }

            return null;
        }

        private static async Task<AudioMetadata?> FetchFromYouTubeUrlAsync(Uri uri, CancellationToken ct)
        {
            try
            {
                // Normalize URL to standard watch URL so oEmbed succeeds reliably
                string normUrl = uri.ToString();
                if (uri.Host.Contains("youtu.be"))
                {
                    string videoId = uri.AbsolutePath.Trim('/');
                    normUrl = $"https://www.youtube.com/watch?v={videoId}";
                }
                else if (uri.Host.Contains("music.youtube.com"))
                {
                    normUrl = $"https://www.youtube.com{uri.PathAndQuery}";
                }

                string oEmbedEndpoint = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(normUrl)}&format=json";
                string json = await _httpClient.GetStringAsync(oEmbedEndpoint, ct);
                var doc = JsonNode.Parse(json);
                if (doc == null) return null;

                string rawTitle = doc["title"]?.ToString() ?? "";
                string rawAuthor = doc["author_name"]?.ToString() ?? "";

                // Clean author name: strip " - Topic", "VEVO"
                string author = rawAuthor.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase)
                                         .Replace("VEVO", "", StringComparison.OrdinalIgnoreCase)
                                         .Trim();

                string title = rawTitle;
                string artist = author;

                // Detect pattern: "Song" by Artist
                var byMatch = System.Text.RegularExpressions.Regex.Match(rawTitle, @"^""(.*?)""\s+by\s+(.*?)(?:\s+-.*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (byMatch.Success)
                {
                    title = byMatch.Groups[1].Value.Trim();
                    artist = byMatch.Groups[2].Value.Trim();
                }
                else
                {
                    // Detect pattern: Artist - Song
                    int dash = rawTitle.IndexOf(" - ", StringComparison.Ordinal);
                    if (dash > 0)
                    {
                        artist = rawTitle.Substring(0, dash).Trim();
                        title = rawTitle.Substring(dash + 3).Trim();
                    }
                }

                title = CleanQueryForSearch(title);

                // Enrich with iTunes Search API or MusicBrainz to retrieve full album, year, track number, and genre
                if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(title))
                {
                    var enriched = await SearchItunesApiAsync($"{artist} {title}", ct, title, artist);
                    if (enriched != null) return enriched;

                    var mbEnriched = await SearchMusicBrainzStructuredAsync(artist, title, ct);
                    if (mbEnriched != null && !string.IsNullOrEmpty(mbEnriched.Album)) return mbEnriched;
                }

                return new AudioMetadata
                {
                    Title = title,
                    Artist = artist,
                    AlbumArtist = artist
                };
            }
            catch { }
            return null;
        }

        private static async Task<AudioMetadata?> FetchFromSoundCloudUrlAsync(Uri uri, CancellationToken ct)
        {
            try
            {
                string oEmbedEndpoint = $"https://soundcloud.com/oembed?url={Uri.EscapeDataString(uri.ToString())}&format=json";
                string json = await _httpClient.GetStringAsync(oEmbedEndpoint, ct);
                var doc = JsonNode.Parse(json);
                if (doc == null) return null;

                string title = doc["title"]?.ToString() ?? "";
                string author = doc["author_name"]?.ToString() ?? "";

                int dash = title.IndexOf(" - ", StringComparison.Ordinal);
                if (dash > 0)
                {
                    author = title.Substring(0, dash).Trim();
                    title = title.Substring(dash + 3).Trim();
                }

                title = CleanQueryForSearch(title);
                if (!string.IsNullOrEmpty(author) && !string.IsNullOrEmpty(title))
                {
                    var enriched = await SearchItunesApiAsync($"{author} {title}", ct, title, author);
                    if (enriched != null) return enriched;

                    var mbEnriched = await SearchMusicBrainzStructuredAsync(author, title, ct);
                    if (mbEnriched != null && !string.IsNullOrEmpty(mbEnriched.Album)) return mbEnriched;
                }

                return new AudioMetadata
                {
                    Title = title,
                    Artist = author,
                    AlbumArtist = author
                };
            }
            catch { }
            return null;
        }
    }

    #endregion
}
