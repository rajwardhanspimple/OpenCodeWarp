using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenCodeWarp;

public sealed record ModelSwitchResult(bool Success, string Message);

/// <summary>
/// Talks to the private OpenCode v2 server that belongs to the TUI launched by this
/// app. It is used to move a session to a different provider when the OpenCode Zen
/// account quota is exhausted; WARP cannot reset that account-level counter.
/// </summary>
public sealed class OpenCodeApiClient : IDisposable
{
    private readonly AppSettings _settings;
    private readonly OpenCodeLauncher _launcher;
    private readonly AppLog _log;
    private readonly HttpClient _http;
    private readonly object _cacheLock = new();
    private string? _cachedEndpoint;
    private int _cachedPid;
    private DateTimeOffset _cachedAt;

    public OpenCodeApiClient(AppSettings settings, OpenCodeLauncher launcher, AppLog log)
    {
        _settings = settings;
        _launcher = launcher;
        _log = log;
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseCookies = false
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
    }

    public async Task<ModelSwitchResult> SwitchModelAsync(
        string? sessionId,
        string providerId,
        string modelId,
        CancellationToken cancellationToken)
    {
        providerId = (providerId ?? "").Trim();
        modelId = (modelId ?? "").Trim();
        if (providerId.Length == 0 || modelId.Length == 0)
            return new ModelSwitchResult(false, "No fallback provider/model is configured.");

        if (!_launcher.IsRunning || _launcher.ProcessId is not int pid)
            return new ModelSwitchResult(false, "OpenCode is not running under this dashboard.");

        var password = _launcher.ServerPassword;
        if (string.IsNullOrWhiteSpace(password))
        {
            return new ModelSwitchResult(false,
                "This OpenCode was launched before model switching existed. Relaunch it once from the dashboard.");
        }

        var endpoint = await ResolveEndpointAsync(pid, password, cancellationToken);
        if (endpoint is null)
        {
            return new ModelSwitchResult(false,
                "The private OpenCode server was not found. Relaunch OpenCode from this dashboard once.");
        }

        var session = await ResolveSessionAsync(endpoint, password, sessionId, cancellationToken);
        if (session is null)
            return new ModelSwitchResult(false, "Could not identify the session that hit the quota.");

        if (!await IsManagedWorkspaceSessionAsync(endpoint, password, session, cancellationToken))
        {
            return new ModelSwitchResult(false,
                $"Session {session} is not in the workspace launched by this app; left it unchanged.");
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{endpoint}/api/session/{Uri.EscapeDataString(session)}/model");
            request.Headers.Authorization = BasicAuth(password);
            var body = JsonSerializer.Serialize(new
            {
                model = new { id = modelId, providerID = providerId }
            });
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var message = $"Switched {session} to {providerId}/{modelId}.";
                _log.Info(message);
                return new ModelSwitchResult(true, message);
            }

            var text = await SafeBodyAsync(response);
            var reason = $"The OpenCode API rejected the switch ({(int)response.StatusCode}): {Trim(text)}";
            _log.Warn(reason);
            return new ModelSwitchResult(false, reason);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ModelSwitchResult(false, "The model switch timed out.");
        }
        catch (Exception ex)
        {
            _log.Warn("Model switch failed: " + ex.Message);
            return new ModelSwitchResult(false, "The model switch failed: " + ex.Message);
        }
    }

    private async Task<string?> ResolveEndpointAsync(
        int pid,
        string password,
        CancellationToken cancellationToken)
    {
        // New launches use a known private server endpoint. Prefer it over
        // scanning the TUI process tree (which was needed by older builds).
        var managedEndpoint = _launcher.ServerUrl;
        if (!string.IsNullOrWhiteSpace(managedEndpoint)
            && await VerifyAsync(managedEndpoint, password, cancellationToken))
        {
            return managedEndpoint;
        }

        lock (_cacheLock)
        {
            if (_cachedPid == pid
                && _cachedEndpoint is not null
                && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(30))
            {
                return _cachedEndpoint;
            }
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            foreach (var port in WindowsPortFinder.GetLoopbackListenerPorts(pid))
            {
                var endpoint = $"http://127.0.0.1:{port}";
                if (await VerifyAsync(endpoint, password, cancellationToken))
                {
                    lock (_cacheLock)
                    {
                        _cachedEndpoint = endpoint;
                        _cachedPid = pid;
                        _cachedAt = DateTimeOffset.UtcNow;
                    }
                    _log.Debug($"OpenCode API endpoint: {endpoint}.");
                    return endpoint;
                }
            }

            try { await Task.Delay(400, cancellationToken); }
            catch (OperationCanceledException) { return null; }
        }

        return null;
    }

    private async Task<bool> VerifyAsync(
        string endpoint,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/api/model");
            request.Headers.Authorization = BasicAuth(password);
            using var response = await _http.SendAsync(request, timeout.Token);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> ResolveSessionAsync(
        string endpoint,
        string password,
        string? requestedSessionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedSessionId)
            && requestedSessionId.StartsWith("ses_", StringComparison.Ordinal))
        {
            return requestedSessionId.Trim();
        }

        try
        {
            var directory = Uri.EscapeDataString(_settings.ProjectFolder);
            var url = $"{endpoint}/api/session?limit=5&order=desc&directory={directory}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = BasicAuth(password);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                    return value;
            }
        }
        catch (Exception ex)
        {
            _log.Debug("Could not list OpenCode sessions: " + ex.Message);
        }

        return null;
    }

    private async Task<bool> IsManagedWorkspaceSessionAsync(
        string endpoint,
        string password,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{endpoint}/api/session/{Uri.EscapeDataString(sessionId)}");
            request.Headers.Authorization = BasicAuth(password);
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("location", out var location)
                || !location.TryGetProperty("directory", out var directory))
            {
                return false;
            }

            var actual = directory.GetString();
            if (string.IsNullOrWhiteSpace(actual))
                return false;

            return NormalizePath(actual) == NormalizePath(_settings.ProjectFolder);
        }
        catch (Exception ex)
        {
            _log.Debug("Could not verify the OpenCode session workspace: " + ex.Message);
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private static AuthenticationHeaderValue BasicAuth(string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password));
        return new AuthenticationHeaderValue("Basic", token);
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage response)
    {
        try { return await response.Content.ReadAsStringAsync(); }
        catch { return ""; }
    }

    private static string Trim(string value)
    {
        value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= 240 ? value : value[..240] + "...";
    }

    public void Dispose() => _http.Dispose();
}
