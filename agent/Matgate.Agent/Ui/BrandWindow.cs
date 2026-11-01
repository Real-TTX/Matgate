using System.Drawing.Drawing2D;

namespace Matgate.Agent.Ui;

// The frame of the agent's small windows (Info, connection details), after MatMon's BrandWindow: the
// header, a white body as tall as its content, the grey bar with the version on the left.
internal abstract class BrandWindow : Form
{
    public const int ClientWidth = 616;

    private readonly Panel _body = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 12) };
    private readonly BrandHeader _header;
    private Panel? _footer;

    protected readonly FlowLayoutPanel Content = new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
    };

    protected BrandWindow(string windowTitle, string subtitle)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = windowTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = Brand.BodyFont();
        BackColor = Color.White;
        ClientSize = new Size(ClientWidth, 360);
        Icon = Mark.Create(null);
        _header = new BrandHeader(subtitle);
        _body.Controls.Add(Content);
    }

    protected void Complete(params Button[] buttons)
    {
        _footer = Brand.Footer($"Version {Brand.Version}", buttons);
        Controls.Add(_body);
        Controls.Add(_footer);
        Controls.Add(_header);
        ResumeLayout(false);
        PerformLayout();
        FitHeight();
    }

    // As tall as what it shows - no scrollbar, no empty half.
    public void FitHeight()
    {
        if (_footer is null)
        {
            return;
        }

        Content.PerformLayout();
        var content = Content.GetPreferredSize(new Size(Content.Width, 0)).Height;
        ClientSize = new Size(ClientSize.Width, _header.Height + _body.Padding.Vertical + content + _footer.Height);
    }
}

// The state the tray icon says: a dot in its colour, a word, a line of detail, a rule under it.
internal sealed class StateLine : Control
{
    private Color _color = Brand.Neutral;
    private string _title = "";
    private string _detail = "";

    public StateLine()
    {
        DoubleBuffered = true;
        Margin = new Padding(0, 0, 0, 8);
        Size = new Size(Brand.ContentWidth, 60);
    }

    public void Set(Color color, string title, string detail)
    {
        (_color, _title, _detail) = (color, title, detail);
        Height = Measure();
        Invalidate();
    }

    private float Factor => DeviceDpi / 96f;

    private int TextLeft => (int)(26 * Factor);

    private static Font TitleFont() => new("Segoe UI Semibold", 13f);

    private int Measure()
    {
        using var titleFont = TitleFont();
        var titleHeight = TextRenderer.MeasureText(_title, titleFont).Height;
        var detailHeight = TextRenderer.MeasureText(_detail, Font, new Size(Width - TextLeft, 0), TextFormatFlags.WordBreak).Height;
        return titleHeight + (int)(2 * Factor) + detailHeight + (int)(16 * Factor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var titleFont = TitleFont();
        var titleHeight = TextRenderer.MeasureText(_title, titleFont).Height;
        var dot = 12 * Factor;
        using var brush = new SolidBrush(_color);
        graphics.FillEllipse(brush, 2 * Factor, (titleHeight - dot) / 2f, dot, dot);
        TextRenderer.DrawText(graphics, _title, titleFont, new Point(TextLeft - (int)(3 * Factor), 0), Brand.HeadingColor, TextFormatFlags.NoPadding);
        var detailTop = titleHeight + (int)(2 * Factor);
        TextRenderer.DrawText(graphics, _detail, Font, new Rectangle(TextLeft - (int)(3 * Factor), detailTop, Width - TextLeft, Height - detailTop),
            Brand.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        using var line = new Pen(Brand.Line);
        graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}
