namespace OpenCodeWarp;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCodeWarp");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogDirectory => Path.Combine(Root, "logs");
    public static string DefaultWorkspace => Path.Combine(Root, "workspace");

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string OpenCodeLogDirectory => Path.Combine(UserProfile, ".local", "share", "opencode", "log");
    public static string OpenCodeDataDirectory => Path.Combine(UserProfile, ".local", "share", "opencode");

    public static IEnumerable<string> OpenCodeConfigPaths
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appData, "opencode", "opencode.json");
            yield return Path.Combine(UserProfile, ".config", "opencode", "opencode.json");
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}
