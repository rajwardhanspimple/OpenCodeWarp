using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace OpenCodeWarp.App;

internal class CardPanel : Panel
{
    public int Radius { get; set; } = 14;

    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        Padding = new Padding(18, 14, 18, 14);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(rect, Radius);
        using var fill = new SolidBrush(Theme.Surface);
        using var border = new Pen(Theme.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }
}

internal sealed class FlatButton : Button
{
    private bool _hover;
    private bool _pressed;

    public Color NormalBackColor { get; set; } = Theme.SurfaceAlt;
    public Color HoverBackColor { get; set; } = Theme.SurfaceHover;
    public Color PressedBackColor { get; set; } = Theme.Border;
    public Color BorderColor { get; set; } = Theme.Border;
    public Color DisabledBackColor { get; set; } = Color.FromArgb(25, 32, 41);
    public int Radius { get; set; } = 8;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.SurfaceAlt;
        ForeColor = Theme.Text;
        Font = Theme.Font(9.25f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Size = new Size(110, 34);
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(rect, Radius);

        var background = !Enabled
            ? DisabledBackColor
            : _pressed ? PressedBackColor : _hover ? HoverBackColor : NormalBackColor;
        using var fill = new SolidBrush(background);
        e.Graphics.FillPath(fill, path);

        if (BorderColor.A > 0)
        {
            using var border = new Pen(BorderColor);
            e.Graphics.DrawPath(border, path);
        }

        var textColor = Enabled ? ForeColor : Theme.Muted;
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            rect,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class StatusPill : Control
{
    private string _pillText = "STARTING";
    private Color _pillColor = Theme.Muted;

    public StatusPill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        Height = 28;
        Width = 150;
        BackColor = Theme.Background;
        Font = Theme.Font(8.25f, FontStyle.Bold);
    }

    public void SetStatus(string text, Color color)
    {
        var normalized = text.ToUpperInvariant();
        if (_pillText == normalized && _pillColor == color)
            return;

        _pillText = normalized;
        _pillColor = color;
        Width = Math.Max(110, TextRenderer.MeasureText(_pillText, Font).Width + 42);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(rect, Height / 2);
        using var fill = new SolidBrush(Theme.WithAlpha(_pillColor, 28));
        using var border = new Pen(Theme.WithAlpha(_pillColor, 90));
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        using var dot = new SolidBrush(_pillColor);
        e.Graphics.FillEllipse(dot, 12, Height / 2 - 3, 6, 6);
        TextRenderer.DrawText(
            e.Graphics,
            _pillText,
            Font,
            new Rectangle(24, 0, Width - 30, Height),
            _pillColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
    }
}

internal sealed class StatusCard : CardPanel
{
    public Label Kicker { get; }
    public StatusPill Pill { get; }
    public Label Value { get; }
    public Label Detail { get; }

    public StatusCard(string kicker)
    {
        Margin = new Padding(0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Surface,
            Margin = new Padding(0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
            Height = 28
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Kicker = new Label
        {
            Text = kicker.ToUpperInvariant(),
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Font(8f, FontStyle.Bold),
            Margin = new Padding(0),
            BackColor = Theme.Surface
        };
        Pill = new StatusPill
        {
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0)
        };

        top.Controls.Add(Kicker, 0, 0);
        top.Controls.Add(Pill, 1, 0);

        Value = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.Text,
            Font = Theme.Font(18.5f, FontStyle.Bold),
            Margin = new Padding(0),
            AutoEllipsis = true,
            BackColor = Theme.Surface
        };

        Detail = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Theme.Muted,
            Font = Theme.Font(8.5f),
            Margin = new Padding(0),
            BackColor = Theme.Surface
        };

        layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(Value, 0, 1);
        layout.Controls.Add(Detail, 0, 2);
        Controls.Add(layout);

        Pill.SetStatus("WAIT", Theme.Muted);
    }
}

internal sealed class DarkCheckBox : CheckBox
{
    public DarkCheckBox()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Font(9.25f);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 10, 0, 0);
        TabStop = true;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var available = proposedSize.Width > 0 && proposedSize.Width < 10000
            ? Math.Max(120, proposedSize.Width - 27)
            : 700;
        var textSize = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(available, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return new Size(Math.Max(120, textSize.Width + 27), Math.Max(22, textSize.Height + 4));
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var top = Math.Max(0, (Height - 16) / 2);
        var box = new Rectangle(1, top, 16, 16);
        using var path = Theme.RoundedRect(box, 4);
        using var fill = new SolidBrush(Checked ? Theme.Accent : Theme.SurfaceAlt);
        using var border = new Pen(Checked ? Theme.Accent : Focused ? Theme.Muted : Theme.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        if (Checked)
        {
            using var check = new Pen(Color.FromArgb(7, 24, 17), 2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawLines(check, new[]
            {
                new Point(5, top + 8),
                new Point(8, top + 11),
                new Point(13, top + 5)
            });
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            new Rectangle(26, 0, Math.Max(0, Width - 26), Height),
            Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
}

internal sealed class DarkRadioButton : RadioButton
{
    public DarkRadioButton()
    {
        SetStyle(ControlStyles.UserPaint
                 | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Font(9.75f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 4, 0, 0);
        TabStop = true;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var available = proposedSize.Width > 0 && proposedSize.Width < 10000
            ? Math.Max(120, proposedSize.Width - 27)
            : 700;
        var textSize = TextRenderer.MeasureText(
            Text,
            Font,
            new Size(available, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return new Size(Math.Max(120, textSize.Width + 27), Math.Max(23, textSize.Height + 4));
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var top = Math.Max(0, (Height - 16) / 2);
        var box = new Rectangle(1, top, 16, 16);
        using var fill = new SolidBrush(Checked ? Theme.Accent : Theme.SurfaceAlt);
        using var border = new Pen(Checked ? Theme.Accent : Focused ? Theme.Muted : Theme.Border);
        e.Graphics.FillEllipse(fill, box);
        e.Graphics.DrawEllipse(border, box);

        if (Checked)
        {
            using var dot = new SolidBrush(Color.FromArgb(7, 24, 17));
            e.Graphics.FillEllipse(dot, 6, top + 5, 6, 6);
        }

        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            new Rectangle(26, 0, Math.Max(0, Width - 26), Height),
            Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Surface;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.Border;
    public override Color MenuItemSelected => Theme.SurfaceHover;
    public override Color MenuItemSelectedGradientBegin => Theme.SurfaceHover;
    public override Color MenuItemSelectedGradientEnd => Theme.SurfaceHover;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
    public override Color CheckBackground => Theme.AccentSoft;
    public override Color CheckSelectedBackground => Theme.AccentSoft;
    public override Color CheckPressedBackground => Theme.AccentSoft;
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item?.Enabled == false ? Theme.Muted : Theme.Text;
        base.OnRenderItemText(e);
    }
}

internal sealed class LogoPanel : Control
{
    public LogoPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        Size = new Size(46, 46);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var dark = new SolidBrush(Theme.SurfaceAlt);
        e.Graphics.FillEllipse(dark, 1, 1, Width - 2, Height - 2);

        using var accent = new Pen(Theme.Accent, 2.6f);
        e.Graphics.DrawArc(accent, 5, 5, Width - 10, Height - 10, -40, 285);

        using var font = Theme.Font(18, FontStyle.Bold);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        e.Graphics.DrawString("W", font, Brushes.White, new RectangleF(0, 1, Width, Height), format);
    }
}
