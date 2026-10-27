using KanTuNext.Core.Metadata;

namespace KanTuNext.Core.Library;

/// <summary>Finds images and videos under a folder and reads their capture dates.</summary>
public static class FolderScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Code, build output and dependencies: their images are app icons and UI graphics, not photos.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", "bin", "obj", "build", "dist", "out", "target", "Pods", "Carthage",
        "DerivedData", "vendor", "venv", "site-packages", "__pycache__",
    };

    /// <summary>Photos libraries, apps and other bundles are folders with a dot-extension; their contents are private.</summary>
    private static readonly HashSet<string> Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".photoslibrary", ".photolibrary", ".app", ".appex", ".bundle", ".framework", ".plugin",
        ".xcassets", ".iconset", ".xcodeproj", ".xcworkspace",
    };

    /// <summary>
    /// Lists media under <paramref name="folder"/> (recursively, skipping hidden files, bundles' insides and
    /// <see cref="SkippedFolders"/>), then reads image headers in parallel. Cancellation stops both phases.
    /// </summary>
    public static IReadOnlyList<MediaFile> Scan(string folder, CancellationToken cancel = default, MetadataCache? cache = null)
    {
        if (!Directory.Exists(folder)) return [];
        var files = new List<MediaFile>();
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(folder)]);
        while (pending.TryPop(out var dir))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                foreach (var info in dir.EnumerateFiles("*", Options))
                    if (MediaTypes.KindOf(info.Name) is { } kind) files.Add(new MediaFile(info.FullName, kind, info.Length, info.LastWriteTime));
                // Links are not followed (as recursive enumeration never did): they can loop or lead out of the folder
                foreach (var sub in dir.EnumerateDirectories("*", Options))
                    if (!IsSkipped(sub.Name) && sub.LinkTarget is null) pending.Push(sub);
            }
            catch (IOException)
            {
                // Removed or unmounted while scanning
            }
        }

        files = PairLivePhotos(files);
        var result = new MediaFile[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var file = files[i];
            if (file.Kind == MediaKind.Image)
                file = file.With(cache?.GetOrRead(file.Path, file.Modified, file.Length) ?? ImageHeader.Read(file.Path));
            result[i] = file;
        });
        return result;
    }

    /// <summary>
    /// Live Photos arrive as a still (HEIC/JPG) and a .mov with the same name in the same folder (AirDrop, Image
    /// Capture, iCloud exports). The pair shows as one item: the still, marked live, carrying its clip.
    /// </summary>
    internal static List<MediaFile> PairLivePhotos(List<MediaFile> files)
    {
        static string Stem(MediaFile f) => Path.Combine(Path.GetDirectoryName(f.Path) ?? "", Path.GetFileNameWithoutExtension(f.Path));
        var clips = files.Where(f => f.Kind == MediaKind.Video && Path.GetExtension(f.Path).Equals(".mov", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Stem, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (clips.Count == 0) return files;

        var paired = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MediaFile>(files.Count);
        foreach (var file in files)
        {
            if (file.Kind == MediaKind.Image && Path.GetExtension(file.Path).ToLowerInvariant() is ".heic" or ".heif" or ".jpg" or ".jpeg"
                && clips.TryGetValue(Stem(file), out var clip))
            {
                result.Add(file with { LiveVideo = clip.Path });
                paired.Add(clip.Path);
            }
            else result.Add(file);
        }
        result.RemoveAll(f => paired.Contains(f.Path));
        return result;
    }

    private static bool IsSkipped(string name) =>
        SkippedFolders.Contains(name) || Packages.Contains(Path.GetExtension(name))
        // Android resources: drawable-hdpi, mipmap-xxhdpi…
        || name.StartsWith("drawable", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mipmap", StringComparison.OrdinalIgnoreCase);
}
