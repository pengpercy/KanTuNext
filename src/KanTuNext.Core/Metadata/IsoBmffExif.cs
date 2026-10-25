using System.Buffers.Binary;

namespace KanTuNext.Core.Metadata;

/// <summary>
/// Finds the EXIF block in ISO-BMFF images: HEIC/HEIF and AVIF (an "Exif" item listed in meta/iinf and located by
/// meta/iloc) and JPEG XL containers (a top-level "Exif" box). Both payloads start with a 4-byte offset to the
/// TIFF header. Reads only box headers, the meta box and the EXIF bytes themselves.
/// </summary>
internal static class IsoBmffExif
{
    private const int MaxMeta = 4 * 1024 * 1024;
    private const int MaxExif = 1024 * 1024;

    /// <summary>The TIFF bytes of the EXIF block, or null.</summary>
    public static byte[]? Read(Stream stream)
    {
        long pos = 0, end = stream.Length;
        Span<byte> header = stackalloc byte[16];
        for (var i = 0; i < 64 && pos + 8 <= end; i++)
        {
            stream.Position = pos;
            if (stream.Read(header[..8]) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header.Slice(4, 4);
            var headerSize = 8;
            if (size == 1)
            {
                if (stream.Read(header.Slice(8, 8)) < 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < headerSize || pos + size > end) return null;

            if (type.SequenceEqual("meta"u8) && size - headerSize <= MaxMeta)
            {
                var meta = new byte[size - headerSize];
                stream.ReadExactly(meta);
                return FromMeta(stream, meta);
            }
            if (type.SequenceEqual("Exif"u8) && size - headerSize <= MaxExif)
            {
                var payload = new byte[size - headerSize];
                stream.ReadExactly(payload);
                return Tiff(payload);
            }
            pos += size;
        }
        return null;
    }

    /// <summary>meta is a FullBox: 4 bytes of version/flags, then child boxes.</summary>
    private static byte[]? FromMeta(Stream stream, byte[] meta)
    {
        uint? exifItem = null;
        (long Offset, long Length)? location = null;
        ReadOnlySpan<byte> iloc = default;
        var children = meta.AsSpan(4);
        while (children.Length >= 8)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(children), (uint)children.Length);
            if (size < 8) break;
            var type = children.Slice(4, 4);
            var body = children[8..size];
            if (type.SequenceEqual("iinf"u8)) exifItem = FindExifItem(body);
            else if (type.SequenceEqual("iloc"u8)) iloc = body;
            children = children[size..];
        }
        if (exifItem is not { } id || iloc.IsEmpty) return null;
        location = Locate(iloc, id);
        if (location is not { } loc || loc.Length <= 4 || loc.Length > MaxExif || loc.Offset + loc.Length > stream.Length) return null;

        var payload = new byte[loc.Length];
        stream.Position = loc.Offset;
        stream.ReadExactly(payload);
        return Tiff(payload);
    }

    /// <summary>The item id whose infe (version 2/3) has item_type "Exif".</summary>
    private static uint? FindExifItem(ReadOnlySpan<byte> iinf)
    {
        if (iinf.Length < 6) return null;
        var version = iinf[0];
        var entries = version == 0 ? 6 : 8; // FullBox header + u16 or u32 entry count
        var rest = iinf[entries..];
        while (rest.Length >= 8)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(rest), (uint)rest.Length);
            if (size < 8) break;
            if (rest.Slice(4, 4).SequenceEqual("infe"u8))
            {
                var infe = rest[8..size];
                var v = infe[0];
                if (v >= 2)
                {
                    var idSize = v == 2 ? 2 : 4;
                    if (infe.Length >= 4 + idSize + 2 + 4)
                    {
                        var id = idSize == 2 ? BinaryPrimitives.ReadUInt16BigEndian(infe[4..]) : BinaryPrimitives.ReadUInt32BigEndian(infe[4..]);
                        if (infe.Slice(4 + idSize + 2, 4).SequenceEqual("Exif"u8)) return id;
                    }
                }
            }
            rest = rest[size..];
        }
        return null;
    }

    /// <summary>First extent of the item in iloc (construction method 0: a file offset).</summary>
    private static (long, long)? Locate(ReadOnlySpan<byte> iloc, uint item)
    {
        if (iloc.Length < 8) return null;
        var version = iloc[0];
        int offsetSize = iloc[4] >> 4, lengthSize = iloc[4] & 15, baseSize = iloc[5] >> 4;
        var indexSize = version is 1 or 2 ? iloc[5] & 15 : 0;
        var p = 6;
        uint count = version < 2 ? Read(iloc, ref p, 2) : Read(iloc, ref p, 4);
        for (var i = 0; i < count && p < iloc.Length; i++)
        {
            var id = version < 2 ? Read(iloc, ref p, 2) : Read(iloc, ref p, 4);
            var method = 0u;
            if (version is 1 or 2) method = Read(iloc, ref p, 2) & 15;
            p += 2; // data_reference_index
            var baseOffset = ReadLong(iloc, ref p, baseSize);
            var extents = Read(iloc, ref p, 2);
            long first = -1, firstLength = 0;
            for (var e = 0; e < extents; e++)
            {
                if (indexSize > 0) ReadLong(iloc, ref p, indexSize);
                var offset = ReadLong(iloc, ref p, offsetSize);
                var length = ReadLong(iloc, ref p, lengthSize);
                if (e == 0) (first, firstLength) = (offset, length);
            }
            if (id == item) return method == 0 && first >= 0 ? (baseOffset + first, firstLength) : null;
        }
        return null;
    }

    private static uint Read(ReadOnlySpan<byte> s, ref int p, int size) => (uint)ReadLong(s, ref p, size);

    private static long ReadLong(ReadOnlySpan<byte> s, ref int p, int size)
    {
        if (size == 0) return 0;
        if (p + size > s.Length) throw new EndOfStreamException();
        long value = 0;
        for (var i = 0; i < size; i++) value = (value << 8) | s[p + i];
        p += size;
        return value;
    }

    /// <summary>Exif payloads begin with a big-endian offset from byte 4 to the TIFF header.</summary>
    private static byte[]? Tiff(byte[] payload)
    {
        if (payload.Length < 8) return null;
        var start = 4 + (long)BinaryPrimitives.ReadUInt32BigEndian(payload);
        return start < payload.Length - 8 ? payload[(int)start..] : null;
    }
}
