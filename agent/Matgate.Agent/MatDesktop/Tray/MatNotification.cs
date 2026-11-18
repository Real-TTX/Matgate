// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MatDesktop;

/// <summary>
/// A notification of the program's own (TRAY-04; Avalonia has none of the system on every platform): 360 px wide, at the bottom right above the task bar (on macOS at the top right under
/// the menu bar), for 7 seconds; a click opens the program. It never takes the focus. Only for what the user did not cause (the connection was lost or came back, an update is ready, an
/// approval came through), never for an action the user just took, and never with a secret.
/// </summary>
public sealed class MatNotification : MatWindow
{
    /// <summary>The width of a notification.</summary>
    public const double NotificationWidth = 360;

    /// <summary>How long a notification stays.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(7);

    private static readonly List<MatNotification> Open = [];

    /// <summary>A notification with a title and a sentence; <paramref name="isWarning"/> draws the icon of a warning instead of the icon of news. <paramref name="onClick"/> opens the program.</summary>
    public MatNotification(string title, string text, bool isWarning, Action? onClick)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Width = NotificationWidth;
        SizeToContent = SizeToContent.Height;
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        Title = title;
        this.Use(BackgroundProperty, "Mat.Surface");

        var icon = new MatIcon { Icon = isWarning ? "alert" : "info", Size = 20, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top }
            .Use(MatIcon.ForegroundProperty, isWarning ? "Mat.WarningText" : "Mat.AccentText");
        DockPanel.SetDock(icon, Dock.Left);
        TitleText = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        BodyText = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 120 }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        Content = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14),
            Child = new DockPanel { Children = { icon, new StackPanel { Children = { TitleText, BodyText } } } },
        }.Use(Border.BorderBrushProperty, "Mat.BorderStrong");

        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                Close();
                onClick?.Invoke();
            }
        };
        Opened += (_, _) => PlaceInCorner();
        Closed += (_, _) =>
        {
            Open.Remove(this);
            foreach (var other in Open)
            {
                other.PlaceInCorner();
            }
        };
    }

    /// <summary>The title.</summary>
    public TextBlock TitleText { get; }

    /// <summary>The sentence.</summary>
    public TextBlock BodyText { get; }

    /// <summary>Shows a notification for <see cref="Duration"/>; several stand one above the other.</summary>
    public static MatNotification Show(string title, string text, bool isWarning = false, Action? onClick = null)
    {
        var toast = new MatNotification(title, text, isWarning, onClick);
        Open.Add(toast);
        toast.Show();
        DispatcherTimer.RunOnce(toast.Close, Duration);
        return toast;
    }

    private void PlaceInCorner()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var width = (int)(Bounds.Width * scaling);
        var height = (int)(Bounds.Height * scaling);
        var offset = Open.TakeWhile(toast => toast != this).Sum(toast => (int)((toast.Bounds.Height + 10) * scaling));
        var margin = (int)(12 * scaling);
        Position = OperatingSystem.IsMacOS()
            ? new PixelPoint(area.Right - width - margin, area.Y + margin + offset)
            : new PixelPoint(area.Right - width - margin, area.Bottom - height - margin - offset);
    }
}
