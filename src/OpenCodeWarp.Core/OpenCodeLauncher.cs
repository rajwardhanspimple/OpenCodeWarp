using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenCodeWarp;

public sealed record OpenCodeLaunchResult(bool Success, int Pid, string Executable, string Message);

/// <summary>
/// Starts the real OpenCode CLI with a private environment block. The proxy variables
/// exist only in the OpenCode process tree; this application and Windows stay direct.
///
/// A managed launch has two processes: a hidden <c>opencode serve</c> instance with a
/// generated password, and the visible TUI attached to that server with
/// <c>--server http://127.0.0.1:&lt;port&gt;</c>.
/// </summary>
public sealed class OpenCodeLauncher : IDisposable
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwShowNormal = 1;

    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly List<string> _serverTail = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly HttpClient _probe;
    private Process? _process;
    private Process? _serverProcess;
    private string? _serverUrl;
    private string? _serverPassword;
    private int _serverPort;
    private int _generation;

    public event Action? StateChanged;
    public event Action<int>? Exited;

    public OpenCodeLauncher(AppSettings settings, AppLog log)
    {
        _settings = settings;
        _log = log;
        _probe = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                try { return _process is { HasExited: false }; }
                catch { return false; }
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                try { return _process is { HasExited: false } ? _process.Id : null; }
                catch { return null; }
            }
        }
    }

    /// <summary>PID of the hidden private server that belongs to the managed TUI.</summary>
    public int? ServerProcessId
    {
        get
        {
            lock (_gate)
            {
                try { return _serverProcess is { HasExited: false } ? _serverProcess.Id : null; }
                catch { return null; }
            }
        }
    }

    public int? ServerPort
    {
        get
        {
            lock (_gate)
            {
                return _serverPort > 0 ? _serverPort : null;
            }
        }
    }

    public string? ServerUrl
    {
        get
        {
            lock (_gate)
            {
                return _serverUrl;
            }
        }
    }

    /// <summary>Password for the private OpenCode server started by this launch.</summary>
    public string? ServerPassword
    {
        get
        {
            lock (_gate)
            {
                return _serverPassword;
            }
        }
    }

    public string ServerUsername => "opencode";

    public DateTime? StartedAt { get; private set; }
    public string? LastExecutable { get; private set; }

    /// <summary>First existing candidate; used where a quick guess is enough.</summary>
    public string? DiscoverExecutable() => DiscoverExecutableCandidates().FirstOrDefault();

    /// <summary>
    /// Every plausible OpenCode CLI location in preference order (settings override,
    /// the official installer dir, npm, bun, scoop, winget, Chocolatey, then PATH).
    /// Callers should verify a candidate actually runs — a half-installed npm shim
    /// exists on disk but exits with an error.
    /// </summary>
    public List<string> DiscoverExecutableCandidates()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_settings.OpenCodeExe))
            candidates.Add(_settings.OpenCodeExe!);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // The official installer and this project's documented zip method both place a
        // real executable here; prefer it over package-manager shims, which npm 12+ can
        // leave broken when it skips postinstall scripts (allowScripts).
        candidates.Add(Path.Combine(profile, ".opencode", "bin", "opencode.exe"));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        // npm global installs put their shims directly in the prefix (default %APPDATA%\npm),
        // which a GUI process may not have on PATH even when a terminal does.
        candidates.Add(Path.Combine(appData, "npm", "opencode.cmd"));
        candidates.Add(Path.Combine(appData, "npm", "opencode.exe"));
        candidates.Add(Path.Combine(appData, "npm", "node_modules", "opencode-ai", "bin", "opencode.exe"));
        candidates.Add(Path.Combine(appData, "npm", "node_modules", "opencode-ai", "bin", "opencode.cmd"));
        candidates.Add(Path.Combine(appData, "npm", "node_modules", "@opencode", "cli", "bin", "opencode.exe"));
        candidates.Add(Path.Combine(appData, "npm", "node_modules", "@opencode", "cli", "bin", "opencode.cmd"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(localAppData, "Programs", "opencode", "opencode.exe"));
        candidates.Add(Path.Combine(localAppData, "opencode", "bin", "opencode.exe"));
        candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "opencode.exe"));

        candidates.Add(Path.Combine(profile, ".bun", "bin", "opencode.exe"));
        candidates.Add(Path.Combine(profile, "scoop", "shims", "opencode.exe"));

        var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        candidates.Add(Path.Combine(commonAppData, "chocolatey", "bin", "opencode.exe"));

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                candidates.Add(Path.Combine(part, "opencode.exe"));
                candidates.Add(Path.Combine(part, "opencode.cmd"));
                candidates.Add(Path.Combine(part, "opencode.bat"));
            }
            catch { }
        }

        var existing = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    existing.Add(Path.GetFullPath(candidate));
            }
            catch { }
        }

        return existing;
    }

    /// <summary>Runs <c>opencode --version</c> to prove a candidate actually works.</summary>
    private async Task<(bool Ok, string Version, string Detail)> VerifyExecutableAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        var (application, arguments) = BuildCommandLine(executable, "--version");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = application,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return (false, "", "did not start");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "", "did not answer within 12s");
            }

            var stdout = await SafeOutputAsync(stdoutTask);
            var stderr = await SafeOutputAsync(stderrTask);
            var version = stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";
            if (process.ExitCode == 0 && version.Length > 0)
                return (true, version, "");

            var detail = (stderr.Length > 0 ? stderr : stdout).Trim()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? $"exit code {process.ExitCode}";
            return (false, "", ClipDetail(detail, 160));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, "", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<string> SafeOutputAsync(Task<string> task)
    {
        try { return await task; } catch { return ""; }
    }

    private static string ClipDetail(string value, int max)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }

    public async Task<OpenCodeLaunchResult> StartAsync(
        string proxyUrl,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
                return new OpenCodeLaunchResult(false, ProcessId ?? 0, LastExecutable ?? "", "OpenCode is already running.");

            Process? staleServer;
            lock (_gate)
            {
                staleServer = _serverProcess;
                _serverProcess = null;
                _serverUrl = null;
                _serverPort = 0;
                _serverPassword = null;
                _serverTail.Clear();
            }
            StopServer(staleServer);

            var candidates = DiscoverExecutableCandidates();
            if (candidates.Count == 0)
                return new OpenCodeLaunchResult(false, 0, "",
                    "OpenCode CLI was not found.\n\n" +
                    "Install it with npm (needs Node.js):\n" +
                    "  npm install -g opencode-ai\n\n" +
                    "Or without Node.js, download opencode-windows-x64.zip from\n" +
                    "  https://opencode.ai/update/api/latest/cli/opencode/\n" +
                    "and copy opencode.exe into %USERPROFILE%\\.opencode\\bin\\.\n\n" +
                    "Then click Launch OpenCode again, or set the exact path in Settings.");

            // A file existing is not enough: a half-installed npm shim (npm 12 skipping
            // postinstall scripts) is on disk but exits with an error. Verify each
            // candidate with `opencode --version` and use the first one that works.
            string? executable = null;
            var broken = new List<string>();
            foreach (var candidate in candidates)
            {
                var check = await VerifyExecutableAsync(candidate, cancellationToken);
                if (check.Ok)
                {
                    executable = candidate;
                    _log.Info($"Using OpenCode CLI {check.Version} at {candidate}.");
                    break;
                }

                broken.Add($"  {candidate} — {check.Detail}");
                _log.Warn($"OpenCode CLI candidate failed verification: {candidate} — {check.Detail}");
            }

            if (executable is null)
            {
                return new OpenCodeLaunchResult(false, 0, "",
                    "OpenCode CLI candidates were found, but none of them works:\n" +
                    string.Join("\n", broken) +
                    "\n\nReinstall OpenCode, or pick a working executable in Settings.");
            }

            try
            {
                Directory.CreateDirectory(_settings.ProjectFolder);
            }
            catch (Exception ex)
            {
                return new OpenCodeLaunchResult(false, 0, executable, $"The project folder could not be created: {ex.Message}");
            }

            var password = GeneratePassword();
            var environment = BuildEnvironmentEntries(proxyUrl, password);
            Process? server = null;
            Process? tui = null;
            var tuiStarted = false;
            try
            {
                server = StartServer(executable, environment);
                lock (_gate)
                {
                    _serverProcess = server;
                    _serverPassword = password;
                }

                var endpoint = await WaitForServerAsync(server, password, cancellationToken);
                if (endpoint is null)
                {
                    var detail = SafeExitDetail(server);
                    var tail = ServerTailSnapshot();
                    StopFailedLaunch(null, server, tuiStarted: false);
                    _log.Warn("The private OpenCode server did not become ready" + detail + "." +
                              (tail.Length > 0 ? "\nServer output:\n" + tail : ""));
                    return new OpenCodeLaunchResult(false, 0, executable,
                        "The private OpenCode server did not become ready" + detail + "." +
                        (tail.Length > 0 ? "\n\nServer output:\n" + tail : "") +
                        "\n\nExecutable: " + executable);
                }

                var tuiArguments = BuildTuiArguments(endpoint.Url);
                tui = StartTui(executable, tuiArguments, environment);
                tuiStarted = true;

                var generation = ++_generation;
                tui.Exited += (_, _) => HandleTuiExited(generation, tui);

                lock (_gate)
                {
                    _process = tui;
                    _serverProcess = server;
                    _serverUrl = endpoint.Url;
                    _serverPort = endpoint.Port;
                    _serverPassword = password;
                    LastExecutable = executable;
                    StartedAt = DateTime.Now;
                }

                _log.Info($"OpenCode started: TUI pid {tui.Id}, private server pid {server.Id} on " +
                          $"{endpoint.Url}, folder \"{_settings.ProjectFolder}\", proxy {proxyUrl} (process-only).");
                StateChanged?.Invoke();

                if (tui.HasExited)
                    HandleTuiExited(generation, tui);

                return new OpenCodeLaunchResult(true, tui.Id, executable,
                    $"OpenCode started (private API {endpoint.Url}).");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StopFailedLaunch(tui, server, tuiStarted);
                return new OpenCodeLaunchResult(false, 0, executable, "OpenCode launch was cancelled.");
            }
            catch (Exception ex)
            {
                StopFailedLaunch(tui, server, tuiStarted);
                _log.Error("OpenCode launch failed", ex);
                return new OpenCodeLaunchResult(false, 0, executable, ex.Message);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private Process StartServer(string executable, IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        var (application, arguments) = BuildCommandLine(executable, "serve --hostname 127.0.0.1 --port 0");
        var startInfo = new ProcessStartInfo
        {
            FileName = application,
            Arguments = arguments,
            WorkingDirectory = _settings.ProjectFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment.Clear();
        foreach (var entry in environment)
            startInfo.Environment[entry.Key] = entry.Value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => LogServerLine("out", e.Data);
        process.ErrorDataReceived += (_, e) => LogServerLine("err", e.Data);
        process.Exited += (_, _) => LogServerExit(process);

        if (!process.Start())
            throw new InvalidOperationException("The private OpenCode server could not be started.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _log.Debug($"Private OpenCode server starting: pid {process.Id}.");
        return process;
    }

    private Process StartTui(
        string executable,
        string arguments,
        IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        var (application, fullArguments) = BuildCommandLine(executable, arguments);
        var isShim = !string.Equals(application, executable, StringComparison.OrdinalIgnoreCase);
        var commandLine = new StringBuilder($"{Quote(application)} {fullArguments}");
        var environmentHandle = BuildEnvironmentBlock(environment, out var handle);
        var startup = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpTitle = "OpenCode (WARP relay)",
            dwFlags = StartfUseShowWindow,
            wShowWindow = SwShowNormal
        };

        PROCESS_INFORMATION processInfo = default;
        try
        {
            var ok = CreateProcessW(
                isShim ? null : application,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateNewConsole | CreateUnicodeEnvironment,
                environmentHandle,
                _settings.ProjectFolder,
                ref startup,
                out processInfo);

            if (!ok)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"Could not start OpenCode (Win32 error {error}).");
            }

            var process = Process.GetProcessById((int)processInfo.dwProcessId);
            process.EnableRaisingEvents = true;
            return process;
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
            if (processInfo.hThread != IntPtr.Zero)
                CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero)
                CloseHandle(processInfo.hProcess);
        }
    }

    private async Task<ServerEndpoint?> WaitForServerAsync(
        Process server,
        string password,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (server.HasExited)
                {
                    _log.Error($"Private OpenCode server exited before it became ready (code {server.ExitCode}).");
                    return null;
                }
            }
            catch { }

            foreach (var port in WindowsPortFinder.GetLoopbackListenerPorts(server.Id))
            {
                var endpoint = $"http://127.0.0.1:{port}";
                if (await ProbeAsync(endpoint, password, cancellationToken))
                {
                    _log.Debug($"Private OpenCode server ready on {endpoint}.");
                    return new ServerEndpoint(port, endpoint);
                }
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        _log.Error("Timed out waiting for the private OpenCode server to accept its generated password.");
        return null;
    }

    private async Task<bool> ProbeAsync(string endpoint, string password, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/api/model");
            request.Headers.Authorization = BasicAuth(password);
            using var response = await _probe.SendAsync(request, timeout.Token);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private string BuildTuiArguments(string serverUrl)
    {
        var cleaned = (_settings.OpenCodeExtraArgs ?? "").Trim();
        cleaned = Regex.Replace(cleaned, @"(^|\s)--standalone(?=\s|$)", " ",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"(^|\s)--server(?:=\S+|\s+\S+)", " ",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned)
            ? $"--server {serverUrl}"
            : $"--server {serverUrl} {cleaned}";
    }

    private void HandleTuiExited(int generation, Process process)
    {
        int exitCode = 0;
        try { exitCode = process.ExitCode; } catch { }

        Process? server;
        lock (_gate)
        {
            if (generation != _generation)
                return;
            if (!ReferenceEquals(_process, process))
                return;
            _process = null;
            server = _serverProcess;
            _serverProcess = null;
            _serverUrl = null;
            _serverPort = 0;
            _serverPassword = null;
        }

        StopServer(server);
        _log.Info($"OpenCode exited with code {exitCode}.");
        try { Exited?.Invoke(exitCode); } catch { }
        try { StateChanged?.Invoke(); } catch { }
    }

    private void LogServerLine(string stream, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_gate)
        {
            _serverTail.Add($"{stream}: {line.Trim()}");
            if (_serverTail.Count > 12)
                _serverTail.RemoveAt(0);
        }

        if (line.Contains("server listening", StringComparison.OrdinalIgnoreCase))
            _log.Info("Private OpenCode server: " + line.Trim());
        else
            _log.Debug($"OpenCode server {stream}: {line.Trim()}");
    }

    /// <summary>The last server output lines, shown when the server fails to start.</summary>
    private string ServerTailSnapshot()
    {
        lock (_gate)
            return string.Join("\n", _serverTail);
    }

    private void LogServerExit(Process process)
    {
        try
        {
            var code = process.ExitCode;
            _log.Warn($"Private OpenCode server pid {process.Id} exited with code {code}.");
        }
        catch
        {
            // The process was already disposed during an explicit stop.
        }
    }

    private void StopFailedLaunch(Process? tui, Process? server, bool tuiStarted)
    {
        if (tuiStarted && tui is not null)
        {
            try
            {
                if (!tui.HasExited)
                    tui.Kill(entireProcessTree: true);
            }
            catch { }
            try { tui.Dispose(); } catch { }
        }

        lock (_gate)
        {
            if (ReferenceEquals(_process, tui))
                _process = null;
            if (server is not null && ReferenceEquals(_serverProcess, server))
                _serverProcess = null;
            _serverUrl = null;
            _serverPort = 0;
            _serverPassword = null;
        }

        StopServer(server);
    }

    private void StopServer(Process? server)
    {
        if (server is null)
            return;

        try
        {
            if (!server.HasExited)
            {
                _log.Info($"Stopping private OpenCode server pid {server.Id}.");
                server.Kill(entireProcessTree: true);
                try { server.WaitForExit(3000); } catch { }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not stop the private OpenCode server: {ex.Message}");
        }
        finally
        {
            try { server.Dispose(); } catch { }
        }
    }

    private static string SafeExitDetail(Process server)
    {
        try
        {
            if (server.HasExited)
                return $" (server exited with code {server.ExitCode})";
        }
        catch { }
        return "";
    }

    /// <summary>Explicit Stop button / app exit only. It is never called for a free-limit rotation.</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            Process? process;
            Process? server;
            lock (_gate)
            {
                process = _process;
                server = _serverProcess;
                _process = null;
                _serverProcess = null;
                _serverUrl = null;
                _serverPort = 0;
                _serverPassword = null;
            }

            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        _log.Info($"Stopping OpenCode pid {process.Id} (explicit request).");
                        process.Kill(entireProcessTree: true);
                        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"Could not stop OpenCode: {ex.Message}");
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }

            StopServer(server);
            StateChanged?.Invoke();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Builds the process environment for a child OpenCode process: everything inherited
    /// except proxy and stale launcher variables, plus the relay proxy entries. With a
    /// non-null password it also carries the private-server credentials used by the
    /// managed serve + TUI pair; pass null for one-shot runs such as the usability probe.
    /// </summary>
    public List<KeyValuePair<string, string>> BuildEnvironmentEntries(string proxyUrl, string? serverPassword)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (DictionaryEntry raw in Environment.GetEnvironmentVariables())
        {
            var key = raw.Key?.ToString() ?? "";
            if (key.Length == 0 || key[0] == '=')
                continue;
            if (IsProxyVariable(key) || IsInheritedLauncherVariable(key))
                continue;
            entries.Add(new KeyValuePair<string, string>(key, raw.Value?.ToString() ?? ""));
        }

        const string noProxy = "localhost,127.0.0.1,::1";
        entries.Add(new("HTTP_PROXY", proxyUrl));
        entries.Add(new("HTTPS_PROXY", proxyUrl));
        entries.Add(new("http_proxy", proxyUrl));
        entries.Add(new("https_proxy", proxyUrl));
        entries.Add(new("NO_PROXY", noProxy));
        entries.Add(new("no_proxy", noProxy));

        // The private server gets a known password so the dashboard can switch
        // models after an account quota hit. The attached TUI reads the same
        // password from OPENCODE_PASSWORD when it connects with --server.
        if (!string.IsNullOrEmpty(serverPassword))
        {
            entries.Add(new("OPENCODE_SERVER_PASSWORD", serverPassword));
            entries.Add(new("OPENCODE_SERVER_USERNAME", "opencode"));
            entries.Add(new("OPENCODE_PASSWORD", serverPassword));
        }
        entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key));
        return entries;
    }

    private static IntPtr BuildEnvironmentBlock(
        IReadOnlyList<KeyValuePair<string, string>> entries,
        out GCHandle handle)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
            builder.Append(entry.Key).Append('=').Append(entry.Value).Append('\0');
        builder.Append('\0');

        var chars = builder.ToString().ToCharArray();
        handle = GCHandle.Alloc(chars, GCHandleType.Pinned);
        return handle.AddrOfPinnedObject();
    }

    public static (string Application, string Arguments) BuildCommandLine(
        string executable,
        string arguments)
    {
        var isShim = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                     || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (!isShim)
            return (executable, arguments);

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return (cmd, $"/d /s /c \"\"{executable}\" {arguments}\"");
    }

    private static bool IsProxyVariable(string key)
        => ProxyEnvironment.IsProxyName(key);

    /// <summary>
    /// Removes state inherited from the shell or from the older opencode-infinite
    /// launcher. Leaving OPENCODE_SESSION_ID or the tokenharbor base URLs in place
    /// makes a fresh TUI resume a stale session or talk to the wrong provider.
    /// </summary>
    private static bool IsInheritedLauncherVariable(string key)
    {
        if (key.StartsWith("OC_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OCI_", StringComparison.OrdinalIgnoreCase))
            return true;

        return key.ToUpperInvariant() switch
        {
            "OPENCODE" => true,
            "OPENCODE_SESSION_ID" => true,
            "OPENCODE_CLIENT" => true,
            "OPENCODE_TERMINAL" => true,
            "OPENCODE_DISABLE_EMBEDDED_WEB_UI" => true,
            "OPENCODE_PASSWORD" => true,
            "OPENCODE_SERVER_PASSWORD" => true,
            "OPENCODE_SERVER_USERNAME" => true,
            "ANTHROPIC_BASE_URL" => true,
            "ANTHROPIC_AUTH_TOKEN" => true,
            "OPENAI_BASE_URL" => true,
            "TOKENHARBOR_API_KEY" => true,
            _ => false
        };
    }

    private static AuthenticationHeaderValue BasicAuth(string password)
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password));
        return new AuthenticationHeaderValue("Basic", token);
    }

    private static string GeneratePassword()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private sealed record ServerEndpoint(int Port, string Url);

    public void Dispose()
    {
        // Do not kill OpenCode when the controller is disposed.
        lock (_gate)
        {
            try { _process?.Dispose(); } catch { }
            try { _serverProcess?.Dispose(); } catch { }
            _process = null;
            _serverProcess = null;
            _serverUrl = null;
            _serverPort = 0;
            _serverPassword = null;
        }

        try { _probe.Dispose(); } catch { }
        try { _lifecycle.Dispose(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpReserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
