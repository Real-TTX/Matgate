using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Matgate.Agent.Setup;

namespace Matgate.Agent.Ui;

// The window chrome of the agent's windows, the one of the Mat family's WinForms trays and setups (AGENT-05): a header with the mark
// beside the name, a body that grows with its content, a footer with the version on the left and the buttons on the right, headings,
// paragraphs and notices. Every colour comes from Theme (the tokens), in the light or dark mode of the system.
internal static class Brand
{
    public const int ContentWidth = 544;

    public static Font BodyFont() => new("Segoe UI", 9.5f);

    // A button of the footer: one height, one margin rule, so all of them sit on one line.
    public static Button FooterButton(string text, int width = 112, ButtonKind kind = ButtonKind.Secondary)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(width, 32),
            Margin = new Padding(8, 0, 0, 0),
        };
        Style(button, kind);
        return button;
    }

    // The flat look of the toolkit: the primary action filled with the accent, the danger action in red, every other button the
    // surface with a line around it. Nothing lifts on hover; it only gets a step darker.
    public static void Style(Button button, ButtonKind kind)
    {
        var (back, fore, line, hover, down) = kind switch
        {
            ButtonKind.Primary => (Theme.Accent, Theme.OnAccent, Theme.Accent, Theme.AccentHover, Theme.AccentActive),
            ButtonKind.Danger => (Theme.Danger, Theme.OnDanger, Theme.Danger, ControlPaint.Dark(Theme.Danger, 0.05f), ControlPaint.Dark(Theme.Danger, 0.1f)),
            _ => (Theme.Surface, Theme.Text, Theme.LineStrong, Theme.Surface2, Theme.Surface3),
        };
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = back;
        button.ForeColor = fore;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = line;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = down;
    }

    // The footer: the version on the left; the buttons on the right in the order given, left to right (the primary action first,
    // then Back, then Cancel - SETUP-01; Info: Log folder, then Close).
    public static Panel Footer(string note, params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            BackColor = Theme.FooterBack,
        };
        row.Controls.AddRange(buttons);
        var label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Theme.Muted, Text = note };
        var footer = new Panel { Dock = DockStyle.Bottom, Padding = new Padding(28, 13, 20, 13), Height = 58, BackColor = Theme.FooterBack };
        footer.Paint += (_, e) =>
        {
            using var line = new Pen(Theme.FooterLine);
            e.Graphics.DrawLine(line, 0, 0, footer.Width, 0);
        };
        footer.Controls.Add(label);
        footer.Controls.Add(row);
        return footer;
    }

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 14f),
        ForeColor = Theme.Text,
        Margin = new Padding(0, 0, 0, 8),
    };

    public static Label Paragraph(string text, int width = ContentWidth) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = Theme.Text,
        Margin = new Padding(0, 0, 0, 10),
    };

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Text,
        Font = new Font("Segoe UI Semibold", 9.5f),
        Margin = new Padding(0, 6, 0, 3),
    };

    public static Label Hint(string text, int indent = 0) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth - indent, 0),
        ForeColor = Theme.Muted,
        Margin = new Padding(indent, 2, 0, 8),
    };

    // A field, a list or a box of the system in the colours of the mode (the colour mode of WinForms draws its frame).
    public static T Field<T>(T control)
        where T : Control
    {
        control.BackColor = Theme.Surface;
        control.ForeColor = Theme.Text;
        return control;
    }

    // The shield of Windows: what it is on needs administrator rights (TRAY-01, the actions of a service). One picture for the life of
    // the process - the menu is built again every time it opens.
    private static Image? _shield;

    public static Image Shield() => _shield ??= SystemIcons.GetStockIcon(StockIconId.Shield, StockIconOptions.SmallIcon).ToBitmap();
}

internal enum ButtonKind
{
    Secondary,
    Primary,
    Danger,
}

// The Matgate mark - the rounded square with the gate, as Matgate's /favicon.svg draws it - and the tray icons made from it at start-up
// instead of shipped as files. The two colours of the square are the logo's own (Matgate's favicon), not colours of a window.
internal static class Mark
{
    private static readonly Color From = Color.FromArgb(0x17, 0x6B, 0x5B);
    private static readonly Color To = Color.FromArgb(0x2B, 0x58, 0x76);

    // favicon.svg: viewBox 0 0 64 64, rect rx 14, path M17 48 V16 H47 V48 H40 V23 H24 V48 Z.
    public static void Draw(Graphics graphics, RectangleF bounds)
    {
        var scale = bounds.Width / 64f;
        using var square = RoundedRectangle(bounds, 14 * scale);
        using var brush = new LinearGradientBrush(bounds, From, To, LinearGradientMode.ForwardDiagonal);
        graphics.FillPath(brush, square);

        PointF P(float x, float y) => new(bounds.X + x * scale, bounds.Y + y * scale);
        using var gate = new GraphicsPath();
        gate.AddPolygon([P(17, 48), P(17, 16), P(47, 16), P(47, 48), P(40, 48), P(40, 23), P(24, 23), P(24, 48)]);
        graphics.FillPath(Brushes.White, gate);
    }

    // The mark as a bitmap: with the dot of a state at the bottom right for the tray (TRAY-02), without for a window. The dot sits in a
    // cut-out of the mark - a ring of transparency around it, so that it reads on a light and on a dark task bar - like MatDesktop's
    // tray icons: centre at three quarters of the size, radius 0.22, gap 0.08.
    public static Bitmap Picture(AgentState? state, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        Draw(graphics, new RectangleF(0, 0, size, size));
        if (state is { } s)
        {
            var centre = size * 0.75f;
            var radius = size * 0.22f;
            var gap = size * 0.08f;
            using (var cut = new SolidBrush(Color.Transparent))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.FillEllipse(cut, centre - radius - gap, centre - radius - gap, (radius + gap) * 2, (radius + gap) * 2);
                graphics.CompositingMode = CompositingMode.SourceOver;
            }

            using var dot = new SolidBrush(Theme.TrayDot(s));
            graphics.FillEllipse(dot, centre - radius, centre - radius, radius * 2, radius * 2);
        }

        return bitmap;
    }

    // The same as an icon (the tray and the windows).
    public static Icon Create(AgentState? state, int size = 32)
    {
        using var bitmap = Picture(state, size);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius) => RoundedRectangle(bounds, radius);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

// The top band: the mark beside "Matgate Agent" and a word for the mode or the window (Einrichtung, Update, Deinstallation, Info,
// Verbindungsdaten).
internal sealed class BrandHeader : Control
{
    private string _subtitle;

    public BrandHeader(string subtitle)
    {
        _subtitle = subtitle;
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Dock = DockStyle.Top;
        Height = 84;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            _subtitle = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var scale = DeviceDpi / 96f;
        var inset = 28 * scale;
        var markSize = 44f * scale;
        Mark.Draw(graphics, new RectangleF(inset, (Height - markSize) / 2f, markSize, markSize));

        using var titleFont = new Font("Segoe UI Semibold", 20f);
        using var subtitleFont = new Font("Segoe UI", 12f);
        using var title = new SolidBrush(Theme.Text);
        using var soft = new SolidBrush(Theme.Muted);
        var left = inset + markSize + 14 * scale;
        var titleSize = graphics.MeasureString(Installer.ProductName, titleFont);
        var subtitleSize = graphics.MeasureString(_subtitle, subtitleFont);
        graphics.DrawString(Installer.ProductName, titleFont, title, left, (Height - titleSize.Height) / 2f);
        graphics.DrawString(_subtitle, subtitleFont, soft, left + titleSize.Width + 6 * scale, (Height - subtitleSize.Height) / 2f + 2 * scale);

        using var line = new Pen(Theme.Line);
        graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}

internal enum NoticeTone
{
    Info,
    Success,
    Warning,
    Error,
}

// A notice, as MatUi's mat-alert: the tint of its tone with rounded corners and the text in the tone's colour - no bar at one side.
internal sealed class BrandNotice : Label
{
    private NoticeTone _tone;
    private Color _tint;

    public BrandNotice(string text, NoticeTone tone, int width = Brand.ContentWidth)
    {
        Text = text;
        AutoSize = true;
        MaximumSize = new Size(width, 0);
        MinimumSize = new Size(width, 0);
        Padding = new Padding(14, 10, 14, 10);
        Margin = new Padding(0, 4, 0, 10);
        Tone = tone;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public NoticeTone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            (_tint, ForeColor) = Theme.Tone(value);
            Invalidate();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (var surface = new SolidBrush(Parent?.BackColor ?? Theme.Surface))
        {
            e.Graphics.FillRectangle(surface, ClientRectangle);
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var radius = 8 * DeviceDpi / 96f;
        using var shape = Mark.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), radius);
        using var tint = new SolidBrush(_tint);
        e.Graphics.FillPath(tint, shape);
    }
}

// The bar of the progress step: a track and a piece in the accent that runs along it while the work has no measure (MatUi's progress
// bar; the bar of the system has no track in the dark mode).
internal sealed class MarqueeBar : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private float _phase = 0.1f;

    public MarqueeBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        AccessibleRole = AccessibleRole.ProgressBar;
        _timer.Tick += (_, _) =>
        {
            _phase = (_phase + 0.012f) % 1.3f;
            Invalidate();
        };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _timer.Enabled = Visible;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        var radius = Height / 2f;
        using (var track = Mark.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), radius))
        using (var trackBrush = new SolidBrush(Theme.Surface3))
        {
            graphics.FillPath(trackBrush, track);
        }

        var piece = Width * 0.3f;
        var left = Math.Max(0, (_phase - 0.3f) * Width);
        var right = Math.Min(Width - 1, _phase * Width);
        if (right - left > Height)
        {
            using var chunk = Mark.Rounded(new RectangleF(left, 0, Math.Min(piece, right - left), Height - 1), radius);
            using var accent = new SolidBrush(Theme.Accent);
            graphics.FillPath(accent, chunk);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}

// A value to hand over to Matgate: the label, the value in a read-only field, and a Copy button (AGENT-02).
internal sealed class CopyField : TableLayoutPanel
{
    public CopyField(string label, string value, bool monospace = false, bool wrap = false)
    {
        ColumnCount = 3;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 2, 0, 2);
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        var box = new TextBox
        {
            Text = value,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface2,
            ForeColor = Theme.Text,
            Width = 300,
            Font = monospace ? new Font("Consolas", 9.5f) : Brand.BodyFont(),
            Margin = new Padding(0, 2, 6, 2),
        };
        if (wrap)
        {
            box.Multiline = true;
            box.WordWrap = true;
            box.Text = value.Length > 32 ? value[..32] + Environment.NewLine + value[32..] : value;
            box.Height = (int)(box.Font.GetHeight() * 2) + 8;
        }

        var copy = new Button { Text = "Kopieren", Size = new Size(84, 28), Margin = new Padding(0, 1, 0, 1) };
        Brand.Style(copy, ButtonKind.Secondary);
        copy.AccessibleName = label + " kopieren";
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(value);
            copy.Text = "Kopiert";
        };
        Controls.Add(box, 1, 0);
        Controls.Add(copy, 2, 0);
    }
}
