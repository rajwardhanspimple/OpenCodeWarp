using System.Runtime.InteropServices;

namespace OpenCodeWarp.App;

internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void EnableDarkTitleBar(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        var enabled = 1;
        try
        {
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
        catch
        {
            // Older Windows builds do not support the dark title bar. The app still works.
        }
    }
}
