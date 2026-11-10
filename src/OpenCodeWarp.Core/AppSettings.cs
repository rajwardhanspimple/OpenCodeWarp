using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCodeWarp;

public enum WarpScope
{
    /// <summary>Only AI provider endpoints use WARP. Search and web fetch stay direct.</summary>
    AiOnly,

    /// <summary>All non-local traffic from OpenCode uses WARP, except the direct-host compatibility list.</summary>
    All
}

public sealed class AppSettings
{
    private const int CurrentSettingsVersion = 3;

    public int SettingsVersion { get; set; }
    public int RelayPort { get; set; } = 41000;
    public int WarpProxyPort { get; set; } = 40000;
    public WarpScope WarpScope { get; set; } = WarpScope.AiOnly;

    /// <summary>Extra AI provider hosts (comma/semicolon separated, *.example.com allowed).</summary>
    public string ExtraAiHosts { get; set; } = "";

    /// <summary>Compatibility exceptions used when WarpScope=All.</summary>
    public string DirectHosts { get; set; } = DefaultDirectHosts;

    public string? OpenCodeExe { get; set; }
    public string ProjectFolder { get; set; } = AppPaths.DefaultWorkspace;

    public bool RotateOnRateLimit { get; set; }

    /// <summary>Rotate WARP when OpenCode's log reports the free usage limit.</summary>
    public bool RotateOnUsageLimit { get; set; } = true;

    /// <summary>Scheduled rotation interval in minutes; 0 disables the timer.</summary>
    public int AutoRotateMinutes { get; set; } = 30;

    public bool AutoLaunchOpenCode { get; set; }

    /// <summary>
    /// Persist the relay proxy into opencode's background-service environment config
    /// (<c>opencode service set env</c>), so the <c>opencode api</c> path — used by the
    /// opencode-proxy bridge and other API clients — also exits through WARP.
    /// </summary>
    public bool ServiceProxyEnabled { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }

    public string OpenCodeExtraArgs { get; set; } = "";
    public int MinSecondsBetweenRotations { get; set; } = 25;
    public int RotationAttempts { get; set; } = 3;
    public int WarpWaitSeconds { get; set; } = 30;
    public int HealthCheckSeconds { get; set; } = 25;

    public string LogLevel { get; set; } = "info";
    public bool LogDirectConnections { get; set; }

    /// <summary>Test hook; when set, the quota watcher reads this directory instead of the real OpenCode log.</summary>
    public string? QuotaLogDirectoryOverride { get; set; }

    public static string DefaultDirectHosts =>
        "mcp.exa.ai,*.exa.ai,mcp.firecrawl.dev,search.parallel.ai,api.tavily.com," +
        "agent.tinyfish.ai,duckduckgo.com,html.duckduckgo.com,www.google.com," +
        "www.bing.com,search.brave.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static AppSettings Load()
    {
        AppPaths.EnsureDirectories();
        if (!File.Exists(AppPaths.SettingsFile))
        {
            var fresh = new AppSettings();
            fresh.Normalize();
            fresh.Save();
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            var migrated = settings.SettingsVersion < CurrentSettingsVersion;
            settings.Normalize();
            if (migrated)
            {
                try { settings.Save(); } catch { }
            }
            return settings;
        }
        catch
        {
            // A broken settings file must never stop the relay from starting.
            var fallback = new AppSettings();
            fallback.Normalize();
            return fallback;
        }
    }

    public void Save()
    {
        Normalize();
        AppPaths.EnsureDirectories();
        var temp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, AppPaths.SettingsFile, true);
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void CopyFrom(AppSettings other)
    {
        foreach (var property in typeof(AppSettings).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite)
                property.SetValue(this, property.GetValue(other));
        }
    }

    public void Normalize()
    {
        SettingsVersion = CurrentSettingsVersion;
        RelayPort = Clamp(RelayPort, 1024, 65535, 41000);
        WarpProxyPort = Clamp(WarpProxyPort, 1, 65535, 40000);
        MinSecondsBetweenRotations = Clamp(MinSecondsBetweenRotations, 20, 600, 25);
        AutoRotateMinutes = Clamp(AutoRotateMinutes, 0, 720, 30);
        RotationAttempts = Clamp(RotationAttempts, 1, 10, 3);
        WarpWaitSeconds = Clamp(WarpWaitSeconds, 5, 180, 30);
        HealthCheckSeconds = Clamp(HealthCheckSeconds, 5, 300, 25);
        if (string.IsNullOrWhiteSpace(ProjectFolder))
            ProjectFolder = AppPaths.DefaultWorkspace;
        if (string.IsNullOrWhiteSpace(DirectHosts))
            DirectHosts = DefaultDirectHosts;
        ExtraAiHosts ??= "";
        OpenCodeExtraArgs ??= "";
        OpenCodeExe = string.IsNullOrWhiteSpace(OpenCodeExe) ? null : OpenCodeExe.Trim();
    }

    private static int Clamp(int value, int min, int max, int fallback)
        => value < min || value > max ? fallback : value;
}
