namespace OpenCodeWarp.App;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _draft;

    private RadioButton _aiOnlyRadio = null!;
    private RadioButton _allRadio = null!;
    private TextBox _extraAiBox = null!;
    private TextBox _directBox = null!;
    private TextBox _exeBox = null!;
    private TextBox _folderBox = null!;
    private TextBox _argsBox = null!;
    private CheckBox _autoLaunch = null!;
    private CheckBox _rateRotate = null!;
    private CheckBox _usageRotate = null!;
    private CheckBox _probeRotate = null!;
    private TextBox _autoRotateMinutes = null!;
    private TextBox _relayPort = null!;
    private TextBox _warpPort = null!;
    private TextBox _attempts = null!;
    private TextBox _minSeconds = null!;
    private TextBox _waitSeconds = null!;
    private TextBox _healthSeconds = null!;
    private CheckBox _startWithWindows = null!;
    private CheckBox _startMinimized = null!;
    private CheckBox _minimizeToTray = null!;
    private CheckBox _logDirect = null!;
    private readonly Dictionary<string, RadioButton> _logLevelRadios = new(StringComparer.OrdinalIgnoreCase);

    public SettingsForm(AppSettings draft)
    {
        _draft = draft;
        Text = "Settings — OpenCode Warp";
        ClientSize = new Size(860, 840);
        MinimumSize = new Size(800, 620);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Font(9f);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildUi();
        HandleCreated += (_, _) => WindowTheme.EnableDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner is not null)
            Icon = Owner.Icon;
        DarkMode.ApplyToTree(this);
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

        root.Controls.Add(BuildHeader(), 0, 0);

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Background,
            Padding = new Padding(24, 0, 24, 8),
            Margin = new Padding(0)
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Background,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowCount = 4;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(BuildRoutingSection(), 0, 0);
        stack.Controls.Add(BuildOpenCodeSection(), 0, 1);
        stack.Controls.Add(BuildPortsSection(), 0, 2);
        stack.Controls.Add(BuildWindowsSection(), 0, 3);

        scroll.Controls.Add(stack);
        root.Controls.Add(scroll, 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);

        Controls.Add(root);
    }

    private Control BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Padding = new Padding(24, 16, 24, 8)
        };

        var title = new Label
        {
            Text = "Settings",
            Font = Theme.Font(18f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            BackColor = Theme.Background,
            Location = new Point(24, 14)
        };
        var subtitle = new Label
        {
            Text = "Routing, OpenCode launch behavior, ports, and Windows integration.",
            Font = Theme.Font(8.75f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            BackColor = Theme.Background,
            Location = new Point(26, 48)
        };

        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control BuildRoutingSection()
    {
        _aiOnlyRadio = Radio("AI-only split routing  (recommended)", _draft.WarpScope == WarpScope.AiOnly);
        _allRadio = Radio("All OpenCode traffic", _draft.WarpScope == WarpScope.All);

        var scope = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 4,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 10, 0, 0)
        };
        scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        scope.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var aiDescription = Hint(
            "Model and provider calls use the rotating WARP IP. Search hosts such as mcp.exa.ai and ordinary " +
            "web fetch requests stay direct, so they keep working.");
        var allDescription = Hint(
            "Every OpenCode connection uses WARP except the direct-host compatibility list below. " +
            "Use this only if a provider still needs a full tunnel.");

        scope.Controls.Add(_aiOnlyRadio, 0, 0);
        scope.Controls.Add(aiDescription, 0, 1);
        scope.Controls.Add(_allRadio, 0, 2);
        scope.Controls.Add(allDescription, 0, 3);

        _extraAiBox = TextInput(_draft.ExtraAiHosts, multiline: true);
        _directBox = TextInput(_draft.DirectHosts.Replace(",", ", "), multiline: true);

        var body = NewBody();
        AddFull(body, scope);
        AddField(body, "Extra AI provider hosts  (comma separated, *.example.com allowed)", _extraAiBox);
        AddField(body, "Direct-host compatibility list  (used by All traffic mode)", _directBox);

        return Section(
            "Routing",
            "Keep WARP on the traffic that needs a new IP, while search and web fetch stay fast and direct.",
            body);
    }

    private Control BuildOpenCodeSection()
    {
        _exeBox = TextInput(_draft.OpenCodeExe ?? "");
        _folderBox = TextInput(_draft.ProjectFolder);
        _argsBox = TextInput(_draft.OpenCodeExtraArgs);

        var executable = PathField(_exeBox, BrowseExecutable);
        var folder = PathField(_folderBox, BrowseFolder);

        _autoLaunch = Check("Launch OpenCode automatically when the relay starts", _draft.AutoLaunchOpenCode);
        _rateRotate = Check("Rotate on HTTP 429 / rate-limit errors", _draft.RotateOnRateLimit);
        _usageRotate = Check("Rotate on free usage limit errors", _draft.RotateOnUsageLimit);
        _probeRotate = Check("After every rotation, test the IP with the free DeepSeek model and rotate again if burned",
            _draft.ProbeOnRotation);

        var hint = Hint(
            "Leave the executable empty to auto-detect the installed OpenCode CLI. Only OpenCode's process tree " +
            "receives the relay proxy. Connection resets trigger a WARP reconnect and endpoint check, " +
            "without restarting OpenCode or changing models.");

        var body = NewBody();
        AddField(body, "OpenCode executable  (empty = auto-detect)", executable);
        AddField(body, "Project folder", folder);
        AddField(body, "Extra OpenCode arguments  (optional)", _argsBox);
        AddFull(body, hint);
        AddFull(body, _autoLaunch);
        AddFull(body, _usageRotate);
        AddFull(body, _rateRotate);
        AddFull(body, _probeRotate);

        return Section(
            "OpenCode",
            "The TUI stays open while WARP renews after a detected usage limit.",
            body);
    }

    private Control BuildPortsSection()
    {
        _relayPort = NumberInput(_draft.RelayPort);
        _warpPort = NumberInput(_draft.WarpProxyPort);
        _attempts = NumberInput(_draft.RotationAttempts);
        _minSeconds = NumberInput(_draft.MinSecondsBetweenRotations);
        _waitSeconds = NumberInput(_draft.WarpWaitSeconds);
        _healthSeconds = NumberInput(_draft.HealthCheckSeconds);
        _autoRotateMinutes = NumberInput(_draft.AutoRotateMinutes);

        var portsHint = Hint(
            "The relay is the stable local endpoint used by OpenCode. Port changes take effect after the app restarts. " +
            "Cloudflare throttles registrations, so the minimum spacing is 20 seconds.");

        var autoRotateHint = Hint(
            "0 disables the timer. After every rotation the app checks through the relay that the new IP is really in use.");
        autoRotateHint.MaximumSize = new Size(380, 0);

        var body = NewBody();
        AddPair(body,
            Labeled("Local relay port", _relayPort),
            Labeled("Cloudflare WARP proxy port", _warpPort));
        AddPair(body,
            Labeled("Rotation attempts", _attempts),
            Labeled("Minimum seconds between rotations", _minSeconds));
        AddPair(body,
            Labeled("Seconds to wait for a new WARP IP", _waitSeconds),
            Labeled("Health-check interval (seconds)", _healthSeconds));
        AddPair(body,
            Labeled("Auto-rotate WARP every (minutes, 0 = off)", _autoRotateMinutes),
            autoRotateHint);
        AddFull(body, portsHint);

        return Section("Ports and timing", "Defaults are tuned for a fast rotation without rate limiting.", body);
    }

    private Control BuildWindowsSection()
    {
        _startWithWindows = Check("Start with Windows  (minimized to the tray)", _draft.StartWithWindows);
        _startMinimized = Check("Start minimized", _draft.StartMinimized);
        _minimizeToTray = Check("Closing the window keeps the relay running in the tray", _draft.MinimizeToTray);

        var logLevels = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 2, 0, 0),
            Padding = new Padding(0)
        };
        var currentLevel = (_draft.LogLevel ?? "info").Trim().ToLowerInvariant();
        foreach (var level in new[] { "debug", "info", "warn", "error", "none" })
        {
            var radio = new DarkRadioButton
            {
                Text = char.ToUpperInvariant(level[0]) + level[1..],
                Checked = level == currentLevel,
                Font = Theme.Font(9f),
                ForeColor = Theme.Text,
                BackColor = Theme.Surface,
                Margin = new Padding(0, 2, 16, 0),
                AutoSize = true
            };
            _logLevelRadios[level] = radio;
            logLevels.Controls.Add(radio);
        }

        _logDirect = Check("Log every direct connection  (debugging only)", _draft.LogDirectConnections);

        var hint = Hint("Settings are stored in " + AppPaths.SettingsFile + ".");

        var body = NewBody();
        AddFull(body, _startWithWindows);
        AddFull(body, _startMinimized);
        AddFull(body, _minimizeToTray);
        AddField(body, "Log level", logLevels);
        AddFull(body, _logDirect);
        AddFull(body, hint);

        return Section("Windows and logging", "Tray behavior and diagnostics.", body);
    }

    private Control BuildFooter()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(8, 12, 16),
            Padding = new Padding(24, 16, 24, 14)
        };

        var note = new Label
        {
            Text = "Scope and host changes apply immediately. Port changes apply next launch.",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = Theme.Font(8.25f),
            BackColor = Color.FromArgb(8, 12, 16),
            Location = new Point(24, 27)
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.FromArgb(8, 12, 16),
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        var save = new FlatButton
        {
            Text = "Save settings",
            Width = 140,
            Height = 38,
            Margin = new Padding(8, 0, 0, 0),
            NormalBackColor = Theme.Accent,
            HoverBackColor = Color.FromArgb(94, 238, 178),
            PressedBackColor = Color.FromArgb(43, 197, 133),
            ForeColor = Color.FromArgb(7, 24, 17),
            BorderColor = Color.Transparent,
            Font = Theme.Font(9.25f, FontStyle.Bold),
            Radius = 9
        };
        save.Click += (_, _) => SaveAndClose();

        var cancel = new FlatButton
        {
            Text = "Cancel",
            Width = 100,
            Height = 38,
            Margin = new Padding(0),
            NormalBackColor = Theme.SurfaceAlt,
            HoverBackColor = Theme.SurfaceHover,
            PressedBackColor = Theme.Border,
            ForeColor = Theme.Text,
            BorderColor = Theme.Border,
            Font = Theme.Font(9.25f, FontStyle.Bold),
            Radius = 9
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        panel.Controls.Add(note);
        panel.Controls.Add(buttons);
        CancelButton = cancel;
        return panel;
    }

    private void SaveAndClose()
    {
        var executable = _exeBox.Text.Trim();
        if (executable.Length > 0 && !File.Exists(executable))
        {
            MessageBox.Show(this,
                "The OpenCode executable path does not exist:\n" + executable +
                "\n\nLeave it empty to auto-detect, or choose the correct file.",
                "OpenCode Warp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _exeBox.Focus();
            return;
        }

        var folder = _folderBox.Text.Trim();
        if (folder.Length == 0)
            folder = AppPaths.DefaultWorkspace;

        var errors = new List<string>();
        int ReadNumber(TextBox box, string name, int minimum, int maximum)
        {
            if (!int.TryParse(box.Text.Trim(), out var value) || value < minimum || value > maximum)
            {
                errors.Add($"{name}: enter a whole number from {minimum} to {maximum}.");
                return minimum;
            }
            return value;
        }

        var relayPort = ReadNumber(_relayPort, "Local relay port", 1024, 65535);
        var warpPort = ReadNumber(_warpPort, "Cloudflare WARP proxy port", 1, 65535);
        var attempts = ReadNumber(_attempts, "Rotation attempts", 1, 10);
        var minSeconds = ReadNumber(_minSeconds, "Minimum seconds between rotations", 20, 600);
        var waitSeconds = ReadNumber(_waitSeconds, "Seconds to wait for a new WARP IP", 5, 180);
        var healthSeconds = ReadNumber(_healthSeconds, "Health-check interval", 5, 300);
        var autoRotate = ReadNumber(_autoRotateMinutes, "Auto-rotate minutes", 0, 720);

        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                "Please fix these values:\n\n" + string.Join("\n", errors),
                "OpenCode Warp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _draft.WarpScope = _aiOnlyRadio.Checked ? WarpScope.AiOnly : WarpScope.All;
        _draft.ExtraAiHosts = _extraAiBox.Text.Trim();
        _draft.DirectHosts = _directBox.Text.Trim();
        _draft.OpenCodeExe = executable.Length == 0 ? null : executable;
        _draft.ProjectFolder = folder;
        _draft.OpenCodeExtraArgs = _argsBox.Text.Trim();
        _draft.AutoLaunchOpenCode = _autoLaunch.Checked;
        _draft.RotateOnRateLimit = _rateRotate.Checked;
        _draft.RotateOnUsageLimit = _usageRotate.Checked;
        _draft.ProbeOnRotation = _probeRotate.Checked;
        _draft.AutoRotateMinutes = autoRotate;
        _draft.RelayPort = relayPort;
        _draft.WarpProxyPort = warpPort;
        _draft.RotationAttempts = attempts;
        _draft.MinSecondsBetweenRotations = minSeconds;
        _draft.WarpWaitSeconds = waitSeconds;
        _draft.HealthCheckSeconds = healthSeconds;
        _draft.StartWithWindows = _startWithWindows.Checked;
        _draft.StartMinimized = _startMinimized.Checked;
        _draft.MinimizeToTray = _minimizeToTray.Checked;
        _draft.LogDirectConnections = _logDirect.Checked;
        _draft.LogLevel = _logLevelRadios
            .FirstOrDefault(pair => pair.Value.Checked).Key ?? "info";

        DialogResult = DialogResult.OK;
        Close();
    }

    private void BrowseExecutable()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select the OpenCode CLI",
            Filter = "OpenCode CLI|opencode.exe;opencode.cmd;opencode.bat|Executables|*.exe;*.cmd;*.bat|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (Directory.Exists(Path.GetDirectoryName(_exeBox.Text)))
            dialog.InitialDirectory = Path.GetDirectoryName(_exeBox.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _exeBox.Text = dialog.FileName;
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the folder OpenCode should open",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_folderBox.Text) ? _folderBox.Text : AppPaths.DefaultWorkspace
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _folderBox.Text = dialog.SelectedPath;
    }

    private static TableLayoutPanel NewBody()
    {
        var body = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 0,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 12, 0, 0),
            Padding = new Padding(0)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        return body;
    }

    private static CardPanel Section(string title, string subtitle, TableLayoutPanel body)
    {
        var titleLabel = new Label
        {
            Text = title,
            Font = Theme.Font(12.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoSize = true,
            BackColor = Theme.Surface,
            Margin = new Padding(0)
        };
        var subtitleLabel = new Label
        {
            Text = subtitle,
            Font = Theme.Font(8.75f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            MaximumSize = new Size(690, 0),
            BackColor = Theme.Surface,
            Margin = new Padding(0, 3, 0, 0)
        };

        var inner = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.Controls.Add(titleLabel, 0, 0);
        inner.Controls.Add(subtitleLabel, 0, 1);
        inner.Controls.Add(body, 0, 2);

        var card = new CardPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20, 16, 20, 18),
            Margin = new Padding(0, 0, 0, 12)
        };
        card.Controls.Add(inner);
        return card;
    }

    private static void AddFull(TableLayoutPanel table, Control control, int topMargin = 10)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(0, topMargin, 0, 0);
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private static void AddField(TableLayoutPanel table, string caption, Control input, int topMargin = 12)
    {
        AddFull(table, Labeled(caption, input), topMargin);
    }

    private static void AddPair(TableLayoutPanel table, Control left, Control right, int topMargin = 12)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.Margin = new Padding(0, topMargin, 8, 0);
        right.Margin = new Padding(8, topMargin, 0, 0);
        left.Dock = DockStyle.Fill;
        right.Dock = DockStyle.Fill;
        table.Controls.Add(left, 0, row);
        table.Controls.Add(right, 1, row);
    }

    private static TableLayoutPanel Labeled(string caption, Control input)
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = Theme.Font(8.25f, FontStyle.Bold),
            BackColor = Theme.Surface,
            Margin = new Padding(0, 0, 0, 4)
        };
        input.Margin = new Padding(0);
        if (input is TextBox or TableLayoutPanel)
            input.Dock = DockStyle.Top;
        else if (input is NumericUpDown or ComboBox)
        {
            input.Dock = DockStyle.Left;
            input.Width = 180;
        }

        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(input, 0, 1);
        return panel;
    }

    private static TableLayoutPanel PathField(TextBox box, Action browse)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(0, 0, 8, 0);

        var button = new FlatButton
        {
            Text = "Browse",
            Dock = DockStyle.Fill,
            Height = 32,
            Margin = new Padding(0, 1, 0, 0),
            NormalBackColor = Theme.SurfaceAlt,
            HoverBackColor = Theme.SurfaceHover,
            PressedBackColor = Theme.Border,
            ForeColor = Theme.Text,
            BorderColor = Theme.Border,
            Font = Theme.Font(8.75f, FontStyle.Bold),
            Radius = 8
        };
        button.Click += (_, _) => browse();

        table.Controls.Add(box, 0, 0);
        table.Controls.Add(button, 1, 0);
        return table;
    }

    private static TextBox TextInput(string value, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = value ?? "",
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Font(9.5f),
            Margin = new Padding(0),
            AcceptsReturn = multiline,
            Multiline = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None
        };
        if (multiline)
            box.Height = 58;
        return box;
    }

    private static TextBox NumberInput(int value)
    {
        var box = TextInput(value.ToString());
        box.TextAlign = HorizontalAlignment.Left;
        box.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                e.Handled = true;
        };
        return box;
    }

    private static CheckBox Check(string text, bool value)
    {
        return new DarkCheckBox
        {
            Text = text,
            Checked = value,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface
        };
    }

    private static RadioButton Radio(string text, bool value)
    {
        return new DarkRadioButton
        {
            Text = text,
            Checked = value,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface
        };
    }

    private static Label Hint(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(690, 0),
            ForeColor = Theme.Muted,
            BackColor = Theme.Surface,
            Font = Theme.Font(8.5f),
            Margin = new Padding(0, 4, 0, 0)
        };
    }
}
