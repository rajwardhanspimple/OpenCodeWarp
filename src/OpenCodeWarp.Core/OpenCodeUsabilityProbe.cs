using System.Diagnostics;
using System.Text;

namespace OpenCodeWarp;

public sealed record UsabilityProbeResult(bool Usable, bool RotateAgain, string Detail, TimeSpan Duration)
{
    public static UsabilityProbeResult Skipped(string reason)
        => new(false, false, reason, TimeSpan.Zero);
}

/// <summary>
/// Proves that a freshly rotated WARP IP is actually usable by asking the OpenCode CLI
/// for a one-word completion from the free DeepSeek model — exactly like a human would
/// type it in a terminal. The probe process runs with the relay proxy environment, so
/// the request exercises the new tunnel end to end. Provider-side rejections (usage
/// limit, 429, quota) or no response mean the IP is burned and a new rotation is
/// warranted; local problems (CLI missing, not authenticated) are reported as
/// inconclusive because rotating cannot fix them.
/// </summary>
public sealed class OpenCodeUsabilityProbe
{
    public const string ProbeModel = "opencode/deepseek-v4.1-flash-free";
    public const string ProbePrompt = "Reply with exactly one word: OK";

    /// <summary>Provider-side rejections that a new IP can plausibly fix.</summary>
    private static readonly string[] BurnedMarkers =
    {
        "free usage limit",
        "usage limit",
        "out of usage",
        "quota",
        "insufficient credits",
        "no credits",
        "credit balance",
        "payment required",
        "too many requests",
        "rate limit",
        "rate_limit",
        "statuscode=429",
        "status=429",
        "http 429",
        "code 429"
    };

    /// <summary>Local problems that no WARP rotation can fix.</summary>
    private static readonly string[] LocalErrorMarkers =
    {
        "not authenticated",
        "not logged in",
        "login required",
        "no api key",
        "api key",
        "unauthorized",
        "command not found",
        "is not recognized",
        "no such model",
        "unknown model"
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private readonly AppSettings _settings;
    private readonly OpenCodeLauncher _launcher;
    private readonly AppLog _log;

    public OpenCodeUsabilityProbe(AppSettings settings, OpenCodeLauncher launcher, AppLog log)
    {
        _settings = settings;
        _launcher = launcher;
        _log = log;
    }

    public async Task<UsabilityProbeResult> ProbeAsync(string proxyUrl, CancellationToken cancellationToken)
    {
        var executable = _launcher.DiscoverExecutable();
        if (executable is null)
            return UsabilityProbeResult.Skipped("the OpenCode CLI was not found");

        var stopwatch = Stopwatch.StartNew();
        // One fixed archived session is reused for every probe, so probing never
        // accumulates sessions in OpenCode's store.
        var (application, arguments) = OpenCodeLauncher.BuildCommandLine(
            executable,
            $"run --model {ProbeModel} --session ocwarp-ip-probe --archived " +
            $"--title \"OpenCodeWarp IP probe\" --auto \"{ProbePrompt}\"");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);
        var timedOut = false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = application,
                Arguments = arguments,
                WorkingDirectory = _settings.ProjectFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment.Clear();
            foreach (var entry in _launcher.BuildEnvironmentEntries(proxyUrl, serverPassword: null))
                psi.Environment[entry.Key] = entry.Value;

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return new UsabilityProbeResult(false, false, "the probe process did not start", stopwatch.Elapsed);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            }

            var stdout = await SafeReadAsync(stdoutTask);
            var stderr = await SafeReadAsync(stderrTask);
            var duration = stopwatch.Elapsed;
            var response = stdout.Trim();
            var combined = (stdout + "\n" + stderr).Trim();

            if (timedOut)
                return new UsabilityProbeResult(false, true, $"no response within {Timeout.TotalSeconds:0}s", duration);

            var exited = SafeExitCode(process);
            if (exited == 0 && response.Length > 0)
                return new UsabilityProbeResult(true, false, $"model replied: {Clip(response, 120)}", duration);

            var burned = FindMarker(combined, BurnedMarkers);
            if (burned is not null)
                return new UsabilityProbeResult(false, true, Clip(burned, 240), duration);

            var local = FindMarker(combined, LocalErrorMarkers);
            if (local is not null)
                return new UsabilityProbeResult(false, false, Clip(local, 240), duration);

            if (response.Length == 0 && exited == 0)
                return new UsabilityProbeResult(false, true, "the CLI exited without a model response", duration);

            return new UsabilityProbeResult(
                false,
                false,
                $"probe failed (exit {exited}): {Clip(combined, 240)}",
                duration);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("Usability probe crashed: " + ex.Message);
            return new UsabilityProbeResult(false, false, $"probe error: {ex.Message}", stopwatch.Elapsed);
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    private static string? FindMarker(string text, string[] markers)
    {
        if (text.Length == 0)
            return null;
        var lower = text.ToLowerInvariant();
        foreach (var marker in markers)
        {
            var index = lower.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                var start = Math.Max(0, index - 60);
                var length = Math.Min(text.Length - start, marker.Length + 160);
                return text[start..(start + length)].Replace("\n", " ").Trim();
            }
        }
        return null;
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task; } catch { return ""; }
    }

    private static string Clip(string value, int max)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "...";
    }
}
