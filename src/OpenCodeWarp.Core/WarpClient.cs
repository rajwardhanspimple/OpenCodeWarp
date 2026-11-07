using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenCodeWarp;

public sealed record WarpCommandResult(
    bool Ok,
    int ExitCode,
    string StdOut,
    string StdErr,
    string Command,
    TimeSpan Duration)
{
    public string Combined => (StdOut + "\n" + StdErr).Trim();
}

public sealed record WarpReadyResult(bool Success, string Message, WarpTrace? Trace);

public sealed record WarpProbeResult(bool Ok, string StatusLine, string Error);

public sealed record RotationResult(
    bool Success,
    string OldIp,
    string NewIp,
    string Colo,
    int Attempts,
    TimeSpan Duration,
    string? Error,
    WarpTrace? LastTrace);

public sealed partial class WarpClient
{
    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRotation = DateTimeOffset.MinValue;

    public string? WarpCliPath { get; }
    public bool Available => !string.IsNullOrWhiteSpace(WarpCliPath);
    public DateTimeOffset? LastRotationUtc => _lastRotation == DateTimeOffset.MinValue ? null : _lastRotation;

    public WarpClient(AppSettings settings, AppLog log)
    {
        _settings = settings;
        _log = log;
        WarpCliPath = FindWarpCli();
        if (WarpCliPath is null)
            _log.Warn("warp-cli.exe was not found; WARP rotation is unavailable.");
        else
            _log.Info($"warp-cli: {WarpCliPath}");
    }

    public static string? FindWarpCli()
    {
        var candidates = new List<string>();
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "Cloudflare", "Cloudflare WARP", "warp-cli.exe"));
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            candidates.Add(Path.Combine(programFilesX86, "Cloudflare", "Cloudflare WARP", "warp-cli.exe"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { candidates.Add(Path.Combine(part, "warp-cli.exe")); } catch { }
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }

    public async Task<WarpCommandResult> RunAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var command = "warp-cli " + string.Join(' ', args);
        var stopwatch = Stopwatch.StartNew();
        if (!Available)
            return new WarpCommandResult(false, -1, "", "warp-cli.exe not found", command, stopwatch.Elapsed);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = WarpCliPath!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(WarpCliPath!) ?? Environment.CurrentDirectory
            };
            psi.ArgumentList.Add("--accept-tos");
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return new WarpCommandResult(false, -1, "", "warp-cli did not start", command, stopwatch.Elapsed);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                var partialOut = await SafeReadAsync(stdoutTask);
                var partialErr = await SafeReadAsync(stderrTask);
                var timeoutMessage = $"timed out after {timeout.TotalSeconds:0}s";
                _log.Warn($"warp-cli {string.Join(' ', args)} {timeoutMessage}");
                return new WarpCommandResult(false, -2, partialOut, partialErr + "\n" + timeoutMessage, command, stopwatch.Elapsed);
            }

            var stdout = await SafeReadAsync(stdoutTask);
            var stderr = await SafeReadAsync(stderrTask);
            var result = new WarpCommandResult(
                process.ExitCode == 0,
                process.ExitCode,
                stdout.Trim(),
                stderr.Trim(),
                command,
                stopwatch.Elapsed);

            _log.Debug($"{command} => exit {result.ExitCode} in {result.Duration.TotalSeconds:0.0}s" +
                       (result.Combined.Length > 0 ? $"\n{TrimForLog(result.Combined)}" : ""));
            if (!result.Ok)
                _log.Warn($"{command} failed with exit {result.ExitCode}: {FirstLine(result.Combined)}");
            return result;
        }
        catch (Exception ex)
        {
            _log.Error($"{command} failed", ex);
            return new WarpCommandResult(false, -1, "", ex.Message, command, stopwatch.Elapsed);
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task; } catch { return ""; }
    }

    public async Task<WarpCommandResult> GetStatusAsync(CancellationToken cancellationToken)
        => await RunAsync(new[] { "status" }, TimeSpan.FromSeconds(15), cancellationToken);

    public async Task<WarpCommandResult> GetSettingsAsync(CancellationToken cancellationToken)
        => await RunAsync(new[] { "settings" }, TimeSpan.FromSeconds(20), cancellationToken);

    public static bool IsConnected(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var match = StatusLineRegex().Match(line);
            if (match.Success)
                return match.Groups[1].Value.Equals("Connected", StringComparison.OrdinalIgnoreCase);

            if (line.StartsWith("Status update:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["Status update:".Length..].Trim();
                return value.Equals("Connected", StringComparison.OrdinalIgnoreCase);
            }
        }

        return text.Contains("Status update: Connected", StringComparison.OrdinalIgnoreCase)
               && !text.Contains("Status update: Disconnected", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<WarpTrace> TraceAsync(CancellationToken cancellationToken)
        => await TraceProbe.GetAsync($"http://127.0.0.1:{_settings.WarpProxyPort}", 16, cancellationToken);

    /// <summary>
    /// Checks that the local WARP proxy can actually open a CONNECT tunnel to a
    /// specific host. The Cloudflare trace endpoint can stay reachable while an
    /// individual destination is temporarily refused with 502.
    /// </summary>
    public async Task<WarpProbeResult> ProbeHostAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        if (!Available)
            return new WarpProbeResult(false, "", "warp-cli.exe was not found");

        var target = $"{host}:{port}";
        try
        {
            using var client = new TcpClient();
            client.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await client.ConnectAsync("127.0.0.1", _settings.WarpProxyPort, timeout.Token);

            var stream = client.GetStream();
            var request =
                $"CONNECT {target} HTTP/1.1\r\n" +
                $"Host: {target}\r\n" +
                "Proxy-Connection: Keep-Alive\r\n" +
                "Connection: Keep-Alive\r\n" +
                "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);

            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var statusLine = await reader.ReadLineAsync(timeout.Token) ?? "";
            string? line;
            do
            {
                line = await reader.ReadLineAsync(timeout.Token);
            } while (!string.IsNullOrEmpty(line));

            var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var ok = parts.Length >= 2 && int.TryParse(parts[1], out var status) && status == 200;
            return new WarpProbeResult(ok, statusLine, ok ? "" : statusLine);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WarpProbeResult(false, "", "probe timed out");
        }
        catch (Exception ex)
        {
            return new WarpProbeResult(false, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<WarpReadyResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!Available)
            return new WarpReadyResult(false, "Cloudflare WARP CLI (warp-cli.exe) was not found.", null);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await EnsureReadyCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forces a clean disconnect/connect cycle. Used when the trace is healthy but
    /// individual WARP tunnels keep failing (for example a half-open tunnel).
    /// </summary>
    public async Task<WarpReadyResult> ReconnectAsync(CancellationToken cancellationToken)
    {
        if (!Available)
            return new WarpReadyResult(false, "Cloudflare WARP CLI (warp-cli.exe) was not found.", null);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _log.Info("Recreating the WARP tunnel; OpenCode stays open.");
            await RunAsync(new[] { "disconnect" }, TimeSpan.FromSeconds(20), cancellationToken);
            await Task.Delay(500, cancellationToken);
            return await EnsureReadyCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WarpReadyResult> EnsureReadyCoreAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        var connected = IsConnected(status.Combined);
        _log.Info($"WARP status: {(connected ? "Connected" : "Not connected")}");

        if (!connected)
        {
            var registration = await RunAsync(new[] { "registration", "show" }, TimeSpan.FromSeconds(25), cancellationToken);
            var missing = !registration.Ok
                          || registration.Combined.Contains("Missing", StringComparison.OrdinalIgnoreCase)
                          || registration.Combined.Contains("no registration", StringComparison.OrdinalIgnoreCase);
            if (missing)
            {
                _log.Info("WARP registration is missing; creating one.");
                var newRegistration = await RunAsync(new[] { "registration", "new" }, TimeSpan.FromSeconds(120), cancellationToken);
                if (!newRegistration.Ok)
                    return new WarpReadyResult(false, "Could not create a WARP registration: " + FirstLine(newRegistration.Combined), null);
            }

            await RunAsync(new[] { "mode", "proxy" }, TimeSpan.FromSeconds(20), cancellationToken);
            await RunAsync(new[] { "proxy", "port", _settings.WarpProxyPort.ToString() }, TimeSpan.FromSeconds(20), cancellationToken);
            await RunAsync(new[] { "connect" }, TimeSpan.FromSeconds(30), cancellationToken);
        }
        else
        {
            // Keep the existing tunnel untouched, but make sure it is the loopback proxy mode this app needs.
            var settings = await GetSettingsAsync(cancellationToken);
            var proxyMode = settings.Combined.Contains("WarpProxy", StringComparison.OrdinalIgnoreCase)
                            || settings.Combined.Contains("proxy on port", StringComparison.OrdinalIgnoreCase);
            if (!proxyMode)
            {
                _log.Info("WARP is connected in another mode; switching it to local proxy mode (system traffic stays direct).");
                await RunAsync(new[] { "disconnect" }, TimeSpan.FromSeconds(20), cancellationToken);
                await RunAsync(new[] { "mode", "proxy" }, TimeSpan.FromSeconds(20), cancellationToken);
                await RunAsync(new[] { "proxy", "port", _settings.WarpProxyPort.ToString() }, TimeSpan.FromSeconds(20), cancellationToken);
                await RunAsync(new[] { "connect" }, TimeSpan.FromSeconds(30), cancellationToken);
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(12, _settings.WarpWaitSeconds));
        WarpTrace? lastTrace = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, cancellationToken);
            lastTrace = await TraceAsync(cancellationToken);
            if (lastTrace.WarpOn)
            {
                _log.Info($"WARP is ready: exit IP {lastTrace.Ip} ({lastTrace.Colo}).");
                return new WarpReadyResult(true, "WARP is ready.", lastTrace);
            }
        }

        return new WarpReadyResult(
            false,
            "WARP is connected, but the trace did not report warp=on. " + (lastTrace?.Error ?? "The local proxy did not answer."),
            lastTrace);
    }

    /// <summary>
    /// Drops the current WARP registration, registers again, reconnects the local proxy,
    /// and waits until the exit IP actually changes. OpenCode is never touched here.
    /// </summary>
    public async Task<RotationResult> RotateAsync(string reason, bool force, CancellationToken cancellationToken)
    {
        if (!Available)
            return new RotationResult(false, "", "", "", 0, TimeSpan.Zero, "warp-cli.exe not found", null);

        await _gate.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRotation;
            var required = TimeSpan.FromSeconds(_settings.MinSecondsBetweenRotations);
            if (!force && _lastRotation != DateTimeOffset.MinValue && elapsed < required)
            {
                var wait = required - elapsed;
                _log.Info($"Waiting {wait.TotalSeconds:0}s so Cloudflare does not throttle the new registration.");
                await Task.Delay(wait, cancellationToken);
            }

            var before = await TraceAsync(cancellationToken);
            var oldIp = before.Ok ? before.Ip : "";
            _log.Info($"Rotating WARP for {reason}. Current exit IP: {(oldIp.Length > 0 ? oldIp : "unknown")}.");

            var attempts = Math.Max(1, _settings.RotationAttempts);
            string? lastError = null;
            WarpTrace? lastTrace = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _log.Info($"Rotation attempt {attempt}/{attempts}: disconnecting WARP.");
                await RunAsync(new[] { "disconnect" }, TimeSpan.FromSeconds(20), cancellationToken);
                await RunAsync(new[] { "registration", "delete" }, TimeSpan.FromSeconds(35), cancellationToken);

                _log.Info($"Rotation attempt {attempt}/{attempts}: creating a new WARP registration.");
                var registration = await RunAsync(new[] { "registration", "new" }, TimeSpan.FromSeconds(120), cancellationToken);
                if (!registration.Ok)
                {
                    lastError = FirstLine(registration.Combined);
                    _log.Warn($"Registration failed: {lastError}");
                    if (registration.Combined.Contains("429", StringComparison.OrdinalIgnoreCase)
                        || registration.Combined.Contains("too many", StringComparison.OrdinalIgnoreCase))
                    {
                        var backoff = TimeSpan.FromSeconds(15 + attempt * 10);
                        _log.Warn($"Cloudflare is throttling registrations; waiting {backoff.TotalSeconds:0}s.");
                        await Task.Delay(backoff, cancellationToken);
                    }
                    continue;
                }

                await RunAsync(new[] { "mode", "proxy" }, TimeSpan.FromSeconds(20), cancellationToken);
                await RunAsync(new[] { "proxy", "port", _settings.WarpProxyPort.ToString() }, TimeSpan.FromSeconds(20), cancellationToken);
                await RunAsync(new[] { "connect" }, TimeSpan.FromSeconds(30), cancellationToken);

                var deadline = DateTime.UtcNow.AddSeconds(Math.Max(12, _settings.WarpWaitSeconds));
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(1000, cancellationToken);
                    lastTrace = await TraceAsync(cancellationToken);
                    if (!lastTrace.WarpOn)
                    {
                        lastError = lastTrace.Error ?? "trace did not report warp=on";
                        continue;
                    }

                    var changed = oldIp.Length == 0 || !TraceProbe.SameExit(oldIp, lastTrace.Ip);
                    if (changed)
                    {
                        _lastRotation = DateTimeOffset.UtcNow;
                        var result = new RotationResult(
                            true,
                            oldIp,
                            lastTrace.Ip,
                            lastTrace.Colo,
                            attempt,
                            stopwatch.Elapsed,
                            null,
                            lastTrace);
                        _log.Info($"Rotation done in {stopwatch.Elapsed.TotalSeconds:0.0}s: " +
                                  $"{DisplayIp(oldIp)} -> {lastTrace.Ip} ({lastTrace.Colo}).");
                        return result;
                    }

                    lastError = "the exit IP did not change";
                }

                _log.Warn($"Rotation attempt {attempt}/{attempts} did not produce a new exit IP: {lastError}");
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }

            _lastRotation = DateTimeOffset.UtcNow;
            var failure = new RotationResult(
                false,
                oldIp,
                lastTrace?.Ip ?? "",
                lastTrace?.Colo ?? "",
                attempts,
                stopwatch.Elapsed,
                lastError ?? "no new WARP exit IP",
                lastTrace);
            _log.Error($"WARP rotation failed after {attempts} attempt(s): {failure.Error}");
            return failure;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("WARP rotation crashed", ex);
            return new RotationResult(false, "", "", "", 0, stopwatch.Elapsed, ex.Message, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string DisplayIp(string ip) => string.IsNullOrWhiteSpace(ip) ? "unknown" : ip;

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return line.Length > 240 ? line[..240] : line;
    }

    private static string TrimForLog(string text)
    {
        text = text.Replace("\r", "");
        return text.Length > 2500 ? text[..2500] + "..." : text;
    }

    [GeneratedRegex(@"^Status update:\s*(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex StatusLineRegex();
}
