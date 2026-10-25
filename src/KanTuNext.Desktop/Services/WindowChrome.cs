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
    /// Whether Avalonia draws the title bar on Linux (otherwise the window manager does). Fixed at startup
    /// because X11 options are; KANTUNEXT_LINUX_CSD=0/1 overrides. A settings switch comes with the settings window.
    /// </summary>
    public static bool UsesLinuxDrawnDecorations { get; } =
        OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("KANTUNEXT_LINUX_CSD") != "0";

    /// <summary>Drawn min/max/close in the client area: always on Windows, on Linux only with drawn decorations.</summary>
    public static bool HasClientCaptionButtons { get; } = OperatingSystem.IsWindows() || UsesLinuxDrawnDecorations;

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
                // Windows extended into the title bar draw their own title; hide the template's duplicate.
                case "PART_TitleTextPanel" when TopLevel.GetTopLevel(panel) is Window { ExtendClientAreaToDecorationsHint: true }:
                    foreach (var text in panel.Children.OfType<TextBlock>()) text.IsVisible = false;
                    break;
            }
        });
    }
}
