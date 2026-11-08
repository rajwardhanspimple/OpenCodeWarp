using System.Text;

namespace OpenCodeWarp;

public enum QuotaKind
{
    UsageLimit,
    RateLimit,
    TransportFailure
}

public sealed record QuotaHit(
    QuotaKind Kind,
    string Line,
    DateTimeOffset DetectedAt,
    string File,
    string? SessionId);

/// <summary>
/// Tails OpenCode's newest log file for quota, rate-limit, and transport errors.
/// It never stops or restarts OpenCode. Transport resets request a WARP reconnect;
/// usage-limit and rate-limit hits request a WARP IP rotation when the matching
/// rotate-on setting is enabled.
/// </summary>
public sealed partial class QuotaWatcher : IAsyncDisposable
{
    private static readonly string[] UsagePatterns =
    {
        "free usage limit",
        "usage limit reached",
        "out of usage",
        "insufficient credits",
        "payment required",
        "quota exceeded",
        "exceeded your current quota",
        "no credits left",
        "credit balance is too low"
    };

    private static readonly string[] RatePatterns =
    {
        "rate limit",
        "rate_limit_exceeded",
        "too many requests",
        "statuscode=429",
        "status=429",
        "http 429",
        "http/1.1 429"
    };

    private static readonly string[] TransportPatterns =
    {
        "econnreset",
        "socket connection was closed unexpectedly",
        "connection reset by peer"
    };

    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly Func<QuotaHit, Task> _onHit;
    private readonly string _directory;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _settle = TimeSpan.FromSeconds(4);
    private readonly TimeSpan _cooldown = TimeSpan.FromSeconds(45);

    private Task? _loop;
    private string? _currentFile;
    private long _offset;
    private string _partial = "";
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private QuotaHit? _pending;
    private DateTimeOffset _pendingAt;
    private DateTimeOffset _lastHit = DateTimeOffset.MinValue;
    private volatile bool _handling;

    public QuotaWatcher(AppSettings settings, AppLog log, Func<QuotaHit, Task> onHit)
    {
        _settings = settings;
        _log = log;
        _onHit = onHit;
        _directory = settings.QuotaLogDirectoryOverride is { Length: > 0 }
            ? settings.QuotaLogDirectoryOverride
            : AppPaths.OpenCodeLogDirectory;
    }

    public string DirectoryPath => _directory;
    public string? CurrentFile => _currentFile;
    public DateTimeOffset? LastHit { get; private set; }
    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        if (_loop is not null)
            return;
        _log.Info($"Quota watcher starting on {_directory}.");
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        try { _cts.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            _loop = null;
        }
        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            _log.Warn($"OpenCode log directory does not exist yet: {_directory}");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var newest = FindNewestLog();
                if (newest is null)
                {
                    await Task.Delay(1500, cancellationToken);
                    continue;
                }

                if (!string.Equals(newest, _currentFile, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFile = newest;
                    _partial = "";
                    _decoder = Encoding.UTF8.GetDecoder();
                    // Always start at the end of a newly selected file. OpenCode rotates
                    // between several logs (opencode.log, ecc-hooks.log, ...); reading a
                    // newly selected file from byte 0 replays old quota errors as fresh
                    // detections and caused extra rotations.
                    _offset = SafeLength(newest);
                    _log.Info($"Quota watcher is following {newest} from the end.");
                }

                var length = SafeLength(newest);
                if (length < _offset)
                {
                    _offset = 0;
                    _partial = "";
                    _decoder = Encoding.UTF8.GetDecoder();
                }

                if (length > _offset)
                {
                    var text = ReadNewText(newest, ref _offset);
                    if (text.Length > 0)
                        ConsumeText(text);
                }

                MaybeFirePending();
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _log.Debug($"Quota watcher I/O: {ex.Message}");
                try { await Task.Delay(1500, cancellationToken); } catch { break; }
            }
            catch (Exception ex)
            {
                _log.Warn($"Quota watcher error: {ex.Message}");
                try { await Task.Delay(2000, cancellationToken); } catch { break; }
            }
        }
    }

    private string? FindNewestLog()
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
                return null;
            return System.IO.Directory
                .EnumerateFiles(_directory, "*.log", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Select(info => info.FullName)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private string ReadNewText(string path, ref long offset)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length < offset)
        {
            offset = 0;
            _partial = "";
            _decoder = Encoding.UTF8.GetDecoder();
        }
        stream.Seek(offset, SeekOrigin.Begin);

        var builder = new StringBuilder();
        var buffer = new byte[64 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            offset += read;
            var count = _decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
            if (count > 0)
                builder.Append(chars, 0, count);
        }
        return builder.ToString();
    }

    private void ConsumeText(string text)
    {
        _partial += text;
        if (_partial.Length > 2 * 1024 * 1024)
        {
            _log.Warn("Quota watcher discarded an oversized partial log line.");
            _partial = "";
            return;
        }

        var lines = _partial.Split('\n');
        if (lines.Length == 1)
            return;

        _partial = lines[^1];
        for (var i = 0; i < lines.Length - 1; i++)
            InspectLine(lines[i].TrimEnd('\r'));
    }

    public static QuotaKind? ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        var lower = line.ToLowerInvariant();
        var isError = lower.Contains("level=error", StringComparison.Ordinal)
                      || lower.Contains("level=fatal", StringComparison.Ordinal);
        if (!isError)
            return null;
        if (ContainsAny(lower, UsagePatterns))
            return QuotaKind.UsageLimit;
        if (ContainsAny(lower, RatePatterns))
            return QuotaKind.RateLimit;
        if (ContainsAny(lower, TransportPatterns))
            return QuotaKind.TransportFailure;
        return null;
    }

    private void InspectLine(string line)
    {
        if (line.Length == 0)
            return;

        var lower = line.ToLowerInvariant();
        var kind = ClassifyLine(line);
        var isError = lower.Contains("level=error", StringComparison.Ordinal)
                      || lower.Contains("level=fatal", StringComparison.Ordinal);

        if (kind == QuotaKind.UsageLimit)
        {
            var hit = CreateHit(QuotaKind.UsageLimit, line);
            _pending = hit;
            _pendingAt = DateTimeOffset.Now;
            _log.Warn($"Free-limit error detected in {Path.GetFileName(_currentFile)}" +
                      (hit.SessionId is null ? "" : $" for {hit.SessionId}") +
                      $": {Clip(line, 320)}");
            return;
        }

        if (kind == QuotaKind.TransportFailure)
        {
            _pending = CreateHit(QuotaKind.TransportFailure, line);
            _pendingAt = DateTimeOffset.Now;
            _log.Warn($"OpenCode transport reset detected: {Clip(line, 320)}");
            return;
        }

        if (_pending is not null && !isError
            && (lower.Contains("level=info", StringComparison.Ordinal)
                || lower.Contains("level=debug", StringComparison.Ordinal)))
        {
            _log.Info("The free-limit error was followed by normal activity; clearing the pending detection.");
            _pending = null;
            return;
        }

        if (kind == QuotaKind.RateLimit)
        {
            if (_settings.RotateOnRateLimit)
            {
                _pending = CreateHit(QuotaKind.RateLimit, line);
                _pendingAt = DateTimeOffset.Now;
                _log.Warn($"Rate-limit error detected: {Clip(line, 260)}");
            }
            else
            {
                _log.Info("Rate limit seen; auto-rotate for rate limits is off.");
            }
        }
    }

    private void MaybeFirePending()
    {
        if (_pending is null)
            return;
        if (DateTimeOffset.Now - _pendingAt < _settle)
            return;

        var hit = _pending;
        _pending = null;

        if (DateTimeOffset.Now - _lastHit < _cooldown)
        {
            _log.Info("Quota hit ignored because a rotation was already requested recently.");
            return;
        }

        if (_handling)
        {
            _log.Info("Quota hit ignored because the previous rotation is still running.");
            return;
        }

        _lastHit = DateTimeOffset.Now;
        LastHit = _lastHit;
        _handling = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _onHit(hit);
            }
            catch (Exception ex)
            {
                _log.Error("Quota handler failed", ex);
            }
            finally
            {
                _handling = false;
            }
        });
    }

    private QuotaHit CreateHit(QuotaKind kind, string line)
    {
        var match = SessionIdRegex().Match(line);
        var sessionId = match.Success ? match.Groups[1].Value : null;
        return new QuotaHit(kind, Clip(line), DateTimeOffset.Now, _currentFile ?? "", sessionId);
    }

    private static bool ContainsAny(string text, IEnumerable<string> patterns)
        => patterns.Any(pattern => text.Contains(pattern, StringComparison.Ordinal));

    [System.Text.RegularExpressions.GeneratedRegex(@"sessionID=(ses_[A-Za-z0-9]+)")]
    private static partial System.Text.RegularExpressions.Regex SessionIdRegex();

    private static string Clip(string value, int max = 500)
        => value.Length <= max ? value : value[..max] + "...";

    public async ValueTask DisposeAsync() => await StopAsync();
}
