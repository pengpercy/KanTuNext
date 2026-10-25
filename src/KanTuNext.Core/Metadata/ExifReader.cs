using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace KanTuNext.Core.Metadata;

/// <summary>EXIF fields the timeline and viewer need. Reflection-free and allocation-light, so AOT-safe.</summary>
public readonly record struct ExifInfo(DateTime? Taken, int Orientation);

/// <summary>
/// Minimal EXIF reader for JPEG (APP1 "Exif") and bare TIFF headers: capture time (DateTimeOriginal, then
/// DateTime) and orientation. Anything malformed yields an empty result instead of throwing.
/// </summary>
public static class ExifReader
{
    private const int MaxHeader = 128 * 1024;
    private const ushort TagOrientation = 0x0112, TagDateTime = 0x0132, TagExifIfd = 0x8769, TagDateTimeOriginal = 0x9003;

    /// <summary>
    /// Reads only what it needs: for JPEG it hops from segment header to segment header and loads just the
    /// APP1 (Exif) segment; TIFF reads its first 128 KB; other formats return nothing without reading.
    /// </summary>
    public static ExifInfo Read(string path)
    {
        // Only JPEG and TIFF carry EXIF where this reader looks; don't even open anything else
        if (!HasExifContainer(path)) return default;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
            Span<byte> head = stackalloc byte[4];
            if (stream.Read(head) < 4) return default;
            if (head[0] == 0xFF && head[1] == 0xD8) return ReadJpeg(stream);
            if ((head[0] == 'I' && head[1] == 'I') || (head[0] == 'M' && head[1] == 'M'))
            {
                var buffer = new byte[(int)Math.Min(MaxHeader, stream.Length)];
                stream.Position = 0;
                stream.ReadExactly(buffer);
                return ParseTiff(buffer);
            }
            return default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return default;
        }
    }

    private static bool HasExifContainer(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".tif" or ".tiff";

    private static ExifInfo ReadJpeg(FileStream stream)
    {
        Span<byte> header = stackalloc byte[4];
        long pos = 2;
        for (var i = 0; i < 32 && pos + 4 <= stream.Length; i++)
        {
            stream.Position = pos;
            stream.ReadExactly(header);
            if (header[0] != 0xFF) break;
            var marker = header[1];
            if (marker is 0xD9 or 0xDA) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
            if (length < 2) break;
            if (marker == 0xE1)
            {
                var segment = new byte[length - 2];
                stream.ReadExactly(segment);
                if (segment.AsSpan().StartsWith("Exif\0\0"u8)) return ParseTiff(segment.AsSpan(6));
            }
            pos += 2 + length;
        }
        return default;
    }

    public static ExifInfo Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return default;
        if (data[0] == 0xFF && data[1] == 0xD8) return ParseJpeg(data);
        if ((data[0] == 'I' && data[1] == 'I') || (data[0] == 'M' && data[1] == 'M')) return ParseTiff(data);
        return default;
    }

    private static ExifInfo ParseJpeg(ReadOnlySpan<byte> data)
    {
        var pos = 2;
        while (pos + 4 <= data.Length && data[pos] == 0xFF)
        {
            var marker = data[pos + 1];
            if (marker is 0xD9 or 0xDA) break; // end of image / start of scan: no metadata past here
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            if (length < 2 || pos + 2 + length > data.Length) break;
            var segment = data.Slice(pos + 4, length - 2);
            if (marker == 0xE1 && segment.StartsWith("Exif\0\0"u8)) return ParseTiff(segment[6..]);
            pos += 2 + length;
        }
        return default;
    }

    private static ExifInfo ParseTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return default;
        var little = tiff[0] == 'I';
        DateTime? original = null, modified = null;
        var orientation = 1;
        uint exifIfd = 0;
        ReadIfd(tiff, little, U32(tiff, 4, little), false, ref original, ref modified, ref orientation, ref exifIfd);
        ReadIfd(tiff, little, exifIfd, true, ref original, ref modified, ref orientation, ref exifIfd);
        return new ExifInfo(original ?? modified, orientation);
    }

    private static void ReadIfd(ReadOnlySpan<byte> tiff, bool little, uint offset, bool isExif,
        ref DateTime? original, ref DateTime? modified, ref int orientation, ref uint exifIfd)
    {
        if (offset == 0 || offset + 2 > tiff.Length) return;
        int count = U16(tiff, (int)offset, little);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)offset + 2 + i * 12;
            if (entry + 12 > tiff.Length) return;
            var tag = U16(tiff, entry, little);
            var type = U16(tiff, entry + 2, little);
            switch (tag)
            {
                case TagOrientation when !isExif && type == 3:
                    orientation = U16(tiff, entry + 8, little) is var o and >= 1 and <= 8 ? o : 1;
                    break;
                case TagDateTime when !isExif:
                    modified = ReadDate(tiff, (int)U32(tiff, entry + 8, little));
                    break;
                case TagExifIfd when !isExif:
                    exifIfd = U32(tiff, entry + 8, little);
                    break;
                case TagDateTimeOriginal when isExif:
                    original = ReadDate(tiff, (int)U32(tiff, entry + 8, little));
                    break;
            }
        }
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt16BigEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(s[at..]) : BinaryPrimitives.ReadUInt32BigEndian(s[at..]);

    /// <summary>EXIF dates are 20 ASCII bytes "YYYY:MM:DD HH:MM:SS\0"; zero-filled placeholders are ignored.</summary>
    private static DateTime? ReadDate(ReadOnlySpan<byte> tiff, int offset)
    {
        if (offset < 0 || offset + 19 > tiff.Length) return null;
        var text = Encoding.ASCII.GetString(tiff.Slice(offset, 19));
        return DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var value) ? value : null;
    }
}
