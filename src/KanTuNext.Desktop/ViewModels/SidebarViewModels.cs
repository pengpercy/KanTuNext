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
        bool expandable = false, string? path = null, int depth = 0)
    {
        Depth = depth;
        Category = category;
        Title = title;
        Icon = LookupIcon(icon);
        Expandable = expandable;
        Path = path;
        _selected = selected;
    }

    public LibraryCategory Category { get; }

    /// <summary>Nesting level under 常用文件夹 (subfolders are listed right after their parent).</summary>
    public int Depth { get; }
    public Avalonia.Thickness Indent => new(Depth * 14, 0, 0, 0);

    /// <summary>The subfolder rows currently shown under this one.</summary>
    public List<SidebarItem> Children { get; } = [];
    public string Title { get; }
    public Geometry? Icon { get; }
    public bool Expandable { get; }
    /// <summary>Folder path for <see cref="LibraryCategory.Folder"/> items.</summary>
    public string? Path { get; }

    [ObservableProperty] public partial string Count { get; set; } = "";
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronAngle))]
    public partial bool IsExpanded { get; set; }

    /// <summary>The disclosure chevron points right, or down when expanded.</summary>
    public double ChevronAngle => IsExpanded ? 90 : 0;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _selected(this);
    }

    private static Geometry? LookupIcon(string name) =>
        Application.Current?.TryGetResource("Icon." + name, null, out var value) == true ? value as Geometry : null;
}
