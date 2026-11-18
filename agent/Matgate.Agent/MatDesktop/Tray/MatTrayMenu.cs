// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
// Plain C#, only System: a WinForms tray can build its ContextMenuStrip from Build() too.
namespace MatDesktop;

/// <summary>What an entry of the tray menu is (TRAY-01), in the order of the menu.</summary>
public enum MatTrayRole
{
    /// <summary>The name and the version of the program, disabled.</summary>
    Title,

    /// <summary>The state in words, disabled.</summary>
    State,

    /// <summary><c>Open &lt;App&gt;</c>, the default item (bold where the system can).</summary>
    Open,

    /// <summary>An item of the program's own (the last entries, Pause).</summary>
    Own,

    /// <summary><c>Info …</c>.</summary>
    Info,

    /// <summary><c>Set up …</c>.</summary>
    SetUp,

    /// <summary><c>Settings …</c>.</summary>
    Settings,

    /// <summary><c>Open log folder</c>.</summary>
    LogFolder,

    /// <summary><c>Install update …</c>, only while an update is waiting.</summary>
    InstallUpdate,

    /// <summary>An action of a service (start, stop, restart), with the shield where it needs administrator rights.</summary>
    Service,

    /// <summary><c>Hide icon</c> of an agent's tray: ends the tray like Quit and is undone in the setup.</summary>
    HideIcon,

    /// <summary><c>Quit</c>, always the last item.</summary>
    Quit,

    /// <summary>A line between two groups.</summary>
    Separator,
}

/// <summary>The actions of a service in a tray menu.</summary>
public enum MatServiceAction
{
    /// <summary><c>Start service …</c>.</summary>
    Start,

    /// <summary><c>Stop service …</c>.</summary>
    Stop,

    /// <summary><c>Restart service …</c>.</summary>
    Restart,
}

/// <summary>One entry of the built menu: its role, its text, whether it can be chosen, whether it is the default item, whether it needs administrator rights (the shield) and what it does.</summary>
public sealed record MatTrayEntry(MatTrayRole Role, string Text, bool IsEnabled = true, bool IsDefault = false, bool NeedsElevation = false, Action? Run = null)
{
    /// <summary>The shortcut of the item (<c>Ctrl+Shift+V</c>), shown at the right of its text where the menu of the system can, or null.</summary>
    public string? Shortcut { get; init; }

    /// <summary>A separator.</summary>
    public static MatTrayEntry Line { get; } = new(MatTrayRole.Separator, string.Empty, IsEnabled: false);
}

/// <summary>
/// The tray menu of TRAY-01 as a model: a program says what it has, in any order, and <see cref="Build"/> puts it in THE order: the name and the version (disabled), the state in words
/// (disabled), a separator; <c>Open &lt;App&gt;</c> (the default, with the shortcut that opens the program); the program's own items between separators, in groups of their own
/// (<see cref="AddOwnSeparator"/>); <c>Info …</c>, <c>Set up …</c> or <c>Settings …</c>, <c>Open log folder</c>,
/// <c>Install update …</c> only while an update waits; a separator; the actions of a service; a separator; <c>Hide icon</c> of an agent; <c>Quit</c>, always last. What a program does not
/// have is left out and no separator stands twice, first or last. There is no Uninstall item: a program is removed where it was installed (SETUP-04).
/// </summary>
public sealed class MatTrayMenu
{
    /// <summary>The longest tooltip of a tray icon: Windows shows 127 characters (NOTIFYICONDATA), a longer one is cut there with an ellipsis.</summary>
    public const int TooltipMax = 127;

    private readonly List<List<MatTrayEntry>> _own = [[]];
    private readonly List<MatTrayEntry> _services = [];

    /// <summary>A menu for a program and its version (the number, as in <c>Example Agent 1.4.2</c>); <paramref name="quit"/> ends the program, or the tray of an agent.</summary>
    public MatTrayMenu(string appName, string version, Action quit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(quit);
        AppName = appName;
        Version = version;
        Quit = quit;
    }

    /// <summary>The name of the program.</summary>
    public string AppName { get; }

    /// <summary>The version number.</summary>
    public string Version { get; }

    /// <summary>The state shown in the second line (TRAY-02).</summary>
    public MatState State { get; set; } = MatState.Running;

    /// <summary>Opens the main window, or the web page when there is no window, or Info when there is neither. Required: a menu without it cannot be built.</summary>
    public Action? Open { get; set; }

    /// <summary>
    /// The text of the default item when <c>Open &lt;App&gt;</c> does not say what it opens (an agent whose window is a kiosk: <c>Open Example Kiosk</c>); null takes
    /// <c>Open &lt;App&gt;</c>. It stays the default item in the place of TRAY-01.
    /// </summary>
    public string? OpenText { get; set; }

    /// <summary>The shortcut that opens the program from anywhere (<c>Ctrl+Shift+V</c>, DESK-07), shown at the right of Open, or null.</summary>
    public string? OpenShortcut { get; set; }

    /// <summary>
    /// More words for the tooltip of the icon after the name and the state (<c>Example: Running</c>, then a new line and these words: <c>Synchronised 14:32</c>), or null. The tooltip
    /// always begins with the name and the state (TRAY-02); see <see cref="Tooltip"/>.
    /// </summary>
    public string? TooltipText { get; set; }

    /// <summary>The tooltip of the icon: <c>&lt;App&gt;: &lt;state&gt;</c> and, on a line of its own, <see cref="TooltipText"/>; at most <see cref="TooltipMax"/> characters.</summary>
    public string Tooltip
    {
        get
        {
            var text = State.Tooltip(AppName);
            if (!string.IsNullOrWhiteSpace(TooltipText))
            {
                text += "\n" + TooltipText.Trim();
            }

            return text.Length <= TooltipMax ? text : text[..(TooltipMax - 1)] + "…";
        }
    }

    /// <summary><c>Info …</c>, or null when the program has no Info window.</summary>
    public Action? Info { get; set; }

    /// <summary><c>Set up …</c> (an agent that is set up in its setup window); null when the program has none. Not together with <see cref="Settings"/>.</summary>
    public Action? SetUp { get; set; }

    /// <summary><c>Settings …</c>; null when the program has none. Not together with <see cref="SetUp"/>.</summary>
    public Action? Settings { get; set; }

    /// <summary><c>Open log folder</c>, or null.</summary>
    public Action? LogFolder { get; set; }

    /// <summary><c>Install update …</c>: shown only while it is set, that is while an update is waiting.</summary>
    public Action? InstallUpdate { get; set; }

    /// <summary><c>Hide icon</c> of an agent's tray (it ends the tray like Quit; the setup brings the icon back), or null.</summary>
    public Action? HideIcon { get; set; }

    /// <summary>Ends the program (or the tray of an agent, never its service: AGENT-04).</summary>
    public Action Quit { get; }

    /// <summary>Adds an item of the program's own; these keep the order in which they are added. Without an action it is shown disabled (a heading such as <c>Last copied</c>).</summary>
    public MatTrayMenu AddOwn(string text, Action? run, bool enabled = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _own[^1].Add(new MatTrayEntry(MatTrayRole.Own, text, enabled && run is not null, Run: run));
        return this;
    }

    /// <summary>
    /// Starts a new group of the program's own items: a separator stands between the groups (the last entries, then Pause), all of them still in the place of TRAY-01. An empty
    /// group is left out, so a separator never stands twice.
    /// </summary>
    public MatTrayMenu AddOwnSeparator()
    {
        if (_own[^1].Count > 0)
        {
            _own.Add([]);
        }

        return this;
    }

    /// <summary>Adds an action of a service (start, stop, restart) with the shield when it needs administrator rights; they stand in the order start, stop, restart.</summary>
    public MatTrayMenu AddService(MatServiceAction action, Action run, bool needsElevation = true, bool enabled = true)
    {
        ArgumentNullException.ThrowIfNull(run);
        var key = action switch
        {
            MatServiceAction.Start => "tray.serviceStart",
            MatServiceAction.Stop => "tray.serviceStop",
            _ => "tray.serviceRestart",
        };
        _services.RemoveAll(s => s.Text == MatText.Get(key));
        _services.Add(new MatTrayEntry(MatTrayRole.Service, MatText.Get(key), enabled, NeedsElevation: needsElevation, Run: run));
        _services.Sort((a, b) => Order(a.Text).CompareTo(Order(b.Text)));
        return this;

        static int Order(string text) => text == MatText.Get("tray.serviceStart") ? 0 : text == MatText.Get("tray.serviceStop") ? 1 : 2;
    }

    /// <summary>Removes the items of the program's own (before they are added again, when the last entries changed).</summary>
    public void ClearOwn()
    {
        _own.Clear();
        _own.Add([]);
    }

    /// <summary>The entries in the order of TRAY-01, with the separators.</summary>
    public IReadOnlyList<MatTrayEntry> Build()
    {
        if (Open is null)
        {
            throw new InvalidOperationException("A tray menu needs Open: the window, the web page or Info (TRAY-01).");
        }

        if (SetUp is not null && Settings is not null)
        {
            throw new InvalidOperationException("A tray menu has Set up … or Settings …, not both (TRAY-01).");
        }

        List<List<MatTrayEntry>> groups =
        [
            [new(MatTrayRole.Title, MatVersion.Title(AppName, Version), IsEnabled: false), new(MatTrayRole.State, State.Word, IsEnabled: false)],
            [new(MatTrayRole.Open, string.IsNullOrWhiteSpace(OpenText) ? MatText.Format("tray.open", AppName) : OpenText.Trim(), IsDefault: true, Run: Open)
            {
                Shortcut = string.IsNullOrWhiteSpace(OpenShortcut) ? null : OpenShortcut.Trim(),
            }],
            .. _own.Select(g => g.ToList()),
            Present(
                (MatTrayRole.Info, "tray.info", Info),
                (MatTrayRole.SetUp, "tray.setUp", SetUp),
                (MatTrayRole.Settings, "tray.settings", Settings),
                (MatTrayRole.LogFolder, "tray.logFolder", LogFolder),
                (MatTrayRole.InstallUpdate, "tray.installUpdate", InstallUpdate)),
            [.. _services],
            [.. Present((MatTrayRole.HideIcon, "tray.hideIcon", HideIcon)), new(MatTrayRole.Quit, MatText.Get("tray.quit"), Run: Quit)],
        ];

        var entries = new List<MatTrayEntry>();
        foreach (var group in groups.Where(g => g.Count > 0))
        {
            if (entries.Count > 0)
            {
                entries.Add(MatTrayEntry.Line);
            }

            entries.AddRange(group);
        }

        return entries;
    }

    private static List<MatTrayEntry> Present(params (MatTrayRole Role, string Key, Action? Run)[] items)
        => items.Where(i => i.Run is not null).Select(i => new MatTrayEntry(i.Role, MatText.Get(i.Key), Run: i.Run)).ToList();

    /// <summary>
    /// What a click on the tray icon does (TRAY-03): on Windows a left click runs the default item (Open) and the right click opens the menu, which the system does; on macOS and Linux every
    /// click opens the menu, so the click itself does nothing. True when the click runs Open.
    /// </summary>
    public static bool LeftClickOpens(bool isWindows) => isWindows;
}
