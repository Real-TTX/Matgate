using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MatDesktop;

namespace Matgate.Agent.Ui;

// The agent's windows, on every system: Avalonia in the look of MatDesktop (MatDesktop/, the desktop
// toolkit of MatDesign - the same window, setup and tray as the other programs of the family). The agent
// itself (the server Matgate connects to) never starts any of this: it has no screen of its own.
internal sealed class AgentApp : Application
{
    // Matgate's accent, as matui.apps.css has it.
    public const string Accent = "#0a7d57";

    // What the program does once the application runs: show the setup, or put the icon into the notification area.
    public static Action<AgentApp, IClassicDesktopStyleApplicationLifetime>? Started { get; set; }

    public override void Initialize()
    {
        // The agent speaks German in its windows, like every text of it so far.
        MatText.Language = "de";
        MatTheme.Install(this, new MatThemeOptions(Accent));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        MatWindow.DefaultIcon = AgentMark.Icon();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Started?.Invoke(this, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Runs the application until it is shut down; `start` shows what this start is for.
    public static int Run(Action<AgentApp, IClassicDesktopStyleApplicationLifetime> start)
    {
        Started = start;
        return Build().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder Build() => AppBuilder.Configure<AgentApp>()
        .UsePlatformDetect()
        // An icon in the menu bar and a setup window: no place in the Dock.
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .LogToTrace();

    // Whether this start can show a window at all: Windows and macOS always, Linux only with a desktop.
    public static bool HasDisplay => !OperatingSystem.IsLinux()
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}

// Matgate's mark: the rounded square in the gradient of the logo with the white gate (favicon.svg, viewBox 64,
// corner 14, path M17 48 V16 H47 V48 H40 V23 H24 V48 Z). Drawn, not loaded - one program file, no pictures beside it.
internal static class AgentMark
{
    private static readonly Color From = Color.FromRgb(0x17, 0x6B, 0x5B);
    private static readonly Color To = Color.FromRgb(0x2B, 0x58, 0x76);

    public static RenderTargetBitmap Picture(int size = 256)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using var context = bitmap.CreateDrawingContext();
        var scale = size / 64.0;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(From, 0), new GradientStop(To, 1) },
        };
        context.DrawRectangle(gradient, null, new RoundedRect(new Rect(0, 0, size, size), 14 * scale));
        var gate = new StreamGeometry();
        using (var path = gate.Open())
        {
            Point P(double x, double y) => new(x * scale, y * scale);
            path.BeginFigure(P(17, 48), isFilled: true);
            foreach (var (x, y) in new[] { (17, 16), (47, 16), (47, 48), (40, 48), (40, 23), (24, 23), (24, 48) })
            {
                path.LineTo(P(x, y));
            }

            path.EndFigure(isClosed: true);
        }

        context.DrawGeometry(Brushes.White, null, gate);
        return bitmap;
    }

    public static WindowIcon Icon()
    {
        var stream = new MemoryStream();
        Picture(256).Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}

// A note in a tinted box with an icon (what a step of the setup wants said, a warning, a result) - MatUi's alert.
internal sealed class Notice : Border
{
    public Notice(string text, string state = "Info")
    {
        var icon = new MatIcon
        {
            Icon = state switch { "Success" => "check-circle", "Warning" => "alert", "Danger" => "x-circle", _ => "info" },
            Size = 18,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0),
        }.Use(MatIcon.ForegroundProperty, $"Mat.{state}Text");
        var body = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap }.Use(SelectableTextBlock.ForegroundProperty, "Mat.Text");
        var dock = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(body);
        Child = dock;
        Padding = new Thickness(MatTokens.Size.Space4, MatTokens.Size.Space3);
        Margin = new Thickness(0, 0, 0, 12);
        this.Use(CornerRadiusProperty, "Mat.Radius");
        this.Use(BackgroundProperty, $"Mat.{state}Bg");
        Text = text;
    }

    public string Text { get; }
}
