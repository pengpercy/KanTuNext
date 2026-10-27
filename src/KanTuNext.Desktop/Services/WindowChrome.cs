using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.VisualTree;

namespace KanTuNext.Desktop.Services;

/// <summary>Fixes for Avalonia 12's drawn title bar (WindowDrawnDecorations) across platforms. From VodBox.</summary>
internal static class WindowChrome
{
    /// <summary>Width reserved for the drawn min/max/close buttons (3 × 46).</summary>
    public const double CaptionButtonsWidth = 138;

    /// <summary>
    /// Whether Avalonia draws the title bar on Linux (otherwise the window manager does): on by default on every
    /// desktop (GNOME, KDE, Xfce…), off with 设置 → 使用自绘标题栏. Fixed at startup because X11 options are;
    /// KANTUNEXT_LINUX_CSD=0/1 overrides the setting.
    /// </summary>
    public static bool UsesLinuxDrawnDecorations { get; } = OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("KANTUNEXT_LINUX_CSD") switch
        {
            "0" => false,
            "1" => true,
            _ => KanTuNext.Core.Settings.AppSettings.Load().LinuxDrawnTitleBar,
        };

    /// <summary>Drawn min/max/close in the client area: always on Windows, on Linux only with drawn decorations.</summary>
    public static bool HasClientCaptionButtons { get; } = OperatingSystem.IsWindows() || UsesLinuxDrawnDecorations;

    /// <summary>
    /// Whether the window buttons sit at the top left: always on macOS, on Linux when the window manager draws
    /// the title bar and is set up that way (drawn decorations always put them on the right). Decides the
    /// sidebar toggle: macOS-style after the buttons, or a hamburger in the corner.
    /// </summary>
    public static bool CaptionButtonsOnLeft { get; } = OperatingSystem.IsMacOS() ||
        OperatingSystem.IsLinux() && !UsesLinuxDrawnDecorations && LinuxButtonsOnLeft();

    private static bool LinuxButtonsOnLeft()
    {
        try
        {
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.ToUpperInvariant() ?? "";
            if (desktop.Contains("KDE"))
            {
                // ButtonsOnLeft letters: X close, A maximize, I minimize
                var left = (Run("kreadconfig6", "--file kwinrc --group org.kde.kwin.decoration --key ButtonsOnLeft")
                            ?? Run("kreadconfig5", "--file kwinrc --group org.kde.kwin.decoration --key ButtonsOnLeft"))?.ToUpperInvariant();
                return left is not null && left.IndexOfAny(['X', 'A', 'I']) >= 0;
            }
            if (desktop.Contains("XFCE"))
            {
                // "O|HMC": left of the title | right of it; H minimize, M maximize, C close
                var layout = Run("xfconf-query", "-c xfwm4 -p /general/button_layout")?.ToUpperInvariant();
                return layout is not null && layout.Split('|')[0].IndexOfAny(['H', 'M', 'C']) >= 0;
            }
            // GNOME and other GTK desktops: "close,minimize,maximize:appmenu"
            var gtk = Run("gsettings", "get org.gnome.desktop.wm.preferences button-layout")?.Trim('\'', '"');
            if (gtk is null) return false;
            static bool HasButton(string s) => s.Contains("close") || s.Contains("maximize") || s.Contains("minimize");
            var parts = gtk.Split(':', 2);
            return HasButton(parts[0]) && (parts.Length < 2 || !HasButton(parts[1]));
        }
        catch
        {
            return false;
        }
    }

    private static string? Run(string file, string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(1000)) return null;
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Call once from App.Initialize: fixes every window's title bar template as it loads.</summary>
    public static void Install()
    {
        Control.LoadedEvent.AddClassHandler<Panel>((panel, _) =>
        {
            if (panel.TemplatedParent is not WindowDrawnDecorations) return;
            switch (panel.Name)
            {
                // Fluent hard-codes Spacing="2" and :has-border/:has-shadow insets; our content fills the window,
                // so the buttons sit flush with the corner and their hover fill reaches the edge.
                case "PART_OverlayPanel" when panel is StackPanel stack:
                    stack.Spacing = 0;
                    stack.Margin = default;
                    if (stack.GetVisualParent() is Panel { Name: "PART_OverlayWrapper" } wrapper)
                        wrapper.Margin = default;
                    break;
                // Windows extended into the title bar paint their own; the template's title bar fill underneath
                // would show through the main window's translucent sidebar as an opaque strip.
                case "PART_TitleBar" when OwnerWindow(panel) is { ExtendClientAreaToDecorationsHint: true }:
                    panel.Background = Avalonia.Media.Brushes.Transparent;
                    break;
                // Windows extended into the title bar draw their own title; hide the template's duplicate.
                case "PART_TitleTextPanel" when OwnerWindow(panel) is { ExtendClientAreaToDecorationsHint: true }:
                    foreach (var text in panel.Children.OfType<TextBlock>()) text.IsVisible = false;
                    break;
            }
        });
    }

    /// <summary>
    /// The window a decorations part belongs to. Avalonia 12.1 hosts the decorations beside the window under an
    /// internal TopLevelHost, not inside it, so TopLevel.GetTopLevel finds no window from them.
    /// </summary>
    private static Window? OwnerWindow(Visual visual)
    {
        var root = visual;
        while (root.GetVisualParent() is { } parent) root = parent;
        return root as Window ?? root.GetVisualChildren().OfType<Window>().FirstOrDefault();
    }
}
