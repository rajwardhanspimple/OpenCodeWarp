using Microsoft.Win32;

namespace OpenCodeWarp.App;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenCodeWarp";

    public static string LaunchCommand
        => $"\"{Application.ExecutablePath}\" --minimized";

    public static bool Apply(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                error = "The Windows startup registry key could not be opened.";
                return false;
            }

            if (enabled)
                key.SetValue(ValueName, LaunchCommand, RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
