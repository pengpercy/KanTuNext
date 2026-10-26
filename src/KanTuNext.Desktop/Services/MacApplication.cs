using System.Runtime.InteropServices;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// The standard NSApplication actions behind the app menu's Hide / Hide Others / Show All items, and activation.
/// Avalonia's built-in versions of those items are English-only, so the app menu provides its
/// own localized items and routes them here.
/// </summary>
internal static class MacApplication
{
    private const string ObjcLib = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjcLib)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjcLib)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void Send(IntPtr receiver, IntPtr selector, IntPtr arg1);

    public static void Hide() => SendToApp("hide:");

    public static void HideOthers() => SendToApp("hideOtherApplications:");

    public static void ShowAll() => SendToApp("unhideAllApplications:");

    /// <summary>Brings the app forward even when another app is active (a second launch handing over).</summary>
    public static void Activate()
    {
        SendToApp("unhide:");
        SendBool("activateIgnoringOtherApps:", true);
    }

    private static void SendBool(string selector, bool value)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != IntPtr.Zero) SendBool(app, sel_registerName(selector), value);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool value);

    private static void SendToApp(string selector)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != IntPtr.Zero) Send(app, sel_registerName(selector), IntPtr.Zero);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Nothing to fall back to; the menu item just does nothing.
        }
    }
}
