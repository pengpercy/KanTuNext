using System.Text.Json;
using System.Text.Json.Serialization;

namespace KanTuNext.Core.Library;

/// <summary>The 好/中/差 marks the original lets you put on a photo.</summary>
public enum PhotoTag
{
    None = 0,
    Good = 1,
    Neutral = 2,
    Bad = 3,
}

/// <summary>Photo marks by path, stored as tags.json next to the settings. Thread-safe.</summary>
public sealed class TagStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PhotoTag> _tags;
    private readonly string _file;

    private TagStore(string file, Dictionary<string, PhotoTag> tags)
    {
        _file = file;
        _tags = tags;
    }

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "tags.json");

    /// <summary>Raised after a mark changes (sidebar counts, toolbar state).</summary>
    public event Action? Changed;

    public static TagStore Load(string file)
    {
        try
        {
            if (File.Exists(file) &&
                JsonSerializer.Deserialize(File.ReadAllText(file), TagJson.Default.DictionaryStringPhotoTag) is { } tags)
                return new TagStore(file, new Dictionary<string, PhotoTag>(tags, StringComparer.Ordinal));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new TagStore(file, new Dictionary<string, PhotoTag>(StringComparer.Ordinal));
    }

    public PhotoTag Get(string path)
    {
        lock (_gate) return _tags.GetValueOrDefault(path);
    }

    /// <summary>Sets a mark; setting the mark a photo already has clears it (the toolbar buttons toggle).</summary>
    public PhotoTag Toggle(string path, PhotoTag tag)
    {
        lock (_gate)
        {
            var next = _tags.GetValueOrDefault(path) == tag ? PhotoTag.None : tag;
            if (next == PhotoTag.None) _tags.Remove(path);
            else _tags[path] = next;
            Save();
            Changed?.Invoke();
            return next;
        }
    }

    /// <summary>Follows a file that moved (rename) or forgets one that's gone (delete: <paramref name="to"/> null).</summary>
    public void Move(string from, string? to)
    {
        lock (_gate)
        {
            if (!_tags.Remove(from, out var tag)) return;
            if (to is not null) _tags[to] = tag;
            Save();
            Changed?.Invoke();
        }
    }

    public IReadOnlyList<string> PathsWith(PhotoTag tag)
    {
        lock (_gate) return _tags.Where(kv => kv.Value == tag).Select(kv => kv.Key).ToArray();
    }

    public int Count(PhotoTag tag)
    {
        lock (_gate) return _tags.Count(kv => kv.Value == tag);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_tags, TagJson.Default.DictionaryStringPhotoTag));
            File.Move(temp, _file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Marks stay in memory for this session; a later change retries the write
        }
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true, WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, PhotoTag>))]
internal sealed partial class TagJson : JsonSerializerContext;
