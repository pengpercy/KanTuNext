using System.Runtime.InteropServices;

namespace KanTuNext.Desktop.Services;

/// <summary>
/// The assembly's single DllImport resolver (only one may be set): maps the logical library names used in
/// [DllImport] to loaders that find the bundled copy or a system one.
/// </summary>
internal static class NativeLibraries
{
    private static readonly Dictionary<string, Func<IntPtr>> Loaders = new();

    static NativeLibraries()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, (name, _, _) =>
        {
            Func<IntPtr>? load;
            lock (Loaders) Loaders.TryGetValue(name, out load);
            return load?.Invoke() ?? IntPtr.Zero;
        });
    }

    public static void Register(string name, Func<IntPtr> load)
    {
        lock (Loaders) Loaders[name] = load;
    }
}
