using System.Collections.Concurrent;

namespace KanTuNext.Core.Metadata;

/// <summary>
/// EXIF results keyed by path, valid while the file's mtime and length are unchanged, so a rescan reads no
/// file contents. Stored as a small binary file; a missing, damaged or older-format file just starts empty.
/// </summary>
public sealed class MetadataCache
{
    private const int Magic = 0x434D544B; // "KTMC"
    private const int Version = 2; // 2: HEIC/AVIF/JPEG XL capture dates are read now

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private volatile bool _dirty;

    private readonly record struct Entry(long Modified, long Length, long Taken, byte Orientation);

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "metadata.bin");

    public bool TryGet(string path, DateTime modified, long length, out ExifInfo info)
    {
        if (_entries.TryGetValue(path, out var e) && e.Modified == modified.Ticks && e.Length == length)
        {
            info = new ExifInfo(e.Taken == 0 ? null : new DateTime(e.Taken), e.Orientation);
            return true;
        }
        info = default;
        return false;
    }

    public void Set(string path, DateTime modified, long length, ExifInfo info)
    {
        _entries[path] = new Entry(modified.Ticks, length, info.Taken?.Ticks ?? 0, (byte)info.Orientation);
        _dirty = true;
    }

    public static MetadataCache Load(string file)
    {
        var cache = new MetadataCache();
        try
        {
            if (!File.Exists(file)) return cache;
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) return cache;
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var path = reader.ReadString();
                cache._entries[path] = new Entry(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadByte());
            }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException or FormatException)
        {
            cache._entries.Clear();
        }
        return cache;
    }

    /// <summary>Writes the cache if anything changed since it was loaded or last saved.</summary>
    public void Save(string file)
    {
        if (!_dirty) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp)))
        {
            var snapshot = _entries.ToArray();
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(snapshot.Length);
            foreach (var (path, e) in snapshot)
            {
                writer.Write(path);
                writer.Write(e.Modified);
                writer.Write(e.Length);
                writer.Write(e.Taken);
                writer.Write(e.Orientation);
            }
        }
        File.Move(temp, file, true);
        _dirty = false;
    }
}
