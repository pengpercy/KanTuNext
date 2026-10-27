using System.Globalization;
using System.Runtime.InteropServices;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// A link that opens a point in the system's own maps app: Apple 地图 on macOS, Windows 地图 (bingmaps:) on Windows
/// while it is installed. Without one (Linux, Windows after the app's removal) it is an OpenStreetMap page.
/// </summary>
internal static class SystemMaps
{
    private static readonly Lazy<bool> Available = new(() =>
        OperatingSystem.IsMacOS() || (OperatingSystem.IsWindows() && HasProtocolHandler("bingmaps")));

    public static string Url(double latitude, double longitude)
    {
        if (!Available.Value)
            return string.Create(CultureInfo.InvariantCulture,
                $"https://www.openstreetmap.org/?mlat={latitude:0.######}&mlon={longitude:0.######}#map=16/{latitude:0.######}/{longitude:0.######}");
        var title = Uri.EscapeDataString("拍摄位置");
        return OperatingSystem.IsMacOS()
            ? string.Create(CultureInfo.InvariantCulture, $"maps://?ll={latitude:0.######},{longitude:0.######}&q={title}&z=16")
            : string.Create(CultureInfo.InvariantCulture,
                $"bingmaps:?collection=point.{latitude:0.######}_{longitude:0.######}_{title}&cp={latitude:0.######}~{longitude:0.######}&lvl=16");
    }

    // ---- Windows: the Maps app is being retired and may be missing, so ask the shell whether bingmaps: has a handler
    private const int AssocfIsProtocol = 0x1000, AssocfInitIgnoreUnknown = 0x400, AssocstrFriendlyAppName = 4;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, char[]? output, ref uint length);

    private static bool HasProtocolHandler(string scheme)
    {
        try
        {
            uint length = 0;
            // S_FALSE (1) with the needed length means there is a name to return, i.e. something handles it
            var result = AssocQueryString(AssocfIsProtocol | AssocfInitIgnoreUnknown, AssocstrFriendlyAppName, scheme, null, null, ref length);
            return result is 0 or 1 && length > 1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
