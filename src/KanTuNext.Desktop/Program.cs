using Avalonia;
using Avalonia.Media;
using KanTuNext.Desktop.Services;

namespace KanTuNext.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Before Avalonia starts: a second copy hands over to the running one and exits
        using var instance = SingleInstance.TryAcquire();
        if (instance is null && SingleInstance.NotifyRunning()) return;
        App.Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static FontFallback[] CjkFallbacks =>
        OperatingSystem.IsMacOS() ? [new FontFallback { FontFamily = new FontFamily("PingFang SC") }]
        : OperatingSystem.IsWindows() ? [new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") }]
        : [];

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia would register the process as "Avalonia Application" before App.axaml loads; leave the
            // name to CFBundleName. Its default app-menu items are English-only; App.axaml has Chinese ones.
            .With(new MacOSPlatformOptions { DisableSetProcessName = true, DisableDefaultApplicationMenuItems = true })
            // Naming the CJK font up front spares the first layout a system-wide fallback search (~50 ms on
            // macOS); Latin text keeps the system UI font. Linux leaves the choice to fontconfig.
            .With(new FontManagerOptions { FontFallbacks = CjkFallbacks })
#pragma warning disable AVALONIA_X11_CSD
            // Without drawn decorations X11 ignores ExtendClientAreaToDecorationsHint and the WM stacks its
            // own title bar on ours. WmClass must match StartupWMClass in kantunext.desktop for dock grouping.
            .With(new X11PlatformOptions { EnableDrawnDecorations = WindowChrome.UsesLinuxDrawnDecorations, WmClass = "kantunext" })
#pragma warning restore AVALONIA_X11_CSD
            .LogToTrace();
}
