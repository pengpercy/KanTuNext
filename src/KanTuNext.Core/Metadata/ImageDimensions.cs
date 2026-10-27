using System.Buffers.Binary;

namespace KanTuNext.Core.Metadata;

/// <summary>
/// Pixel size from the header of JPEG, PNG, GIF, BMP and WebP, the formats icons and UI graphics come in. Other
/// formats, and anything malformed, give (0, 0): unknown.
/// </summary>
public static class ImageDimensions
{
    public static (int Width, int Height) Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None);
            Span<byte> buffer = stackalloc byte[30];
            var head = buffer[..stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)];
            if (head.Length >= 4 && head[0] == 0xFF && head[1] == 0xD8) return ReadJpeg(stream);
            if (head.Length >= 24 && head[..8].SequenceEqual(MetadataChunks.PngSignature) && head[12..16].SequenceEqual("IHDR"u8))
                return (BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]));
            if (head.Length >= 10 && head[..4].SequenceEqual("GIF8"u8))
                return (BinaryPrimitives.ReadUInt16LittleEndian(head[6..]), BinaryPrimitives.ReadUInt16LittleEndian(head[8..]));
            if (head.Length >= 26 && head[0] == 'B' && head[1] == 'M')
            {
                // OS/2 core header (12 bytes) has 16-bit sizes; the rest 32-bit, height negative when top-down
                if (BinaryPrimitives.ReadInt32LittleEndian(head[14..]) == 12)
                    return (BinaryPrimitives.ReadUInt16LittleEndian(head[18..]), BinaryPrimitives.ReadUInt16LittleEndian(head[20..]));
                return (BinaryPrimitives.ReadInt32LittleEndian(head[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..])));
            }
            if (head.Length >= 30 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return ReadWebP(head);
            return default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return default;
        }
    }

    /// <summary>Hops from segment header to segment header to the first SOFn, which holds height then width.</summary>
    private static (int, int) ReadJpeg(FileStream stream)
    {
        Span<byte> header = stackalloc byte[9];
        long pos = 2;
        for (var i = 0; i < 64 && pos + 9 <= stream.Length; i++)
        {
            stream.Position = pos;
            stream.ReadExactly(header);
            if (header[0] != 0xFF) break;
            var marker = header[1];
            if (marker is 0xD9 or 0xDA) break;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BinaryPrimitives.ReadUInt16BigEndian(header[7..]), BinaryPrimitives.ReadUInt16BigEndian(header[5..]));
            var length = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
            if (length < 2) break;
            pos += 2 + length;
        }
        return default;
    }

    /// <summary>The first chunk says which: lossy VP8 frame header, lossless VP8L bit-packed sizes, or extended VP8X canvas.</summary>
    private static (int, int) ReadWebP(ReadOnlySpan<byte> head)
    {
        var chunk = head[12..16];
        if (chunk.SequenceEqual("VP8 "u8) && head[23] == 0x9D && head[24] == 0x01 && head[25] == 0x2A)
            return (BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFF);
        if (chunk.SequenceEqual("VP8L"u8) && head[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8X"u8))
            return (1 + (head[24] | head[25] << 8 | head[26] << 16), 1 + (head[27] | head[28] << 8 | head[29] << 16));
        return default;
    }
}

/// <summary>What a library scan reads from an image: EXIF capture time and orientation, and pixel size (0 × 0 when unknown).</summary>
public readonly record struct ImageHeader(DateTime? Taken, int Orientation, int Width, int Height)
{
    public static ImageHeader Read(string path)
    {
        var exif = ExifReader.Read(path);
        var (width, height) = ImageDimensions.Read(path);
        return new ImageHeader(exif.Taken, exif.Orientation, width, height);
    }
}
