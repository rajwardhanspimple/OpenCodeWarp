using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace OpenCodeWarp;

public sealed record WarpTrace(
    bool Ok,
    bool WarpOn,
    string Ip,
    string Warp,
    string Colo,
    string Loc,
    string? Error,
    string Raw)
{
    public static WarpTrace Failed(string error)
        => new(false, false, "", "", "", "", error, "");
}

public static class TraceProbe
{
    public const string DefaultTraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    public static Task<WarpTrace> GetAsync(
        string? proxyUrl,
        int timeoutSeconds,
        CancellationToken cancellationToken)
        => GetAsync(proxyUrl, DefaultTraceUrl, timeoutSeconds, cancellationToken);

    public static async Task<WarpTrace> GetAsync(
        string? proxyUrl,
        string url,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = !string.IsNullOrWhiteSpace(proxyUrl),
                Proxy = string.IsNullOrWhiteSpace(proxyUrl) ? null : new WebProxy(proxyUrl),
                ConnectTimeout = TimeSpan.FromSeconds(Math.Min(15, Math.Max(3, timeoutSeconds))),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds))
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenCodeWarp/1.0");

            using var response = await client.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in body.Split('\n'))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }

            var ip = values.GetValueOrDefault("ip", "");
            var warp = values.GetValueOrDefault("warp", "");
            var warpOn = warp.Equals("on", StringComparison.OrdinalIgnoreCase)
                         || warp.Equals("plus", StringComparison.OrdinalIgnoreCase);
            var ok = response.IsSuccessStatusCode && ip.Length > 0;
            return new WarpTrace(
                ok,
                warpOn,
                ip,
                warp,
                values.GetValueOrDefault("colo", ""),
                values.GetValueOrDefault("loc", ""),
                ok ? null : $"HTTP {(int)response.StatusCode}",
                body.Length > 4096 ? body[..4096] : body);
        }
        catch (Exception ex)
        {
            return WarpTrace.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static string IpKey(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return "";

        var value = ip.Trim();
        var zone = value.IndexOf('%');
        if (zone >= 0)
            value = value[..zone];

        if (!IPAddress.TryParse(value, out var address))
            return value.ToLowerInvariant();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // A new WARP registration often keeps the same IPv6 /64 and changes the host part.
            return Convert.ToHexString(bytes.AsSpan(0, 8));
        }

        return Convert.ToHexString(bytes);
    }

    public static bool SameExit(string? a, string? b)
    {
        var ka = IpKey(a);
        var kb = IpKey(b);
        return ka.Length > 0 && ka.Equals(kb, StringComparison.OrdinalIgnoreCase);
    }
}
