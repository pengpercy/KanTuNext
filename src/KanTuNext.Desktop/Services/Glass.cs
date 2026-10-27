using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// Frosted glass, as Finder's. Right-click menus (and <see cref="GlassMenuFlyout"/>s): the menu's popup window blurs what is behind it and the menu
/// draws a thin tint over that; macOS rounds the popup window to the menu's corners and uses the system's menu
/// material, Windows 11 rounds it through DWM (and tints it thicker: its acrylic lets more through), elsewhere
/// (Windows 10, Linux) the menu stays opaque. The main window's sidebar: macOS swaps Avalonia's HUD material for the
/// system's sidebar one.
/// </summary>
internal static class Glass
{
    private static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Asks for a blurred popup window before the menu shows, so it never flashes opaque. <paramref name="menu"/>
    /// is a ContextMenu, or a MenuFlyout's presenter (reused from one opening to the next, each in a new popup).
    /// </summary>
    public static void Attach(TemplatedControl menu)
    {
        if (!Supported) return;
        menu.AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(menu) is not PopupRoot root) return;
            root.Background = Brushes.Transparent;
            root.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
            Frost(root, menu);
        };
        // Again once shown: the platform may report the blur only then, and AppKit may have laid the window out again
        menu.Loaded += (_, _) =>
        {
            if (TopLevel.GetTopLevel(menu) is PopupRoot root) Frost(root, menu);
        };
    }

    /// <summary>Tints instead of filling the menu, once its window really blurs (otherwise it stays opaque).</summary>
    private static void Frost(PopupRoot root, TemplatedControl menu)
    {
        if (root.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur
            && root.ActualTransparencyLevel != WindowTransparencyLevel.Blur) return;
        menu.Classes.Add("glass");
        if (OperatingSystem.IsWindows())
        {
            menu.Classes.Add("windows");
            menu.CornerRadius = new Avalonia.CornerRadius(8);
        }
        Round(root, menu);
    }

    private static void Round(PopupRoot root, TemplatedControl menu)
    {
        if (root.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        try
        {
            if (OperatingSystem.IsMacOS()) MacRound(handle, menu.CornerRadius.TopLeft, menu.ActualThemeVariant == ThemeVariant.Dark);
            else WindowsRound(handle);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // The menu still works, with square glass corners
        }
    }

    // ---- macOS: clip the content view (and the NSVisualEffectView in it) to the menu's corners

    private const string ObjcLib = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjcLib)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjcLib)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void SendNint(IntPtr receiver, IntPtr selector, nint arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(IntPtr receiver, IntPtr selector, double arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SendBoolResult(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static void MacRound(IntPtr window, double radius, bool dark)
    {
        // Avalonia paints a blurred window's background with the system window colour: clear it, or the corners
        // cut off below would show it
        SendBool(window, Sel("setOpaque:"), false);
        Send(window, Sel("setBackgroundColor:"), Send(objc_getClass("NSColor"), Sel("clearColor")));
        var appearance = Send(objc_getClass("NSAppearance"), Sel("appearanceNamed:"),
            SendString(objc_getClass("NSString"), Sel("stringWithUTF8String:"), dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"));
        Send(window, Sel("setAppearance:"), appearance);

        var content = Send(window, Sel("contentView"));
        if (content == IntPtr.Zero) return;
        SendBool(content, Sel("setWantsLayer:"), true);
        var layer = Send(content, Sel("layer"));
        if (layer != IntPtr.Zero)
        {
            SendDouble(layer, Sel("setCornerRadius:"), radius);
            SendBool(layer, Sel("setMasksToBounds:"), true);
        }

        // The system's menu material instead of Avalonia's HUD one, and active even though popups never are key
        foreach (var view in EffectViews(content))
        {
            SendNint(view, Sel("setMaterial:"), 5); // NSVisualEffectMaterialMenu
            SendNint(view, Sel("setState:"), 1); // NSVisualEffectStateActive
            SendBool(view, Sel("setWantsLayer:"), true);
            if (Send(view, Sel("layer")) is var viewLayer && viewLayer != IntPtr.Zero)
            {
                SendDouble(viewLayer, Sel("setCornerRadius:"), radius);
                SendBool(viewLayer, Sel("setMasksToBounds:"), true);
            }
        }
        // The window shadow follows the rounded shape
        SendBool(window, Sel("setHasShadow:"), true);
        Send(window, Sel("invalidateShadow"));
    }

    /// <summary>The NSVisualEffectViews Avalonia puts in a window's content view for its blur.</summary>
    private static List<IntPtr> EffectViews(IntPtr content)
    {
        var effectClass = objc_getClass("NSVisualEffectView");
        var subviews = Send(content, Sel("subviews"));
        var count = (nint)Send(subviews, Sel("count"));
        var views = new List<IntPtr>();
        for (nuint i = 0; i < (nuint)count; i++)
        {
            var view = SendIndex(subviews, Sel("objectAtIndex:"), i);
            if (SendBoolResult(view, Sel("isKindOfClass:"), effectClass)) views.Add(view);
        }
        return views;
    }

    /// <summary>
    /// macOS: the blur behind a window's translucent parts becomes the system's sidebar material (Finder's: darker
    /// and more muted than Avalonia's HUD material, and following the light or dark appearance). It stays the
    /// same whether or not the window is active, as Avalonia's does.
    /// </summary>
    public static void UseSidebarMaterial(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        try
        {
            var content = Send(handle, Sel("contentView"));
            if (content == IntPtr.Zero) return;
            foreach (var view in EffectViews(content))
            {
                SendNint(view, Sel("setMaterial:"), 7); // NSVisualEffectMaterialSidebar
                SendNint(view, Sel("setState:"), 1); // NSVisualEffectStateActive
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    // ---- Windows 11: DWM rounds the popup (and its acrylic) smoothly, like the system's own menus

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void WindowsRound(IntPtr hwnd)
    {
        var round = 2; // DWMWCP_ROUND: 8px, matched by the glass menu's corner radius
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }
}

/// <summary>A menu flyout (the viewer's 轮播间隔 choices) on the same frosted glass as right-click menus.</summary>
internal sealed class GlassMenuFlyout : MenuFlyout
{
    protected override Control CreatePresenter()
    {
        var presenter = base.CreatePresenter();
        if (presenter is TemplatedControl menu) Glass.Attach(menu);
        return presenter;
    }
}
