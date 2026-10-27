using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace KanTuNext.Desktop.Services;

/// <summary>Right-click menu rows built one way everywhere (styled in Resources/Styles.axaml).</summary>
internal static class Menus
{
    /// <summary>The platform's command key: ⌘ on macOS, Ctrl elsewhere.</summary>
    public static KeyModifiers Command => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public static string RevealText =>
        OperatingSystem.IsMacOS() ? "在访达中显示" : OperatingSystem.IsWindows() ? "在资源管理器中显示" : "打开所在文件夹";

    /// <summary>A row with a 13px MDI icon (a 12px check instead when checked) and its shortcut on the right.</summary>
    public static MenuItem Item(Control owner, string header, string? icon, Action action, KeyGesture? gesture = null,
        bool isChecked = false)
    {
        var item = new MenuItem { Header = header, InputGesture = gesture };
        if (isChecked) item.Icon = new PathIcon { Data = (Geometry)owner.FindResource("Icon.check")!, Width = 12, Height = 12 };
        else if (icon is not null) item.Icon = new PathIcon { Data = (Geometry)owner.FindResource("Icon." + icon)!, Width = 13, Height = 13 };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Opens a right-click menu of <paramref name="items"/> on <paramref name="target"/>, over frosted glass
    /// where the platform can blur behind it (<see cref="Glass"/>).</summary>
    public static void Open(Control target, IEnumerable<object> items)
    {
        var menu = new ContextMenu { ItemsSource = items };
        Glass.Attach(menu);
        menu.Open(target);
    }
}
