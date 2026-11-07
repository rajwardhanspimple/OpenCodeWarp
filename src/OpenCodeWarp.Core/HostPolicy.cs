using System.Net;
using System.Net.Sockets;

namespace OpenCodeWarp;

public enum RouteMode
{
    Direct,
    Warp
}

public sealed record RouteDecision(RouteMode Mode, string Reason);

public sealed class HostPolicy
{
    private readonly AppSettings _settings;
    private readonly AiHostCatalog _aiHosts;
    private List<string> _directRules;

    public HostPolicy(AppSettings settings, AiHostCatalog aiHosts)
    {
        _settings = settings;
        _aiHosts = aiHosts;
        _directRules = ParseRules(settings.DirectHosts);
    }

    public AiHostCatalog AiHosts => _aiHosts;

    public string ScopeDescription => _settings.WarpScope == WarpScope.AiOnly
        ? "AI-only"
        : "all OpenCode traffic";

    public void ReloadDirectRules()
        => _directRules = ParseRules(_settings.DirectHosts);

    public RouteDecision Decide(string host, int port)
    {
        host = Normalize(host);
        if (IsLocal(host))
            return new RouteDecision(RouteMode.Direct, "loopback or private network");

        if (MatchesRule(host, _directRules))
            return new RouteDecision(RouteMode.Direct, "direct-host compatibility list");

        if (_settings.WarpScope == WarpScope.AiOnly)
        {
            return _aiHosts.Matches(host)
                ? new RouteDecision(RouteMode.Warp, "AI provider host")
                : new RouteDecision(RouteMode.Direct, "non-AI host (search/web fetch)");
        }

        return new RouteDecision(RouteMode.Warp, "all OpenCode traffic");
    }

    public static bool IsLocal(string host)
    {
        host = Normalize(host);
        if (host.Length == 0)
            return false;

        if (host is "localhost" or "localhost.localdomain" or "0.0.0.0" or "::1" or "[::1]")
            return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return true;

        var zone = host.IndexOf('%');
        if (zone >= 0)
            host = host[..zone];

        if (!IPAddress.TryParse(host, out var address))
            return false;

        if (IPAddress.IsLoopback(address))
            return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254)
                   || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                   || bytes[0] == 0
                   || bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast)
                return true;
            return (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        return false;
    }

    private static List<string> ParseRules(string? value)
        => (value ?? "")
            .Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool MatchesRule(string host, IReadOnlyCollection<string> rules)
    {
        foreach (var rule in rules)
        {
            var suffix = rule.StartsWith("*.", StringComparison.Ordinal) || rule.StartsWith(".", StringComparison.Ordinal);
            var value = rule.TrimStart('*').TrimStart('.');
            if (value.Length == 0)
                continue;
            if (!suffix && host.Equals(value, StringComparison.OrdinalIgnoreCase))
                return true;
            if (suffix && (host.Equals(value, StringComparison.OrdinalIgnoreCase)
                           || host.EndsWith("." + value, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string Normalize(string host)
    {
        host = (host ?? "").Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && host[(colon + 1)..].All(char.IsDigit))
            host = host[..colon];
        return host;
    }
}
