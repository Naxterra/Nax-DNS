using System.Reflection;
using System.Runtime.InteropServices;
using Divert.Windows;

namespace BetterDns.Service.Kernel;

public static class WinDivertNativeLoader
{
    private static readonly string NativeDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "WinDivert-2.2.2",
        "runtimes",
        "win-x64",
        "native");

    public static void Configure()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(DivertService).Assembly,
            ResolveLibrary);
    }

    private static IntPtr ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("WinDivert", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var libraryPath = Path.Combine(NativeDirectory, "WinDivert.dll");
        if (!File.Exists(libraryPath))
        {
            throw new DllNotFoundException(
                $"WinDivert.dll was not installed in the expected directory: {libraryPath}");
        }

        return NativeLibrary.Load(libraryPath, assembly, searchPath);
    }
}
