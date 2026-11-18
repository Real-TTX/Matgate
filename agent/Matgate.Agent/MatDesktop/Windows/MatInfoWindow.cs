// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MatDesktop;

/// <summary>What the Info window of a tray shows (TRAY-05).</summary>
/// <param name="AppName">The name of the program (<c>Example Agent</c>).</param>
/// <param name="Version">The version number.</param>
/// <param name="Channel">The channel (release, nightly, local).</param>
/// <param name="State">The state, as a dot and a word.</param>
/// <param name="Sentence">One sentence about the state (<c>Connected to monitor.example.com since 09:12.</c>).</param>
/// <param name="Facts">The facts as rows: machine, server or port, mode, started, version, data folder, what else matters. Never a secret (AGENT-03).</param>
public sealed record MatInfo(string AppName, string Version, string Channel, MatState State, string Sentence, IReadOnlyList<MatFact> Facts)
{
    /// <summary>The logo of the header, 44 px.</summary>
    public IImage? Logo { get; init; }

    /// <summary>Opens the page of the program, or null when it has none (then there is no Open button).</summary>
    public Action? Open { get; init; }

    /// <summary>Opens the folder of the logs.</summary>
    public Action? OpenLogFolder { get; init; }
}

/// <summary>
/// The one window of a tray (TRAY-05), about 630 × 500: a header with the logo, the name and "Info"; the state as a dot, a word and one sentence; the facts as rows; a footer with
/// <c>Version 1.4.2 · release</c> at the left and Open (if there is a page), Log folder and Close at the right. Further windows (connection details, set up) are reached from the menu or a
/// button of Info that the program adds to <see cref="Body"/>.
/// </summary>
public sealed class MatInfoWindow : MatWindow
{
    /// <summary>The width of the window.</summary>
    public const double InfoWidth = 630;

    /// <summary>The height of the window.</summary>
    public const double InfoHeight = 500;

    /// <summary>An Info window for <paramref name="info"/>.</summary>
    public MatInfoWindow(MatInfo info)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        Width = InfoWidth;
        Height = InfoHeight;
        MinWidth = 480;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = $"{info.AppName} – {MatText.Get("info.title")}";

        var header = MatDialogChrome.Header(info.Logo, info.AppName, MatText.Get("info.title"));

        StateDot = new MatStateDot { State = info.State, DotSize = 12 };
        StateDot.Children.OfType<TextBlock>().First().FontSize = MatTokens.Type.FsLg;
        var sentence = new TextBlock { Text = info.Sentence, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18) }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");
        Facts = new MatFacts { Facts = info.Facts };
        var card = new Border { Child = Facts, Padding = new Thickness(16, 2) }.Themed("MatCard");
        Body = new StackPanel { Margin = new Thickness(28, 22, 28, 20), Children = { StateDot, sentence, card } };

        OpenButton = new Button { Content = MatText.Get("info.open"), IsVisible = info.Open is not null }.Themed("MatButton.Primary");
        OpenButton.Click += (_, _) => info.Open?.Invoke();
        LogFolderButton = new Button { Content = MatText.Get("info.logFolder"), IsVisible = info.OpenLogFolder is not null };
        LogFolderButton.Click += (_, _) => info.OpenLogFolder?.Invoke();
        CloseButton = new Button { Content = MatText.Get("info.close"), IsCancel = true };
        CloseButton.Click += (_, _) => Close();
        VersionText = MatDialogChrome.VersionText(info.Version, info.Channel);
        var footer = MatDialogChrome.Footer(VersionText, OpenButton, LogFolderButton, CloseButton);

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content = Body, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = dock;
    }

    /// <summary>What the window shows.</summary>
    public MatInfo Info { get; }

    /// <summary>The state with its word.</summary>
    public MatStateDot StateDot { get; }

    /// <summary>The facts.</summary>
    public MatFacts Facts { get; }

    /// <summary>The body under the header: the state, the sentence, the facts; a program may add a button for a further window.</summary>
    public StackPanel Body { get; }

    /// <summary>The version at the left of the footer.</summary>
    public TextBlock VersionText { get; }

    /// <summary>Open (hidden when the program has no page).</summary>
    public Button OpenButton { get; }

    /// <summary>Log folder.</summary>
    public Button LogFolderButton { get; }

    /// <summary>Close.</summary>
    public Button CloseButton { get; }
}
