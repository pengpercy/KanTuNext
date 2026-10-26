using System.Text.Json;
using System.Text.Json.Serialization;

namespace KanTuNext.Core.Settings;

/// <summary>User preferences, stored as settings.json in <see cref="AppPaths.DataDirectory"/>.</summary>
public sealed class AppSettings
{
    /// <summary>Folders added with 「＋」 under 常用文件夹 (图片 and 下载 are always there).</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>The folder open when the app last quit, reopened on launch.</summary>
    public string? LastFolder { get; set; }

    /// <summary>小/中/大/最大, as last chosen.</summary>
    public Library.ThumbnailSize ThumbnailSize { get; set; } = Library.ThumbnailSize.Small;

    /// <summary>Follow the system, or force light/dark.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>The main window's size and maximized state when the app last quit (it always opens centred).</summary>
    public WindowPlacement? MainWindow { get; set; }

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged or unreadable file falls back to defaults rather than blocking startup
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        File.Move(temp, FilePath, true);
    }
}

/// <summary>
/// A window's normal (restored) size in DIPs, plus whether it was maximized. The normal size is kept while
/// maximized, so un-maximizing after a restart returns to it.
/// </summary>
public sealed class WindowPlacement
{
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
