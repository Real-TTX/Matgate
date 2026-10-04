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

    // Welche Palette. "light | dark | system" oben sagt die Helligkeit, das hier die Farben - die
    // beiden sind unabhaengig: jedes Thema hat einen hellen und einen dunklen Satz.
    public string PreferredThemeName { get; set; } = "matgate";

    // Eine eigene Akzentfarbe, die über der des Themas liegt. Leer heißt: die des Themas. Gespeichert
    // wird, was gewählt wurde; was angezeigt wird, kann davon abweichen - siehe ThemeService.SafeAccent.
    public string AccentColor { get; set; } = "";

    // Die zweite Akzentfarbe: das Zeichen, die Verläufe und die Nebenhervorhebungen. Leer heißt
    // wieder die des Themas.
    public string AccentColor2 { get; set; } = "";

    // Der Hintergrund der Anwendung. Leer heißt der des Themas. Aus ihm leitet der Dienst die
    // Flächen darüber ab - Felder, Linien, Schrift -, damit eine frei gewählte Farbe nicht die
    // Lesbarkeit mitnimmt.
    public string BackgroundColor { get; set; } = "";

    // Die Startseite: welche Abschnitte sie zeigt und in welcher Reihenfolge. Leer heisst die
    // Vorgabe - und die steht in HomeSectionKeys, nicht in der Datenbank, damit ein neuer
    // Abschnitt bei allen auftaucht, die nie etwas eingestellt haben.
    public List<string> HomeSections { get; set; } = [];

    public List<string> HiddenHomeSections { get; set; } = [];

    // Verbindungen, die auf der Startseite als Knopf in der Aktionsleiste stehen - ein Druck
    // verbindet. Die Reihenfolge ist die der Liste; was nicht darin steht, erscheint nicht.
    public List<Guid> ActionBarServerIds { get; set; } = [];

    // Der zweite Faktor. Das Geheimnis liegt verschlüsselt in der Datei (siehe JsonDataStore);
    // eingeschaltet ist er erst, wenn einmal ein gültiger Code eingegeben wurde - sonst sperrt
    // sich aus, wer die Einrichtung abbricht.
    public string TotpSecret { get; set; } = "";

    public bool TotpEnabled { get; set; }

    public DateTimeOffset? TotpConfirmedAt { get; set; }

    // Der zuletzt verbrauchte Zeitschritt. Ein Code gilt 30 Sekunden - lange genug, dass ein
    // Mitleser ihn ein zweites Mal verwenden könnte. Alles, was nicht neuer ist, wird abgewiesen.
    public long TotpLastStep { get; set; }

    // Nur die Abdrücke der Wiederherstellungs-Codes, nie die Codes selbst. Ein gebrauchter wird
    // aus der Liste genommen.
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
    // Die Abschnitte der Startseite in ihrer eingebauten Reihenfolge. Wer nichts einstellt,
    // sieht genau das.
    public static readonly string[] Keys =
    [
        "search", "quick", "folders", "recent", "connections", "places", "workspaces", "farm",
    ];

    public static bool IsKnown(string key)
    {
        return Keys.Contains(key, StringComparer.Ordinal);
    }

    // Die gespeicherte Reihenfolge, ergaenzt um alles, was noch nicht darin steht. Ein Abschnitt,
    // den es beim letzten Speichern noch nicht gab, verschwindet so nicht - er haengt sich hinten
    // an und laesst sich von dort wegziehen.
    public static IReadOnlyList<string> Order(IEnumerable<string>? gespeichert)
    {
        var reihe = new List<string>();
        foreach (var key in gespeichert ?? [])
        {
            if (IsKnown(key) && !reihe.Contains(key, StringComparer.Ordinal))
            {
                reihe.Add(key);
            }
        }

        foreach (var key in Keys)
        {
            if (!reihe.Contains(key, StringComparer.Ordinal))
            {
                reihe.Add(key);
            }
        }

        return reihe;
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

    // Add an F1-F12 row to the on-screen keyboard.
    public bool FunctionKeys { get; set; }

    // Offer Ctrl+Alt+Del as a toolbar button (in addition to the on-screen keyboard key).
    public bool CtrlAltDelHotkey { get; set; } = true;

    // --- Session toolbar ---
    // The order of the action buttons in a session, by key. Only as many fit in the row on a phone as
    // there is room for; the rest move into the overflow menu, so this decides which ones stay within
    // reach. Keys the user never sorted keep their built-in place at the end, and an empty list means
    // the built-in order - so a new action never disappears because of an order saved before it
    // existed. "disconnect" is always last and is not part of this.
    public List<string> ActionOrder { get; set; } = [];

    // Actions the user does not want to see at all. They are not merely pushed into the overflow
    // menu - they are gone, which is the point for anyone who never uses them. "disconnect" is not
    // hideable: ending a session has to stay reachable.
    public List<string> HiddenActions { get; set; } = [];

    // Quick-connect offers one chip per protocol. Not everyone uses all of them, and a row of chips
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
