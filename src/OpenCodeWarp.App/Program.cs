using System.Text;

namespace OpenCodeWarp.App;

internal static class Program
{
    private const string MutexName = "OpenCodeWarp.App.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        // The app must never inherit a proxy from a shell that was pointed at WARP.
        // Only the OpenCode child process receives a proxy environment block.
        ProxyEnvironment.ClearInherited();
        DarkMode.Enable();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "OpenCode Warp is already running. Look for the W icon in the system tray.",
                "OpenCode Warp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        // "Start minimized" in Settings and the --minimized/--tray flags (used by the
        // Start-with-Windows entry) both lead to a tray launch.
        var startMinimized = AppSettings.Load().StartMinimized
            || args.Any(arg =>
                arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        Application.Run(new MainForm(startMinimized));
    }

    private static void ReportCrash(Exception? exception)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var path = Path.Combine(AppPaths.Root, "crash.log");
            var text = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception?.GetType().Name}: {exception?.Message}")
                .AppendLine(exception?.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(path, text, Encoding.UTF8);

            MessageBox.Show(
                "OpenCode Warp hit an unexpected error. Details were written to:\n" + path +
                "\n\n" + exception?.Message,
                "OpenCode Warp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // A crash reporter must never throw.
        }
    }
}
