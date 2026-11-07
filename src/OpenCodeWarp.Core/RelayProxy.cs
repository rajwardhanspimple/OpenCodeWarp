using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenCodeWarp;

public sealed class RelayConnection : IDisposable
{
    private int _closed;
    private TcpClient? _upstream;

    public long Id { get; }
    public string Host { get; }
    public int Port { get; }
    public RouteMode Route { get; }
    public TcpClient Client { get; }
    public CancellationTokenSource Cancellation { get; } = new();
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public long BytesToUpstream;
    public long BytesToClient;
    public bool HeadersSent;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public TcpClient? Upstream
    {
        get => _upstream;
        set
        {
            _upstream = value;
            if (IsClosed)
            {
                try { value?.Dispose(); } catch { }
            }
        }
    }

    public RelayConnection(long id, string host, int port, RouteMode route, TcpClient client)
    {
        Id = id;
        Host = host;
        Port = port;
        Route = route;
        Client = client;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        try { Cancellation.Cancel(); } catch { }
        try { Client.Close(); } catch { }
        try { _upstream?.Close(); } catch { }
    }

    public void Dispose() => Close();
}

public sealed record ProxyRequest(
    string Method,
    string Target,
    string Host,
    int Port,
    bool IsConnect,
    Uri? Uri,
    byte[] HeaderBytes,
    byte[] Leftover);

public sealed record WarpTunnelFailure(
    string Host,
    int Port,
    string Error,
    DateTimeOffset DetectedAt);

public sealed class RelayProxy : IAsyncDisposable
{
    private readonly int _requestedPort;
    private readonly int _warpPort;
    private readonly HostPolicy _policy;
    private readonly AppLog _log;
    private readonly bool _logDirect;
    private readonly ConcurrentDictionary<long, RelayConnection> _connections = new();

    private readonly object _warpGateLock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private long _nextId;
    private long _totalConnections;
    private long _bytesToUpstream;
    private long _bytesToClient;
    private int _started;
    private int _warpPauseDepth;
    private TaskCompletionSource _warpGate = CompletedGate();

    public RelayProxy(int port, int warpPort, HostPolicy policy, AppLog log, bool logDirectConnections = false)
    {
        _requestedPort = port;
        _warpPort = warpPort;
        _policy = policy;
        _log = log;
        _logDirect = logDirectConnections;
    }

    public int BoundPort => _listener is null
        ? _requestedPort
        : ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool IsRunning => _listener is not null;
    public int ActiveConnections => _connections.Count;
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long BytesToUpstream => Interlocked.Read(ref _bytesToUpstream);
    public long BytesToClient => Interlocked.Read(ref _bytesToClient);
    public HostPolicy Policy => _policy;

    public event Action? StatsChanged;
    public event Action<WarpTunnelFailure>? WarpTunnelFailed;

    public bool WarpPaused
    {
        get
        {
            lock (_warpGateLock)
                return _warpPauseDepth > 0;
        }
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    /// <summary>
    /// Holds new WARP-routed connections while the tunnel is being replaced or repaired.
    /// Existing tunnels keep flowing until they are reset by the caller.
    /// </summary>
    public void PauseWarp(string reason)
    {
        lock (_warpGateLock)
        {
            if (_warpPauseDepth++ > 0)
                return;
            _warpGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _log.Info($"WARP gate closed ({reason}); new AI connections will wait for the tunnel.");
        }
    }

    /// <summary>Releases connections waiting for the WARP tunnel.</summary>
    public void ResumeWarp()
    {
        TaskCompletionSource? gate = null;
        lock (_warpGateLock)
        {
            if (_warpPauseDepth == 0)
                return;
            if (--_warpPauseDepth == 0)
            {
                gate = _warpGate;
                _warpGate = CompletedGate();
                _log.Info("WARP gate open; AI connections can continue.");
            }
        }

        try { gate?.TrySetResult(); } catch { }
    }

    private async Task WaitForWarpGateAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_warpGateLock)
            wait = _warpGate.Task;

        if (wait.IsCompleted)
            return;

        await wait.WaitAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
        try
        {
            _listener.Start(512);
        }
        catch
        {
            _listener = null;
            Interlocked.Exchange(ref _started, 0);
            _cts.Dispose();
            _cts = null;
            throw;
        }

        _acceptLoop = AcceptLoopAsync(_cts.Token);
        _log.Info($"Relay listening on 127.0.0.1:{BoundPort}; WARP proxy is 127.0.0.1:{_warpPort}. " +
                  $"Scope: {_policy.ScopeDescription}.");
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn($"Relay accept failed: {ex.Message}");
                try { await Task.Delay(150, cancellationToken); } catch { break; }
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        RelayConnection? connection = null;
        try
        {
            client.NoDelay = true;
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));

            var request = await ReadRequestAsync(client.GetStream(), headerTimeout.Token);
            if (request is null)
                return;

            if (HostPolicy.IsLocal(request.Host) && request.Port == BoundPort)
            {
                await WriteErrorAsync(client, 400, "Refusing to proxy back into this relay.", cancellationToken);
                return;
            }

            var decision = _policy.Decide(request.Host, request.Port);
            connection = new RelayConnection(
                Interlocked.Increment(ref _nextId),
                request.Host,
                request.Port,
                decision.Mode,
                client);
            _connections[connection.Id] = connection;
            Interlocked.Increment(ref _totalConnections);
            StatsChanged?.Invoke();

            if (decision.Mode == RouteMode.Warp)
            {
                _log.Debug($"CONNECT {request.Host}:{request.Port} -> WARP ({decision.Reason}).");
            }
            else if (_logDirect)
            {
                _log.Debug($"CONNECT {request.Host}:{request.Port} -> direct ({decision.Reason}).");
            }

            if (request.IsConnect)
                await HandleConnectAsync(connection, request, decision, cancellationToken);
            else
                await HandleHttpAsync(connection, request, decision, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A normal close, a rotation reset, or the 30 s header timeout.
        }
        catch (Exception ex)
        {
            _log.Debug($"Relay connection failed: {ex.GetType().Name}: {ex.Message}");
            if (connection is { HeadersSent: false })
            {
                try
                {
                    await WriteErrorAsync(client, 502, "WARP relay could not establish the connection.", cancellationToken);
                }
                catch { }
            }
            else if (connection is null)
            {
                try { client.Close(); } catch { }
            }
        }
        finally
        {
            if (connection is not null)
            {
                _connections.TryRemove(connection.Id, out _);
                connection.Dispose();
                StatsChanged?.Invoke();
            }
        }
    }

    private async Task HandleConnectAsync(
        RelayConnection connection,
        ProxyRequest request,
        RouteDecision decision,
        CancellationToken cancellationToken)
    {
        var clientStream = connection.Client.GetStream();
        var upstreamPreamble = Array.Empty<byte>();

        if (decision.Mode == RouteMode.Warp)
        {
            var tunnel = await OpenWarpTunnelAsync(connection, request.Host, request.Port, cancellationToken);
            if (tunnel.Client is null)
            {
                await WriteErrorAsync(connection.Client, 502,
                    $"WARP could not open {request.Host}:{request.Port} after retrying.", cancellationToken);
                return;
            }

            connection.Upstream = tunnel.Client;
            upstreamPreamble = tunnel.Leftover;
            if (connection.IsClosed)
                return;
        }
        else
        {
            var upstream = await ConnectDirectAsync(request.Host, request.Port, cancellationToken);
            connection.Upstream = upstream;
            if (connection.IsClosed)
                return;
        }

        var established = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 Connection Established\r\n" +
            "Proxy-Agent: OpenCodeWarp/1.0\r\n" +
            "\r\n");
        await clientStream.WriteAsync(established, cancellationToken);
        connection.HeadersSent = true;

        var upstreamStream = connection.Upstream!.GetStream();
        await RelayAsync(
            clientStream,
            upstreamStream,
            request.Leftover,
            upstreamPreamble,
            connection,
            cancellationToken);
    }

    private async Task HandleHttpAsync(
        RelayConnection connection,
        ProxyRequest request,
        RouteDecision decision,
        CancellationToken cancellationToken)
    {
        if (request.Uri is null)
        {
            await WriteErrorAsync(connection.Client, 400, "The relay expects absolute-form HTTP requests.", cancellationToken);
            return;
        }

        var clientStream = connection.Client.GetStream();
        if (decision.Mode == RouteMode.Warp)
        {
            await WaitForWarpGateAsync(cancellationToken);
            var upstream = await ConnectTcpAsync("127.0.0.1", _warpPort, cancellationToken);
            connection.Upstream = upstream;
            if (connection.IsClosed)
                return;

            var upstreamStream = upstream.GetStream();
            await upstreamStream.WriteAsync(request.HeaderBytes, cancellationToken);
            if (request.Leftover.Length > 0)
                await upstreamStream.WriteAsync(request.Leftover, cancellationToken);
            connection.HeadersSent = true;

            await RelayAsync(
                clientStream,
                upstreamStream,
                Array.Empty<byte>(),
                Array.Empty<byte>(),
                connection,
                cancellationToken);
            return;
        }

        var direct = await ConnectDirectAsync(request.Host, request.Port, cancellationToken);
        connection.Upstream = direct;
        if (connection.IsClosed)
            return;

        var rewritten = RewriteForOriginServer(request);
        var directStream = direct.GetStream();
        await directStream.WriteAsync(rewritten, cancellationToken);
        if (request.Leftover.Length > 0)
            await directStream.WriteAsync(request.Leftover, cancellationToken);
        connection.HeadersSent = true;

        await RelayAsync(
            clientStream,
            directStream,
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            connection,
            cancellationToken);
    }

    private static byte[] RewriteForOriginServer(ProxyRequest request)
    {
        var uri = request.Uri!;
        var header = Encoding.ASCII.GetString(request.HeaderBytes);
        var lines = header.Split("\r\n");
        var output = new List<string>(lines.Length + 2);
        var path = uri.PathAndQuery;
        if (path.Length == 0)
            path = "/";
        output.Add($"{request.Method} {path} HTTP/1.1");

        var hasHost = false;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                break;
            if (line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                hasHost = true;
            output.Add(line);
        }

        if (!hasHost)
            output.Insert(1, $"Host: {uri.Authority}");
        output.Add("Connection: close");
        output.Add("");
        output.Add("");
        return Encoding.ASCII.GetBytes(string.Join("\r\n", output));
    }

    private async Task<TcpClient> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        client.NoDelay = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> ConnectDirectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var parsed))
        {
            addresses = new[] { parsed };
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }

        // Prefer IPv4. Some networks advertise IPv6 that cannot actually reach the destination.
        var ordered = addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        Exception? lastError = null;
        foreach (var address in ordered)
        {
            var client = new TcpClient(address.AddressFamily);
            client.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await client.ConnectAsync(new IPEndPoint(address, port), timeout.Token);
                return client;
            }
            catch (Exception ex)
            {
                lastError = ex;
                client.Dispose();
            }
        }

        throw new IOException($"Could not connect to {host}:{port}.", lastError);
    }

    /// <summary>
    /// Opens a WARP CONNECT tunnel with a short retry window. Cloudflare WARP answers
    /// 502/503/504 while its tunnel is being replaced, even though the local proxy
    /// socket is still accepting. Retrying here keeps those errors away from OpenCode.
    /// </summary>
    private async Task<(TcpClient? Client, byte[] Leftover, string Error)> OpenWarpTunnelAsync(
        RelayConnection connection,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        var error = "WARP tunnel was not established";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await WaitForWarpGateAsync(cancellationToken);
            if (connection.IsClosed)
                return (null, Array.Empty<byte>(), "client closed");

            TcpClient? upstream = null;
            try
            {
                upstream = await ConnectTcpAsync("127.0.0.1", _warpPort, cancellationToken);
                var tunnel = await EstablishWarpTunnelAsync(
                    upstream.GetStream(), host, port, cancellationToken);
                if (tunnel.Ok)
                    return (upstream, tunnel.Leftover, "");

                error = tunnel.Error;
                upstream.Dispose();
                upstream = null;

                if (!IsRetryableWarpStatus(tunnel.Status))
                {
                    _log.Warn($"WARP refused {host}:{port}: {error}");
                    return (null, Array.Empty<byte>(), error);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                upstream?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                upstream?.Dispose();
            }

            if (attempt < maxAttempts)
            {
                _log.Debug($"WARP tunnel to {host}:{port} failed (attempt {attempt}/{maxAttempts}): {error}. Retrying.");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return (null, Array.Empty<byte>(), "cancelled");
                }
            }
        }

        _log.Warn($"WARP tunnel to {host}:{port} failed after {maxAttempts} attempt(s): {error}");
        try { WarpTunnelFailed?.Invoke(new WarpTunnelFailure(host, port, error, DateTimeOffset.Now)); } catch { }
        return (null, Array.Empty<byte>(), error);
    }

    private static bool IsRetryableWarpStatus(int status)
        => status is 0 or 500 or 502 or 503 or 504;

    private async Task<(bool Ok, byte[] Leftover, string Error, int Status)> EstablishWarpTunnelAsync(
        NetworkStream upstream,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var target = FormatHostPort(host, port);
        var request =
            $"CONNECT {target} HTTP/1.1\r\n" +
            $"Host: {target}\r\n" +
            "Proxy-Connection: Keep-Alive\r\n" +
            "Connection: Keep-Alive\r\n" +
            "\r\n";
        await upstream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);

        var (header, leftover) = await ReadHeaderBlockAsync(upstream, 32 * 1024, cancellationToken);
        var statusLine = header.Split("\r\n", 2, StringSplitOptions.None)[0];
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[1], out var status))
        {
            if (status == 200)
                return (true, leftover, "", status);
            return (false, Array.Empty<byte>(), statusLine, status);
        }

        return (false, Array.Empty<byte>(),
            statusLine.Length > 0 ? statusLine : "empty proxy response", 0);
    }

    private static async Task<ProxyRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();

        while (memory.Length < 64 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return null;
            memory.Write(buffer, 0, read);

            var data = memory.GetBuffer();
            var length = (int)memory.Length;
            var headerEnd = FindHeaderEnd(data, length);
            if (headerEnd < 0)
                continue;

            var headerLength = headerEnd + 4;
            var headerText = Encoding.ASCII.GetString(data, 0, headerLength);
            var leftover = new byte[length - headerLength];
            Array.Copy(data, headerLength, leftover, 0, leftover.Length);

            var lines = headerText.Split("\r\n");
            if (lines.Length == 0)
                return null;
            var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return null;

            var method = parts[0].ToUpperInvariant();
            var target = parts[1];

            if (method == "CONNECT")
            {
                if (!TryParseHostPort(target, 443, out var host, out var port))
                    return null;
                return new ProxyRequest(method, target, host, port, true, null, 
                    Encoding.ASCII.GetBytes(headerText), leftover);
            }

            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            var uriHost = uri.IdnHost.Trim('[', ']');
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                // HTTPS through a proxy must use CONNECT. A few clients still send absolute-form.
                return new ProxyRequest("CONNECT", FormatHostPort(uriHost, uri.Port), uriHost, uri.Port,
                    true, null, Encoding.ASCII.GetBytes(headerText), leftover);
            }

            return new ProxyRequest(method, target, uriHost, uri.Port, false, uri,
                Encoding.ASCII.GetBytes(headerText), leftover);
        }

        return null;
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n'
                && buffer[i + 2] == (byte)'\r' && buffer[i + 3] == (byte)'\n')
                return i;
        }
        return -1;
    }

    private static async Task<(string Header, byte[] Leftover)> ReadHeaderBlockAsync(
        NetworkStream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();
        while (memory.Length < maxBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            memory.Write(buffer, 0, read);

            var data = memory.GetBuffer();
            var length = (int)memory.Length;
            var headerEnd = FindHeaderEnd(data, length);
            if (headerEnd < 0)
                continue;

            var headerLength = headerEnd + 4;
            var header = Encoding.ASCII.GetString(data, 0, headerLength);
            var leftover = new byte[length - headerLength];
            Array.Copy(data, headerLength, leftover, 0, leftover.Length);
            return (header, leftover);
        }

        throw new IOException("The upstream proxy response headers were too large or incomplete.");
    }

    private async Task RelayAsync(
        NetworkStream client,
        NetworkStream upstream,
        byte[] clientPreamble,
        byte[] upstreamPreamble,
        RelayConnection connection,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            connection.Cancellation.Token);
        Task? upTask = null;
        Task? downTask = null;

        try
        {
            if (clientPreamble.Length > 0)
                await upstream.WriteAsync(clientPreamble, linked.Token);
            if (upstreamPreamble.Length > 0)
                await client.WriteAsync(upstreamPreamble, linked.Token);

            upTask = CopyAsync(client, upstream, connection, true, linked.Token);
            downTask = CopyAsync(upstream, client, connection, false, linked.Token);
            var first = await Task.WhenAny(upTask, downTask);

            try { await first; } catch { }

            if (first == upTask)
            {
                try { connection.Upstream?.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
            else
            {
                try { connection.Client.Client.Shutdown(SocketShutdown.Send); } catch { }
            }

            var other = first == upTask ? downTask : upTask;
            try { await other.WaitAsync(TimeSpan.FromSeconds(60), linked.Token); } catch { }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            connection.Close();
            if (upTask is not null && downTask is not null)
            {
                try { await Task.WhenAll(upTask, downTask).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            }
        }
    }

    private async Task CopyAsync(
        NetworkStream from,
        NetworkStream to,
        RelayConnection connection,
        bool toUpstream,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                    break;
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (toUpstream)
                {
                    Interlocked.Add(ref connection.BytesToUpstream, read);
                    Interlocked.Add(ref _bytesToUpstream, read);
                }
                else
                {
                    Interlocked.Add(ref connection.BytesToClient, read);
                    Interlocked.Add(ref _bytesToClient, read);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteErrorAsync(TcpClient client, int code, string message, CancellationToken cancellationToken)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(message + "\n");
            var reason = code switch
            {
                400 => "Bad Request",
                502 => "Bad Gateway",
                _ => "Error"
            };
            var header =
                $"HTTP/1.1 {code} {reason}\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n" +
                "Proxy-Agent: OpenCodeWarp/1.0\r\n" +
                "\r\n";
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
        catch
        {
        }
    }

    private static bool TryParseHostPort(string value, int defaultPort, out string host, out int port)
    {
        host = "";
        port = defaultPort;
        value = (value ?? "").Trim();
        if (value.Length == 0)
            return false;

        if (value[0] == '[')
        {
            var end = value.IndexOf(']');
            if (end < 0)
                return false;
            host = value[1..end];
            if (value.Length > end + 1)
            {
                if (value[end + 1] != ':' || !int.TryParse(value[(end + 2)..], out port))
                    return false;
            }
            return host.Length > 0 && port is > 0 and <= 65535;
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon)
        {
            host = value[..colon];
            if (!int.TryParse(value[(colon + 1)..], out port))
                return false;
        }
        else
        {
            host = value;
        }

        return host.Length > 0 && port is > 0 and <= 65535;
    }

    private static string FormatHostPort(string host, int port)
        => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";

    public int ResetWarpConnections()
    {
        var closed = 0;
        foreach (var connection in _connections.Values)
        {
            if (connection.Route != RouteMode.Warp)
                continue;
            connection.Close();
            closed++;
        }

        if (closed > 0)
            _log.Info($"Closed {closed} WARP tunnel(s); OpenCode will reconnect through the new exit IP.");
        return closed;
    }

    public int ResetAllConnections()
    {
        var closed = 0;
        foreach (var connection in _connections.Values)
        {
            connection.Close();
            closed++;
        }
        return closed;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0 && _listener is null)
            return;

        _log.Info("Relay stopping.");
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { ResumeWarp(); } catch { }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            _acceptLoop = null;
        }

        foreach (var connection in _connections.Values)
            connection.Close();
        _connections.Clear();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        StatsChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
