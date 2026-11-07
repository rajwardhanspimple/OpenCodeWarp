using System.Runtime.InteropServices;

namespace OpenCodeWarp.App;

internal static class DarkMode
{
    // Undocumented uxtheme ordinals supported by Windows 10 1903+ and Windows 11.
    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr handle, string? subAppName, string? subIdList);

    private const int ForceDark = 2;

    public static void Enable()
    {
        try
        {
            SetPreferredAppMode(ForceDark);
            FlushMenuThemes();
        }
        catch
        {
            // Dark scrollbars and native menus are cosmetic. Older Windows builds can skip them.
        }
    }

    public static void ApplyToTree(Control root)
    {
        if (root.IsDisposed)
            return;

        if (root.IsHandleCreated)
            Apply(root.Handle);

        foreach (Control child in root.Controls)
            ApplyToTree(child);
    }

    private static void Apply(IntPtr handle)
    {
        try
        {
            SetWindowTheme(handle, "DarkMode_Explorer", null);
        }
        catch
        {
            // Best effort.
        }
    }
}
