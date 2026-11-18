// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
// Plain C#, only System: a WinForms tray can take it too.
namespace MatDesktop;

/// <summary>The states of a program in its tray (TRAY-02).</summary>
public enum MatStateKind
{
    /// <summary>It runs: green.</summary>
    Running,

    /// <summary>It waits or needs attention (an approval, a connection that comes back): amber.</summary>
    Waiting,

    /// <summary>It stopped or failed: red.</summary>
    Stopped,

    /// <summary>It is not set up yet: grey.</summary>
    NotSetUp,

    /// <summary>A state of the program's own (paused).</summary>
    Custom,
}

/// <summary>How a state is drawn on a macOS template image, which has no colour (the system paints it): a shape in the dot, or none.</summary>
public enum MatStateMark
{
    /// <summary>No dot: the plain mark.</summary>
    None,

    /// <summary>A filled dot.</summary>
    Dot,

    /// <summary>A ring.</summary>
    Ring,

    /// <summary>A dot with a pause sign cut out.</summary>
    Pause,

    /// <summary>A dot with a cross cut out.</summary>
    Cross,
}

/// <summary>
/// A state of a program: its kind, its word (the second line of the tray menu, the tooltip, Info), the colour token of its dot and its mark on a template image. A state is never shown by
/// its colour alone (COLOR-04): the word goes with it.
/// </summary>
public sealed record MatState(MatStateKind Kind, string Word, string ColorToken, MatStateMark TemplateMark)
{
    /// <summary>Running: green (<c>--mat-success</c>), no mark on a template image.</summary>
    public static MatState Running => new(MatStateKind.Running, MatText.Get("state.running"), "--mat-success", MatStateMark.None);

    /// <summary>Waiting or attention: amber (<c>--mat-warning</c>), a ring on a template image.</summary>
    public static MatState Waiting => new(MatStateKind.Waiting, MatText.Get("state.waiting"), "--mat-warning", MatStateMark.Ring);

    /// <summary>Stopped or an error: red (<c>--mat-danger</c>), a cross on a template image.</summary>
    public static MatState Stopped => new(MatStateKind.Stopped, MatText.Get("state.stopped"), "--mat-danger", MatStateMark.Cross);

    /// <summary>Not set up: grey (<c>--mat-text-muted</c>), a dot on a template image.</summary>
    public static MatState NotSetUp => new(MatStateKind.NotSetUp, MatText.Get("state.notSetUp"), "--mat-text-muted", MatStateMark.Dot);

    /// <summary>The four states of every program, in the order of TRAY-02.</summary>
    public static IReadOnlyList<MatState> Standard => [Running, Waiting, Stopped, NotSetUp];

    /// <summary>A state of the program's own (paused: amber with a pause sign): its word, the colour token of its dot and its mark on a template image.</summary>
    public static MatState Custom(string word, string colorToken, MatStateMark templateMark)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        if (!MatTokens.Colors.Any(c => c.Token == colorToken) && !colorToken.StartsWith("--mat-accent", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{colorToken} is not a colour token.", nameof(colorToken));
        }

        return new MatState(MatStateKind.Custom, word, colorToken, templateMark);
    }

    /// <summary>
    /// The colour token of the dot on a tray icon. The task bar is light or dark whatever the window is, so the grey of Not set up is <c>--mat-text-faint</c> there (3.5:1 on a light,
    /// 4.2:1 on a dark task bar), not <c>--mat-text-muted</c>, which is 2.4:1 on a dark one; every other state keeps its token.
    /// </summary>
    public string TrayColorToken => Kind == MatStateKind.NotSetUp ? "--mat-text-faint" : ColorToken;

    /// <summary>The tooltip of the tray icon: <c>Example Agent: Running</c>.</summary>
    public string Tooltip(string appName) => MatText.Format("state.tooltip", appName, Word);
}
