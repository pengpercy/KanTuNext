using KanTuNext.Core.Library;

namespace KanTuNext.Tests;

public sealed class TagStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kantunext-tags-").FullName;
    private string File => Path.Combine(_dir, "tags.json");

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ToggleSetsAndClearsAndPersists()
    {
        var store = TagStore.Load(File);
        Assert.Equal(PhotoTag.Good, store.Toggle("/a.jpg", PhotoTag.Good));
        Assert.Equal(PhotoTag.Bad, store.Toggle("/b.jpg", PhotoTag.Bad));
        Assert.Equal(PhotoTag.None, store.Toggle("/b.jpg", PhotoTag.Bad));

        var reloaded = TagStore.Load(File);
        Assert.Equal(PhotoTag.Good, reloaded.Get("/a.jpg"));
        Assert.Equal(PhotoTag.None, reloaded.Get("/b.jpg"));
        Assert.Equal(["/a.jpg"], reloaded.PathsWith(PhotoTag.Good));
        Assert.Contains("\"Good\"", System.IO.File.ReadAllText(File));
    }

    [Fact]
    public void MoveFollowsRenameAndForgetsDeletes()
    {
        var store = TagStore.Load(File);
        store.Toggle("/a.jpg", PhotoTag.Neutral);
        store.Move("/a.jpg", "/renamed.jpg");
        Assert.Equal(PhotoTag.Neutral, store.Get("/renamed.jpg"));
        store.Move("/renamed.jpg", null);
        Assert.Equal(0, store.Count(PhotoTag.Neutral));
    }

    [Fact]
    public void DamagedFileStartsEmpty()
    {
        System.IO.File.WriteAllText(File, "{not json");
        Assert.Equal(PhotoTag.None, TagStore.Load(File).Get("/a.jpg"));
    }
}
