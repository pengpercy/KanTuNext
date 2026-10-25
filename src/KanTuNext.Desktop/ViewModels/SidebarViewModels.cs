using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using KanTuNext.Core.Library;

namespace KanTuNext.Desktop.ViewModels;

public sealed class SidebarSection(string header, List<SidebarItem> items, bool canAdd = false)
{
    public string Header { get; } = header;
    public bool CanAdd { get; } = canAdd;
    public System.Collections.ObjectModel.ObservableCollection<SidebarItem> Items { get; } = new(items);
}

public sealed partial class SidebarItem : ObservableObject
{
    private readonly Action<SidebarItem> _selected;

    public SidebarItem(LibraryCategory category, string title, string icon, Action<SidebarItem> selected,
        bool expandable = false, string? path = null)
    {
        Category = category;
        Title = title;
        Icon = LookupIcon(icon);
        Expandable = expandable;
        Path = path;
        _selected = selected;
    }

    public LibraryCategory Category { get; }
    public string Title { get; }
    public Geometry? Icon { get; }
    public bool Expandable { get; }
    /// <summary>Folder path for <see cref="LibraryCategory.Folder"/> items.</summary>
    public string? Path { get; }

    [ObservableProperty] public partial string Count { get; set; } = "";
    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _selected(this);
    }

    private static Geometry? LookupIcon(string name) =>
        Application.Current?.TryGetResource("Icon." + name, null, out var value) == true ? value as Geometry : null;
}
