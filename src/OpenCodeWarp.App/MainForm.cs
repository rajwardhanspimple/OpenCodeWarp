using System.Diagnostics;
using System.Net;
using System.Net.Http;

namespace OpenCodeWarp.App;

internal sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly AppLog _log;
    private readonly AppController _controller;
    private readonly bool _startMinimized;
    private readonly CancellationTokenSource _startupCts = new();
    private readonly Icon _appIcon;
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly Font _monoFont;

    private Task? _startupTask;
    private NotifyIcon _tray = null!;
    private ToolStripMenuItem _trayOpen = null!;
    private ToolStripMenuItem _trayLaunch = null!;
    private ToolStripMenuItem _trayRotate = null!;

    private StatusPill _overallPill = null!;
    private Label _headerSubtitle = null!;
    private Label _footerInfo = null!;
    private Label _bannerTitle = null!;
    private Label _bannerText = null!;
    private StatusPill _scopePill = null!;
    private StatusCard _warpCard = null!;
    private StatusCard _relayCard = null!;
    private StatusCard _openCodeCard = null!;
    private StatusCard _rotationCard = null!;
    private FlatButton _launchButton = null!;
    private FlatButton _rotateButton = null!;
    private FlatButton _testWarpButton = null!;
    private FlatButton _reconnectButton = null!;
    private FlatButton _splitButton = null!;
    private RichTextBox _logBox = null!;

    private int _logLines;
    private bool _starting;
    private bool _launching;
    private bool _rotating;
    private bool _exiting;
    private bool _exitRequested;
    private bool _trayHintShown;
    private bool _launchButtonShowsStop;
    private bool _hidingFromResize;
    private int _externalOpenCodeCount;
    private DateTime _lastExternalScan = DateTime.MinValue;

    public MainForm(bool startMinimized)
    {
        _startMinimized = startMinimized;
        ProxyEnvironment.ClearInherited();

        _settings = AppSettings.Load();
        _appIcon = AppIcon.Create();
        _monoFont = CreateMonoFont();
        _log = new AppLog(null, _settings.LogLevel) { EchoToConsole = false };
        _log.SetLevel(_settings.LogLevel);
        _controller = new AppController(_settings, _log);

        InitializeWindow();
        BuildUi();
        BuildTray();
        HookEvents();

        _refreshTimer.Interval = 1000;
        _refreshTimer.Tick += (_, _) => RefreshUi();
        _refreshTimer.Start();

        RefreshUi();
    }

    private void InitializeWindow()
    {
        Text = "OpenCode Warp";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1200, 780);
        MinimumSize = new Size(1060, 700);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Font(9f);
        Icon = _appIcon;
        DoubleBuffered = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        HandleCreated += (_, _) => WindowTheme.EnableDarkTitleBar(Handle);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        root.Controls.Add(BuildHeader(), 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Background,
            Margin = new Padding(0),
            Padding = new Padding(24, 4, 24, 12)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(BuildStatusRow(), 0, 0);
        content.Controls.Add(BuildBanner(), 0, 1);
        content.Controls.Add(BuildLower(), 0, 2);
        root.Controls.Add(content, 0, 1);

        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Padding = new Padding(24, 14, 24, 10)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Theme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var logo = new LogoPanel { Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 14, 0) };

        var text = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            BackColor = Theme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        text.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        text.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "OpenCode Warp",
            Font = Theme.Font(18f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            BackColor = Theme.Background,
            Margin = new Padding(0)
        };
        _headerSubtitle = new Label
        {
            Text = "Model traffic uses WARP. Search and web fetch stay direct.",
            Font = Theme.Font(8.75f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            BackColor = Theme.Background,
            Margin = new Padding(0, 2, 0, 0)
        };
        text.Controls.Add(title, 0, 0);
        text.Controls.Add(_headerSubtitle, 0, 1);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Right,
            BackColor = Theme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };

        _overallPill = new StatusPill { Margin = new Padding(0, 7, 12, 0), Anchor = AnchorStyles.Left };

        var settings = SmallButton("Settings", 104, 34);
        settings.Margin = new Padding(0, 4, 0, 0);
        settings.Click += (_, _) => OpenSettings();

        var hide = SmallButton("Hide", 74, 34);
        hide.Margin = new Padding(8, 4, 0, 0);
        hide.Click += (_, _) => HideToTray();
        _toolTip.SetToolTip(hide, "Keep the relay running in the system tray");

        actions.Controls.Add(_overallPill);
        actions.Controls.Add(settings);
        actions.Controls.Add(hide);

        grid.Controls.Add(logo, 0, 0);
        grid.Controls.Add(text, 1, 0);
        grid.Controls.Add(actions, 2, 0);
        header.Controls.Add(grid);
        return header;
    }

    private Control BuildStatusRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Theme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        for (var i = 0; i < 4; i++)
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        _warpCard = new StatusCard("WARP tunnel") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };
        _relayCard = new StatusCard("Local relay") { Dock = DockStyle.Fill, Margin = new Padding(5, 0, 5, 0) };
        _openCodeCard = new StatusCard("OpenCode") { Dock = DockStyle.Fill, Margin = new Padding(5, 0, 5, 0) };
        _rotationCard = new StatusCard("Rotations") { Dock = DockStyle.Fill, Margin = new Padding(10, 0, 0, 0) };

        row.Controls.Add(_warpCard, 0, 0);
        row.Controls.Add(_relayCard, 1, 0);
        row.Controls.Add(_openCodeCard, 2, 0);
        row.Controls.Add(_rotationCard, 3, 0);
        return row;
    }

    private Control BuildBanner()
    {
        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 12, 18, 12),
            Margin = new Padding(0, 12, 0, 0)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var text = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        text.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        text.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _bannerTitle = new Label
        {
            Text = "AI-only split routing",
            Font = Theme.Font(11.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            BackColor = Theme.Surface,
            Margin = new Padding(0)
        };
        _bannerText = new Label
        {
            Text = "Model and provider calls use the rotating WARP IP. Search and web fetch stay direct, so they keep working.",
            Font = Theme.Font(8.5f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            MaximumSize = new Size(680, 0),
            BackColor = Theme.Surface,
            Margin = new Padding(0, 3, 0, 0)
        };
        text.Controls.Add(_bannerTitle, 0, 0);
        text.Controls.Add(_bannerText, 0, 1);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Anchor = AnchorStyles.Right,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _scopePill = new StatusPill { Margin = new Padding(0, 3, 10, 0) };
        _splitButton = SmallButton("Test split routing", 146, 32);
        _splitButton.Margin = new Padding(0);
        _splitButton.Click += (_, _) => RunGuardedAsync(TestSplitAsync);
        _toolTip.SetToolTip(_splitButton, "Verify that AI hosts use WARP, search hosts stay direct, and web fetch works");

        actions.Controls.Add(_scopePill);
        actions.Controls.Add(_splitButton);

        grid.Controls.Add(text, 0, 0);
        grid.Controls.Add(actions, 1, 0);
        card.Controls.Add(grid);
        return card;
    }

    private Control BuildLower()
    {
        var lower = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Background,
            Margin = new Padding(0, 12, 0, 0),
            Padding = new Padding(0)
        };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 69));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));

        var log = BuildLogCard();
        log.Dock = DockStyle.Fill;
        log.Margin = new Padding(0, 0, 10, 0);

        var actions = BuildActionsCard();
        actions.Dock = DockStyle.Fill;
        actions.Margin = new Padding(10, 0, 0, 0);

        lower.Controls.Add(log, 0, 0);
        lower.Controls.Add(actions, 1, 0);
        return lower;
    }

    private Control BuildLogCard()
    {
        var card = new CardPanel
        {
            Padding = new Padding(18, 14, 18, 14),
            Margin = new Padding(0)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        var head = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Activity",
            Font = Theme.Font(11f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Theme.Surface,
            Margin = new Padding(0)
        };

        var clear = SmallButton("Clear", 62, 24);
        clear.Font = Theme.Font(8f, FontStyle.Bold);
        clear.Margin = new Padding(0, 2, 0, 0);
        clear.Click += (_, _) => ClearLog();

        head.Controls.Add(title, 0, 0);
        head.Controls.Add(clear, 1, 0);

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(8, 12, 17),
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = _monoFont,
            ReadOnly = true,
            WordWrap = true,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin = new Padding(0, 4, 0, 4),
            TabStop = false
        };

        var hint = new Label
        {
            Text = "Live relay, WARP, quota, and rotation events.",
            Font = Theme.Font(8f),
            ForeColor = Theme.Muted,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            BackColor = Theme.Surface,
            Margin = new Padding(0)
        };

        grid.Controls.Add(head, 0, 0);
        grid.Controls.Add(_logBox, 0, 1);
        grid.Controls.Add(hint, 0, 2);
        card.Controls.Add(grid);
        return card;
    }

    private Control BuildActionsCard()
    {
        var card = new CardPanel
        {
            Padding = new Padding(18, 14, 18, 14),
            Margin = new Padding(0)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 9,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        for (var i = 0; i < 8; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "OpenCode stays open",
            Font = Theme.Font(13f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Dock = DockStyle.Top
        };
        var subtitle = new Label
        {
            Text = "WARP rotates underneath the running TUI.",
            Font = Theme.Font(8.5f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 3, 0, 0),
            Dock = DockStyle.Top
        };

        _launchButton = new FlatButton
        {
            Text = "Launch OpenCode",
            Dock = DockStyle.Top,
            Height = 46,
            NormalBackColor = Theme.Accent,
            HoverBackColor = Color.FromArgb(94, 238, 178),
            PressedBackColor = Color.FromArgb(43, 197, 133),
            ForeColor = Color.FromArgb(7, 24, 17),
            BorderColor = Color.Transparent,
            Font = Theme.Font(10f, FontStyle.Bold),
            Radius = 10,
            Margin = new Padding(0, 12, 0, 0)
        };
        _launchButton.Click += (_, _) => RunGuardedAsync(LaunchOrStopAsync);
        _toolTip.SetToolTip(_launchButton,
            "Starts OpenCode with a private proxy environment. This button never restarts a running TUI automatically.");

        _rotateButton = new FlatButton
        {
            Text = "Rotate WARP now",
            Dock = DockStyle.Top,
            Height = 42,
            NormalBackColor = Theme.SurfaceAlt,
            HoverBackColor = Theme.SurfaceHover,
            PressedBackColor = Theme.Border,
            ForeColor = Theme.Warn,
            BorderColor = Theme.WithAlpha(Theme.Warn, 75),
            Font = Theme.Font(9.75f, FontStyle.Bold),
            Radius = 9,
            Margin = new Padding(0, 8, 0, 0)
        };
        _rotateButton.Click += (_, _) => RunGuardedAsync(RotateNowAsync);
        _toolTip.SetToolTip(_rotateButton, "Disconnect, create a fresh WARP registration, reconnect, and keep OpenCode running");

        var testRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 10, 0, 0),
            Height = 34,
            Padding = new Padding(0)
        };
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        testRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _testWarpButton = SmallButton("Test WARP", 0, 32);
        _testWarpButton.Dock = DockStyle.Fill;
        _testWarpButton.Margin = new Padding(0, 0, 4, 0);
        _testWarpButton.Click += (_, _) => RunGuardedAsync(TestWarpAsync);

        _reconnectButton = SmallButton("Reconnect", 0, 32);
        _reconnectButton.Dock = DockStyle.Fill;
        _reconnectButton.Margin = new Padding(4, 0, 0, 0);
        _reconnectButton.Click += (_, _) => RunGuardedAsync(ReconnectAsync);
        _toolTip.SetToolTip(_reconnectButton, "Make sure WARP is connected in proxy mode and wait for warp=on");

        testRow.Controls.Add(_testWarpButton, 0, 0);
        testRow.Controls.Add(_reconnectButton, 1, 0);

        var utilityRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 8, 0, 0),
            Height = 34,
            Padding = new Padding(0)
        };
        utilityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        utilityRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var project = SmallButton("Project folder", 0, 32);
        project.Dock = DockStyle.Fill;
        project.Margin = new Padding(0, 0, 4, 0);
        project.Click += (_, _) => OpenFolder(_settings.ProjectFolder);

        var logs = SmallButton("Log folder", 0, 32);
        logs.Dock = DockStyle.Fill;
        logs.Margin = new Padding(4, 0, 0, 0);
        logs.Click += (_, _) => OpenFolder(AppPaths.LogDirectory);

        utilityRow.Controls.Add(project, 0, 0);
        utilityRow.Controls.Add(logs, 1, 0);

        var note = new Label
        {
            Text = "Connection resets trigger a WARP reconnect and endpoint check. OpenCode stays open.",
            Font = Theme.Font(8.25f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            MaximumSize = new Size(250, 0),
            BackColor = Theme.Surface,
            Margin = new Padding(0, 12, 0, 0),
            Dock = DockStyle.Top
        };

        grid.Controls.Add(title, 0, 0);
        grid.Controls.Add(subtitle, 0, 1);
        grid.Controls.Add(_launchButton, 0, 2);
        grid.Controls.Add(_rotateButton, 0, 3);
        grid.Controls.Add(testRow, 0, 4);
        grid.Controls.Add(utilityRow, 0, 5);
        grid.Controls.Add(note, 0, 8);

        card.Controls.Add(grid);
        return card;
    }

    private Control BuildFooter()
    {
        var footer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(8, 12, 16),
            Padding = new Padding(24, 0, 24, 0)
        };

        _footerInfo = new Label
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(8.25f),
            ForeColor = Theme.Muted,
            BackColor = Color.FromArgb(8, 12, 16),
            Text = "Relay starting…"
        };

        var version = new Label
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.Font(8.25f),
            ForeColor = Theme.Muted,
            BackColor = Color.FromArgb(8, 12, 16),
            Text = "v1.0  ·  .NET 8"
        };

        footer.Controls.Add(_footerInfo);
        footer.Controls.Add(version);
        return footer;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Font(9.25f),
            ShowImageMargin = false,
            Padding = new Padding(4)
        };

        _trayOpen = new ToolStripMenuItem("Open dashboard", null, (_, _) => ShowFromTray())
        {
            Font = Theme.Font(9.25f, FontStyle.Bold)
        };
        _trayLaunch = new ToolStripMenuItem("Launch OpenCode", null, (_, _) => RunGuardedAsync(LaunchOrStopAsync));
        _trayRotate = new ToolStripMenuItem("Rotate WARP now", null, (_, _) => RunGuardedAsync(RotateNowAsync));
        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings());
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => _ = ExitAsync());

        menu.Items.Add(_trayOpen);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_trayLaunch);
        menu.Items.Add(_trayRotate);
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);
        menu.Opening += (_, _) => UpdateTrayMenu();

        _tray = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "OpenCode Warp",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void HookEvents()
    {
        _log.EntryWritten += entry => PostToUi(() => AppendLogEntry(entry));
        _controller.StateChanged += () => PostToUi(RefreshUi);

        _controller.QuotaDetected += hit => PostToUi(() =>
        {
            if (hit.Kind == QuotaKind.UsageLimit)
            {
                Balloon("Free usage limit detected",
                    "Renewing the WARP IP. OpenCode stays open; the account quota may still apply.",
                    ToolTipIcon.Warning);
            }
            else if (hit.Kind == QuotaKind.TransportFailure)
            {
                Balloon("Connection issue detected",
                    "Checking the WARP connection. OpenCode stays open; retry after it reconnects.",
                    ToolTipIcon.Warning);
            }
            else
            {
                Balloon("Rate limit detected",
                    _settings.RotateOnRateLimit
                        ? "WARP rotation is enabled for rate limits. OpenCode stays open."
                        : "Automatic WARP rotation for rate limits is off.",
                    ToolTipIcon.Warning);
            }
            RefreshUi();
        });

        _controller.RotationFinished += result => PostToUi(() =>
        {
            if (result.Success)
            {
                var verified = _controller.LastRelayVerified == true
                    ? " Verified: OpenCode is using the new IP."
                    : " Warning: the relay has not confirmed the new IP yet.";
                var probe = _controller.LastProbe;
                var probeText = probe is null
                    ? ""
                    : probe.Usable
                        ? " Usability probe passed: the free DeepSeek model responded."
                        : $" Usability probe FAILED: {probe.Detail}";
                var ok = _controller.LastRelayVerified == true && probe is null or { Usable: true };
                Balloon("WARP IP rotated",
                        $"{ShortIp(result.OldIp)} → {ShortIp(result.NewIp)} ({result.Colo}). OpenCode was not restarted.{verified}{probeText}",
                        ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            else
            {
                Balloon("WARP rotation failed", result.Error ?? "No new exit IP was obtained.", ToolTipIcon.Error);
            }
            RefreshUi();
        });

        _controller.OpenCodeStarted += result => PostToUi(() =>
        {
            Balloon("OpenCode started", $"PID {result.Pid} is using the local WARP relay.", ToolTipIcon.Info);
            RefreshUi();
        });

        _controller.OpenCodeExited += code => PostToUi(() =>
        {
            Balloon("OpenCode exited", $"Exit code {code}. The relay is still running.", ToolTipIcon.Info);
            RefreshUi();
        });
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        DarkMode.ApplyToTree(this);
        if (_startMinimized)
            HideToTray(showHint: false);
        else
            Activate();

        _startupTask = StartControllerAsync();
        await _startupTask;
    }

    private async Task StartControllerAsync()
    {
        if (_starting)
            return;

        _starting = true;
        RefreshUi();
        try
        {
            await _controller.StartAsync(_startupCts.Token);
            _log.Info("Dashboard is ready. OpenCode can be launched with its own proxy environment.");
            _log.Info("Launch OpenCode from this dashboard so its process gets the relay proxy; " +
                      "an already-running OpenCode cannot be retrofitted with a new environment.");
        }
        catch (OperationCanceledException) when (_startupCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Startup failed", ex);
            PostToUi(() => MessageBox.Show(this,
                "OpenCode Warp could not finish starting.\n\n" + ex.Message +
                "\n\nYou can retry with Reconnect or check the activity log.",
                "OpenCode Warp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error));
        }
        finally
        {
            _starting = false;
            RefreshUi();
        }
    }

    private void RefreshUi()
    {
        if (IsDisposed || Disposing)
            return;

        try
        {
            var relay = _controller.Relay;
            var warpTrace = _controller.LastTrace;
            var warpOn = _controller.WarpOnline;
            var relayRunning = relay.IsRunning;
            var running = _controller.OpenCodeRunning;
            var rotating = _rotating || _controller.IsRotating;
            var starting = _starting;

            if (DateTime.UtcNow - _lastExternalScan > TimeSpan.FromSeconds(5))
            {
                _externalOpenCodeCount = ScanExternalOpenCode();
                _lastExternalScan = DateTime.UtcNow;
            }

            if (_exiting)
                _overallPill.SetStatus("STOPPING", Theme.Muted);
            else if (starting)
                _overallPill.SetStatus("STARTING", Theme.Warn);
            else if (rotating)
                _overallPill.SetStatus("ROTATING", Theme.Warn);
            else if (warpOn && relayRunning)
                _overallPill.SetStatus("LIVE", Theme.Accent);
            else
                _overallPill.SetStatus("OFFLINE", Theme.Error);

            _headerSubtitle.Text = _settings.WarpScope == WarpScope.AiOnly
                ? "Model traffic uses WARP. Search and web fetch stay direct."
                : "All OpenCode traffic uses WARP except the direct-host list.";

            _warpCard.Pill.SetStatus(warpOn ? "ONLINE" : starting ? "CHECK" : "OFFLINE",
                warpOn ? Theme.Accent : starting ? Theme.Warn : Theme.Error);

            var exitIp = warpTrace is not null && warpTrace.Ip.Length > 0
                ? warpTrace.Ip
                : _controller.LastExitIp ?? "";
            if (warpOn)
            {
                _warpCard.Value.Text = ShortenIp(exitIp);
                var warp = warpTrace?.Warp ?? "on";
                var suffix = warp == "plus" ? " · WARP+" : "";
                var colo = DisplayColo(_controller.LastColo);
                _warpCard.Detail.Text = exitIp.Contains(':')
                    ? $"{exitIp}\n{colo} · warp={warp}{suffix}"
                    : $"{colo} · warp={warp}{suffix}";
            }
            else
            {
                _warpCard.Value.Text = starting ? "Checking…" : "Not connected";
                _warpCard.Detail.Text = rotating
                    ? "New WARP registration in progress…"
                    : _controller.LastError ?? "Waiting for the WARP tunnel";
            }

            _relayCard.Pill.SetStatus(relayRunning ? "LISTENING" : "STOPPED",
                relayRunning ? Theme.Accent : starting ? Theme.Warn : Theme.Error);
            _relayCard.Value.Text = relayRunning ? $"127.0.0.1:{relay.BoundPort}" : "Not listening";
            _relayCard.Detail.Text = relayRunning
                ? $"{relay.ActiveConnections} active · {relay.TotalConnections} total · " +
                  Theme.FormatBytes(relay.BytesToUpstream + relay.BytesToClient)
                : starting ? "Starting local relay…" : "Start the app to accept connections";

            var externalOpenCode = !running && _externalOpenCodeCount > 0;
            _openCodeCard.Pill.SetStatus(
                running ? "RUNNING" : externalOpenCode ? "EXTERNAL" : "STOPPED",
                running ? Theme.Accent : externalOpenCode ? Theme.Warn : Theme.Muted);
            _openCodeCard.Value.Text = running && _controller.OpenCode.ProcessId is int pid
                ? $"PID {pid}"
                : _launching ? "Starting…" : externalOpenCode ? "Outside app" : "Not running";
            _openCodeCard.Detail.Text = running
                ? $"{FormatUptime(_controller.OpenCode.StartedAt)} · process-only proxy"
                : externalOpenCode
                    ? $"{_externalOpenCodeCount} process(es) outside this dashboard\nLaunch here to use the relay"
                    : "Launch from here to use the relay";

            var quotaMode = "AUTO CHECK";
            _rotationCard.Pill.SetStatus(rotating ? "ROTATING" : quotaMode,
                rotating ? Theme.Warn : Theme.Accent);
            _rotationCard.Value.Text = _controller.RotationCount.ToString();
            _rotationCard.Detail.Text = _controller.LastRotationAt is DateTime last
                ? $"On manual/rate rotation · Last {last:HH:mm:ss} · {ShortIp(_controller.LastExitIp)}"
                : "Checks WARP after connection reset";

            var aiOnly = _settings.WarpScope == WarpScope.AiOnly;
            _bannerTitle.Text = aiOnly ? "AI-only split routing" : "All OpenCode traffic via WARP";
            _bannerText.Text = aiOnly
                ? "Model and provider calls use the rotating WARP IP. Search (mcp.exa.ai) and ordinary web fetch stay direct."
                : "Everything uses WARP except the direct-host compatibility list. Search and fetch exceptions stay direct.";
            _scopePill.SetStatus(aiOnly ? "AI ONLY" : "ALL TRAFFIC", aiOnly ? Theme.Accent : Theme.Cyan);

            UpdateLaunchButton(running);
            var canOperate = _controller.IsStarted && !_starting && !_exiting;
            _launchButton.Enabled = canOperate && !_launching && !rotating;
            _rotateButton.Enabled = canOperate && !_rotating && !_launching;
            _testWarpButton.Enabled = !_starting && !_exiting && !_launching;
            _reconnectButton.Enabled = !_starting && !_exiting && !rotating && !_launching;
            _splitButton.Enabled = relayRunning && !_exiting;

            _footerInfo.Text = $"Relay {_controller.ProxyUrl}  ·  " +
                               (aiOnly ? "AI traffic → WARP, search & web fetch → direct" : "all traffic → WARP, direct-host exceptions") +
                               "  ·  OpenCode is never restarted on a limit hit";
            UpdateTray();
        }
        catch
        {
            // A refresh must never tear down the dashboard.
        }
    }

    private void UpdateLaunchButton(bool running)
    {
        if (running == _launchButtonShowsStop)
            return;

        _launchButtonShowsStop = running;
        if (running)
        {
            _launchButton.Text = "Stop OpenCode";
            _launchButton.NormalBackColor = Color.FromArgb(49, 27, 32);
            _launchButton.HoverBackColor = Color.FromArgb(67, 34, 40);
            _launchButton.PressedBackColor = Color.FromArgb(38, 20, 24);
            _launchButton.ForeColor = Theme.Error;
            _launchButton.BorderColor = Theme.WithAlpha(Theme.Error, 80);
        }
        else
        {
            _launchButton.Text = _launching ? "Starting OpenCode…" : "Launch OpenCode";
            _launchButton.NormalBackColor = Theme.Accent;
            _launchButton.HoverBackColor = Color.FromArgb(94, 238, 178);
            _launchButton.PressedBackColor = Color.FromArgb(43, 197, 133);
            _launchButton.ForeColor = Color.FromArgb(7, 24, 17);
            _launchButton.BorderColor = Color.Transparent;
        }
    }

    private void UpdateTray()
    {
        try
        {
            var status = _controller.WarpOnline
                ? "WARP online"
                : _starting ? "starting" : "WARP offline";
            var running = _controller.OpenCodeRunning ? " · OpenCode running" : "";
            var text = $"OpenCode Warp — {status}{running}";
            _tray.Text = text.Length <= 63 ? text : text[..63];
            UpdateTrayMenu();
        }
        catch
        {
            // The tray is optional.
        }
    }

    private void UpdateTrayMenu()
    {
        var running = _controller.OpenCodeRunning;
        _trayLaunch.Text = running ? "Stop OpenCode" : "Launch OpenCode";
        _trayRotate.Enabled = _controller.IsStarted && !_rotating && !_exitRequested;
        _trayOpen.Text = Visible ? "Open dashboard" : "Open dashboard";
    }

    private async Task LaunchOrStopAsync()
    {
        if (_launching)
            return;

        if (_controller.OpenCodeRunning)
        {
            var confirm = MessageBox.Show(this,
                "Stop OpenCode?\n\nThe TUI will close. WARP and the relay stay running.",
                "Stop OpenCode",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes)
                return;

            _launching = true;
            RefreshUi();
            try
            {
                await _controller.StopOpenCodeAsync();
                Balloon("OpenCode stopped", "The relay and WARP are still available.", ToolTipIcon.Info);
            }
            finally
            {
                _launching = false;
                RefreshUi();
            }
            return;
        }

        _launching = true;
        RefreshUi();
        try
        {
            var result = await _controller.LaunchOpenCodeAsync();
            if (!result.Success)
            {
                _log.Warn("OpenCode did not start: " + result.Message);
                MessageBox.Show(this, result.Message, "OpenCode Warp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _log.Error("OpenCode launch failed", ex);
            MessageBox.Show(this, ex.Message, "OpenCode Warp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _launching = false;
            RefreshUi();
        }
    }

    private async Task RotateNowAsync()
    {
        if (_rotating || !_controller.IsStarted)
            return;

        _rotating = true;
        RefreshUi();
        try
        {
            var result = await _controller.RotateAsync("manual rotation from the dashboard", force: true);
            if (!result.Success)
            {
                MessageBox.Show(this,
                    "WARP rotation failed:\n" + (result.Error ?? "No new exit IP was obtained.") +
                    "\n\nOpenCode is still running. You can retry with Rotate WARP now.",
                    "OpenCode Warp",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Manual rotation failed", ex);
            MessageBox.Show(this, ex.Message, "OpenCode Warp", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _rotating = false;
            RefreshUi();
        }
    }

    private async Task TestWarpAsync()
    {
        var trace = await _controller.TestWarpAsync();
        var text = trace.WarpOn
            ? "WARP is connected and reporting warp=on.\n\n" +
              $"Exit IP : {trace.Ip}\nColo    : {DisplayColo(trace.Colo)}\nWarp    : {trace.Warp}\nLocation: {trace.Loc}"
            : "The WARP test did not report warp=on.\n\n" +
              $"warp    : {(trace.Warp.Length > 0 ? trace.Warp : "unknown")}\n" +
              $"error   : {trace.Error ?? "none"}\n\nUse Reconnect to repair the tunnel.";
        MessageBox.Show(this, text, "WARP test", MessageBoxButtons.OK,
            trace.WarpOn ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task ReconnectAsync()
    {
        if (!_controller.IsStarted)
        {
            _starting = true;
            RefreshUi();
            try
            {
                await _controller.StartAsync(_startupCts.Token);
            }
            finally
            {
                _starting = false;
                RefreshUi();
            }
        }

        var ready = await _controller.EnsureWarpAsync();
        if (ready)
        {
            await _controller.TestRelayAsync();
            MessageBox.Show(this,
                "WARP is ready in local proxy mode.\n\n" +
                $"Exit IP: {_controller.LastExitIp}\nColo   : {DisplayColo(_controller.LastColo)}\nRelay  : {_controller.ProxyUrl}",
                "WARP reconnected",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            MessageBox.Show(this,
                "WARP could not be reconnected:\n" + (_controller.LastError ?? "The trace did not report warp=on."),
                "WARP reconnected",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async Task TestSplitAsync()
    {
        if (!_controller.Relay.IsRunning)
        {
            MessageBox.Show(this, "The local relay is not running yet.", "Split routing test",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var proxy = _controller.ProxyUrl;
        var directTask = TraceProbe.GetAsync(null, 15, CancellationToken.None);
        var aiTask = TraceProbe.GetAsync(proxy, "https://opencode.ai/cdn-cgi/trace", 20, CancellationToken.None);
        var searchTask = TraceProbe.GetAsync(proxy, "https://www.cloudflare.com/cdn-cgi/trace", 20, CancellationToken.None);
        var fetchTask = FetchViaRelayAsync(proxy, "https://example.com/", TimeSpan.FromSeconds(20));

        await Task.WhenAll(directTask, aiTask, searchTask, fetchTask);
        var direct = await directTask;
        var ai = await aiTask;
        var search = await searchTask;
        var fetch = await fetchTask;

        var ok = ai.WarpOn && !search.WarpOn && fetch.StartsWith("200", StringComparison.Ordinal);
        var verdict = ok
            ? "PASS — AI traffic uses WARP, search/fetch stays direct, and web fetch works."
            : "FAIL — one of the routes is wrong. OpenCode may lose search or web fetch.";

        _log.Info($"Split routing test: direct={direct.Ip} warp={direct.Warp}; AI via relay={ai.Ip} warp={ai.Warp}; " +
                  $"search via relay={search.Ip} warp={search.Warp}; example.com={fetch}; {verdict}");

        var text =
            "AI host via relay     : " + Describe(ai) + "\n" +
            "Search host via relay : " + Describe(search) + "\n" +
            "Direct egress         : " + Describe(direct) + "\n" +
            "example.com fetch     : " + fetch + "\n\n" + verdict;
        MessageBox.Show(this, text, "Split routing test", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task<string> FetchViaRelayAsync(string proxy, string url, TimeSpan timeout)
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
            return "error: " + ex.Message;
        }
    }

    private static string Describe(WarpTrace trace)
    {
        if (trace.Ip.Length == 0)
            return trace.Error ?? "no response";
        return $"{trace.Ip} ({DisplayColo(trace.Colo)}, warp={trace.Warp})";
    }

    private void OpenSettings()
    {
        var draft = _settings.Clone();
        using var dialog = new SettingsForm(draft);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var oldRelayPort = _settings.RelayPort;
        var oldWarpPort = _settings.WarpProxyPort;

        _settings.CopyFrom(draft);
        _controller.SaveSettings();
        _log.SetLevel(_settings.LogLevel);

        if (!StartupRegistration.Apply(_settings.StartWithWindows, out var startupError))
        {
            _log.Warn("Could not update the Windows startup entry: " + startupError);
            MessageBox.Show(this,
                "Settings were saved, but the Windows startup entry could not be updated:\n" + startupError,
                "OpenCode Warp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        RefreshUi();

        if (_settings.RelayPort != oldRelayPort || _settings.WarpProxyPort != oldWarpPort)
        {
            MessageBox.Show(this,
                "Port changes take effect after OpenCode Warp restarts.\n\n" +
                $"Current relay: {_controller.ProxyUrl}\n" +
                $"New relay port: {_settings.RelayPort}",
                "OpenCode Warp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        else
        {
            Balloon("Settings saved", "Routing and OpenCode launch settings were updated.", ToolTipIcon.Info);
        }
    }

    private void RunGuardedAsync(Func<Task> action)
    {
        _ = RunGuardedCoreAsync(action);
    }

    private async Task RunGuardedCoreAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_exiting)
        {
        }
        catch (Exception ex)
        {
            _log.Error("Dashboard action failed", ex);
            if (!_exiting)
            {
                MessageBox.Show(this, ex.Message, "OpenCode Warp", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            RefreshUi();
        }
    }

    private void HideToTray(bool showHint = true)
    {
        if (_exiting || _exitRequested)
            return;

        Hide();
        if (showHint && !_trayHintShown)
        {
            _trayHintShown = true;
            Balloon("Still running",
                "OpenCode Warp keeps the relay alive in the tray. Double-click the W icon to reopen.",
                ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        if (_exiting)
            return;
        Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized || _exiting || _hidingFromResize)
            return;
        if (!_settings.MinimizeToTray)
            return;

        _hidingFromResize = true;
        BeginInvoke(new Action(() =>
        {
            _hidingFromResize = false;
            HideToTray();
        }));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_exitRequested)
        {
            base.OnFormClosing(e);
            return;
        }

        if (e.CloseReason != CloseReason.UserClosing)
        {
            _exitRequested = true;
            _refreshTimer.Stop();
            try { _tray.Visible = false; } catch { }
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        if (_settings.MinimizeToTray && !_exiting)
        {
            HideToTray();
            return;
        }

        _ = ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
            return;

        _exiting = true;
        _refreshTimer.Stop();
        try { _tray.Visible = false; } catch { }
        RefreshUi();

        try
        {
            _startupCts.Cancel();
            if (_startupTask is not null)
            {
                try { await _startupTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }

            await _controller.ShutdownAsync(stopOpenCode: true);
        }
        catch (Exception ex)
        {
            try { _log.Error("Shutdown failed", ex); } catch { }
        }
        finally
        {
            _exitRequested = true;
            try { _controller.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { Close(); } catch { }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refreshTimer.Dispose();
        try { _tray.Dispose(); } catch { }
        _toolTip.Dispose();
        _monoFont.Dispose();
        _appIcon.Dispose();
        _startupCts.Dispose();
        base.OnFormClosed(e);
    }

    private void AppendLogEntry(LogEntry entry)
        => AppendLog(entry.Timestamp.ToString("HH:mm:ss"), entry.Level, entry.Message);

    private void AppendLog(string timestamp, string level, string message)
    {
        if (_logBox.IsDisposed)
            return;

        if (_logBox.TextLength > 300_000)
        {
            _logBox.Clear();
            _logLines = 0;
        }

        var atBottom = _logBox.SelectionStart >= _logBox.TextLength - 4;
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;

        if (timestamp.Length > 0)
            AppendLogText(timestamp + " ", Theme.Muted);
        AppendLogText(level.ToUpperInvariant().PadRight(5) + " ", LevelColor(level));
        AppendLogText(message + Environment.NewLine, MessageColor(level));
        _logLines++;

        if (_logLines > 1500)
            TrimLog();

        if (atBottom)
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.ScrollToCaret();
        }
    }

    private void AppendLogText(string text, Color color)
    {
        _logBox.SelectionColor = color;
        _logBox.AppendText(text);
    }

    private void TrimLog()
    {
        try
        {
            var index = _logBox.GetFirstCharIndexFromLine(350);
            if (index > 0)
            {
                _logBox.Select(0, index);
                _logBox.SelectedText = "";
                _logLines = Math.Max(0, _logLines - 350);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private void ClearLog()
    {
        _logBox.Clear();
        _logLines = 0;
        AppendLog(DateTime.Now.ToString("HH:mm:ss"), "info", "Activity cleared. Relay is still running.");
    }

    private void Balloon(string title, string message, ToolTipIcon icon)
    {
        try
        {
            if (!_tray.Visible)
                return;
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = message.Length > 500 ? message[..500] : message;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(4500);
        }
        catch
        {
            // Notifications are best effort.
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open folder:\n" + ex.Message, "OpenCode Warp",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing)
            return;
        if (!IsHandleCreated)
            return;

        try
        {
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Color LevelColor(string level)
        => level.ToLowerInvariant() switch
        {
            "error" => Theme.Error,
            "warn" => Theme.Warn,
            "debug" => Theme.Muted,
            _ => Theme.Info
        };

    private static Color MessageColor(string level)
        => level.ToLowerInvariant() switch
        {
            "error" => Color.FromArgb(252, 165, 165),
            "warn" => Color.FromArgb(253, 210, 130),
            "debug" => Theme.Muted,
            _ => Theme.Text
        };

    private int ScanExternalOpenCode()
    {
        try
        {
            var managed = _controller.OpenCode.ProcessId;
            var managedServer = _controller.OpenCode.ServerProcessId;
            var ids = new HashSet<int>();
            foreach (var name in new[] { "opencode", "opencode-cli", "OpenCode" })
            {
                Process[] processes;
                try { processes = Process.GetProcessesByName(name); }
                catch { continue; }

                foreach (var process in processes)
                {
                    try
                    {
                        if (process.Id != managed && process.Id != managedServer)
                            ids.Add(process.Id);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            return ids.Count;
        }
        catch
        {
            return 0;
        }
    }

    private static string DisplayColo(string? colo)
        => string.IsNullOrWhiteSpace(colo) ? "Colo unknown" : colo!.ToUpperInvariant();

    private static string ShortenIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return "Not connected";
        var value = ip.Trim();
        if (value.Length <= 20)
            return value;
        if (value.Contains(':'))
            return value[..8] + "…" + value[^6..];
        return value;
    }

    private static string ShortIp(string? ip)
        => string.IsNullOrWhiteSpace(ip) ? "unknown" : ip;

    private static string FormatUptime(DateTime? startedAt)
    {
        if (startedAt is null)
            return "Uptime unknown";
        var span = DateTime.Now - startedAt.Value;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"Up {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"Up {span.Minutes:00}:{span.Seconds:00}";
    }

    private static FlatButton SmallButton(string text, int width, int height)
    {
        return new FlatButton
        {
            Text = text,
            Width = width,
            Height = height,
            NormalBackColor = Theme.SurfaceAlt,
            HoverBackColor = Theme.SurfaceHover,
            PressedBackColor = Theme.Border,
            ForeColor = Theme.Text,
            BorderColor = Theme.Border,
            Font = Theme.Font(8.75f, FontStyle.Bold),
            Radius = 8,
            Margin = new Padding(0)
        };
    }

    private static Font CreateMonoFont()
    {
        foreach (var name in new[] { "Cascadia Mono", "Consolas", "Courier New" })
        {
            try
            {
                var family = FontFamily.Families.FirstOrDefault(f =>
                    f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (family is not null)
                    return new Font(family, 8.75f, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch
            {
                // Try the next font.
            }
        }
        return new Font(FontFamily.GenericMonospace, 8.75f, FontStyle.Regular, GraphicsUnit.Point);
    }
}
