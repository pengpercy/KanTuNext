using KanTuNext.Core.Metadata;

namespace KanTuNext.Core.Library;

/// <summary>Finds images and videos under a folder and reads their capture dates.</summary>
public static class FolderScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Lists media under <paramref name="folder"/> (recursively, skipping hidden files and macOS bundles'
    /// insides), then reads EXIF for images in parallel. Cancellation stops both phases.
    /// </summary>
    public static IReadOnlyList<MediaFile> Scan(string folder, CancellationToken cancel = default, MetadataCache? cache = null)
    {
        if (!Directory.Exists(folder)) return [];
        var files = new List<MediaFile>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFiles("*", Options))
        {
            cancel.ThrowIfCancellationRequested();
            if (MediaTypes.KindOf(info.Name) is not { } kind) continue;
            if (IsInsidePackage(info.FullName, folder)) continue;
            files.Add(new MediaFile(info.FullName, kind, info.Length, info.LastWriteTime));
        }

        var result = new MediaFile[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var file = files[i];
            if (file.Kind == MediaKind.Image)
            {
                if (cache is null || !cache.TryGet(file.Path, file.Modified, file.Length, out var exif))
                {
                    exif = ExifReader.Read(file.Path);
                    cache?.Set(file.Path, file.Modified, file.Length, exif);
                }
                file = file with { Taken = exif.Taken, Orientation = exif.Orientation };
            }
            result[i] = file;
        });
        return result;
    }

    /// <summary>Photos libraries, apps and other bundles are folders with a dot-extension; their contents are private.</summary>
    private static bool IsInsidePackage(string path, string root)
    {
        var dir = Path.GetDirectoryName(path);
        while (dir is not null && dir.Length > root.Length)
        {
            var ext = Path.GetExtension(dir);
            if (ext is ".photoslibrary" or ".app" or ".bundle" or ".framework" or ".photolibrary") return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }
}
