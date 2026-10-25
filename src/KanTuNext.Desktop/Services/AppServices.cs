using KanTuNext.Core.Library;

namespace KanTuNext.Desktop.Services;

/// <summary>Process-wide stores shared by the main window and every viewer window.</summary>
internal static class AppServices
{
    public static TagStore Tags { get; } = TagStore.Load(TagStore.DefaultPath);
}
