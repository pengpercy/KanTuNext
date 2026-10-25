using KanTuNext.Core.Library;

namespace KanTuNext.Tests;

public sealed class DeletedStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kantunext-deleted-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>Joined per segment, so the separators match what Path.Combine produces on every OS.</summary>
    private string P(params string[] parts) => Path.Combine([_dir, .. parts]);

    [Fact]
    public void ListsTrashedFilesAndRestoresUnderAFreeName()
    {
        Directory.CreateDirectory(P("trash"));
        File.WriteAllText(P("trash", "a.jpg"), "x");
        var store = DeletedStore.Load(P("deleted.json"));
        store.Add(new DeletedItem(P("photos", "a.jpg"), P("trash", "a.jpg"), DateTime.Now));

        Assert.Single(DeletedStore.Load(P("deleted.json")).Present());

        // Something else now lives at the original path: restore beside it instead of overwriting
        Directory.CreateDirectory(P("photos"));
        File.WriteAllText(P("photos", "a.jpg"), "new");
        var restored = store.Restore(P("trash", "a.jpg"));
        Assert.Equal(P("photos", "a 2.jpg"), restored);
        Assert.Equal("x", File.ReadAllText(restored!));
        Assert.Empty(store.Present());
    }

    [Fact]
    public void ForgetsItemsNoLongerInTheTrash()
    {
        var store = DeletedStore.Load(P("deleted.json"));
        store.Add(new DeletedItem(P("a.jpg"), P("gone.jpg"), DateTime.Now));
        Assert.Empty(store.Present());
    }
}
