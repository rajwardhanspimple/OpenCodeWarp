using System.Text.Json;

namespace OpenCodeWarp;

/// <summary>
/// The hosts that carry model/API traffic and therefore need the rotating WARP IP.
/// Search and web-fetch hosts are intentionally absent in AI-only mode.
/// </summary>
public sealed class AiHostCatalog
{
    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes = new();
    private readonly List<string> _configHosts = new();

    private static readonly string[] DefaultHosts =
    {
        "opencode.ai",
        "*.opencode.ai",
        "api.anthropic.com",
        "api.openai.com",
        "chatgpt.com",
        "*.chatgpt.com",
        "auth.openai.com",
        "generativelanguage.googleapis.com",
        "aiplatform.googleapis.com",
        "openrouter.ai",
        "*.openrouter.ai",
        "api.groq.com",
        "api.mistral.ai",
        "api.deepseek.com",
        "api.x.ai",
        "api.together.xyz",
        "api.fireworks.ai",
        "api.cerebras.ai",
        "api.perplexity.ai",
        "api.cohere.ai",
        "api.voyageai.com",
        "api.githubcopilot.com",
        "api.freeaiapikey.com",
        "gwarden.su",
        "*.gwarden.su",
        "api.tokenjuice.ai",
        "routixai.online",
        "*.routixai.online",
        "zro.moonmath.ai",
        "models.dev"
    };

    public AiHostCatalog(AppSettings settings, AppLog log)
    {
        _settings = settings;
        _log = log;
        Reload();
    }

    public int Count => _exact.Count + _suffixes.Count;
    public IReadOnlyCollection<string> ExactHosts => _exact;
    public IReadOnlyCollection<string> SuffixHosts => _suffixes;
    public IReadOnlyCollection<string> ConfigHosts => _configHosts;

    public void Reload()
    {
        _exact.Clear();
        _suffixes.Clear();
        _configHosts.Clear();

        foreach (var rule in DefaultHosts)
            AddRule(rule);

        foreach (var path in AppPaths.OpenCodeConfigPaths)
            ReadConfigHosts(path);

        foreach (var rule in SplitHosts(_settings.ExtraAiHosts))
            AddRule(rule);

        _log.Debug($"AI host catalog: {Count} rule(s), { _configHosts.Count } from opencode.json.");
    }

    public bool Matches(string host)
    {
        host = NormalizeHost(host);
        if (host.Length == 0)
            return false;
        if (_exact.Contains(host))
            return true;
        foreach (var suffix in _suffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
            if (host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void ReadConfigHosts(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Walk(document.RootElement, path);
        }
        catch (Exception ex)
        {
            _log.Debug($"Could not read AI hosts from {path}: {ex.Message}");
        }
    }

    private void Walk(JsonElement element, string source)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.NameEquals("baseURL") || property.NameEquals("baseUrl"))
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        TryAddUri(property.Value.GetString(), source);
                    }
                    Walk(property.Value, source);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Walk(item, source);
                break;
        }
    }

    private void TryAddUri(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return;
        if (uri.Scheme is not ("http" or "https"))
            return;
        var host = NormalizeHost(uri.Host);
        if (host.Length == 0 || HostPolicy.IsLocal(host))
            return;
        AddRule(host);
        _configHosts.Add(host);
        _log.Debug($"AI host from {Path.GetFileName(Path.GetDirectoryName(source))}: {host}");
    }

    private void AddRule(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule))
            return;

        var value = rule.Trim().TrimEnd('.');
        var suffix = value.StartsWith("*.", StringComparison.Ordinal);
        if (suffix)
            value = value[2..];
        else if (value.StartsWith(".", StringComparison.Ordinal))
        {
            suffix = true;
            value = value[1..];
        }

        value = NormalizeHost(value);
        if (!IsPlausibleHost(value) || HostPolicy.IsLocal(value))
            return;

        if (suffix)
        {
            if (!_suffixes.Contains(value, StringComparer.OrdinalIgnoreCase))
                _suffixes.Add(value);
        }
        else
        {
            _exact.Add(value);
        }
    }

    private static IEnumerable<string> SplitHosts(string? value)
        => (value ?? "").Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeHost(string host)
    {
        host = (host ?? "").Trim().Trim('[', ']').TrimEnd('.');
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && host[(colon + 1)..].All(char.IsDigit))
            host = host[..colon];
        return host.ToLowerInvariant();
    }

    private static bool IsPlausibleHost(string host)
        => host.Length is > 0 and <= 253
           && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');
}
