using Avalonia;
using KanTuNext.Desktop.Services;

namespace KanTuNext.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia would register the process as "Avalonia Application" before App.axaml loads;
            // leave the name to CFBundleName instead.
            .With(new MacOSPlatformOptions { DisableSetProcessName = true })
#pragma warning disable AVALONIA_X11_CSD
            // Without drawn decorations X11 ignores ExtendClientAreaToDecorationsHint and the WM stacks its
            // own title bar on ours. WmClass must match StartupWMClass in kantunext.desktop for dock grouping.
            .With(new X11PlatformOptions { EnableDrawnDecorations = WindowChrome.UsesLinuxDrawnDecorations, WmClass = "kantunext" })
#pragma warning restore AVALONIA_X11_CSD
            .LogToTrace();
}
