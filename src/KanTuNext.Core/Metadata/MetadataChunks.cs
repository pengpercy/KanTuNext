using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace KanTuNext.Core.Metadata;

/// <summary>
/// Finds metadata blocks in PNG and WebP chunk streams, and the embedded ICC colour profile of JPEG, PNG and
/// WebP files. Hops from chunk header to chunk header; only the wanted chunk's bytes are read.
/// </summary>
public static class MetadataChunks
{
    private const int MaxChunk = 4 * 1024 * 1024;

    public static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The data of the first PNG chunk of <paramref name="type"/>, or null. The stream is positioned anywhere.</summary>
    public static byte[]? FindPng(Stream stream, ReadOnlySpan<byte> type)
    {
        Span<byte> header = stackalloc byte[8];
        long pos = 8;
        for (var i = 0; i < 4096 && pos + 8 <= stream.Length; i++)
        {
            stream.Position = pos;
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (header[4..].SequenceEqual(type)) return length <= MaxChunk ? ReadBytes(stream, (int)length) : null;
            if (header[4..].SequenceEqual("IEND"u8)) return null;
            pos += 12 + length; // length, type, data, CRC
        }
        return null;
    }

    /// <summary>The data of the first WebP (RIFF) chunk with this FourCC, or null.</summary>
    public static byte[]? FindWebP(Stream stream, ReadOnlySpan<byte> fourCc)
    {
        Span<byte> header = stackalloc byte[8];
        long pos = 12;
        for (var i = 0; i < 256 && pos + 8 <= stream.Length; i++)
        {
            stream.Position = pos;
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (header[..4].SequenceEqual(fourCc)) return length <= MaxChunk ? ReadBytes(stream, (int)length) : null;
            pos += 8 + length + (length & 1); // chunks are padded to even sizes
        }
        return null;
    }

    /// <summary>The embedded ICC profile (JPEG APP2 "ICC_PROFILE", PNG iCCP, WebP ICCP), or null.</summary>
    public static byte[]? ReadIcc(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
            Span<byte> head = stackalloc byte[12];
            var read = stream.Read(head);
            if (read >= 2 && head[0] == 0xFF && head[1] == 0xD8) return JpegIcc(stream);
            if (read >= 8 && head[..8].SequenceEqual(PngSignature))
                return FindPng(stream, "iCCP"u8) is { } iccp ? Inflate(iccp) : null;
            if (read >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
                return FindWebP(stream, "ICCP"u8);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A JPEG profile may span several APP2 segments, each tagged with its sequence number.</summary>
    private static byte[]? JpegIcc(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        var parts = new SortedDictionary<int, byte[]>();
        long pos = 2;
        for (var i = 0; i < 64 && pos + 4 <= stream.Length; i++)
        {
            stream.Position = pos;
            stream.ReadExactly(header);
            if (header[0] != 0xFF) break;
            var marker = header[1];
            if (marker is 0xD9 or 0xDA) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
            if (length < 2) break;
            if (marker == 0xE2 && length > 16)
            {
                var segment = ReadBytes(stream, length - 2);
                if (segment.AsSpan().StartsWith("ICC_PROFILE\0"u8)) parts[segment[12]] = segment[14..];
            }
            pos += 2 + length;
        }
        return parts.Count == 0 ? null : parts.Values.SelectMany(p => p).ToArray();
    }

    /// <summary>iCCP: profile name, NUL, compression method, then the zlib-compressed profile.</summary>
    private static byte[]? Inflate(byte[] iccp)
    {
        var nul = Array.IndexOf(iccp, (byte)0);
        if (nul < 0 || nul + 2 > iccp.Length) return null;
        using var input = new MemoryStream(iccp, nul + 2, iccp.Length - nul - 2);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// The profile's human-readable name ("Display P3", "sRGB IEC61966-2.1"…) from its 'desc' tag: ICC v2
    /// textDescriptionType (ASCII) or v4 multiLocalizedUnicodeType (UTF-16BE, first record).
    /// </summary>
    public static string? IccDescription(ReadOnlySpan<byte> icc)
    {
        if (icc.Length < 132) return null;
        var count = BinaryPrimitives.ReadUInt32BigEndian(icc[128..]);
        for (var i = 0; i < count && 132 + i * 12 + 12 <= icc.Length; i++)
        {
            var entry = icc.Slice(132 + i * 12, 12);
            if (!entry[..4].SequenceEqual("desc"u8)) continue;
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            if (offset < 0 || size < 12 || offset + size > icc.Length) return null;
            var tag = icc.Slice(offset, size);
            if (tag.StartsWith("desc"u8))
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                if (length <= 0 || 12 + length > tag.Length) return null;
                return Encoding.ASCII.GetString(tag.Slice(12, length)).TrimEnd('\0').Trim();
            }
            if (tag.StartsWith("mluc"u8) && tag.Length >= 28)
            {
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[20..]);
                var start = (int)BinaryPrimitives.ReadUInt32BigEndian(tag[24..]);
                if (start < 0 || length <= 0 || start + length > tag.Length) return null;
                return Encoding.BigEndianUnicode.GetString(tag.Slice(start, length)).TrimEnd('\0').Trim();
            }
            return null;
        }
        return null;
    }
}
