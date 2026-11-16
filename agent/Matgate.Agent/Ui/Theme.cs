using MatDesktop;
using Microsoft.Win32;

namespace Matgate.Agent.Ui;

// The colours of the agent's windows and of its tray icon. They are the tokens of the Mat apps' toolkit (MatUi's matui.tokens.css),
// taken through the copy of MatDesktop in MatDesktop/Tokens (written by tools/matdesktop-copy.js --tokens-only in MatDesign; never
// edited here), for Matgate's accent and in the light or dark mode of the system (rules DESK-02 and AGENT-05). No window writes a
// colour of its own: it asks here.
internal static class Theme
{
    // Matgate's accent, as MatDesign's AppRegistry has it (IDENT-01).
    public const string AccentHex = "#0a7d57";

    private static MatPalette _palette = MatPalette.For(AccentHex, dark: false);
    private static readonly MatPalette LightPalette = MatPalette.For(AccentHex, dark: false);

    public static bool IsDark => _palette.IsDark;

    // The mode of the system: Settings > Personalisation > Colours > "Choose your app mode".
    public static bool SystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    // Follows the system; a window made after this call takes the mode (the controls of Windows - fields, lists, check marks,
    // scroll bars, the title bar - through the colour mode of WinForms).
    public static void FollowSystem() => Use(SystemIsDark());

    public static void Use(bool dark)
    {
        _palette = MatPalette.For(AccentHex, dark);
#pragma warning disable WFO5001 // The colour mode of WinForms is marked experimental; it is what gives the system's controls their dark look.
        Application.SetColorMode(dark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
    }

    // --mat-surface: the header, the body, the cards.
    public static Color Surface => Solid(_palette.Surface);

    // --mat-surface-2: a value to copy, read-only fields.
    public static Color Surface2 => Solid(_palette["--mat-surface-2"]);

    // --mat-surface-3: a button that is pressed.
    public static Color Surface3 => Solid(_palette["--mat-surface-3"]);

    // --mat-nav-bg: the footer with the version and the buttons, like MatDesktop's chrome.
    public static Color FooterBack => Solid(_palette["--mat-nav-bg"]);

    // --mat-text, --mat-text-muted.
    public static Color Text => Solid(_palette.Text);

    public static Color Muted => Solid(_palette.TextMuted);

    // --mat-border painted over the surface it lies on (WinForms has no transparency for a line).
    public static Color Line => Over(_palette.Border, _palette.Surface);

    public static Color FooterLine => Over(_palette.Border, _palette["--mat-nav-bg"]);

    // --mat-border-strong over the surface: the outline of a secondary button.
    public static Color LineStrong => Over(_palette["--mat-border-strong"], _palette.Surface);

    // The accent of the mode and what stands on it: the primary button.
    public static Color Accent => Solid(_palette.Accent);

    public static Color AccentHover => Solid(_palette["--mat-accent-hover"]);

    public static Color AccentActive => Solid(_palette["--mat-accent-active"]);

    public static Color OnAccent => Solid(_palette.OnAccent);

    // --mat-danger and --mat-on-danger: the button that removes the agent.
    public static Color Danger => Solid(_palette.Danger);

    public static Color OnDanger => Solid(_palette["--mat-on-danger"]);

    // A notice (MatUi's mat-alert): the tint of its tone over the surface and the text of its tone - no bar at one side.
    public static (Color Back, Color Fore) Tone(NoticeTone tone)
    {
        var name = tone switch
        {
            NoticeTone.Success => "success",
            NoticeTone.Warning => "warning",
            NoticeTone.Error => "danger",
            _ => "info",
        };
        return (Over(_palette[$"--mat-{name}-bg"], _palette.Surface), Solid(_palette[$"--mat-{name}-text"]));
    }

    // The dot of a state in a window of the current mode (TRAY-02): green, amber, red, grey.
    public static Color State(AgentState state) => Solid(_palette[StateToken(state)]);

    // The dot of the tray icon: the light values, as MatDesktop's tray icons have them - the cut-out around the dot makes it
    // readable on a light and on a dark task bar alike.
    public static Color TrayDot(AgentState state) => Solid(LightPalette[StateToken(state)]);

    private static string StateToken(AgentState state) => state switch
    {
        AgentState.Running => "--mat-success",
        AgentState.Waiting => "--mat-warning",
        AgentState.Stopped => "--mat-danger",
        _ => "--mat-text-muted",
    };

    private static Color Solid(MatRgba colour) => colour.A >= 1 ? Color.FromArgb(colour.R8, colour.G8, colour.B8) : Over(colour, _palette.Surface);

    private static Color Over(MatRgba colour, MatRgba background)
    {
        var opaque = colour.Over(background);
        return Color.FromArgb(opaque.R8, opaque.G8, opaque.B8);
    }
}
