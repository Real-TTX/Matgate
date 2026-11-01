using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Matgate.Agent.Ui;

// The window chrome of the agent's windows, after MatMon's (Matmon.Agent/Tray/BrandChrome.cs): a header
// with the mark beside the name, a white body that grows with its content, a grey bar with the version
// on the left and the buttons on the right, headings, paragraphs and tinted notices.
internal static class Brand
{
    public static readonly Color Ink = Color.FromArgb(0x0F, 0x1B, 0x2D);
    public static readonly Color HeadingColor = Color.FromArgb(0x1B, 0x26, 0x38);
    public static readonly Color TextColor = Color.FromArgb(0x3A, 0x45, 0x55);
    public static readonly Color Muted = Color.FromArgb(0x5B, 0x67, 0x78);
    public static readonly Color FooterBack = Color.FromArgb(0xF4, 0xF6, 0xF9);
    public static readonly Color Line = Color.FromArgb(0xE3, 0xE8, 0xEF);
    public static readonly Color Accent = Color.FromArgb(0x17, 0x6B, 0x5B);

    // The tray's states.
    public static readonly Color Ok = Color.FromArgb(0x3F, 0xC4, 0x7A);
    public static readonly Color Warning = Color.FromArgb(0xF2, 0xB1, 0x3C);
    public static readonly Color Error = Color.FromArgb(0xE5, 0x4B, 0x5E);
    public static readonly Color Neutral = Color.FromArgb(0x9A, 0xA4, 0xB1);

    public const int ContentWidth = 560;

    public static Font BodyFont() => new("Segoe UI", 9.5f);

    public static string Version => typeof(Brand).Assembly.GetName().Version?.ToString(3) ?? "0";

    // A button for the bar: a fixed size and one margin rule, so all of them sit on one line.
    public static Button FooterButton(string text, int width = 112, int gap = 8) => new()
    {
        Text = text,
        Size = new Size(width, 32),
        Margin = new Padding(gap, 0, 0, 0),
    };

    // The bar: the note (version) on the left, the buttons on the right - the first one rightmost.
    public static Panel Footer(string note, params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
        };
        row.Controls.AddRange(buttons);
        var label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Muted, Text = note };
        var footer = new Panel { Dock = DockStyle.Bottom, Padding = new Padding(20, 13, 16, 13), Height = 58, BackColor = FooterBack };
        footer.Controls.Add(label);
        footer.Controls.Add(row);
        return footer;
    }

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 14f),
        ForeColor = HeadingColor,
        Margin = new Padding(0, 0, 0, 12),
    };

    public static Label Paragraph(string text, Color? color = null, int width = ContentWidth) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        ForeColor = color ?? TextColor,
        Margin = new Padding(0, 0, 0, 10),
    };

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = HeadingColor,
        Margin = new Padding(0, 6, 0, 3),
    };

    public static Label Hint(string text, int indent = 0) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth - indent, 0),
        ForeColor = Muted,
        Margin = new Padding(indent, 2, 0, 10),
    };
}

// The Matgate mark - the rounded square with the gate, as /favicon.svg draws it - and the tray icons made
// from it at start-up instead of shipped as files.
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

    // The mark, with a state dot in the corner when there is a state (the tray), without for a window.
    public static Icon Create(Color? state, int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);
            var markSize = state is null ? size : size * 0.84f;
            Draw(graphics, new RectangleF(0, 0, markSize, markSize));
            if (state is { } color)
            {
                // Outlined, so it reads on light and dark taskbars alike.
                var dot = size * 0.42f;
                using var fill = new SolidBrush(color);
                using var outline = new Pen(Color.White, size / 16f);
                graphics.FillEllipse(fill, size - dot - 1, size - dot - 1, dot, dot);
                graphics.DrawEllipse(outline, size - dot - 1, size - dot - 1, dot, dot);
            }
        }

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

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

// The top band: the mark beside "Matgate Agent" and a word (Setup, Update, Deinstallation, Info).
internal sealed class BrandHeader : Control
{
    private string _subtitle;

    public BrandHeader(string subtitle)
    {
        _subtitle = subtitle;
        DoubleBuffered = true;
        BackColor = Color.White;
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
        var inset = 24 * scale;
        var markSize = 40f * scale;
        Mark.Draw(graphics, new RectangleF(inset, (Height - markSize) / 2f, markSize, markSize));

        using var titleFont = new Font("Segoe UI Semibold", 20f);
        using var subtitleFont = new Font("Segoe UI", 12f);
        using var title = new SolidBrush(Brand.Ink);
        using var soft = new SolidBrush(Brand.Muted);
        var left = inset + markSize + 14 * scale;
        const string name = "Matgate Agent";
        var titleSize = graphics.MeasureString(name, titleFont);
        var subtitleSize = graphics.MeasureString(_subtitle, subtitleFont);
        graphics.DrawString(name, titleFont, title, left, (Height - titleSize.Height) / 2f);
        graphics.DrawString(_subtitle, subtitleFont, soft, left + titleSize.Width + 6 * scale, (Height - subtitleSize.Height) / 2f + 2 * scale);

        using var line = new Pen(Brand.Line);
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

// A notice: a tinted box with a bar in the tone's colour on the left.
internal sealed class BrandNotice : Label
{
    private NoticeTone _tone;

    public BrandNotice(string text, NoticeTone tone, int width = Brand.ContentWidth)
    {
        Text = text;
        AutoSize = true;
        MaximumSize = new Size(width, 0);
        MinimumSize = new Size(width, 0);
        Padding = new Padding(14, 9, 12, 9);
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
            (BackColor, ForeColor) = value switch
            {
                NoticeTone.Success => (Color.FromArgb(0xE9, 0xF8, 0xF0), Color.FromArgb(0x1C, 0x6B, 0x43)),
                NoticeTone.Warning => (Color.FromArgb(0xFF, 0xF6, 0xE5), Color.FromArgb(0x7A, 0x4E, 0x00)),
                NoticeTone.Error => (Color.FromArgb(0xFD, 0xEC, 0xEE), Color.FromArgb(0x9B, 0x1C, 0x2E)),
                _ => (Color.FromArgb(0xEE, 0xF4, 0xFB), Color.FromArgb(0x1F, 0x3B, 0x63)),
            };
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var bar = _tone switch
        {
            NoticeTone.Success => Brand.Ok,
            NoticeTone.Warning => Brand.Warning,
            NoticeTone.Error => Brand.Error,
            _ => Brand.Accent,
        };
        using var brush = new SolidBrush(bar);
        e.Graphics.FillRectangle(brush, 0, 0, Math.Max(3, (int)Math.Round(4 * DeviceDpi / 96f)), Height);
    }
}

// A value to hand over to Matgate: the label, the value in a read-only field, and a copy button.
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
        Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Brand.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, 0);
        var box = new TextBox
        {
            Text = value,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Brand.FooterBack,
            Width = 318,
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
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(value);
            copy.Text = "Kopiert";
        };
        Controls.Add(box, 1, 0);
        Controls.Add(copy, 2, 0);
    }
}
