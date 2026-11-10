namespace OpenCodeWarp;

/// <summary>Everything the UI needs to ask the user before an automatic IP change.</summary>
public sealed record RotationPrompt(string Reason, string CurrentIp);

/// <summary>Asks the user before restarting the opencode background service for its new proxy env.</summary>
public sealed record ServiceRestartPrompt(string Url, int? Pid);

public sealed class AppController : IAsyncDisposable
{
    private readonly SemaphoreSlim _rotationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _failureLock = new();
    private Task? _healthLoop;
    private Task? _autoRotateLoop;
    private DateTimeOffset _lastRotationUtc = DateTimeOffset.MinValue;
    private bool _started;
    private bool _disposed;
    private int _recoveryRunning;
    private int _tunnelFailureCount;
    private int _confirmInFlight;
    private DateTimeOffset _tunnelFailureWindow = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProbeBurnedAt = DateTimeOffset.MinValue;

    public AppSettings Settings { get; }
    public AppLog Log { get; }
    public AiHostCatalog AiHosts { get; }
    public HostPolicy Policy { get; }
    public RelayProxy Relay { get; }
    public WarpClient Warp { get; }
    public OpenCodeLauncher OpenCode { get; }
    public OpenCodeUsabilityProbe Probe { get; }
    public OpenCodeServiceManager Service { get; }
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

    /// <summary>
    /// True once the relay proved that its traffic exits through the current WARP IP;
    /// false when the last check could not confirm it; null before the first check.
    /// </summary>
    public bool? LastRelayVerified { get; private set; }

    /// <summary>Outcome of the last DeepSeek-free-model usability probe; null when none ran yet.</summary>
    public UsabilityProbeResult? LastProbe { get; private set; }

    /// <summary>Latest known state of the opencode background service; null before the first check.</summary>
    public OpenCodeServiceState? LastServiceState => Service.CurrentState;
    public string? LastError { get; private set; }
    public bool WarpOnline => LastTrace?.WarpOn == true;
    public bool OpenCodeRunning => OpenCode.IsRunning;

    public string ProxyUrl => $"http://127.0.0.1:{Relay.BoundPort}";

    /// <summary>The local WARP proxy endpoint, read live so port changes apply immediately.</summary>
    public string WarpProxyUrl => $"http://127.0.0.1:{Settings.WarpProxyPort}";

    public event Action? StateChanged;
    public event Action<QuotaHit>? QuotaDetected;
    public event Action<RotationResult>? RotationFinished;
    public event Action<OpenCodeLaunchResult>? OpenCodeStarted;
    public event Action<int>? OpenCodeExited;

    /// <summary>
    /// Set by the UI. Every automatic rotation (quota hit, rate limit, scheduled
    /// timer) calls it first and only rotates when the user allows the IP change.
    /// Manual rotations from the dashboard button do not ask. When no handler is
    /// set (headless CLI use), automatic rotations proceed.
    /// </summary>
    public Func<RotationPrompt, Task<bool>>? ConfirmRotation { get; set; }

    /// <summary>
    /// Set by the UI. Asked once whenever the background service's proxy env was just
    /// written while the service is already running: restarting applies the new
    /// environment but drops in-flight opencode api requests. Without a handler
    /// (headless CLI use) the restart proceeds.
    /// </summary>
    public Func<ServiceRestartPrompt, Task<bool>>? ConfirmServiceRestart { get; set; }

    public AppController(AppSettings settings, AppLog log)
    {
        Settings = settings;
        Log = log;
        AiHosts = new AiHostCatalog(settings, log);
        Policy = new HostPolicy(settings, AiHosts);
        Relay = new RelayProxy(settings.RelayPort, settings.WarpProxyPort, Policy, log, settings.LogDirectConnections);
        Warp = new WarpClient(settings, log);
        OpenCode = new OpenCodeLauncher(settings, log);
        Probe = new OpenCodeUsabilityProbe(settings, OpenCode, log);
        Service = new OpenCodeServiceManager(settings, log, OpenCode);
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
        _lastRotationUtc = DateTimeOffset.Now;

        var ready = await Warp.EnsureReadyAsync(cancellationToken);
        ApplyReady(ready);
        if (ready.Success)
        {
            try { await VerifyWarpExitIpAsync(ready.Trace?.Ip ?? "", cancellationToken); }
            catch (Exception ex) { Log.Warn($"End-to-end tunnel check failed: {ex.Message}"); }
        }
        else
        {
            Log.Warn("WARP is not ready yet. The relay is listening, but OpenCode will not be launched " +
                     "until WARP reports warp=on.");
        }

        // Wire the opencode background service (the opencode api path used by the
        // opencode-proxy bridge and other API clients) before anything can use it.
        await EnsureServiceAsync(cancellationToken);

        Watcher.Start();
        _healthLoop = Task.Run(() => HealthLoopAsync(_lifetime.Token));
        _autoRotateLoop = Task.Run(() => AutoRotateLoopAsync(_lifetime.Token));

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

        // Serialize against rotations: launching the TUI into a mid-rotation relay
        // would start it against a paused (or about-to-change) tunnel.
        await _rotationGate.WaitAsync(cancellationToken);
        try
        {
            return await LaunchOpenCodeCoreAsync(cancellationToken);
        }
        finally
        {
            _rotationGate.Release();
        }
    }

    private async Task<OpenCodeLaunchResult> LaunchOpenCodeCoreAsync(CancellationToken cancellationToken)
    {
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
    /// Rotates WARP, verifies the new exit IP through the relay, and then proves the
    /// IP is actually usable by sending a one-word prompt to the free DeepSeek model
    /// through the OpenCode CLI. The probe is compulsory: a burned IP (usage limit,
    /// 429, no response) triggers another rotation, up to RotationAttempts rounds.
    /// It deliberately never calls OpenCode.StopAsync.
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
            LastRelayVerified = null;
            LastProbe = null;
            Relay.PauseWarp("rotation");
            RaiseStateChanged();
            Log.Warn($"Rotation requested: {reason}. OpenCode will stay open; new AI connections wait for the tunnel.");

            // Each round is a full rotate + verify + probe cycle.
            var rounds = Math.Max(1, Settings.RotationAttempts);
            RotationResult? result = null;

            for (var round = 1; round <= rounds; round++)
            {
                var roundReason = round == 1 ? reason : $"{reason} (burned-IP retry {round - 1})";
                result = await Warp.RotateAsync(roundReason, force && round == 1, cancellationToken);
                _lastRotationUtc = DateTimeOffset.UtcNow;

                if (!result.Success)
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
                    break;
                }

                LastTrace = result.LastTrace;
                LastExitIp = result.NewIp;
                LastColo = result.Colo;
                LastRotationAt = DateTime.Now;
                RotationCount++;
                LastError = null;

                var openTunnels = Relay.ResetWarpConnections();
                Log.Info($"New exit IP {result.NewIp} is live; closed {openTunnels} stale WARP tunnel(s). " +
                         "OpenCode was not restarted.");

                // The WARP gate stays closed for the whole rotation: waiting AI connections
                // are only released after the new IP has passed verification AND the
                // usability probe, so nothing flows through an unproven (possibly burned)
                // exit. Both checks dial the WARP proxy directly, which the gate does not
                // block.
                try { await VerifyWarpExitIpAsync(result.NewIp, cancellationToken); }
                catch (Exception ex) { Log.Warn("Exit IP verification after rotation failed: " + ex.Message); }

                Log.Info($"Usability probe: asking {OpenCodeUsabilityProbe.ProbeModel} through the new IP " +
                         "via the OpenCode CLI...");
                var probe = await Probe.ProbeAsync(WarpProxyUrl, cancellationToken);
                LastProbe = probe;
                if (!probe.Usable && probe.RotateAgain)
                    _lastProbeBurnedAt = DateTimeOffset.Now;
                RaiseStateChanged();

                if (probe.Usable)
                {
                    Log.Info($"Usability probe PASSED in {probe.Duration.TotalSeconds:0.0}s: {probe.Detail} " +
                             "The new IP is genuinely usable for model traffic.");
                    break;
                }

                if (round >= rounds)
                {
                    LastError = $"The usability probe failed on every rotated IP: {probe.Detail}";
                    Log.Warn($"Usability probe FAILED again ({probe.Detail}); this was the last rotation round. " +
                             "OpenCode keeps its session and can retry on the next trigger.");
                    break;
                }

                if (!probe.RotateAgain)
                {
                    LastError = $"The usability probe failed for a reason that rotating cannot fix: {probe.Detail}";
                    Log.Warn("Keeping the current IP; not rotating again because: " + probe.Detail);
                    break;
                }

                Log.Warn($"Usability probe FAILED: {probe.Detail} The IP looks burned; the WARP gate stays " +
                         $"closed and the app rotates again (round {round + 1}/{rounds}).");
                LastError = $"The new IP was burned ({probe.Detail}); rotating again.";
                RaiseStateChanged();
            }

            // Never leave the gate closed: if every round was burned, OpenCode should see
            // the provider's own errors rather than wait forever for a healthy tunnel.
            Relay.ResumeWarp();

            if (result!.Success)
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
        // A burned-IP probe writes the same usage-limit lines into OpenCode's log that
        // the watcher tails. The rotation loop has already reacted to it, so do not
        // chain another rotation from our own probe output.
        if ((hit.Kind == QuotaKind.UsageLimit || hit.Kind == QuotaKind.RateLimit)
            && DateTimeOffset.Now - _lastProbeBurnedAt < TimeSpan.FromMinutes(3))
        {
            Log.Info("Ignoring a quota hit that was produced by the usability probe itself; " +
                     "the rotation loop is already handling the burned IP.");
            return;
        }

        if (IsRotating)
        {
            Log.Info("Quota hit ignored: a rotation (with its usability probe) is already running.");
            return;
        }

        Log.Warn($"Quota detector fired: {hit.Kind} at {hit.DetectedAt:HH:mm:ss} " +
                 $"({Path.GetFileName(hit.File)})" +
                 (hit.SessionId is null ? "." : $" for {hit.SessionId}."));
        QuotaDetected?.Invoke(hit);

        if (hit.Kind == QuotaKind.UsageLimit)
        {
            if (Settings.RotateOnUsageLimit)
            {
                if (!await ConfirmChangeAsync("OpenCode hit the free usage limit", _lifetime.Token))
                    return;
                Log.Warn("Free usage limit detected; rotating WARP so OpenCode gets a fresh exit IP. " +
                         "OpenCode stays open. If the provider enforces the limit on the account, " +
                         "the new IP will not restore access on its own.");
                await RotateAsync("free usage limit", false, _lifetime.Token);
                return;
            }

            Log.Warn("Free usage limit detected. Rotate-on-usage-limit is off, so no IP rotation was attempted; " +
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
        {
            if (!await ConfirmChangeAsync("OpenCode was rate-limited (HTTP 429)", _lifetime.Token))
                return;
            await RotateAsync("rate limit", false, _lifetime.Token);
        }
    }

    /// <summary>
    /// Asks the UI whether an automatic IP change is allowed. Without a UI handler
    /// (headless CLI) the change proceeds. Only one confirmation may be open at a
    /// time: a second trigger arriving while a dialog is up is skipped instead of
    /// stacking a second dialog.
    /// </summary>
    private async Task<bool> ConfirmChangeAsync(string reason, CancellationToken cancellationToken)
    {
        var handler = ConfirmRotation;
        if (handler is null)
            return true;

        if (Interlocked.CompareExchange(ref _confirmInFlight, 1, 0) != 0)
        {
            Log.Info("A rotation confirmation is already open; skipping this trigger instead of stacking dialogs.");
            return false;
        }

        try
        {
            var allowed = await handler(new RotationPrompt(reason, LastExitIp ?? ""));
            if (!allowed)
                Log.Warn($"IP change declined by the user ({reason}); no rotation was started.");
            return allowed;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"The rotation confirmation could not be shown ({ex.Message}); no rotation was started.");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _confirmInFlight, 0);
        }
    }

    /// <summary>
    /// Applies the relay proxy to the opencode background service env config. Writes only
    /// what is missing or stale; when a service that is already running predates the
    /// write, the user is asked before it is restarted. Failures are logged, never fatal:
    /// the TUI path keeps working without the service.
    /// </summary>
    private async Task EnsureServiceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Settings.ServiceProxyEnabled)
            {
                await Service.RefreshStateAsync(cancellationToken);
                return;
            }

            var state = await Service.EnsureProxyConfigAsync(ProxyUrl, cancellationToken);
            if (state is { Running: true, RestartNeeded: true })
            {
                if (await ConfirmServiceRestartAsync(state, cancellationToken))
                {
                    await Service.RestartAsync(cancellationToken);
                    state = await Service.RefreshStateAsync(cancellationToken);
                }
                else
                {
                    Log.Warn("The opencode background service was not restarted, so it keeps its old " +
                             "environment: opencode api clients (e.g. the opencode-proxy bridge) do not " +
                             "use the relay until the service is restarted.");
                }
            }

            // Same guarantee the TUI path gets from Launch OpenCode: the dashboard leaves
            // the service running (with the env config it just verified), not the user.
            state = await Service.EnsureRunningAsync(cancellationToken);

            if (state is { Running: true })
                Log.Info($"opencode background service: {state.Url} (pid {state.Pid}) — " +
                         (state.EnvConfigured ? "relay-wired." : "env config incomplete."));
        }
        catch (Exception ex)
        {
            Log.Warn($"opencode background service routing could not be applied: {ex.Message}");
        }
        finally
        {
            RaiseStateChanged();
        }
    }

    /// <summary>Same one-dialog-at-a-time rule as rotation confirmations.</summary>
    private async Task<bool> ConfirmServiceRestartAsync(OpenCodeServiceState state, CancellationToken cancellationToken)
    {
        var handler = ConfirmServiceRestart;
        if (handler is null)
            return true;

        if (Interlocked.CompareExchange(ref _confirmInFlight, 1, 0) != 0)
        {
            Log.Info("A confirmation dialog is already open; the background service restart is skipped " +
                     "instead of stacking a second dialog.");
            return false;
        }

        try
        {
            var allowed = await handler(new ServiceRestartPrompt(state.Url ?? "", state.Pid));
            if (!allowed)
                Log.Warn("Background service restart declined by the user.");
            return allowed;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"The service restart confirmation could not be shown ({ex.Message}); not restarting.");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _confirmInFlight, 0);
        }
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
                        try { await VerifyWarpExitIpAsync(ready.Trace?.Ip ?? "", _lifetime.Token); }
                        catch (Exception ex) { Log.Debug("Tunnel check after repair: " + ex.Message); }
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

    /// <summary>
    /// Proves that the new WARP tunnel really exits via the expected IP by fetching a
    /// trace through the local WARP proxy. This works even while the relay's WARP gate
    /// is closed, which keeps unverified IPs unreachable for OpenCode traffic. Stale
    /// tunnels are closed between attempts so a retry re-dials the fresh registration.
    /// </summary>
    public async Task<bool> VerifyWarpExitIpAsync(string expectedIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedIp))
        {
            Log.Warn("Exit IP verification skipped: the current WARP exit IP is unknown.");
            LastRelayVerified = null;
            RaiseStateChanged();
            return false;
        }

        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var trace = await TraceProbe.GetAsync(
                WarpProxyUrl,
                "https://opencode.ai/cdn-cgi/trace",
                15,
                cancellationToken);
            LastRelayTrace = trace;
            if (trace.Ok && TraceProbe.SameExit(trace.Ip, expectedIp))
            {
                LastRelayVerified = true;
                Log.Info($"Verified: traffic through the tunnel now exits via {trace.Ip} ({trace.Colo}) — " +
                         "the new WARP IP is actually in use.");
                RaiseStateChanged();
                return true;
            }

            if (attempt < attempts)
            {
                Log.Warn($"Exit check {attempt}/{attempts}: the tunnel exits via " +
                         $"{(trace.Ip.Length > 0 ? trace.Ip : "an unknown IP")} but the new WARP IP is {expectedIp}; " +
                         "closing stale tunnels and retrying.");
                Relay.ResetWarpConnections();
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        LastRelayVerified = false;
        LastError = $"The tunnel did not confirm that the WARP IP {expectedIp} is being used.";
        Log.Warn("Exit IP verification failed: OpenCode traffic may still leave through the previous exit. " +
                 "The next rotation, tunnel repair, or health check will retry.");
        RaiseStateChanged();
        return false;
    }

    /// <summary>
    /// Rotates WARP on a fixed interval so the exit IP keeps changing even when
    /// OpenCode's log never shows a quota or rate-limit error. 0 disables the timer.
    /// </summary>
    private async Task AutoRotateLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                var minutes = Settings.AutoRotateMinutes;
                if (minutes <= 0 || IsRotating || IsRecovering || !Relay.IsRunning || !Warp.Available)
                    continue;

                var due = _lastRotationUtc.AddMinutes(minutes);
                if (DateTimeOffset.Now < due)
                    continue;

                if (!await ConfirmChangeAsync($"scheduled rotation is due (every {minutes} minutes)", cancellationToken))
                {
                    // Do not re-ask every 15 seconds; wait a full interval first.
                    _lastRotationUtc = DateTimeOffset.Now;
                    continue;
                }

                Log.Info($"Auto-rotation is due (interval: {minutes} min).");
                await RotateAsync($"scheduled auto-rotation (every {minutes} minutes)", force: false, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn("Auto-rotation loop error: " + ex.Message);
            }
        }
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

        // Unlike the relay's own port, a new WARP proxy port applies live: move the
        // warp-cli listener first, then point the relay at it.
        if (Relay.UpstreamPort != Settings.WarpProxyPort)
        {
            var newPort = Settings.WarpProxyPort;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (Warp.Available)
                    {
                        var result = await Warp.RunAsync(
                            new[] { "proxy", "port", newPort.ToString() },
                            TimeSpan.FromSeconds(20),
                            CancellationToken.None);
                        if (!result.Ok)
                            Log.Warn($"warp-cli proxy port {newPort} failed: {result.Combined.Split('\n').FirstOrDefault()?.Trim()}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not move the WARP proxy port: " + ex.Message);
                }

                Relay.UpstreamPort = newPort;
            });
        }

        // Re-apply the background service env after a save: a relay port change makes the
        // persisted env stale, and a toggled-off feature must remove exactly our keys.
        _ = Task.Run(async () =>
        {
            try
            {
                if (Settings.ServiceProxyEnabled)
                    await EnsureServiceAsync(CancellationToken.None);
                else
                    await Service.RemoveProxyConfigAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warn($"opencode background service env update after settings change failed: {ex.Message}");
            }
            RaiseStateChanged();
        });

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

                // Best-effort: keep the background service state current for the dashboard.
                try { await Service.RefreshStateAsync(cancellationToken); }
                catch { }
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
        if (_autoRotateLoop is not null)
        {
            try { await _autoRotateLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            _autoRotateLoop = null;
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
