using System.Collections;

namespace OpenCodeWarp;

/// <summary>
/// This app is the thing that decides where OpenCode goes. It must not inherit an
/// HTTP_PROXY from the shell or from the currently running OpenCode session, or the
/// relay's own WARP/trace calls would accidentally go through WARP as well.
/// Only the OpenCode child gets a proxy environment block, built explicitly by
/// OpenCodeLauncher.
///
/// Besides the standard HTTP(S)_PROXY names, older wrapper tools sometimes leave
/// ACTIVE_PROXY, PROXY, SOCKS_PROXY, OC_TPROXY and PROXY_* variables behind. Any
/// environment name containing "PROXY" is treated as proxy configuration and cleared.
/// </summary>
public static class ProxyEnvironment
{
    public static bool IsProxyName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Contains("PROXY", StringComparison.OrdinalIgnoreCase);

    public static string? InheritedSummary()
    {
        var parts = new List<string>();
        foreach (DictionaryEntry raw in Environment.GetEnvironmentVariables())
        {
            var name = raw.Key?.ToString();
            if (!IsProxyName(name))
                continue;
            var value = raw.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                parts.Add($"{name}={value}");
        }
        parts.Sort(StringComparer.OrdinalIgnoreCase);
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    public static void ClearInherited()
    {
        var names = new List<string>();
        foreach (DictionaryEntry raw in Environment.GetEnvironmentVariables())
        {
            var name = raw.Key?.ToString();
            if (IsProxyName(name))
                names.Add(name!);
        }

        foreach (var name in names)
        {
            try { Environment.SetEnvironmentVariable(name, null); } catch { }
        }
    }
}
