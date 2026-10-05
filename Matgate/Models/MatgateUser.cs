namespace Matgate.Models;

public sealed class MatgateUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string UserName { get; set; } = "";

    // Optional contact/login address: sign-in accepts the username OR this email. Unique across
    // users when set; legacy users (created before the field existed) simply have it empty.
    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public string GuacamolePassword { get; set; } = "";

    public bool IsAdmin { get; set; }

    public bool CanManageServers { get; set; }

    public bool CanCreateServers { get; set; }

    // Allowed to open ad-hoc "Quick connect" sessions (enter host + credentials, connect without
    // saving a connection). Admins are always allowed.
    public bool CanQuickConnect { get; set; }

    public bool ServerAccessAll { get; set; }

    public string PreferredLanguage { get; set; } = "en";

    public string PreferredTheme { get; set; } = "system";

    // Which palette. "light | dark | system" above decides the brightness, this one the colours - the
    // two are independent: every theme has a light and a dark set.
    public string PreferredThemeName { get; set; } = "matgate";

    // An accent colour of the user's own, layered over the theme's. Empty means: the theme's. What was
    // picked is stored; what is shown may differ from it - see ThemeService.SafeAccent.
    public string AccentColor { get; set; } = "";

    // The second accent colour: the logo, the gradients and the secondary highlights. Empty means the
    // theme's again.
    public string AccentColor2 { get; set; } = "";

    // The background of the application. Empty means the theme's. From it the service derives the
    // surfaces on top of it - fields, lines, text - so that a freely chosen colour does not take
    // readability with it.
    public string BackgroundColor { get; set; } = "";

    // The home page: which sections it shows and in what order. Empty means the default - and that
    // lives in HomeSectionKeys, not in the data file, so a new section shows up for everyone who
    // never configured anything.
    public List<string> HomeSections { get; set; } = [];

    public List<string> HiddenHomeSections { get; set; } = [];

    // Connections that sit on the home page as a button in the action bar - one press connects. The
    // order is the order of the list; what is not in it does not appear.
    public List<Guid> ActionBarServerIds { get; set; } = [];

    // The second factor. The secret is stored encrypted in the file (see JsonDataStore); it is only
    // switched on once a valid code has been entered - otherwise anyone who abandons the setup would
    // lock themselves out.
    public string TotpSecret { get; set; } = "";

    public bool TotpEnabled { get; set; }

    public DateTimeOffset? TotpConfirmedAt { get; set; }

    // The most recently used time step. A code is valid for 30 seconds - long enough for someone
    // reading along to use it a second time. Anything not newer than this is rejected.
    public long TotpLastStep { get; set; }

    // Only the hashes of the recovery codes, never the codes themselves. A used one is removed from
    // the list.
    public List<string> TotpRecoveryHashes { get; set; } = [];

    public bool RememberLoginByDefault { get; set; } = true;

    // Per-user session behaviour (display + keyboard helpers), applied to every remote session the
    // user opens, on any device. Configured under Account -> Session.
    public SessionPreferences Session { get; set; } = new();

    // Which of the gateway's file areas this user may use. A permission, not a preference: only an
    // admin sets it, and the user never sees it in their own account.
    public FileSharePermissions FileShare { get; set; } = new();

    public bool IsEnabled { get; set; } = true;

    public List<Guid> FavoriteServerIds { get; set; } = [];

    // Most-recently-opened connections (most recent first), updated whenever a connection launches.
    // Drives the "Recently used" section on the home page.
    public List<RecentConnectionEntry> RecentConnections { get; set; } = [];

    public List<Guid> ServerAccess { get; set; } = [];

    // Optional per-(user, file-server) restrictions. Absence of a rule = full access.
    public List<FileAccessRule> FileAccessRules { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Per-user remote-session behaviour. All independent on/off switches with sensible defaults; the
// session UI reads these at load and enables the matching controls/behaviours.
public static class HomeLayout
{
    // The sections of the home page in their built-in order. Anyone who configures nothing sees
    // exactly this.
    public static readonly string[] Keys =
    [
        "search", "quick", "folders", "recent", "connections", "places", "workspaces", "farm",
    ];

    public static bool IsKnown(string key)
    {
        return Keys.Contains(key, StringComparer.Ordinal);
    }

    // The stored order, extended by everything not yet in it. A section that did not exist at the
    // time of the last save does not vanish this way - it is appended at the end, and can be dragged
    // away from there.
    public static IReadOnlyList<string> Order(IEnumerable<string>? gespeichert)
    {
        var ordered = new List<string>();
        foreach (var key in gespeichert ?? [])
        {
            if (IsKnown(key) && !ordered.Contains(key, StringComparer.Ordinal))
            {
                ordered.Add(key);
            }
        }

        foreach (var key in Keys)
        {
            if (!ordered.Contains(key, StringComparer.Ordinal))
            {
                ordered.Add(key);
            }
        }

        return ordered;
    }
}

public sealed class SessionPreferences
{
    // --- Display (only relevant in the fixed-resolution "desktop" display mode) ---
    // Move the visible cut-out when the mouse reaches the window edge (map-style panning).
    public bool EdgePanning { get; set; } = true;

    // Pan the cut-out by holding the middle mouse button and dragging.
    public bool DragPanning { get; set; } = true;

    // Stretch the remote image to fill the whole window (may distort the aspect ratio).
    public bool StretchToWindow { get; set; }

    // --- Keyboard helpers ---
    // In fullscreen, capture browser-reserved shortcuts (Windows, Alt+Tab, Alt+F4, Ctrl+W/T, Escape)
    // via the Keyboard Lock API and send them to the session instead of the browser.
    public bool SystemCombos { get; set; } = true;

    // Add an F1-F12 ordered to the on-screen keyboard.
    public bool FunctionKeys { get; set; }

    // Offer Ctrl+Alt+Del as a toolbar button (in addition to the on-screen keyboard key).
    public bool CtrlAltDelHotkey { get; set; } = true;

    // --- Session toolbar ---
    // The order of the action buttons in a session, by key. Only as many fit in the ordered on a phone as
    // there is room for; the rest move into the overflow menu, so this decides which ones stay within
    // reach. Keys the user never sorted keep their built-in place at the end, and an empty list means
    // the built-in order - so a new action never disappears because of an order saved before it
    // existed. "disconnect" is always last and is not part of this.
    public List<string> ActionOrder { get; set; } = [];

    // Actions the user does not want to see at all. They are not merely pushed into the overflow
    // menu - they are gone, which is the point for anyone who never uses them. "disconnect" is not
    // hideable: ending a session has to stay reachable.
    public List<string> HiddenActions { get; set; } = [];

    // Quick-connect offers one chip per protocol. Not everyone uses all of them, and a ordered of chips
    // for things you never connect to is noise - these are left out.
    public List<string> HiddenQuickProtocols { get; set; } = [];

    public static readonly string[] QuickProtocolKeys =
        ["rdp", "ssh", "vnc", "sftp", "smb", "website", "ftp", "webdav"];

    public static bool IsKnownQuickProtocol(string key) => QuickProtocolKeys.Contains(key);

    // The sortable actions, in their built-in order. Kept next to the property it validates so the two
    // cannot drift apart; "disconnect" is deliberately absent because it is pinned last.
    public static readonly string[] SortableActions =
    [
        "fullscreen", "popOut", "reattach", "pointer", "rightClick", "keyboard", "osk",
        "resolution", "autoResize", "zoomOut", "zoomIn", "copyUrl", "clipboard", "cad", "upload", "fileArea",
    ];

    public static bool IsKnownAction(string key) => SortableActions.Contains(key);

    // --- Clipboard ---
    // Paste by TYPING the text as individual key events instead of handing it to the remote over the
    // clipboard channel. Required for SSH/terminal sessions (and a useful fallback elsewhere), where
    // the remote never pastes the clipboard we give it.
    public bool PasteAsKeystrokes { get; set; }
}

// Which of the gateway's own file areas a user may use. They show up as folders on the redirected
// drive of a remote session, next to the session's own scratch folder.
//
// Deliberately two levels per area - handed over or not at all. The drive is served by a single
// system user, so the filesystem cannot tell two Matgate users apart; a "read-only" in between could
// not be enforced and would only look like a guarantee. Anything finer belongs in the file manager,
// where Matgate itself serves every request.
public sealed class FileSharePermissions
{
    // Shared by everyone who has it: the gateway-wide exchange folder.
    public bool Global { get; set; }

    // Belongs to the connection rather than to a person - the same folder for everyone who may open
    // that connection. Never handed to ad-hoc quick connections, whose id is new every time.
    public bool Connection { get; set; }

    // The user's own folder, identical in every session they open.
    public bool Personal { get; set; } = true;
}

// One entry in a user's recently-used connection history.
public sealed class RecentConnectionEntry
{
    public Guid ServerId { get; set; }

    public DateTimeOffset UsedAt { get; set; } = DateTimeOffset.UtcNow;
}

// Restricts a user's access to a single file connection (SMB/FTP/SFTP): optionally read-only
// and/or confined to a subfolder (relative to the server's configured file root).
public sealed class FileAccessRule
{
    public Guid ServerId { get; set; }

    public bool ReadOnly { get; set; }

    public string SubPath { get; set; } = "";
}
