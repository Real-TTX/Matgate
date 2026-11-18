// MatDesktop 0.2.0 – a copy from MatDesign, do not edit here
using Avalonia.Controls;

namespace MatDesktop;

/// <summary>The three modes of the one setup window (SETUP-01).</summary>
public enum MatSetupMode
{
    /// <summary>Install the program.</summary>
    Setup,

    /// <summary>Update it: no questions, the settings are kept.</summary>
    Update,

    /// <summary>Remove it (SETUP-04).</summary>
    Uninstall,
}

/// <summary>The kinds of steps (SETUP-02), in their order.</summary>
public enum MatSetupStepKind
{
    /// <summary>What will happen, where to and with which rights.</summary>
    Welcome,

    /// <summary>The role, only where a program can be installed in more than one.</summary>
    Role,

    /// <summary>Pairing with a server (AGENT-02): the address and the code, checked at once.</summary>
    Connect,

    /// <summary>Switches with the safe choice set.</summary>
    Options,

    /// <summary>What an uninstall removes and the choice to delete the data too (SETUP-04).</summary>
    Uninstall,

    /// <summary>A bar and the list of steps with their state; can be neither cancelled nor closed.</summary>
    Progress,

    /// <summary>The program is ready, the offer to start it now, or what the user has to copy.</summary>
    Finish,
}

/// <summary>The state of a line of the progress list.</summary>
public enum MatSetupStepState
{
    /// <summary>Not started.</summary>
    Waiting,

    /// <summary>Running now.</summary>
    Running,

    /// <summary>Done.</summary>
    Done,

    /// <summary>Failed.</summary>
    Failed,
}

/// <summary>What the install reports: a line of the progress list (a line with the same text is updated) and, if known, how far the whole is (0..100).</summary>
public sealed record MatSetupReport(string Step, MatSetupStepState State, double? Percent = null);

/// <summary>A switch of the Options step, on or off as the safe choice is (SETUP-02).</summary>
/// <param name="Key">The name the program reads the choice by.</param>
/// <param name="Title">The title.</param>
/// <param name="Description">A sentence under it (optional).</param>
/// <param name="IsOn">The safe choice.</param>
public sealed record MatSetupOption(string Key, string Title, string? Description, bool IsOn)
{
    /// <summary>"Start at sign-in", on.</summary>
    public static MatSetupOption Autostart(string? description = null) => new("autostart", MatText.Get("setup.option.autostart"), description, true);

    /// <summary>"Shortcut on the desktop", off.</summary>
    public static MatSetupOption DesktopShortcut(string? description = null) => new("shortcut", MatText.Get("setup.option.shortcut"), description, false);

    /// <summary>"Install updates automatically", on.</summary>
    public static MatSetupOption AutomaticUpdates(string? description = null) => new("updates", MatText.Get("setup.option.updates"), description, true);
}

/// <summary>A role of the Role step: its title and what it means.</summary>
public sealed record MatSetupRole(string Key, string Title, string Description);

/// <summary>
/// A step of the setup window, one of the building blocks of SETUP-02 that a program fills or leaves out: its title (the window shows it), the sentence of what it asks, and what else it
/// needs (the switches, the roles, the fields of Connect with their check, the values of Finish that the user copies).
/// </summary>
public sealed class MatSetupStep
{
    private MatSetupStep(MatSetupStepKind kind, string title, string? text)
    {
        Kind = kind;
        Title = title;
        Text = text;
    }

    /// <summary>The kind.</summary>
    public MatSetupStepKind Kind { get; }

    /// <summary>The title of the step.</summary>
    public string Title { get; set; }

    /// <summary>What the step says or asks.</summary>
    public string? Text { get; set; }

    /// <summary>More content under the text: the fields of Connect, a note on Welcome, the values to copy on Finish.</summary>
    public Control? Content { get; set; }

    /// <summary>The check of Connect when Next is pressed (AGENT-02: checked at once, a bad code installs nothing): an error to show, or null to go on.</summary>
    public Func<Task<string?>>? Check { get; set; }

    /// <summary>The switches of Options.</summary>
    public IReadOnlyList<MatSetupOption> Options { get; private init; } = [];

    /// <summary>The roles of Role.</summary>
    public IReadOnlyList<MatSetupRole> Roles { get; private init; } = [];

    /// <summary>Whether Finish offers "Start &lt;App&gt; now" (on).</summary>
    public bool OffersStart { get; set; } = true;

    /// <summary>Welcome: what will happen, where to and with which rights (SETUP-03: "for your user account, without administrator rights, to ...").</summary>
    public static MatSetupStep Welcome(string text, Control? note = null) => new(MatSetupStepKind.Welcome, MatText.Get("setup.welcome"), text) { Content = note };

    /// <summary>Role: the roles the program can be installed in; the first is chosen.</summary>
    public static MatSetupStep Role(string text, params MatSetupRole[] roles)
    {
        if (roles.Length < 2)
        {
            throw new ArgumentException("A Role step needs at least two roles (otherwise it is left out).", nameof(roles));
        }

        return new MatSetupStep(MatSetupStepKind.Role, MatText.Get("setup.role"), text) { Roles = roles };
    }

    /// <summary>Connect: the fields (an address, a code) and the check that runs when Next is pressed.</summary>
    public static MatSetupStep Connect(string text, Control fields, Func<Task<string?>> check)
        => new(MatSetupStepKind.Connect, MatText.Get("setup.connect"), text) { Content = fields, Check = check };

    /// <summary>Options: switches with the safe choice set.</summary>
    public static MatSetupStep WithOptions(string? text, params MatSetupOption[] options)
        => new(MatSetupStepKind.Options, MatText.Get("setup.options"), text) { Options = options };

    /// <summary>Finish: the sentence when it worked, whether to offer "start now", and what the user has to copy (for an agent that others connect to).</summary>
    public static MatSetupStep Finish(string text, bool offersStart = true, Control? content = null)
        => new(MatSetupStepKind.Finish, string.Empty, text) { OffersStart = offersStart, Content = content };

    /// <summary>Progress: always there, made by the window.</summary>
    internal static MatSetupStep Progress(string title) => new(MatSetupStepKind.Progress, title, null);

    /// <summary>The step of an uninstall: what will be removed. The switch "Also delete my data" (off) is added by the window.</summary>
    public static MatSetupStep Uninstall(string text) => new(MatSetupStepKind.Uninstall, string.Empty, text);
}
