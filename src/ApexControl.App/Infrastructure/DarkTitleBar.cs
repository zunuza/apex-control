using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ApexControl.App.Infrastructure;

// Asks Windows (10 1903+/11) to draw a window's title bar in its dark style so it matches the app. A no-op elsewhere.
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch (Exception) { /* cosmetic only */ }
        };
    }
}