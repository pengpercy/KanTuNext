using KanTuNext.Core.Library;

namespace KanTuNext.Desktop.Services;

/// <summary>Process-wide stores shared by the main window and every viewer window.</summary>
internal static class AppServices
{
    public static TagStore Tags { get; } = TagStore.Load(TagStore.DefaultPath);
    public static DeletedStore Deleted { get; } = DeletedStore.Load(DeletedStore.DefaultPath);

    /// <summary>Moves a photo to the trash and remembers where it went (最近删除); forgets its mark.</summary>
    public static bool Delete(string path)
    {
        if (!Trash.MoveToTrash(path, out var trashed)) return false;
        if (trashed is not null) Deleted.Add(new DeletedItem(path, trashed, DateTime.Now));
        Tags.Move(path, null);
        return true;
    }
}
