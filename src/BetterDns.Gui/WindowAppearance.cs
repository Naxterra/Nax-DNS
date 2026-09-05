using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BetterDns.Gui;

internal static class WindowAppearance
{
    public static void Attach(Window window) => window.SourceInitialized += (_, _) =>
    {
        var handle = new WindowInteropHelper(window).Handle;
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        // Windows 11 caption/text colors use COLORREF (BGR). Unsupported attributes are harmless.
        var caption = 0x2C1D17;
        var text = 0xFCF7F4;
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
