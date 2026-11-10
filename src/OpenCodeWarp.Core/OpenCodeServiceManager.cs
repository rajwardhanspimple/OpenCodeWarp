using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenCodeWarp;

/// <summary>What the dashboard and CLI need to show about the opencode background service.</summary>
public sealed record OpenCodeServiceState(
    bool Managed,
    bool Running,
    string? Url,
    int? Port,
    int? Pid,
    DateTime? StartedAt,
    bool EnvConfigured,
    bool RestartNeeded,
    DateTimeOffset? LastConfigWriteAt,
    string? Note);

/// <summary>
/// Routes opencode's background service (<c>opencode serve --service</c>) through the relay.
/// The service is the process that makes the upstream model calls for <c>opencode api</c>
/// clients — including the opencode-proxy bridge (D:\opencode-proxy) and DeepSeek Harness —
/// so without this they bypass WARP entirely.
///
/// opencode persists environment variables for the service in its own config
/// (<c>opencode service set env KEY VALUE</c>), so writing the relay proxy there makes every
/// future service start relay-wired, no matter which process starts it. A service that is
/// already running keeps its old environment, so after a config change it must be restarted
/// once; that restart is confirmed with the user and never kills anything OpenCodeWarp did
/// not already know about.
/// </summary>
public sealed partial class OpenCodeServiceManager
{
    private const string NoProxyValue = "localhost,127.0.0.1,::1";

    /// <summary>The env keys this manager owns in the service config. Disabling removes exactly these.</summary>
    public static readonly string[] ProxyEnvKeys =
    {
        "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "NO_PROXY", "no_proxy"
    };

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(30);

    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly OpenCodeLauncher _launcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Persisted so a declined restart is still detected after the dashboard reopens.</summary>
    public DateTimeOffset? LastConfigWriteAt { get; private set; }

    public OpenCodeServiceState? CurrentState { get; private set; }

    public OpenCodeServiceManager(AppSettings settings, AppLog log, OpenCodeLauncher launcher)
    {
        _settings = settings;
        _log = log;
        _launcher = launcher;
        LoadWriteTimestamp();
    }

    private void LoadWriteTimestamp()
    {
        try
        {
            if (!File.Exists(AppPaths.ServiceStateFile))
                return;
            var json = File.ReadAllText(AppPaths.ServiceStateFile);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("lastConfigWriteAt", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var parsed))
            {
                LastConfigWriteAt = parsed;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Service state file could not be read: {ex.Message}");
        }
    }

    private void SaveWriteTimestamp()
    {
        if (LastConfigWriteAt is null)
            return;
        try
        {
            var payload = JsonSerializer.Serialize(new { lastConfigWriteAt = LastConfigWriteAt });
            var temp = AppPaths.ServiceStateFile + ".tmp";
            File.WriteAllText(temp, payload);
            File.Move(temp, AppPaths.ServiceStateFile, true);
        }
        catch (Exception ex)
        {
            _log.Debug($"Service state file could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// The opencode background service as it is running right now, or null. The reported
    /// URL is only trusted when a live loopback listener owned by an opencode process
    /// actually answers for its port — a stale status line must not be mistaken for a
    /// running daemon.
    /// </summary>
    public async Task<OpenCodeServiceState?> RefreshStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshStateCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes the persisted service env match the relay proxy. Writes only the keys that
    /// are missing or different (never foreign keys), records the write time, and leaves
    /// the restart decision to the caller.
    /// </summary>
    public async Task<OpenCodeServiceState?> EnsureProxyConfigAsync(
        string proxyUrl,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var env = await GetEnvConfigAsync(cancellationToken);
            var written = new List<string>();

            foreach (var key in ProxyEnvKeys)
            {
                // NO_PROXY/no_proxy hold the loopback exception list; the rest carry the relay URL.
                var desired = key.StartsWith("NO_", StringComparison.OrdinalIgnoreCase)
                    ? NoProxyValue
                    : proxyUrl;

                if (env.TryGetValue(key, out var current) &&
                    string.Equals(current, desired, StringComparison.Ordinal))
                    continue;

                await RunServiceCommandAsync($"service set env {key} {desired}", cancellationToken);
                written.Add(key);
            }

            if (written.Count > 0)
            {
                LastConfigWriteAt = DateTimeOffset.Now;
                SaveWriteTimestamp();
                _log.Info($"opencode background service env updated ({string.Join(", ", written)}) " +
                          $"→ model traffic from opencode api clients now targets {proxyUrl}.");
            }
            else
            {
                _log.Debug("opencode background service env already matches the relay proxy.");
            }

            return await RefreshStateCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes exactly the keys this manager owns (Settings toggle turned off).</summary>
    public async Task RemoveProxyConfigAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var env = await GetEnvConfigAsync(cancellationToken);
            var removed = new List<string>();
            foreach (var key in ProxyEnvKeys)
            {
                if (!env.ContainsKey(key))
                    continue;
                await RunServiceCommandAsync($"service unset env {key}", cancellationToken);
                removed.Add(key);
            }

            if (removed.Count > 0)
                _log.Info($"opencode background service env cleared ({string.Join(", ", removed)}). " +
                          "A service that is already running keeps its old environment until it is restarted.");
            return;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restarts the background service so a freshly written env config takes effect:
    /// stop, wait for the listener to disappear, start, wait for it to answer again.
    /// In-flight opencode api requests are dropped by this — it is only used after the
    /// user allowed it.
    /// </summary>
    public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var before = await GetRunningServiceAsync(cancellationToken);
            if (before is null)
            {
                _log.Info("opencode background service is not running; no restart needed.");
                return true;
            }

            _log.Warn($"Restarting the opencode background service (pid {before.Pid} at {before.Url}) " +
                      "so the relay proxy environment takes effect. In-flight opencode api requests are dropped.");
            await RunServiceCommandAsync("service stop", cancellationToken);

            var stopDeadline = DateTime.UtcNow + RestartTimeout;
            var stopped = false;
            while (DateTime.UtcNow < stopDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await GetRunningServiceAsync(cancellationToken) is null)
                {
                    stopped = true;
                    break;
                }
                try { await Task.Delay(400, cancellationToken); } catch (OperationCanceledException) { throw; }
            }
            if (!stopped)
                _log.Warn("The opencode background service is still listening after 'service stop'; " +
                          "continuing with the start anyway.");

            await RunServiceCommandAsync("service start", cancellationToken);

            var after = await WaitForRunningAsync(cancellationToken);
            if (after is null)
            {
                _log.Error("The opencode background service did not come back after the restart. " +
                           "Start it with 'opencode service start' if an opencode api client needs it.");
                CurrentState = new OpenCodeServiceState(
                    _settings.ServiceProxyEnabled, false, null, null, null, null,
                    EnvConfigured: false, RestartNeeded: false, LastConfigWriteAt, "restart failed");
                return false;
            }

            _log.Info($"opencode background service is back: {after.Url} (pid {after.Pid}).");
            await RefreshStateCoreAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // --- internals -----------------------------------------------------------------

    private async Task<OpenCodeServiceState?> RefreshStateCoreAsync(CancellationToken cancellationToken)
    {
        // Same body as RefreshStateAsync without taking the gate again.
        var running = await GetRunningServiceAsync(cancellationToken);
        bool? envConfigured = null;
        string? note = null;

        if (_settings.ServiceProxyEnabled)
        {
            try
            {
                var env = await GetEnvConfigAsync(cancellationToken);
                envConfigured = MatchesDesiredEnv(env);
            }
            catch (Exception ex)
            {
                note = $"env config unreadable: {ex.Message}";
            }
        }
        else
        {
            note = "not managed (routing is off in Settings)";
        }

        var state = new OpenCodeServiceState(
            Managed: _settings.ServiceProxyEnabled,
            Running: running is not null,
            Url: running?.Url,
            Port: running?.Port,
            Pid: running?.Pid,
            StartedAt: running?.StartedAt,
            EnvConfigured: envConfigured ?? false,
            RestartNeeded: _settings.ServiceProxyEnabled
                           && running is not null
                           && LastConfigWriteAt is not null
                           && running.StartedAt is DateTime started
                           && started < LastConfigWriteAt.Value.LocalDateTime,
            LastConfigWriteAt: LastConfigWriteAt,
            Note: note);
        CurrentState = state;
        return state;
    }

    private sealed record RunningService(string Url, int Port, int Pid, DateTime? StartedAt);

    private async Task<RunningService?> GetRunningServiceAsync(CancellationToken cancellationToken)
    {
        var output = await RunServiceCommandAsync("service status", cancellationToken);
        var match = ServiceUrlRegex().Match(output);
        if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var port) || port <= 0)
            return null;

        var pid = WindowsPortFinder.GetLoopbackListenerOwnerPid(port);
        if (pid is null)
            return null;

        string? name = null;
        DateTime? started = null;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            name = process.ProcessName;
            started = process.StartTime;
        }
        catch
        {
            // The process exited between the port scan and here; treat it as not running.
            return null;
        }

        if (name is null || !name.Contains("opencode", StringComparison.OrdinalIgnoreCase))
        {
            _log.Debug($"Port {port} is owned by {name}, not opencode; ignoring the service status URL.");
            return null;
        }

        return new RunningService(match.Value.TrimEnd('/', ' '), port, pid.Value, started);
    }

    private async Task<RunningService?> WaitForRunningAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + RestartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var running = await GetRunningServiceAsync(cancellationToken);
            if (running is not null)
                return running;
            try { await Task.Delay(400, cancellationToken); } catch (OperationCanceledException) { throw; }
        }
        return null;
    }

    private async Task<Dictionary<string, string>> GetEnvConfigAsync(CancellationToken cancellationToken)
    {
        var output = await RunServiceCommandAsync("service get", cancellationToken);
        var start = output.IndexOf('{');
        if (start < 0)
            return new Dictionary<string, string>();

        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in env.EnumerateObject())
                {
                    if (item.Value.ValueKind == JsonValueKind.String)
                        result[item.Name] = item.Value.GetString() ?? "";
                }
            }
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"'opencode service get' returned unusable JSON: {ex.Message}");
        }
    }

    private bool MatchesDesiredEnv(Dictionary<string, string> env)
    {
        foreach (var key in ProxyEnvKeys)
        {
            var desired = key.StartsWith("NO_", StringComparison.OrdinalIgnoreCase)
                ? NoProxyValue
                : ProxyUrlForComparison();
            if (!env.TryGetValue(key, out var current) ||
                !string.Equals(current, desired, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private string? ProxyUrlForComparison() => $"http://127.0.0.1:{_settings.RelayPort}";

    private async Task<string> RunServiceCommandAsync(string arguments, CancellationToken cancellationToken)
    {
        var executable = _launcher.DiscoverExecutable();
        if (executable is null)
            throw new InvalidOperationException("the OpenCode CLI was not found");

        var (application, fullArguments) = OpenCodeLauncher.BuildCommandLine(executable, arguments);
        var psi = new ProcessStartInfo
        {
            FileName = application,
            Arguments = fullArguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"'opencode {arguments}' did not start");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"'opencode {arguments}' did not answer within {CommandTimeout.TotalSeconds:0}s");
        }

        var stdout = await SafeReadAsync(stdoutTask);
        var stderr = await SafeReadAsync(stderrTask);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'opencode {arguments}' failed (exit {process.ExitCode}): {Clip(stderr.Length > 0 ? stderr : stdout)}");

        return stdout;
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task; } catch { return ""; }
    }

    private static string Clip(string value, int max = 240)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }

    [GeneratedRegex(@"https?://(?:127\.0\.0\.1|localhost|\[::1\]):(?<port>\d+)")]
    private static partial Regex ServiceUrlRegex();
}
