using System.Text.Json;
using System.Text.Json.Serialization;

namespace KanTuNext.Core.Library;

/// <summary>One photo deleted from inside the app: where it was, and where the trash put it.</summary>
public sealed record DeletedItem(string OriginalPath, string TrashedPath, DateTime DeletedAt);

/// <summary>
/// The 最近删除 list: photos the app moved to the trash, kept while they're still there (deleted.json).
/// Only trashes that report where an item went can be listed (macOS, Linux); Windows' recycle bin doesn't.
/// </summary>
public sealed class DeletedStore
{
    private readonly object _gate = new();
    private readonly List<DeletedItem> _items;
    private readonly string _file;

    private DeletedStore(string file, List<DeletedItem> items)
    {
        _file = file;
        _items = items;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "deleted.json");

    public event Action? Changed;

    public static DeletedStore Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize(File.ReadAllText(file), DeletedJson.Default.ListDeletedItem) is { } items)
                return new DeletedStore(file, items);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new DeletedStore(file, []);
    }

    public void Add(DeletedItem item)
    {
        lock (_gate)
        {
            _items.Add(item);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Items whose trashed file still exists (emptying the trash or restoring elsewhere drops them), newest first.</summary>
    public IReadOnlyList<DeletedItem> Present()
    {
        lock (_gate)
        {
            var gone = _items.RemoveAll(i => !File.Exists(i.TrashedPath));
            if (gone > 0) Save();
            return _items.OrderByDescending(i => i.DeletedAt).ToArray();
        }
    }

    /// <summary>
    /// Moves a trashed photo back to where it was, under a free name if that path is taken now
    /// ("a.jpg" → "a 2.jpg"). Returns the restored path, or null if it couldn't be moved.
    /// </summary>
    public string? Restore(string trashedPath)
    {
        DeletedItem? item;
        lock (_gate) item = _items.FirstOrDefault(i => i.TrashedPath == trashedPath);
        if (item is null || !File.Exists(item.TrashedPath)) return null;
        var target = FreePath(item.OriginalPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(item.TrashedPath, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        lock (_gate)
        {
            _items.Remove(item);
            Save();
        }
        Changed?.Invoke();
        return target;
    }

    internal static string FreePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} {i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, DeletedJson.Default.ListDeletedItem));
            File.Move(temp, _file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<DeletedItem>))]
internal sealed partial class DeletedJson : JsonSerializerContext;
