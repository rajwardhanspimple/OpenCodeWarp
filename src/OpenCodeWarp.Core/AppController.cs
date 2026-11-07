namespace OpenCodeWarp;

public sealed class AppController : IAsyncDisposable
{
    private readonly SemaphoreSlim _rotationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _failureLock = new();
    private Task? _healthLoop;
    private bool _started;
    private bool _disposed;
    private int _recoveryRunning;
    private int _tunnelFailureCount;
    private DateTimeOffset _tunnelFailureWindow = DateTimeOffset.MinValue;

    public AppSettings Settings { get; }
    public AppLog Log { get; }
    public AiHostCatalog AiHosts { get; }
    public HostPolicy Policy { get; }
    public RelayProxy Relay { get; }
    public WarpClient Warp { get; }
    public OpenCodeLauncher OpenCode { get; }
    public QuotaWatcher Watcher { get; }

    public bool IsStarted => _started;
    public bool IsRotating { get; private set; }
    public bool IsRecovering => Volatile.Read(ref _recoveryRunning) != 0;
    public int RotationCount { get; private set; }
    public string? LastExitIp { get; private set; }
    public string? LastColo { get; private set; }
    public DateTime? LastRotationAt { get; private set; }
    public WarpTrace? LastTrace { get; private set; }
    public WarpTrace? LastRelayTrace { get; private set; }
    public string? LastError { get; private set; }
    public bool WarpOnline => LastTrace?.WarpOn == true;
    public bool OpenCodeRunning => OpenCode.IsRunning;

    public string ProxyUrl => $"http://127.0.0.1:{Relay.BoundPort}";

    public event Action? StateChanged;
    public event Action<QuotaHit>? QuotaDetected;
    public event Action<RotationResult>? RotationFinished;
    public event Action<OpenCodeLaunchResult>? OpenCodeStarted;
    public event Action<int>? OpenCodeExited;

    public AppController(AppSettings settings, AppLog log)
    {
        Settings = settings;
        Log = log;
        AiHosts = new AiHostCatalog(settings, log);
        Policy = new HostPolicy(settings, AiHosts);
        Relay = new RelayProxy(settings.RelayPort, settings.WarpProxyPort, Policy, log, settings.LogDirectConnections);
        Warp = new WarpClient(settings, log);
        OpenCode = new OpenCodeLauncher(settings, log);
        Watcher = new QuotaWatcher(settings, log, HandleQuotaAsync);

        Relay.StatsChanged += RaiseStateChanged;
        Relay.WarpTunnelFailed += OnWarpTunnelFailed;
        OpenCode.StateChanged += RaiseStateChanged;
        OpenCode.Exited += OnOpenCodeExited;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            return;

        Log.Info("=== OpenCode Warp Relay starting ===");
        Log.Info($"WARP scope: {Policy.ScopeDescription}; AI host rules: {AiHosts.Count}; " +
                 $"relay port: {Settings.RelayPort}; WARP proxy port: {Settings.WarpProxyPort}.");

        await Relay.StartAsync(cancellationToken);
        _started = true;

        var ready = await Warp.EnsureReadyAsync(cancellationToken);
        ApplyReady(ready);
        if (ready.Success)
        {
            try { await TestRelayAsync(cancellationToken); }
            catch (Exception ex) { Log.Warn($"End-to-end relay check failed: {ex.Message}"); }
        }
        else
        {
            Log.Warn("WARP is not ready yet. The relay is listening, but OpenCode will not be launched " +
                     "until WARP reports warp=on.");
        }

        Watcher.Start();
        _healthLoop = Task.Run(() => HealthLoopAsync(_lifetime.Token));

        if (Settings.AutoLaunchOpenCode)
        {
            try
            {
                var result = await LaunchOpenCodeAsync(cancellationToken);
                if (!result.Success)
                    Log.Warn("Auto-launch did not start OpenCode: " + result.Message);
            }
            catch (Exception ex)
            {
                Log.Error("Auto-launch failed", ex);
            }
        }

        RaiseStateChanged();
    }

    public async Task<bool> EnsureWarpAsync(CancellationToken cancellationToken = default)
    {
        var ready = await Warp.EnsureReadyAsync(cancellationToken);
        ApplyReady(ready);
        RaiseStateChanged();
        return ready.Success;
    }

    public async Task<OpenCodeLaunchResult> LaunchOpenCodeAsync(CancellationToken cancellationToken = default)
    {
        if (OpenCode.IsRunning)
            return new OpenCodeLaunchResult(true, OpenCode.ProcessId ?? 0, OpenCode.LastExecutable ?? "", "OpenCode is already running.");

        if (!Relay.IsRunning)
            await Relay.StartAsync(cancellationToken);

        var trace = await Warp.TraceAsync(cancellationToken);
        if (!trace.WarpOn)
        {
            Log.Warn("WARP is not reporting warp=on; attempting to reconnect before launching OpenCode.");
            var ready = await Warp.EnsureReadyAsync(cancellationToken);
            ApplyReady(ready);
            if (!ready.Success)
                throw new InvalidOperationException("WARP is not ready: " + ready.Message);
            trace = ready.Trace ?? trace;
        }

        LastTrace = trace;
        LastExitIp = trace.Ip;
        LastColo = trace.Colo;

        var result = await OpenCode.StartAsync(ProxyUrl, cancellationToken);
        if (result.Success)
        {
            LastError = null;
            Log.Info($"OpenCode is using {ProxyUrl}. Search/web fetch traffic follows the AI-only split " +
                     "when that scope is selected, so those tools keep working.");
            OpenCodeStarted?.Invoke(result);
        }
        else
        {
            LastError = result.Message;
            Log.Error("OpenCode could not be started: " + result.Message);
        }

        RaiseStateChanged();
        return result;
    }

    public async Task StopOpenCodeAsync()
    {
        await OpenCode.StopAsync();
        RaiseStateChanged();
    }

    /// <summary>
    /// Rotates WARP and resets stale WARP tunnels. It deliberately never calls
    /// OpenCode.StopAsync, so a free-limit hit cannot close or restart the TUI.
    /// </summary>
    public async Task<RotationResult> RotateAsync(
        string reason,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _rotationGate.WaitAsync(cancellationToken);
        try
        {
            IsRotating = true;
            LastError = null;
            Relay.PauseWarp("rotation");
            RaiseStateChanged();
            Log.Warn($"Rotation requested: {reason}. OpenCode will stay open; new AI connections wait for the tunnel.");

            var result = await Warp.RotateAsync(reason, force, cancellationToken);
            if (result.Success)
            {
                LastTrace = result.LastTrace;
                LastExitIp = result.NewIp;
                LastColo = result.Colo;
                LastRotationAt = DateTime.Now;
                RotationCount++;
                LastError = null;

                var closed = Relay.ResetWarpConnections();
                Log.Info($"New exit IP {result.NewIp} is live; closed {closed} stale WARP tunnel(s). " +
                         "OpenCode was not restarted.");
            }
            else
            {
                LastError = result.Error ?? "WARP rotation failed";
                var closed = Relay.ResetWarpConnections();
                Log.Warn($"Rotation failed ({LastError}); closed {closed} WARP tunnel(s). " +
                         "Trying to restore WARP before releasing waiting connections.");

                try
                {
                    var ready = await Warp.EnsureReadyAsync(cancellationToken);
                    ApplyReady(ready);
                }
                catch (Exception ex)
                {
                    Log.Warn("WARP recovery after a failed rotation did not complete: " + ex.Message);
                }
            }

            // Release waiting AI connections only after the new tunnel (or a repair
            // attempt) is ready. They then connect without seeing a rotation 502.
            Relay.ResumeWarp();

            if (result.Success)
            {
                try { await TestRelayAsync(cancellationToken); }
                catch (Exception ex) { Log.Debug("Relay check after rotation: " + ex.Message); }
            }

            RotationFinished?.Invoke(result);
            return result;
        }
        finally
        {
            Relay.ResumeWarp();
            IsRotating = false;
            _rotationGate.Release();
            RaiseStateChanged();
        }
    }

    private async Task HandleQuotaAsync(QuotaHit hit)
    {
        Log.Warn($"Quota detector fired: {hit.Kind} at {hit.DetectedAt:HH:mm:ss} " +
                 $"({Path.GetFileName(hit.File)})" +
                 (hit.SessionId is null ? "." : $" for {hit.SessionId}."));
        QuotaDetected?.Invoke(hit);

        if (hit.Kind == QuotaKind.UsageLimit)
        {
            Log.Warn("Free usage limit detected. No model switch or IP rotation was attempted; " +
                     "the provider may require its account quota to reset.");
            return;
        }

        if (hit.Kind == QuotaKind.TransportFailure)
        {
            Log.Warn("OpenCode reported a connection reset. Reconnecting WARP and checking the OpenCode endpoint.");
            await RecoverWarpAsync(new WarpTunnelFailure(
                "opencode.ai", 443, hit.Line, hit.DetectedAt));
            return;
        }

        if (Settings.RotateOnRateLimit)
            await RotateAsync("rate limit", false, _lifetime.Token);
    }

    private void OnWarpTunnelFailed(WarpTunnelFailure failure)
    {
        if (IsRotating || IsRecovering)
            return;

        lock (_failureLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _tunnelFailureWindow > TimeSpan.FromSeconds(30))
            {
                _tunnelFailureWindow = now;
                _tunnelFailureCount = 0;
            }

            _tunnelFailureCount++;
            if (_tunnelFailureCount < 2)
                return;
            _tunnelFailureCount = 0;
        }

        _ = Task.Run(() => RecoverWarpAsync(failure));
    }

    private async Task RecoverWarpAsync(WarpTunnelFailure failure)
    {
        if (Interlocked.Exchange(ref _recoveryRunning, 1) != 0)
            return;

        try
        {
            await _rotationGate.WaitAsync(_lifetime.Token);
            try
            {
                if (IsRotating)
                    return;

                Log.Warn($"Repeated WARP tunnel failures (last: {failure.Host}:{failure.Port}: " +
                         $"{failure.Error}); repairing the tunnel. OpenCode stays open.");
                Relay.PauseWarp("tunnel repair");
                try
                {
                    var ready = await Warp.ReconnectAsync(_lifetime.Token);
                    ApplyReady(ready);

                    var probe = await Warp.ProbeHostAsync("opencode.ai", 443, _lifetime.Token);
                    if (!probe.Ok)
                    {
                        Log.Warn("opencode.ai is still not reachable through WARP after reconnect: " +
                                 (probe.Error.Length > 0 ? probe.Error : probe.StatusLine));
                    }

                    var closed = Relay.ResetWarpConnections();
                    Log.Info($"WARP tunnel repair finished; closed {closed} stale tunnel(s).");

                    if (ready.Success)
                    {
                        try { await TestRelayAsync(_lifetime.Token); }
                        catch (Exception ex) { Log.Debug("Relay check after tunnel repair: " + ex.Message); }
                    }
                }
                finally
                {
                    Relay.ResumeWarp();
                }
            }
            finally
            {
                _rotationGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("WARP tunnel recovery failed: " + ex.Message);
            Relay.ResumeWarp();
        }
        finally
        {
            Interlocked.Exchange(ref _recoveryRunning, 0);
            RaiseStateChanged();
        }
    }

    public async Task<WarpTrace> TestWarpAsync(CancellationToken cancellationToken = default)
    {
        var trace = await Warp.TraceAsync(cancellationToken);
        LastTrace = trace;
        if (trace.Ok)
        {
            LastExitIp = trace.Ip;
            LastColo = trace.Colo;
            LastError = null;
        }
        else
        {
            LastError = trace.Error;
        }
        RaiseStateChanged();
        return trace;
    }

    public async Task<WarpTrace> TestRelayAsync(CancellationToken cancellationToken = default)
    {
        // opencode.ai is in the AI host catalog, so this proves the relay goes through WARP
        // in AI-only mode as well as in all-traffic mode.
        var trace = await TraceProbe.GetAsync(
            ProxyUrl,
            "https://opencode.ai/cdn-cgi/trace",
            15,
            cancellationToken);
        LastRelayTrace = trace;
        if (trace.WarpOn)
            Log.Info($"Relay end-to-end check: warp=on, exit IP {trace.Ip} ({trace.Colo}).");
        else
            Log.Warn($"Relay end-to-end check did not report warp=on: {trace.Error ?? "warp=" + trace.Warp}");
        RaiseStateChanged();
        return trace;
    }

    public void RefreshHosts()
    {
        AiHosts.Reload();
        Policy.ReloadDirectRules();
        Log.Info($"AI host rules refreshed: {AiHosts.Count} rule(s).");
        RaiseStateChanged();
    }

    public void SaveSettings()
    {
        Settings.Normalize();
        Settings.Save();
        AiHosts.Reload();
        Policy.ReloadDirectRules();
        Log.SetLevel(Settings.LogLevel);
        Log.Debug("Settings saved.");
        RaiseStateChanged();
    }

    private void ApplyReady(WarpReadyResult ready)
    {
        if (ready.Trace is not null)
            LastTrace = ready.Trace;
        if (ready.Success)
        {
            LastError = null;
            if (ready.Trace is not null)
            {
                LastExitIp = ready.Trace.Ip;
                LastColo = ready.Trace.Colo;
            }
        }
        else
        {
            LastError = ready.Message;
            Log.Warn("WARP not ready: " + ready.Message);
        }
    }

    private async Task HealthLoopAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        var delay = TimeSpan.FromSeconds(Settings.HealthCheckSeconds);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
                if (IsRotating || IsRecovering || !Relay.IsRunning)
                    continue;

                var trace = await Warp.TraceAsync(cancellationToken);
                if (trace.WarpOn)
                {
                    failures = 0;
                    delay = TimeSpan.FromSeconds(Settings.HealthCheckSeconds);
                    LastTrace = trace;
                    LastExitIp = trace.Ip;
                    LastColo = trace.Colo;
                    LastError = null;
                    RaiseStateChanged();
                    continue;
                }

                failures++;
                delay = TimeSpan.FromSeconds(Math.Min(5, Math.Max(2, Settings.HealthCheckSeconds / 3)));
                LastError = trace.Error ?? "warp=" + trace.Warp;
                Log.Warn($"WARP health check failed ({failures}): {LastError}");
                if (failures < 2)
                {
                    // A single slow trace should not make a healthy tunnel look offline.
                    RaiseStateChanged();
                    continue;
                }

                failures = 0;
                LastTrace = trace;
                RaiseStateChanged();
                Relay.PauseWarp("health-check reconnect");
                try
                {
                    var ready = await Warp.EnsureReadyAsync(cancellationToken);
                    ApplyReady(ready);
                    if (ready.Success)
                    {
                        Log.Info("WARP reconnected during a health check; resetting WARP tunnels.");
                        Relay.ResetWarpConnections();
                    }
                }
                finally
                {
                    Relay.ResumeWarp();
                }
                RaiseStateChanged();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("WARP health loop error: " + ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); } catch { break; }
            }
        }
    }

    private void OnOpenCodeExited(int exitCode)
    {
        try { OpenCodeExited?.Invoke(exitCode); } catch { }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    public async Task ShutdownAsync(bool stopOpenCode = true)
    {
        if (_disposed)
            return;
        _disposed = true;

        Log.Info("Shutting down relay.");
        try { _lifetime.Cancel(); } catch { }
        if (_healthLoop is not null)
        {
            try { await _healthLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            _healthLoop = null;
        }

        if (stopOpenCode)
            await OpenCode.StopAsync();
        else if (OpenCode.IsRunning)
            Log.Warn("OpenCode is being left running, but its relay is stopping; it will lose model connectivity.");

        await Watcher.StopAsync();
        await Relay.StopAsync();
        _lifetime.Dispose();
        Log.Info("=== OpenCode Warp Relay stopped ===");
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(stopOpenCode: false);
        OpenCode.Dispose();
        _rotationGate.Dispose();
    }
}
