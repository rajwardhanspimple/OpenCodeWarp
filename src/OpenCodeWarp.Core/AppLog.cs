using System.Collections.Concurrent;
using System.Text;

namespace OpenCodeWarp;

public sealed record LogEntry(DateTime Timestamp, string Level, string Message)
{
    public override string ToString()
        => $"{Timestamp:HH:mm:ss.fff} {Level,-5} {Message}";
}

public sealed class AppLog
{
    private static readonly ConcurrentDictionary<string, int> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["debug"] = 0,
        ["info"] = 1,
        ["warn"] = 2,
        ["error"] = 3,
        ["none"] = 4
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private string _minimumLevel;

    public event Action<LogEntry>? EntryWritten;
    public bool EchoToConsole { get; set; }
    public string LogFile { get; private set; }

    public AppLog(string? directory = null, string minimumLevel = "info")
    {
        _directory = directory ?? AppPaths.LogDirectory;
        _minimumLevel = NormalizeLevel(minimumLevel);
        Directory.CreateDirectory(_directory);
        LogFile = Path.Combine(_directory, $"relay-{DateTime.Now:yyyyMMdd}.log");
        CleanupOldLogs();
    }

    public void SetLevel(string level) => _minimumLevel = NormalizeLevel(level);

    public void Debug(string message) => Write("debug", message);
    public void Info(string message) => Write("info", message);
    public void Warn(string message) => Write("warn", message);
    public void Error(string message) => Write("error", message);

    public void Error(string message, Exception ex)
        => Write("error", $"{message}: {ex.GetType().Name}: {ex.Message}");

    public void Write(string level, string message)
    {
        level = NormalizeLevel(level);
        if (Levels[level] < Levels[_minimumLevel])
            return;

        var entry = new LogEntry(DateTime.Now, level, message);
        try
        {
            lock (_gate)
            {
                File.AppendAllText(LogFile, entry + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }

        if (EchoToConsole)
        {
            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = level switch
                {
                    "error" => ConsoleColor.Red,
                    "warn" => ConsoleColor.Yellow,
                    "debug" => ConsoleColor.DarkGray,
                    _ => previous
                };
                Console.WriteLine(entry);
            }
            catch
            {
                // no console attached
            }
            finally
            {
                try { Console.ForegroundColor = previous; } catch { }
            }
        }

        try { EntryWritten?.Invoke(entry); } catch { }
    }

    private static string NormalizeLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
            return "info";
        var value = level.Trim().ToLowerInvariant();
        return Levels.ContainsKey(value) ? value : "info";
    }

    private void CleanupOldLogs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "relay-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
                    File.Delete(file);
            }
        }
        catch
        {
            // best effort only
        }
    }
}
