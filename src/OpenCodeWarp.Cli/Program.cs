using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace OpenCodeWarp.Cli;

internal static class Program
{
    private const string Version = "1.0.0";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ProxyEnvironment.ClearInherited();
        var options = ParseOptions(args);
        var command = FindCommand(args);
        var verbose = options.Has("verbose");

        if (command is null or "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return command switch
            {
                "status" => await StatusAsync(options),
                "trace" => await TraceAsync(options),
                "rotate" => await RotateAsync(options),
                "proxy" => await ProxyAsync(options),
                "selftest" => await SelfTestAsync(options),
                "split-test" => await SplitTestAsync(options),
                "test-watcher" => await TestWatcherAsync(options),
                "hosts" => Hosts(options),
                "classify" => Classify(options),
                "service" => await ServiceAsync(options, args),
                "run" => await RunAsync(options, launchOpenCode: true),
                "watch" => await RunAsync(options, launchOpenCode: false),
                _ => Unknown(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            if (verbose)
                Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static AppSettings LoadSettings(CliOptions options)
    {
        var settings = AppSettings.Load();
        if (options.TryGet("port", out var portText) && int.TryParse(portText, out var port))
            settings.RelayPort = port;
        if (options.TryGet("warp-port", out var warpPortText) && int.TryParse(warpPortText, out var warpPort))
            settings.WarpProxyPort = warpPort;
        if (options.TryGet("folder", out var folder) && !string.IsNullOrWhiteSpace(folder))
            settings.ProjectFolder = Path.GetFullPath(folder);
        if (options.TryGet("scope", out var scope))
        {
            settings.WarpScope = scope.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? WarpScope.All
                : WarpScope.AiOnly;
        }
        if (options.TryGet("extra-hosts", out var extra))
            settings.ExtraAiHosts = extra;
        settings.Normalize();
        return settings;
    }

    private static AppLog CreateLog(AppSettings settings, bool echo = true, bool verbose = false)
        => new(null, verbose ? "debug" : settings.LogLevel) { EchoToConsole = echo };

    private static async Task<int> StatusAsync(CliOptions options)
    {
        var settings = LoadSettings(options);
        var log = CreateLog(settings, echo: false, options.Has("verbose"));

        Console.WriteLine($"OpenCode Warp Relay {Version}");
        Console.WriteLine($"Settings : {AppPaths.SettingsFile}");
        Console.WriteLine($"Log      : {log.LogFile}");
        Console.WriteLine($"Scope    : {settings.WarpScope} (relay port {settings.RelayPort}, WARP proxy port {settings.WarpProxyPort})");
        Console.WriteLine("Process proxy env: (cleared in-process; only the OpenCode child gets a proxy block)");

        var warp = new WarpClient(settings, log);
        Console.WriteLine($"warp-cli : {warp.WarpCliPath ?? "NOT FOUND"}");

        var status = await warp.GetStatusAsync(CancellationToken.None);
        Console.WriteLine("WARP status:");
        foreach (var line in status.Combined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Console.WriteLine("  " + line);

        var direct = await TraceProbe.GetAsync(null, 15, CancellationToken.None);
        Console.WriteLine($"Direct egress: {(direct.Ok ? direct.Ip + " (" + direct.Colo + ")" : direct.Error)}");

        var viaWarp = await warp.TraceAsync(CancellationToken.None);
        Console.WriteLine($"WARP egress  : {(viaWarp.Ok ? viaWarp.Ip + " (" + viaWarp.Colo + "), warp=" + viaWarp.Warp : viaWarp.Error)}");

        var launcher = new OpenCodeLauncher(settings, log);
        Console.WriteLine($"OpenCode CLI : {launcher.DiscoverExecutable() ?? "NOT FOUND"}");

        var serviceState = await new OpenCodeServiceManager(settings, log, launcher).RefreshStateAsync();
        Console.WriteLine("Background service (opencode api path): " + DescribeService(serviceState));

        Console.WriteLine($"AI host rules: {new AiHostCatalog(settings, log).Count}");
        Console.WriteLine($"Relay port   : {settings.RelayPort} {(IsPortFree(settings.RelayPort) ? "(free)" : "(in use)")}");
        Console.WriteLine();
        Console.WriteLine("Search/web fetch hosts such as mcp.exa.ai are direct in AI-only mode; model calls use WARP.");
        return 0;
    }

    private static string DescribeService(OpenCodeServiceState? state)
    {
        if (state is null)
            return "unknown (check failed)";
        if (!state.Running)
            return state.Managed
                ? "not running (starts on first opencode api use)"
                : "not running (routing is off)";
        var suffix = state.RestartNeeded
            ? " — RESTART NEEDED to pick up the relay proxy"
            : state.EnvConfigured ? " — relay-wired" : " — env config incomplete";
        return $"{state.Url} (pid {state.Pid}){suffix}";
    }

    private static async Task<int> ServiceAsync(CliOptions options, string[] args)
    {
        var sub = args.SkipWhile(a => !string.Equals(a, "service", StringComparison.Ordinal))
            .Skip(1)
            .FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)) ?? "status";

        var settings = LoadSettings(options);
        var log = CreateLog(settings, echo: true, options.Has("verbose"));
        var launcher = new OpenCodeLauncher(settings, log);
        var manager = new OpenCodeServiceManager(settings, log, launcher);

        if (sub is "status")
        {
            PrintService(DescribeService(await manager.RefreshStateAsync()));
            return 0;
        }

        if (sub is not "ensure")
        {
            Console.WriteLine($"Unknown service subcommand: {sub}. Use 'ocwarp service [status|ensure]'.");
            return 2;
        }

        var state = await manager.EnsureProxyConfigAsync($"http://127.0.0.1:{settings.RelayPort}");
        PrintService(DescribeService(state));
        if (state is { Running: true, RestartNeeded: true })
        {
            if (options.Has("restart"))
            {
                Console.WriteLine("Restarting the opencode background service...");
                var restarted = await manager.RestartAsync();
                PrintService(DescribeService(await manager.RefreshStateAsync()));
                return restarted ? 0 : 1;
            }

            Console.WriteLine("The running service predates the env config; re-run with --restart to restart it now.");
            return 1;
        }

        return 0;
    }

    private static void PrintService(string line) => Console.WriteLine("opencode background service: " + line);

    private static async Task<int> TraceAsync(CliOptions options)
    {
        var settings = LoadSettings(options);
        var log = CreateLog(settings, echo: false, options.Has("verbose"));
        var direct = await TraceProbe.GetAsync(null, 15, CancellationToken.None);
        var warp = await TraceProbe.GetAsync(
            $"http://127.0.0.1:{settings.WarpProxyPort}",
            15,
            CancellationToken.None);

        Console.WriteLine($"direct: ok={direct.Ok} ip={direct.Ip} warp={direct.Warp} colo={direct.Colo} error={direct.Error}");
        Console.WriteLine($"warp  : ok={warp.Ok} ip={warp.Ip} warp={warp.Warp} colo={warp.Colo} error={warp.Error}");
        return warp.WarpOn ? 0 : 1;
    }

    private static async Task<int> RotateAsync(CliOptions options)
    {
        var settings = LoadSettings(options);
        var log = CreateLog(settings, echo: true, options.Has("verbose"));
        var warp = new WarpClient(settings, log);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Console.WriteLine("Rotating WARP. OpenCode is not touched by this process.");
        var result = await warp.RotateAsync("manual CLI rotation", options.Has("force"), cts.Token);
        Console.WriteLine($"success={result.Success} old={result.OldIp} new={result.NewIp} colo={result.Colo} " +
                          $"attempts={result.Attempts} seconds={result.Duration.TotalSeconds:0.0} error={result.Error}");
        return result.Success ? 0 : 1;
    }

    private static async Task<int> ProxyAsync(CliOptions options)
    {
        var settings = LoadSettings(options);
        var log = CreateLog(settings, echo: true, options.Has("verbose"));
        var hosts = new AiHostCatalog(settings, log);
        var policy = new HostPolicy(settings, hosts);
        await using var relay = new RelayProxy(settings.RelayPort, settings.WarpProxyPort, policy, log, settings.LogDirectConnections);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await relay.StartAsync(cts.Token);
        Console.WriteLine($"Relay is listening on http://127.0.0.1:{relay.BoundPort}");
        Console.WriteLine($"Scope: {policy.ScopeDescription}. Press Ctrl+C to stop.");
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
        return 0;
    }

    private static async Task<int> SelfTestAsync(CliOptions options)
    {
        var settings = LoadSettings(options);
        settings.WarpScope = WarpScope.All;
        settings.DirectHosts = "";
        var log = CreateLog(settings, echo: true, options.Has("verbose"));
        var warp = new WarpClient(settings, log);
        var ready = await warp.EnsureReadyAsync(CancellationToken.None);
        Console.WriteLine($"WARP ready: {ready.Success} {ready.Message}");
        if (!ready.Success)
            return 1;

        var hosts = new AiHostCatalog(settings, log);
        var policy = new HostPolicy(settings, hosts);
        var port = options.TryGet("port", out var text) && int.TryParse(text, out var requested) ? requested : 0;
        await using var relay = new RelayProxy(port, settings.WarpProxyPort, policy, log);
        await relay.StartAsync(CancellationToken.None);
        var proxy = $"http://127.0.0.1:{relay.BoundPort}";
        Console.WriteLine($"Relay test endpoint: {proxy}");

        var direct = await TraceProbe.GetAsync(null, 15, CancellationToken.None);
        var throughRelay = await TraceProbe.GetAsync(proxy, 20, CancellationToken.None);
        Console.WriteLine($"direct : ip={direct.Ip} warp={direct.Warp} colo={direct.Colo}");
        Console.WriteLine($"relay  : ip={throughRelay.Ip} warp={throughRelay.Warp} colo={throughRelay.Colo} error={throughRelay.Error}");

        var example = await HttpViaProxyAsync(proxy, "https://example.com/", TimeSpan.FromSeconds(20));
        Console.WriteLine($"example.com via relay: {example}");
        return throughRelay.WarpOn && example.StartsWith("200", StringComparison.Ordinal) ? 0 : 1;
    }

    private static async Task<int> SplitTestAsync(CliOptions options)
    {
        var settings = LoadSettings(options);
        settings.WarpScope = WarpScope.AiOnly;
        settings.DirectHosts = AppSettings.DefaultDirectHosts;
        var log = CreateLog(settings, echo: true, options.Has("verbose"));
        var hosts = new AiHostCatalog(settings, log);
        var policy = new HostPolicy(settings, hosts);
        var port = options.TryGet("port", out var text) && int.TryParse(text, out var requested) ? requested : 0;
        await using var relay = new RelayProxy(port, settings.WarpProxyPort, policy, log, settings.LogDirectConnections);
        await relay.StartAsync(CancellationToken.None);
        var proxy = $"http://127.0.0.1:{relay.BoundPort}";

        var directHost = await TraceProbe.GetAsync(proxy, "https://www.cloudflare.com/cdn-cgi/trace", 20, CancellationToken.None);
        var aiHost = await TraceProbe.GetAsync(proxy, "https://opencode.ai/cdn-cgi/trace", 20, CancellationToken.None);
        var example = await HttpViaProxyAsync(proxy, "https://example.com/", TimeSpan.FromSeconds(20));

        Console.WriteLine($"relay endpoint          : {proxy}");
        Console.WriteLine($"search/fetch host      : ip={directHost.Ip} warp={directHost.Warp} colo={directHost.Colo} error={directHost.Error}");
        Console.WriteLine($"AI host (must be WARP) : ip={aiHost.Ip} warp={aiHost.Warp} colo={aiHost.Colo} error={aiHost.Error}");
        Console.WriteLine($"example.com via relay  : {example}");

        var ok = !directHost.WarpOn && aiHost.WarpOn && example.StartsWith("200", StringComparison.Ordinal);
        Console.WriteLine(ok
            ? "PASS: search/fetch is direct, AI traffic is WARP, OpenCode-style fetches still work."
            : "FAIL: one of the split routes is wrong.");
        return ok ? 0 : 1;
    }

    private static async Task<int> TestWatcherAsync(CliOptions options)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ocwarp-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "opencode.log");
        File.WriteAllText(file, "timestamp=2026-01-01T00:00:00Z level=INFO message=\"start\"\n");

        var settings = new AppSettings { QuotaLogDirectoryOverride = directory };
        var log = CreateLog(settings, echo: true, options.Has("verbose"));
        var hit = new TaskCompletionSource<QuotaHit>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var watcher = new QuotaWatcher(settings, log, quota =>
        {
            hit.TrySetResult(quota);
            return Task.CompletedTask;
        });

        Console.WriteLine($"Watching synthetic log {file}");
        watcher.Start();
        await Task.Delay(1800);
        await File.AppendAllTextAsync(
            file,
            "timestamp=2026-01-01T00:00:03Z level=ERROR message=\"Failed to drain Session\" " +
            "cause=\"AI.Error: Free usage limit reached. Free models work in OpenCode\"\n");

        var completed = await Task.WhenAny(hit.Task, Task.Delay(TimeSpan.FromSeconds(12)));
        var ok = completed == hit.Task;
        if (ok)
        {
            var value = await hit.Task;
            Console.WriteLine($"watcher detected: kind={value.Kind} at={value.DetectedAt:HH:mm:ss}");
        }
        else
        {
            Console.WriteLine("watcher did NOT detect the synthetic quota line within 12 s.");
        }

        await watcher.StopAsync();
        try { Directory.Delete(directory, true); } catch { }
        return ok ? 0 : 1;
    }

    private static int Hosts(CliOptions options)
    {
        var settings = LoadSettings(options);
        var log = CreateLog(settings, echo: false, options.Has("verbose"));
        var hosts = new AiHostCatalog(settings, log);
        var policy = new HostPolicy(settings, hosts);

        Console.WriteLine($"scope: {settings.WarpScope}");
        Console.WriteLine("exact AI hosts:");
        foreach (var host in hosts.ExactHosts.OrderBy(x => x))
            Console.WriteLine("  " + host);
        Console.WriteLine("subdomain AI rules:");
        foreach (var host in hosts.SuffixHosts.OrderBy(x => x))
            Console.WriteLine("  *." + host);
        Console.WriteLine("from opencode.json:");
        foreach (var host in hosts.ConfigHosts.Distinct().OrderBy(x => x))
            Console.WriteLine("  " + host);

        Console.WriteLine();
        foreach (var host in new[]
                 {
                     "opencode.ai", "models.opencode.ai", "api.opencode.ai",
                     "api.anthropic.com", "mcp.exa.ai", "example.com",
                     "www.cloudflare.com", "www.google.com", "127.0.0.1"
                 })
        {
            var decision = policy.Decide(host, 443);
            Console.WriteLine($"{host,-28} -> {decision.Mode,-6} ({decision.Reason})");
        }
        return 0;
    }

    private static int Classify(CliOptions options)
    {
        var line = options.Get("line");
        if (string.IsNullOrWhiteSpace(line))
        {
            Console.WriteLine("Reading lines from stdin. Press Ctrl+Z then Enter on Windows.");
            line = Console.In.ReadToEnd();
        }

        var any = false;
        foreach (var item in line.Split('\n'))
        {
            if (item.Length == 0)
                continue;
            var kind = QuotaWatcher.ClassifyLine(item);
            if (kind is not null)
            {
                any = true;
                Console.WriteLine($"{kind}: {item.Trim()}");
            }
        }
        if (!any)
            Console.WriteLine("no free-limit or rate-limit pattern found");
        return 0;
    }

    private static async Task<int> RunAsync(CliOptions options, bool launchOpenCode)
    {
        var settings = LoadSettings(options);
        settings.AutoLaunchOpenCode = false;
        var log = CreateLog(settings, echo: true, options.Has("verbose"));
        log.Info($"OpenCode Warp Relay {Version} starting in {(launchOpenCode ? "run" : "watch")} mode.");
        log.Info($"Project folder: {settings.ProjectFolder}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var controller = new AppController(settings, log);
        var exitCode = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.OpenCodeExited += code => exitCode.TrySetResult(code);
        controller.QuotaDetected += hit => Console.WriteLine(
            $"[watcher] {hit.Kind} detected at {hit.DetectedAt:HH:mm:ss}.");
        controller.RotationFinished += result => Console.WriteLine(
            $"[rotation] success={result.Success} old={result.OldIp} new={result.NewIp} error={result.Error}");

        try
        {
            await controller.StartAsync(cts.Token);
            if (launchOpenCode)
            {
                var launch = await controller.LaunchOpenCodeAsync(cts.Token);
                Console.WriteLine($"[opencode] {launch.Message} pid={launch.Pid} exe={launch.Executable}");
                if (!launch.Success)
                {
                    await controller.ShutdownAsync(stopOpenCode: false);
                    return 1;
                }
            }

            Console.WriteLine("Press Ctrl+C to stop the relay.");
            var cancelled = Task.Delay(Timeout.Infinite, cts.Token);
            var completed = await Task.WhenAny(exitCode.Task, cancelled);

            var stopOpenCode = completed != exitCode.Task;
            if (completed == exitCode.Task)
            {
                var code = await exitCode.Task;
                log.Info($"OpenCode exited with {code}; shutting down.");
            }

            await controller.ShutdownAsync(stopOpenCode);
            return completed == exitCode.Task ? await exitCode.Task : 0;
        }
        catch (OperationCanceledException)
        {
            await controller.ShutdownAsync(stopOpenCode: launchOpenCode);
            return 130;
        }
        finally
        {
            await controller.DisposeAsync();
        }
    }

    private static async Task<string> HttpViaProxyAsync(string proxy, string url, TimeSpan timeout)
    {
        try
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(proxy),
                UseProxy = true,
                ConnectTimeout = timeout,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
            };
            using var client = new HttpClient(handler) { Timeout = timeout };
            using var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            return $"{(int)response.StatusCode} {response.ReasonPhrase} ({body.Length} chars)";
        }
        catch (Exception ex)
        {
            return $"error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindCommand(string[] args)
        => args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)
                                      && !arg.StartsWith("-", StringComparison.Ordinal)
                                      && !IsOptionValue(args, arg));

    private static bool IsOptionValue(string[] args, string value)
    {
        var index = Array.IndexOf(args, value);
        if (index <= 0)
            return false;
        var previous = args[index - 1];
        return previous is "--port" or "--warp-port" or "--folder" or "--scope"
            or "--extra-hosts" or "--line";
    }

    private static CliOptions ParseOptions(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;
            var body = arg[2..];
            if (body.Length == 0)
                continue;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                options.Set(body[..eq], body[(eq + 1)..]);
                continue;
            }
            if (body is "verbose" or "force")
            {
                options.Set(body, "true");
                continue;
            }
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options.Set(body, args[++i]);
            }
            else
            {
                options.Set(body, "true");
            }
        }
        return options;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
OpenCode Warp Relay CLI {Version}

  ocwarp status              Show WARP, direct/WARP egress IPs, OpenCode path.
  ocwarp trace               Compare direct and WARP traces.
  ocwarp rotate [--force]    Rotate the WARP registration and wait for a new IP.
  ocwarp proxy               Run only the local relay (headless).
  ocwarp selftest            End-to-end relay test through WARP.
  ocwarp split-test          Verify AI traffic uses WARP and search/fetch stays direct.
  ocwarp test-watcher        Test free-limit detection without touching OpenCode.
  ocwarp hosts               Show AI host rules and routing decisions.
  ocwarp service [ensure]    Check (or write) the background service relay env; --restart applies it live.
  ocwarp classify --line "..."  Classify a log line as usage/rate limit.
  ocwarp run [--folder DIR]  Start relay + WARP + OpenCode (no GUI).
  ocwarp watch               Start relay + WARP + quota watcher (no GUI, no OpenCode).

Options:
  --verbose                 Debug logging to the console.
  --port N                  Override the relay port.
  --warp-port N             Override the WARP proxy port (default 40000).
  --folder DIR              Project folder for OpenCode.
  --scope ai|all            AI-only WARP (default) or all OpenCode traffic.
  --extra-hosts "a,b"       Extra AI provider hosts.
  --line "..."              Log line for classify.
""");
    }
}

internal sealed class CliOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string key, string value) => _values[key] = value;
    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key)
        => _values.TryGetValue(key, out var value) ? value : null;

    public bool TryGet(string key, out string value)
    {
        if (_values.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }
        value = "";
        return false;
    }
}
