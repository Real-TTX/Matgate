using System.Net;
using System.Security.Claims;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

public sealed class HtmlViews
{
    private const string LanguageCookieName = "Matgate.Language";
    private const string ThemeCookieName = "Matgate.Theme";

    private static readonly IReadOnlyDictionary<string, string> GermanText = new Dictionary<string, string>
    {
        ["Home Network Gateway"] = "Heimnetz-Gateway",
        ["Home"] = "Home",
        ["Tools"] = "Werkzeuge",
        ["Network tools"] = "Netzwerkwerkzeuge",
        ["Tool"] = "Werkzeug",
        ["Select tool"] = "Werkzeug auswaehlen",
        ["Reachability and latency check."] = "Erreichbarkeit und Latenz pruefen.",
        ["Resolve hostnames and addresses."] = "Hostnamen und Adressen aufloesen.",
        ["Test TCP ports live."] = "TCP-Ports live pruefen.",
        ["Stream a file and watch transfer speed."] = "Eine Datei streamen und die Uebertragungsgeschwindigkeit sehen.",
        ["About"] = "ÃƒÆ’Ã†â€™Ãƒâ€¦Ã¢â‚¬Å“ber",
        ["About Matgate"] = "ÃƒÆ’Ã†â€™Ãƒâ€¦Ã¢â‚¬Å“ber Matgate",
        ["Local login for RDP, VNC, SSH, websites and file access in your home network."] = "Lokale Anmeldung fuer RDP-, VNC-, SSH-, Website- und Dateizugriffe im Heimnetz.",
        ["Live output streams below."] = "Die Ausgabe laeuft live unten ein.",
        ["Account"] = "Konto",
        ["Admin"] = "Admin",
        ["Version"] = "Version",
        ["Global"] = "Global",
        ["Username"] = "Benutzername",
        ["Password"] = "Passwort",
        ["Sign in"] = "Einloggen",
        ["Dashboard"] = "Dashboard",
        ["Connections"] = "Verbindungen",
        ["Page"] = "Seite",
        ["Connect"] = "Verbinden",
        ["Open"] = "Oeffnen",
        ["No global servers yet. An administrator can create servers and grant access."] = "Noch keine globalen Server. Ein Administrator kann Server anlegen und dir Zugriff geben.",
        ["Administration"] = "Administration",
        ["Users"] = "Benutzer",
        ["Create user"] = "Benutzer anlegen",
        ["Display name"] = "Anzeigename",
        ["Initial password"] = "Startpasswort",
        ["Administrator"] = "Administrator",
        ["Manage servers"] = "Server verwalten",
        ["Can create own servers"] = "Eigene Server anlegen",
        ["Preferred language"] = "Bevorzugte Sprache",
        ["Preferred theme"] = "Bevorzugtes Theme",
        ["Theme"] = "Theme",
        ["System"] = "System",
        ["Light"] = "Hell",
        ["Dark"] = "Dunkel",
        ["Default follows system theme."] = "Standard folgt dem System-Theme.",
        ["English"] = "Englisch",
        ["German"] = "Deutsch",
        ["Create"] = "Anlegen",
        ["Create own server"] = "Eigenen Server anlegen",
        ["Basics"] = "Grunddaten",
        ["Credentials"] = "Zugangsdaten",
        ["Connection password"] = "Verbindungs-Passwort",
        ["Access"] = "Zugriff",
        ["Existing users"] = "Vorhandene Benutzer",
        ["No users created yet."] = "Noch keine Benutzer angelegt.",
        ["Name"] = "Name",
        ["Display"] = "Anzeige",
        ["Roles"] = "Rollen",
        ["Status"] = "Status",
        ["Active"] = "Aktiv",
        ["Locked"] = "Gesperrt",
        ["User"] = "Benutzer",
        ["Unknown user"] = "Unbekannter Benutzer",
        ["Back"] = "Zurueck",
        ["Profile and Permissions"] = "Profil und Rechte",
        ["Enabled"] = "Aktiv",
        ["Save"] = "Speichern",
        ["Server access"] = "Serverzugriff",
        ["No servers created yet."] = "Noch keine Server angelegt.",
        ["Save access"] = "Zugriff speichern",
        ["Administrators always see all servers."] = "Administratoren sehen immer alle Server.",
        ["All global servers (*)"] = "Alle globalen Server (*)",
        ["* applies to global servers only."] = "* gilt nur fuer globale Server.",
        ["Only global servers are listed here."] = "Hier werden nur globale Server angezeigt.",
        ["Global server"] = "Globaler Server",
        ["Global servers"] = "Globale Server",
        ["Own server"] = "Eigener Server",
        ["Own servers"] = "Eigene Server",
        ["Scope"] = "Sichtbarkeit",
        ["No global servers created yet."] = "Noch keine globalen Server angelegt.",
        ["No own servers created yet."] = "Noch keine eigenen Server angelegt.",
        ["Profile"] = "Profil",
        ["Permissions"] = "Rechte",
        ["RDP settings"] = "RDP-Einstellungen",
        ["SSH settings"] = "SSH-Einstellungen",
        ["File access"] = "Dateizugriff",
        ["Website settings"] = "Website-Einstellungen",
        ["Website URL"] = "Website-URL",
        ["Ignore certificate"] = "Zertifikat ignorieren",
        ["New password"] = "Neues Passwort",
        ["Set password"] = "Passwort setzen",
        ["Remove"] = "Entfernen",
        ["Cannot delete own user"] = "Eigenen Benutzer nicht loeschen",
        ["Delete user"] = "Benutzer loeschen",
        ["Servers"] = "Server",
        ["Create server"] = "Server anlegen",
        ["Existing servers"] = "Vorhandene Server",
        ["Folder"] = "Ordner",
        ["Folder name"] = "Ordnername",
        ["Folder icon"] = "Ordner-Icon",
        ["Default folder icon"] = "Standard-Ordner-Icon",
        ["Unsorted"] = "Ohne Ordner",
        ["Favorites"] = "Favoriten",
        ["Favorite servers"] = "Favorisierte Server",
        ["No favorite servers yet."] = "Noch keine Favoriten.",
        ["Add to favorites"] = "Zu Favoriten hinzufuegen",
        ["Remove from favorites"] = "Aus Favoriten entfernen",
        ["Optional. Used for grouping in lists."] = "Optional. Wird zur Gruppierung in Listen verwendet.",
        ["Favorites are stored per user."] = "Favoriten werden pro Benutzer gespeichert.",
        ["Type"] = "Typ",
        ["Target"] = "Ziel",
        ["Off"] = "Aus",
        ["Connection"] = "Verbindung",
        ["Clear saved target password"] = "Gespeichertes Ziel-Passwort entfernen",
        ["Delete server"] = "Server loeschen",
        ["Domain"] = "DomÃƒÆ’Ã†â€™Ãƒâ€šÃ‚Â¤ne",
        ["Edit"] = "Bearbeiten",
        ["Name and host are required."] = "Name und Host sind erforderlich.",
        ["Name and website URL are required."] = "Name und Website-URL sind erforderlich.",
        ["This server is not a website connection."] = "Dieser Server ist keine Website-Verbindung.",
        ["Invalid request"] = "Ungueltige Anfrage",
        ["The form has expired."] = "Das Formular ist abgelaufen.",
        ["No access"] = "Kein Zugriff",
        ["Your user is not allowed to perform this action."] = "Dein Benutzer hat fuer diese Aktion keine Berechtigung.",
        ["Global servers only. Own servers are automatically available to the owner."] = "Nur globale Server. Eigene Server sind fuer den Besitzer automatisch verfuegbar.",
        ["This file is not a supported archive."] = "Diese Datei ist kein unterstuetztes Archiv.",
        ["To Home"] = "Zu Home",
        ["New connection"] = "Neue Verbindung",
        ["Actions for the active tab"] = "Aktionen fuer den aktiven Tab",
        ["Clipboard"] = "Zwischenablage",
        ["Unzip"] = "Entpacken",
        ["Fullscreen"] = "Vollbild",
        ["Disconnect"] = "Trennen",
        ["No active tab"] = "Kein aktiver Tab",
        ["Ready"] = "Bereit",
        ["Gateway: -"] = "Gateway: -",
        ["Tunnel: -"] = "Tunnel: -",
        ["Sync: -"] = "Sync: -",
        ["Credentials"] = "Zugangsdaten",
        ["Send"] = "Senden",
        ["Cancel"] = "Abbrechen",
        ["Send clipboard"] = "Zwischenablage senden",
        ["Text"] = "Text",
        ["Send to active tab"] = "An aktiven Tab senden",
        ["Close"] = "Schliessen",
        ["Server"] = "Server",
        ["Server icon"] = "Server-Icon",
        ["Default by connection type"] = "Standard nach Verbindungstyp",
        ["Protocol"] = "Protokoll",
        ["Host or IP"] = "Host oder IP",
        ["Target user"] = "Ziel-Benutzer",
        ["Target password"] = "Ziel-Passwort",
        ["Leave password empty to keep it unchanged."] = "Passwort leer lassen, um es unveraendert zu lassen.",
        ["Domain (RDP/SMB)"] = "Domain (RDP/SMB)",
        ["File start path / SMB share"] = "Datei-Startpfad / SMB-Share",
        ["RDP keyboard layout"] = "RDP-Tastatur-Layout",
        ["SSH font size"] = "SSH-Schriftgroesse",
        ["Ignore RDP certificate"] = "RDP-Zertifikat ignorieren",
        ["Notes"] = "Notizen",
        ["New Connection"] = "Neue Verbindung",
        ["No shared connections."] = "Keine freigegebenen Verbindungen.",
        ["Language"] = "Sprache",
        ["Website"] = "Website",
        ["Website (Beta)"] = "Website (Beta)",
        ["Ping"] = "Ping",
        ["Lookup"] = "Lookup",
        ["Port check"] = "Portpruefung",
        ["Count"] = "Anzahl",
        ["Interval"] = "Intervall",
        ["Timeout"] = "Zeitlimit",
        ["Ports"] = "Ports",
        ["URL"] = "URL",
        ["Start"] = "Starten",
        ["Output"] = "Ausgabe",
        ["Running"] = "Laeuft",
        ["Complete"] = "Fertig",
        ["Failed"] = "Fehlgeschlagen",
        ["Aborted"] = "Abgebrochen",
        ["Downloaded"] = "Heruntergeladen",
        ["Bytes"] = "Bytes",
        ["Speed"] = "Geschwindigkeit",
        ["Duration"] = "Dauer",
        ["Canonical name"] = "Kanonischer Name",
        ["Resolved addresses"] = "Aufgeloeste Adressen",
        ["Status code"] = "Statuscode",
        ["Content type"] = "Inhaltstyp",
        ["Content length"] = "Inhaltslaenge",
        ["Open website"] = "Website oeffnen",
        ["Open in new tab"] = "In neuem Tab oeffnen",
        ["Website proxy"] = "Website-Proxy",
        ["Opening website"] = "Website wird geoeffnet",
        ["Website loaded"] = "Website geladen",
        ["Forward"] = "Vorwaerts",
        ["Logout"] = "Logout",
        ["Username or password is invalid."] = "Benutzername oder Passwort stimmt nicht.",
        ["Invalid data"] = "Ungueltige Daten",
        ["Name and host are required."] = "Name und Host sind erforderlich.",
        ["User already exists"] = "Benutzer existiert",
        ["This username is already taken."] = "Dieser Benutzername ist bereits vergeben.",
        ["Password too short"] = "Passwort zu kurz",
        ["The password must be at least 10 characters long."] = "Das Passwort muss mindestens 10 Zeichen haben.",
        ["Not saved"] = "Nicht gespeichert",
        ["The last active administrator cannot be removed."] = "Der letzte aktive Administrator kann nicht entzogen werden.",
        ["Not deleted"] = "Nicht geloescht",
        ["You cannot delete your own user."] = "Du kannst deinen eigenen Benutzer nicht loeschen.",
        ["This server is not shared with you."] = "Dieser Server ist nicht freigegeben.",
        ["The connection could not be started."] = "Die Verbindung konnte nicht gestartet werden.",
        ["No file selected."] = "Keine Datei ausgewaehlt.",
        ["Folder name is missing."] = "Ordnername fehlt.",
        ["File name is missing."] = "Dateiname fehlt.",
        ["This server is not a file connection."] = "Dieser Server ist keine Dateiverbindung.",
        ["File viewer"] = "Dateiansicht",
        ["Open raw"] = "Rohdatei oeffnen",
        ["Back to Matgate"] = "Zurueck zu Matgate",
        ["No preview available"] = "Keine Vorschau verfuegbar",
        ["File access failed"] = "Dateizugriff fehlgeschlagen"
    };

    public string Login(HttpContext context, string? error = null)
    {
        var errorHtml = string.IsNullOrWhiteSpace(error)
            ? ""
            : $"""<div class="notice error">{E(error)}</div>""";

        var body = $$"""
            <section class="auth-panel">
                <div>
                    <p class="eyebrow">Matgate</p>
                    <h1>{{T(context, "Home Network Gateway")}}</h1>
                    <p class="muted">{{T(context, "Local login for RDP, VNC, SSH, websites and file access in your home network.")}}</p>
                </div>
                <form method="post" action="/login" class="stack">
                    {{errorHtml}}
                    <label>{{T(context, "Username")}}
                        <input name="username" autocomplete="username" required autofocus>
                    </label>
                    <label>{{T(context, "Password")}}
                        <input name="password" type="password" autocomplete="current-password" required>
                    </label>
                    <button type="submit" class="primary">{{Icon("key")}}{{T(context, "Sign in")}}</button>
                </form>
            </section>
            """;

        return Layout(context, null, "Login", body);
    }

    public string Dashboard(HttpContext context, MatgateUser user, IReadOnlyList<ServerEndpoint> servers)
    {
        var cards = servers.Count == 0
            ? $"""<div class="empty">{T(context, "No global servers yet. An administrator can create servers and grant access.")}</div>"""
            : string.Join("", servers.Select(server => $$"""
                <article class="card">
                    <div class="row split">
                        <div class="server-title">
                            {{ServerIcon(server)}}
                            <div>
                                <span class="badge">{{E(ServerProtocolLabel(server.Protocol))}}</span>
                                {{ServerScopeBadge(context, server, currentUser: user)}}
                                <h2>{{E(server.Name)}}</h2>
                            </div>
                        </div>
                        <a class="button primary" href="/sessions?open={{server.Id}}">{{Icon("play")}}{{(server.Protocol == ServerProtocol.Website ? T(context, "Open") : T(context, "Connect"))}}</a>
                    </div>
                    <p class="target">{{E(ServerTargetValue(server))}}</p>
                    <p class="muted">{{E(server.Notes)}}</p>
                </article>
                """));

        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "Home")}}</p>
                    <h1>{{T(context, "Connections")}}</h1>
                </div>
            </section>
            <section class="grid">
                {{cards}}
            </section>
            """;

        return Layout(context, user, T(context, "Home"), body);
    }

    public string Users(HttpContext context, MatgateUser currentUser, IReadOnlyList<MatgateUser> users)
    {
        var rows = users.Count == 0
            ? $"""<tr><td colspan="4" class="muted">{T(context, "No users created yet.")}</td></tr>"""
            : string.Join("", users.OrderBy(user => user.UserName).Select(user => $$"""
                <tr>
                    <td><a href="/admin/users/{{user.Id}}">{{E(user.UserName)}}</a></td>
                    <td>{{E(user.DisplayName)}}</td>
                    <td>{{RoleLabels(context, user)}}</td>
                    <td>{{(user.IsEnabled ? T(context, "Active") : T(context, "Locked"))}}</td>
                </tr>
                """));

        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "Administration")}}</p>
                    <h1>{{T(context, "Users")}}</h1>
                </div>
                <a class="button primary" href="/admin/users/new">{{Icon("plus")}}{{T(context, "Create user")}}</a>
            </section>
            <section class="panel">
                <h2>{{T(context, "Existing users")}}</h2>
                <div class="table-wrap">
                    <table>
                        <thead><tr><th>{{T(context, "Name")}}</th><th>{{T(context, "Display")}}</th><th>{{T(context, "Roles")}}</th><th>{{T(context, "Status")}}</th></tr></thead>
                        <tbody>{{rows}}</tbody>
                    </table>
                </div>
            </section>
            """;

        return Layout(context, currentUser, T(context, "Users"), body);
    }

    public string UserCreate(HttpContext context, MatgateUser currentUser)
    {
        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "User")}}</p>
                    <h1>{{T(context, "Create user")}}</h1>
                </div>
                <a class="button" href="/admin/users">{{Icon("users")}}{{T(context, "Users")}}</a>
            </section>
            <form method="post" action="/admin/users" class="stack">
                {{Csrf(context)}}
                <section class="panel">
                    <h2>{{T(context, "Profile")}}</h2>
                    <div class="form-grid">
                        <label>{{T(context, "Username")}}
                            <input name="username" required minlength="3" maxlength="64">
                        </label>
                        <label>{{T(context, "Display name")}}
                            <input name="displayName">
                        </label>
                        <label>{{T(context, "Initial password")}}
                            <input name="password" type="password" minlength="10" required>
                        </label>
                    <label>{{T(context, "Preferred language")}}
                        <select name="preferredLanguage">
                            {{LanguageOptions(context, "en")}}
                        </select>
                    </label>
                    <label>{{T(context, "Preferred theme")}}
                        <select name="preferredTheme">
                            {{ThemeOptions(context, "system")}}
                        </select>
                    </label>
                </div>
            </section>
                <section class="panel">
                    <h2>{{T(context, "Permissions")}}</h2>
                    <div class="form-grid">
                        <label class="check"><input type="checkbox" name="isAdmin"> {{T(context, "Administrator")}}</label>
                        <label class="check"><input type="checkbox" name="canManageServers"> {{T(context, "Manage servers")}}</label>
                        <label class="check"><input type="checkbox" name="canCreateServers"> {{T(context, "Can create own servers")}}</label>
                    </div>
                </section>
                <div class="actions">
                    <button type="submit" class="primary">{{Icon("plus")}}{{T(context, "Create")}}</button>
                </div>
            </form>
            """;

        return Layout(context, currentUser, T(context, "Create user"), body);
    }

    public string UserDetail(
        HttpContext context,
        MatgateUser currentUser,
        MatgateUser editedUser,
        IReadOnlyList<ServerEndpoint> servers)
    {
        var sharedServers = servers.Where(server => server.OwnerUserId is null).OrderBy(server => server.Name).ToList();
        var allServersChecked = editedUser.IsAdmin || editedUser.ServerAccessAll;
        var accessRows = sharedServers.Count == 0
            ? $"""<tr><td colspan="4" class="muted">{T(context, "No global servers created yet.")}</td></tr>"""
            : string.Join("", sharedServers.Select(server =>
            {
                var isChecked = editedUser.IsAdmin || editedUser.ServerAccessAll || editedUser.ServerAccess.Contains(server.Id);
                var disabled = editedUser.IsAdmin ? " disabled" : "";
                return $$"""
                    <tr>
                        <td class="access-select-cell">
                            <input type="checkbox" name="serverIds" value="{{server.Id}}"{{Checked(isChecked)}}{{disabled}}>
                        </td>
                        <td>
                            <span class="server-name-cell">{{ServerIcon(server, "small")}}<a href="/admin/servers/{{server.Id}}">{{E(server.Name)}}</a></span>
                        </td>
                        <td><span class="badge">{{E(ServerProtocolLabel(server.Protocol))}}</span></td>
                        <td>{{E(ServerTargetValue(server))}}</td>
                    </tr>
                    """;
            }));

        var deleteButton = currentUser.Id == editedUser.Id
            ? $"""<button type="button" disabled>{Icon("trash")}{T(context, "Cannot delete own user")}</button>"""
            : $"""<button type="submit" class="danger">{Icon("trash")}{T(context, "Delete user")}</button>""";

        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "Users")}}</p>
                    <h1>{{E(editedUser.UserName)}}</h1>
                </div>
                <a class="button" href="/admin/users">{{T(context, "Back")}}</a>
            </section>
            <form method="post" action="/admin/users/{{editedUser.Id}}/update" class="stack">
                {{Csrf(context)}}
                <section class="panel">
                    <h2>{{T(context, "Profile")}}</h2>
                    <div class="form-grid">
                        <label>{{T(context, "Display name")}}
                            <input name="displayName" value="{{A(editedUser.DisplayName)}}">
                        </label>
                        <label>{{T(context, "Preferred language")}}
                            <select name="preferredLanguage">
                                {{LanguageOptions(context, editedUser.PreferredLanguage)}}
                            </select>
                        </label>
                        <label>{{T(context, "Preferred theme")}}
                            <select name="preferredTheme">
                                {{ThemeOptions(context, editedUser.PreferredTheme)}}
                            </select>
                        </label>
                        <label class="check"><input type="checkbox" name="isEnabled"{{Checked(editedUser.IsEnabled)}}> {{T(context, "Enabled")}}</label>
                    </div>
                </section>
                <section class="panel">
                    <h2>{{T(context, "Permissions")}}</h2>
                    <div class="form-grid">
                        <label class="check"><input type="checkbox" name="isAdmin"{{Checked(editedUser.IsAdmin)}}> {{T(context, "Administrator")}}</label>
                        <label class="check"><input type="checkbox" name="canManageServers"{{Checked(editedUser.CanManageServers)}}> {{T(context, "Manage servers")}}</label>
                        <label class="check"><input type="checkbox" name="canCreateServers"{{Checked(editedUser.CanCreateServers)}}> {{T(context, "Can create own servers")}}</label>
                    </div>
                </section>
                <div class="actions"><button type="submit" class="primary">{{Icon("save")}}{{T(context, "Save")}}</button></div>
            </form>
            <section class="panel">
                <h2>{{T(context, "Server access")}}</h2>
                <form method="post" action="/admin/users/{{editedUser.Id}}/access" class="stack">
                    {{Csrf(context)}}
                    <label class="check"><input type="checkbox" name="allServers"{{Checked(allServersChecked)}}{{(editedUser.IsAdmin ? " disabled" : "")}}> {{T(context, "All global servers (*)")}}</label>
                    <p class="muted">{{T(context, "Global servers only. Own servers are automatically available to the owner.")}}</p>
                    <div class="table-wrap">
                        <table class="access-table">
                            <thead>
                                <tr>
                                    <th class="access-select-cell">{{T(context, "Access")}}</th>
                                    <th>{{T(context, "Name")}}</th>
                                    <th>{{T(context, "Type")}}</th>
                                    <th>{{T(context, "Target")}}</th>
                                </tr>
                            </thead>
                            <tbody>{{accessRows}}</tbody>
                        </table>
                    </div>
                    <div class="actions"><button type="submit" class="primary"{{(editedUser.IsAdmin ? " disabled" : "")}}>{{Icon("save")}}{{T(context, "Save access")}}</button></div>
                    <p class="muted">{{T(context, "Administrators always see all servers.")}}</p>
                </form>
            </section>
            <section class="panel">
                <h2>{{T(context, "Password")}}</h2>
                <form method="post" action="/admin/users/{{editedUser.Id}}/password" class="form-grid">
                    {{Csrf(context)}}
                    <label>{{T(context, "New password")}}
                        <input name="password" type="password" minlength="10" required>
                    </label>
                    <div class="actions"><button type="submit" class="primary">{{Icon("key")}}{{T(context, "Set password")}}</button></div>
                </form>
            </section>
            <section class="panel danger-zone">
                <h2>{{T(context, "Remove")}}</h2>
                <form method="post" action="/admin/users/{{editedUser.Id}}/delete">
                    {{Csrf(context)}}
                    {{deleteButton}}
                </form>
            </section>
            """;

        return Layout(context, currentUser, Language(context) == "de" ? "Benutzer bearbeiten" : "Edit user", body);
    }

    public string Servers(HttpContext context, MatgateUser currentUser, IReadOnlyList<ServerEndpoint> servers, IReadOnlyList<MatgateUser> users)
    {
        var visibleServers = servers
            .OrderBy(server => server.OwnerUserId is null ? 0 : 1)
            .ThenBy(server => server.Name)
            .ToList();

        var emptyMessage = currentUser.IsAdmin || (currentUser.CanManageServers && currentUser.CanCreateServers)
            ? T(context, "No servers created yet.")
            : currentUser.CanManageServers
                ? T(context, "No global servers created yet.")
                : T(context, "No own servers created yet.");

        var createButton = currentUser.IsAdmin || currentUser.CanManageServers || currentUser.CanCreateServers
            ? $$"""
                <a class="button primary" href="/admin/servers/new">{{Icon("plus")}}{{T(context, "Create server")}}</a>
                """
            : "";

        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "Administration")}}</p>
                    <h1>{{T(context, "Servers")}}</h1>
                </div>
                {{createButton}}
            </section>
            {{ManagedServersSection(context, T(context, "Servers"), visibleServers, emptyMessage, users)}}
            """;

        return Layout(context, currentUser, T(context, "Servers"), body);
    }

    public string ServerDetail(HttpContext context, MatgateUser currentUser, ServerEndpoint server, IReadOnlyList<MatgateUser> users)
    {
        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "Server")}}</p>
                    <h1>{{E(server.Name)}}</h1>
                    <p class="muted">{{ServerScopeText(context, server, users)}}{{(string.IsNullOrWhiteSpace(server.FolderName) ? "" : $" ÃƒÆ’Ã¢â‚¬Å¡Ãƒâ€šÃ‚Â· {ServerFolderBadge(context, server)}")}}</p>
                </div>
                <a class="button" href="/admin/servers">{{Icon("server")}}{{T(context, "Servers")}}</a>
            </section>
            <form id="server-edit-form" method="post" action="/admin/servers/{{server.Id}}" class="stack server-form" data-server-form>
                {{Csrf(context)}}
                {{ServerFields(context, currentUser, server)}}
            </form>
            {{ServerFormScript()}}
            <section class="panel danger-zone">
                <div class="server-form-footer">
                    <div>
                        <h2>{{T(context, "Remove")}}</h2>
                    </div>
                    <div class="actions server-form-actions">
                        <button type="submit" form="server-edit-form" class="primary">{{Icon("save")}}{{T(context, "Save")}}</button>
                        <form method="post" action="/admin/servers/{{server.Id}}/delete" class="server-delete-form">
                            {{Csrf(context)}}
                            <button type="submit" class="danger">{{Icon("trash")}}{{T(context, "Delete server")}}</button>
                        </form>
                    </div>
                </div>
            </section>
            """;

        return Layout(context, currentUser, Language(context) == "de" ? "Server bearbeiten" : "Edit server", body);
    }

    public string ServerCreate(HttpContext context, MatgateUser currentUser)
    {
        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "Server")}}</p>
                    <h1>{{T(context, "Create server")}}</h1>
                </div>
                <a class="button" href="/admin/servers">{{Icon("server")}}{{T(context, "Servers")}}</a>
            </section>
            <form method="post" action="/admin/servers" class="stack server-form" data-server-form>
                {{Csrf(context)}}
                {{ServerFields(context, currentUser)}}
                <div class="actions"><button type="submit" class="primary">{{Icon("plus")}}{{T(context, "Create")}}</button></div>
            </form>
            {{ServerFormScript()}}
            """;

        return Layout(context, currentUser, T(context, "Create server"), body);
    }

    public string Account(HttpContext context, MatgateUser user, IReadOnlyList<ServerEndpoint> servers)
    {
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
        var favoriteServers = servers
            .Where(server => IsFavoriteServer(user, server.Id))
            .OrderBy(server => string.IsNullOrWhiteSpace(server.FolderName) ? 1 : 0)
            .ThenBy(server => server.FolderName)
            .ThenBy(server => server.Name)
            .ToList();
        var favoriteRows = favoriteServers.Count == 0
            ? $"""<tr><td colspan="4" class="muted">{T(context, "No favorite servers yet.")}</td></tr>"""
            : string.Join("", favoriteServers.Select(server => $$"""
                <tr>
                    <td><span class="server-name-cell">{{ServerIcon(server, "small")}}<span>{{E(server.Name)}}</span></span></td>
                    <td>{{(string.IsNullOrWhiteSpace(server.FolderName) ? "<span class=\"muted\">-</span>" : ServerFolderBadge(context, server))}}</td>
                    <td><span class="badge">{{E(ServerProtocolLabel(server.Protocol))}}</span></td>
                    <td class="table-actions">{{FavoriteToggleForm(context, user, server, "/account")}}</td>
                </tr>
                """));
        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "Account")}}</p>
                    <h1>{{E(displayName)}}</h1>
                </div>
                <a class="button" href="/">{{Icon("home")}}{{T(context, "Home")}}</a>
            </section>
            <section class="panel">
                <h2>{{T(context, "Profile")}}</h2>
                <form method="post" action="/account" class="form-grid">
                    {{Csrf(context)}}
                    <label>{{T(context, "Display name")}}
                        <input name="displayName" value="{{A(user.DisplayName)}}">
                    </label>
                    <label>{{T(context, "Preferred language")}}
                        <select name="preferredLanguage">
                            {{LanguageOptions(context, user.PreferredLanguage)}}
                        </select>
                    </label>
                    <label>{{T(context, "Preferred theme")}}
                        <select name="preferredTheme">
                            {{ThemeOptions(context, user.PreferredTheme)}}
                        </select>
                    </label>
                    <div class="actions"><button type="submit" class="primary">{{Icon("save")}}{{T(context, "Save")}}</button></div>
                </form>
            </section>
            <section class="panel">
                <h2>{{T(context, "Favorite servers")}}</h2>
                <p class="muted">{{T(context, "Favorites are stored per user.")}}</p>
                <div class="table-wrap">
                    <table>
                        <thead>
                            <tr>
                                <th>{{T(context, "Name")}}</th>
                                <th>{{T(context, "Folder")}}</th>
                                <th>{{T(context, "Type")}}</th>
                                <th></th>
                            </tr>
                        </thead>
                        <tbody>{{favoriteRows}}</tbody>
                    </table>
                </div>
            </section>
            """;

        return Layout(context, user, T(context, "Account"), body);
    }

    public string About(HttpContext context, MatgateUser user)
    {
        var version = ApplicationVersion();
        var body = AboutBody(context, version);

        return Layout(context, user, T(context, "About"), body, "viewer-main");
    }

    public string Tools(HttpContext context, MatgateUser user)
    {
        var toolText = JsonSerializer.Serialize(new
        {
            ready = T(context, "Ready"),
            running = T(context, "Running"),
            complete = T(context, "Complete"),
            failed = T(context, "Failed"),
            aborted = T(context, "Aborted")
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var body = $$"""
            <section class="page-head">
                <div>
                    <p class="eyebrow">{{T(context, "Tools")}}</p>
                    <h1>{{T(context, "Network tools")}}</h1>
                    <p class="muted">{{T(context, "Live output streams below.")}}</p>
                </div>
            </section>
            <section class="panel tool-panel">
                <form class="tool-form" data-tools-form>
                    {{Csrf(context)}}
                    <div class="tool-select-row">
                        <label class="tool-select-label">{{T(context, "Select tool")}}
                            <select name="tool" data-tool-select>
                                <option value="ping" data-endpoint="/api/tools/ping" data-title="{{A(T(context, "Ping"))}}" data-description="{{A(T(context, "Reachability and latency check."))}}" selected>{{T(context, "Ping")}}</option>
                                <option value="lookup" data-endpoint="/api/tools/lookup" data-title="{{A(T(context, "Lookup"))}}" data-description="{{A(T(context, "Resolve hostnames and addresses."))}}">{{T(context, "Lookup")}}</option>
                                <option value="port-check" data-endpoint="/api/tools/port-check" data-title="{{A(T(context, "Port check"))}}" data-description="{{A(T(context, "Test TCP ports live."))}}">{{T(context, "Port check")}}</option>
                                <option value="download" data-endpoint="/api/tools/download" data-title="{{A(T(context, "Download"))}}" data-description="{{A(T(context, "Stream a file and watch transfer speed."))}}">{{T(context, "Download")}}</option>
                            </select>
                        </label>
                        <span class="badge tool-status" data-tool-status>{{T(context, "Ready")}}</span>
                    </div>
                    <div class="tool-summary" aria-live="polite">
                        <div class="tool-summary-icon-stack">
                            <span class="tool-summary-icon" data-tool-icon="ping">{{Icon("refresh")}}</span>
                            <span class="tool-summary-icon" data-tool-icon="lookup" hidden>{{Icon("globe")}}</span>
                            <span class="tool-summary-icon" data-tool-icon="port-check" hidden>{{Icon("server")}}</span>
                            <span class="tool-summary-icon" data-tool-icon="download" hidden>{{Icon("download")}}</span>
                        </div>
                        <div class="tool-summary-copy">
                            <p class="eyebrow">{{T(context, "Tool")}}</p>
                            <h2 data-tool-title>{{T(context, "Ping")}}</h2>
                            <p class="muted" data-tool-description>{{T(context, "Reachability and latency check.")}}</p>
                        </div>
                    </div>
                    <div class="tool-fields">
                        <section class="tool-field-group" data-tool-fields="ping">
                            <div class="form-grid tool-form-grid">
                                <label>{{T(context, "Host or IP")}}
                                    <input name="host" required placeholder="192.168.1.1">
                                </label>
                                <label>{{T(context, "Count")}}
                                    <input name="count" type="number" min="1" max="30" value="4">
                                </label>
                                <label>{{T(context, "Interval")}} (ms)
                                    <input name="intervalMs" type="number" min="0" max="60000" value="1000">
                                </label>
                                <label>{{T(context, "Timeout")}} (ms)
                                    <input name="timeoutMs" type="number" min="100" max="60000" value="1000">
                                </label>
                            </div>
                        </section>
                        <section class="tool-field-group" data-tool-fields="lookup" hidden>
                            <div class="form-grid tool-form-grid">
                                <label>{{T(context, "Host or IP")}}
                                    <input name="host" required placeholder="nas.local">
                                </label>
                            </div>
                        </section>
                        <section class="tool-field-group" data-tool-fields="port-check" hidden>
                            <div class="form-grid tool-form-grid">
                                <label>{{T(context, "Host or IP")}}
                                    <input name="host" required placeholder="192.168.1.10">
                                </label>
                                <label>{{T(context, "Ports")}}
                                    <input name="ports" required placeholder="80,443,3389">
                                </label>
                                <label>{{T(context, "Timeout")}} (ms)
                                    <input name="timeoutMs" type="number" min="100" max="60000" value="1000">
                                </label>
                            </div>
                        </section>
                        <section class="tool-field-group" data-tool-fields="download" hidden>
                            <div class="form-grid tool-form-grid">
                                <label>{{T(context, "URL")}}
                                    <input name="url" required placeholder="http://192.168.1.10/bigfile.iso">
                                </label>
                            </div>
                        </section>
                    </div>
                    <div class="actions tool-actions">
                        <button type="submit" class="primary">{{Icon("play")}}<span>{{T(context, "Start")}}</span></button>
                    </div>
                    <pre class="tool-output" data-tool-output>{{T(context, "Ready")}}</pre>
                </form>
            </section>
            <script>
                (() => {
                    const toolText = {{toolText}};
                    const form = document.querySelector('[data-tools-form]');
                    if (!form) {
                        return;
                    }

                    const select = form.querySelector('[data-tool-select]');
                    const title = form.querySelector('[data-tool-title]');
                    const description = form.querySelector('[data-tool-description]');
                    const status = form.querySelector('[data-tool-status]');
                    const output = form.querySelector('[data-tool-output]');
                    const submitButton = form.querySelector('button[type="submit"]');
                    const toolIcons = new Map(Array.from(form.querySelectorAll('[data-tool-icon]')).map(element => [element.getAttribute('data-tool-icon') || '', element]));
                    const toolGroups = Array.from(form.querySelectorAll('[data-tool-fields]'));
                    let activeController = null;
                    let activeTool = 'ping';

                    function setStatus(value) {
                        if (status) {
                            status.textContent = value;
                        }
                    }

                    function currentOption() {
                        if (!select || !select.selectedOptions || select.selectedOptions.length === 0) {
                            return null;
                        }

                        return select.selectedOptions[0];
                    }

                    function syncTool() {
                        const option = currentOption();
                        const value = option?.value || 'ping';
                        const endpoint = option?.dataset.endpoint || '/api/tools/ping';
                        activeTool = value;
                        form.dataset.toolEndpoint = endpoint;

                        if (activeController) {
                            activeController.abort();
                            activeController = null;
                        }

                        if (title) {
                            title.textContent = option?.dataset.title || option?.textContent?.trim() || '';
                        }

                        if (description) {
                            description.textContent = option?.dataset.description || '';
                        }

                        if (output) {
                            output.textContent = toolText.ready || 'Ready';
                        }

                        setStatus(toolText.ready || 'Ready');

                        toolIcons.forEach((element, key) => {
                            element.hidden = key !== value;
                        });

                        toolGroups.forEach(group => {
                            const active = (group.getAttribute('data-tool-fields') || '') === value;
                            group.hidden = !active;
                            group.querySelectorAll('input, select, textarea').forEach(control => {
                                control.disabled = !active;
                            });
                        });
                    }

                    async function streamResponse(response, outputElement) {
                        const reader = response.body?.getReader();
                        if (!reader) {
                            outputElement.textContent = await response.text();
                            return;
                        }

                        const decoder = new TextDecoder();
                        outputElement.textContent = '';

                        while (true) {
                            const { value, done } = await reader.read();
                            if (done) {
                                break;
                            }

                            outputElement.textContent += decoder.decode(value, { stream: true });
                            outputElement.scrollTop = outputElement.scrollHeight;
                        }

                        outputElement.textContent += decoder.decode();
                        outputElement.scrollTop = outputElement.scrollHeight;
                    }

                    async function runTool() {
                        const endpoint = form.dataset.toolEndpoint || '';
                        if (!endpoint || !output || !submitButton) {
                            return;
                        }

                        if (activeController) {
                            activeController.abort();
                        }

                        const runToolValue = activeTool;
                        const controller = new AbortController();
                        activeController = controller;

                        output.textContent = '';
                        setStatus(toolText.running || 'Running');
                        submitButton.disabled = true;

                        try {
                            const response = await fetch(endpoint, {
                                method: 'POST',
                                body: new FormData(form),
                                signal: controller.signal,
                                cache: 'no-store'
                            });

                            if (!response.ok) {
                                const text = await response.text().catch(() => '');
                                output.textContent = text || toolText.failed || 'Failed';
                                setStatus(toolText.failed || 'Failed');
                                return;
                            }

                            await streamResponse(response, output);
                            setStatus(toolText.complete || 'Complete');
                        }
                        catch (error) {
                            if (controller.signal.aborted) {
                                if (runToolValue !== activeTool) {
                                    return;
                                }

                                setStatus(toolText.aborted || 'Aborted');
                                return;
                            }

                            output.textContent = error instanceof Error ? error.message : (toolText.failed || 'Failed');
                            setStatus(toolText.failed || 'Failed');
                        }
                        finally {
                            submitButton.disabled = false;
                            if (activeController === controller) {
                                activeController = null;
                            }
                        }
                    }

                    if (select) {
                        select.addEventListener('change', syncTool);
                    }

                    form.addEventListener('submit', event => {
                        event.preventDefault();
                        runTool();
                    });

                    syncTool();
                })();
            </script>
            """;

        return Layout(context, user, T(context, "Tools"), body);
    }    private static string AboutBody(HttpContext context, string version)
    {
        return $$"""
            <section class="about-page">
                <div class="page-head about-head">
                    <div class="about-copy">
                        <p class="eyebrow">{{T(context, "About")}}</p>
                        <h1>{{T(context, "About Matgate")}}</h1>
                        <p class="muted">{{T(context, "Local login for RDP, VNC, SSH, websites and file access in your home network.")}}</p>
                    </div>
                    <div class="about-brand">{{Logo()}}</div>
                </div>
                <section class="panel about-card">
                    <div class="row split about-meta">
                        <strong>MATGATE {{E(version)}}</strong>
                        <span class="badge">MIT</span>
                    </div>
                    <p class="muted">{{T(context, "Matgate keeps browser-based sessions, file access and admin pages in one place.")}}</p>
                </section>
            </section>
            """;
    }

    private static string ManagedServersSection(
        HttpContext context,
        string title,
        IReadOnlyList<ServerEndpoint> servers,
        string emptyMessage,
        IReadOnlyList<MatgateUser> users)
    {
        var rows = servers.Count == 0
            ? $"""<tr><td colspan="6" class="muted">{E(emptyMessage)}</td></tr>"""
            : string.Join("", servers.Select(server => $$"""
                <tr>
                    <td><span class="server-name-cell">{{ServerIcon(server, "small")}}<a href="/admin/servers/{{server.Id}}">{{E(server.Name)}}</a></span></td>
                    <td>{{ServerScopeBadge(context, server, users)}}</td>
                    <td>{{(string.IsNullOrWhiteSpace(server.FolderName) ? "<span class=\"muted\">-</span>" : ServerFolderBadge(context, server))}}</td>
                    <td><span class="badge">{{E(ServerProtocolLabel(server.Protocol))}}</span></td>
                    <td>{{E(ServerTargetValue(server))}}</td>
                    <td>{{(server.IsEnabled ? T(context, "Active") : T(context, "Off"))}}</td>
                </tr>
                """));

        return $$"""
            <section class="home-management">
                <section class="panel">
                    <h2>{{E(title)}}</h2>
                    <div class="table-wrap">
                        <table>
                            <thead><tr><th>{{T(context, "Name")}}</th><th>{{T(context, "Scope")}}</th><th>{{T(context, "Folder")}}</th><th>{{T(context, "Type")}}</th><th>{{T(context, "Target")}}</th><th>{{T(context, "Status")}}</th></tr></thead>
                            <tbody>{{rows}}</tbody>
                        </table>
                    </div>
                </section>
                </section>
            """;
    }

    private static string ConnectionChoiceSections(
        HttpContext context,
        MatgateUser user,
        IReadOnlyList<ServerEndpoint> servers,
        bool includeEditButtons)
    {
        if (servers.Count == 0)
        {
            return $"""<div class="empty">{T(context, includeEditButtons ? "No own servers created yet." : "No shared connections.")}</div>""";
        }

        var returnUrl = $"{context.Request.Path}{context.Request.QueryString}";
        var favoriteIds = user.FavoriteServerIds?.ToHashSet() ?? new HashSet<Guid>();
        var favoriteServers = servers
            .Where(server => favoriteIds.Contains(server.Id))
            .OrderBy(server => string.IsNullOrWhiteSpace(server.FolderName) ? 1 : 0)
            .ThenBy(server => server.FolderName)
            .ThenBy(server => server.Name)
            .ToList();
        var remainingServers = servers
            .Where(server => !favoriteIds.Contains(server.Id))
            .ToList();

        var sections = new List<string>();
        if (favoriteServers.Count > 0)
        {
            sections.Add(ConnectionChoiceSection(
                context,
                user,
                T(context, "Favorites"),
                Icon("star"),
                favoriteServers,
                includeEditButtons,
                returnUrl,
                T(context, "Favorites")));
        }

        var folderGroups = remainingServers
            .GroupBy(server => string.IsNullOrWhiteSpace(server.FolderName) ? "" : server.FolderName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => string.IsNullOrWhiteSpace(group.Key) ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (folderGroups.Count == 1 && string.IsNullOrWhiteSpace(folderGroups[0].Key))
        {
            sections.Add(ConnectionChoiceSection(
                context,
                user,
                T(context, "Folder"),
                Icon("folder"),
                folderGroups[0].ToList(),
                includeEditButtons,
                returnUrl,
                T(context, "Unsorted")));
        }
        else
        {
            foreach (var group in folderGroups)
            {
                var groupTitle = string.IsNullOrWhiteSpace(group.Key)
                    ? T(context, "Unsorted")
                    : group.Key;
                var groupIcon = string.IsNullOrWhiteSpace(group.Key)
                    ? Icon("folder")
                    : ServerFolderIcon(group.FirstOrDefault());

                sections.Add(ConnectionChoiceSection(
                    context,
                    user,
                    T(context, "Folder"),
                    groupIcon,
                    group.ToList(),
                    includeEditButtons,
                    returnUrl,
                    groupTitle));
            }
        }

        return string.Join("", sections);
    }

    private static string ConnectionChoiceSection(
        HttpContext context,
        MatgateUser user,
        string eyebrow,
        string iconHtml,
        IReadOnlyList<ServerEndpoint> servers,
        bool includeEditButtons,
        string returnUrl,
        string title)
    {
        var cards = string.Join("", servers.Select(server => ConnectionChoiceCard(context, user, server, includeEditButtons, returnUrl)));
        return $$"""
            <section class="connection-choice-section">
                <div class="row split connection-choice-section-head">
                    <div>
                        <p class="eyebrow">{{E(eyebrow)}}</p>
                        <h2>{{iconHtml}}<span>{{E(title)}}</span></h2>
                    </div>
                    <span class="badge">{{servers.Count}}</span>
                </div>
                <section class="connection-picker-grid">
                    {{cards}}
                </section>
            </section>
            """;
    }

    private static string ConnectionChoiceCard(
        HttpContext context,
        MatgateUser user,
        ServerEndpoint server,
        bool includeEditButtons,
        string returnUrl)
    {
        var actions = new List<string>
        {
            $"""<button type="button" class="primary workspace-open-button" data-server-id="{server.Id}">{Icon("play")}{T(context, "Open")}</button>""",
            FavoriteToggleForm(context, user, server, returnUrl)
        };

        if (includeEditButtons)
        {
            actions.Insert(1, $"""<a class="button" href="/admin/servers/{server.Id}">{Icon("edit")}{T(context, "Edit")}</a>""");
        }

        return $$"""
            <article class="connection-choice">
                <div>
                    <div class="server-title">
                        {{ServerIcon(server)}}
                        <div class="connection-choice-copy">
                            <span class="badge">{{E(ServerProtocolLabel(server.Protocol))}}</span>
                            {{ServerFolderBadge(context, server)}}
                            {{(server.OwnerUserId is null ? "" : ServerScopeBadge(context, server, currentUser: user))}}
                            <h2>{{E(server.Name)}}</h2>
                        </div>
                    </div>
                    <p class="target">{{E(ServerTargetValue(server))}}</p>
                    <p class="muted">{{E(server.Notes)}}</p>
                </div>
                <div class="connection-choice-actions">
                    {{string.Join("", actions)}}
                </div>
            </article>
            """;
    }

    private static string FavoriteToggleForm(
        HttpContext context,
        MatgateUser user,
        ServerEndpoint server,
        string returnUrl)
    {
        var isFavorite = IsFavoriteServer(user, server.Id);
        var label = isFavorite ? T(context, "Remove from favorites") : T(context, "Add to favorites");
        return $$"""
            <form method="post" action="/account/favorites/{{server.Id}}/toggle" class="favorite-toggle-form">
                {{Csrf(context)}}
                <input type="hidden" name="returnUrl" value="{{A(returnUrl)}}">
                <button type="submit" class="favorite-toggle{{(isFavorite ? " active" : "")}}" title="{{A(label)}}" aria-label="{{A(label)}}">{{Icon("star")}}</button>
            </form>
            """;
    }

    private static string ServerFolderBadge(HttpContext context, ServerEndpoint server)
    {
        var folderName = Clean(server.FolderName, "");
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return "";
        }

        return $"""<span class="badge server-folder-badge">{ServerFolderIcon(server)}<span>{E(folderName)}</span></span>""";
    }

    private static string ServerFolderIcon(ServerEndpoint? server)
    {
        var iconKey = server is null ? "" : ServerEndpoint.NormalizeIconKey(server.FolderIconKey);
        if (string.IsNullOrWhiteSpace(iconKey))
        {
            iconKey = "folder";
        }

        return Icon(iconKey);
    }

    private static bool IsFavoriteServer(MatgateUser user, Guid serverId)
    {
        return user.FavoriteServerIds?.Contains(serverId) == true;
    }

    public string Message(HttpContext context, MatgateUser? user, string title, string message)
    {
        var body = $$"""
            <section class="panel">
                <h1>{{E(title)}}</h1>
                <p>{{E(message)}}</p>
                <p><a class="button" href="/">{{Icon("home")}}{{T(context, "To Home")}}</a></p>
            </section>
            """;

        return Layout(context, user, title, body);
    }

    public string FileViewer(
        HttpContext context,
        MatgateUser user,
        ServerEndpoint server,
        FileGatewayFileInfo file,
        string path,
        bool embedded = false)
    {
        var encodedPath = Uri.EscapeDataString(path);
        var streamUrl = $"/api/files/{server.Id}/view?path={encodedPath}";
        var downloadUrl = $"/api/files/{server.Id}/download?path={encodedPath}";
        var isAudio = file.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        var isVideo = file.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        var isImage = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var isPdf = string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
        var isText = file.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.ContentType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.ContentType, "application/xml", StringComparison.OrdinalIgnoreCase);

        var preview = isAudio
            ? $$"""
                <section class="viewer-stage audio-stage">
                    <div class="viewer-audio-mark">{{Icon("music")}}</div>
                    <audio controls preload="metadata" src="{{A(streamUrl)}}"></audio>
                </section>
                """
            : isVideo
                ? $$"""
                    <section class="viewer-stage video-stage">
                        <video controls preload="metadata" playsinline src="{{A(streamUrl)}}"></video>
                    </section>
                    """
                : isImage
                    ? $$"""
                        <section class="viewer-stage image-stage">
                            <img src="{{A(streamUrl)}}" alt="{{A(file.FileName)}}">
                        </section>
                        """
                    : isPdf || isText
                        ? $$"""
                            <section class="viewer-stage document-stage">
                                <iframe src="{{A(streamUrl)}}" title="{{A(file.FileName)}}"></iframe>
                            </section>
                        """
                        : $$"""
                            <section class="viewer-stage empty-viewer">
                                <h2>{{T(context, "No preview available")}}</h2>
                                <a class="button primary" href="{{A(downloadUrl)}}">{{Icon("download")}}{{T(context, "Download")}}</a>
                            </section>
                            """;

        var actions = embedded
            ? $$"""
                <a class="button" href="/sessions" data-file-viewer-close onclick="return window.MatgateCloseFileViewer(event, this)">{{Icon("x")}}{{T(context, "Close")}}</a>
                <a class="button" href="{{A(streamUrl)}}" target="_blank" rel="noopener">{{Icon("eye")}}{{T(context, "Open raw")}}</a>
                <a class="button primary" href="{{A(downloadUrl)}}">{{Icon("download")}}{{T(context, "Download")}}</a>
                """
            : $$"""
                <a class="button" href="/sessions" data-file-viewer-close onclick="return window.MatgateCloseFileViewer(event, this)">{{Icon("x")}}{{T(context, "Close")}}</a>
                <a class="button" href="{{A(streamUrl)}}" target="_blank" rel="noopener">{{Icon("eye")}}{{T(context, "Open raw")}}</a>
                <a class="button primary" href="{{A(downloadUrl)}}">{{Icon("download")}}{{T(context, "Download")}}</a>
                """;

        var body = $$"""
            <section class="file-viewer-page{{(embedded ? " embedded-viewer" : "")}}">
                <div class="session-tab-row viewer-tab-row">
                    <div class="session-tabs viewer-tabs">
                        <div class="session-tab active viewer-tab">
                            <div class="session-tab-main viewer-tab-main" aria-current="page">
                                <span class="session-tab-title">{{ServerIcon(server, "small")}}<span>{{E(file.FileName)}}</span></span>
                                <small>{{E(path)}} &middot; {{E(file.ContentType)}} &middot; {{FormatFileSize(file.Length)}}</small>
                            </div>
                        </div>
                    </div>
                    <div class="tab-actions viewer-actions">{{actions}}</div>
                </div>
                <div class="viewer-body">
                    {{preview}}
                </div>
            </section>
            """;

        return embedded ? body : Layout(context, user, file.FileName, body, "viewer-main");
    }

    public string FileViewerError(
        HttpContext context,
        ServerEndpoint server,
        string path,
        string title,
        string message)
    {
        return $$"""
            <section class="file-viewer-page embedded-viewer">
                <div class="session-tab-row viewer-tab-row">
                    <div class="session-tabs viewer-tabs">
                        <div class="session-tab active viewer-tab">
                            <div class="session-tab-main viewer-tab-main" aria-current="page">
                                <span class="session-tab-title">{{ServerIcon(server, "small")}}<span>{{E(title)}}</span></span>
                                <small>{{E(path)}}</small>
                            </div>
                        </div>
                    </div>
                    <div class="tab-actions viewer-actions">
                        <a class="button" href="/sessions" data-file-viewer-close onclick="return window.MatgateCloseFileViewer(event, this)">{{Icon("x")}}{{T(context, "Close")}}</a>
                    </div>
                </div>
                <div class="viewer-body">
                    <section class="viewer-stage empty-viewer">
                        <h2>{{E(title)}}</h2>
                        <p>{{E(message)}}</p>
                    </section>
                </div>
            </section>
            """;
    }

    public string SessionsWorkspace(
        HttpContext context,
        MatgateUser user,
        IReadOnlyList<ServerEndpoint> servers,
        Guid? openServerId)
    {
        var sharedServers = servers.Where(server => server.OwnerUserId is null).OrderBy(server => server.Name).ToList();
        var ownServers = servers.Where(server => server.OwnerUserId == user.Id).OrderBy(server => server.Name).ToList();
        var availableServers = JsonSerializer.Serialize(servers.Select(server => new
        {
            id = server.Id.ToString(),
            name = server.Name,
            protocol = server.Protocol.ToString().ToUpperInvariant(),
            iconKey = ServerEndpoint.EffectiveIconKey(server.Protocol, server.IconKey),
            iconHtml = Icon(ServerEndpoint.EffectiveIconKey(server.Protocol, server.IconKey)),
            target = ServerTargetValue(server)
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var initialOpenServerId = JsonSerializer.Serialize(openServerId?.ToString() ?? "");
        var csrfToken = JsonSerializer.Serialize(context.User.FindFirstValue("csrf") ?? "");
        var version = ApplicationVersion();
        var aboutTitle = $"MATGATE {version}";
        var fileIcons = JsonSerializer.Serialize(new
        {
            folder = Icon("folder"),
            file = Icon("file"),
            parent = Icon("folder-up"),
            plus = Icon("plus"),
            mkdir = Icon("folder-plus"),
            refresh = Icon("refresh"),
            menu = Icon("clipboard"),
            chevronDown = Icon("chevron-down"),
            upload = Icon("upload"),
            download = Icon("download"),
            view = Icon("eye"),
            copy = Icon("copy"),
            move = Icon("move"),
            archive = Icon("archive"),
            check = Icon("check"),
            delete = Icon("trash")
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var actionIcons = JsonSerializer.Serialize(new
        {
            fullscreen = Icon("maximize"),
            clipboard = Icon("clipboard"),
            disconnect = Icon("logout")
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var websiteIcons = JsonSerializer.Serialize(new
        {
            back = Icon("arrow-left"),
            forward = Icon("arrow-right"),
            refresh = Icon("refresh"),
            open = Icon("external-link"),
            globe = Icon("globe")
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var archiveExtensions = JsonSerializer.Serialize(FileArchiveFormats.SupportedExtensions, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var uiText = JsonSerializer.Serialize(new
        {
            about = T(context, "About"),
            dashboard = T(context, "Home"),
            connections = T(context, "Connections"),
            page = T(context, "Page"),
            fullscreen = T(context, "Fullscreen"),
            clipboard = T(context, "Clipboard"),
            disconnect = T(context, "Disconnect"),
            username = T(context, "Username"),
            password = T(context, "Password"),
            noActiveTab = T(context, "No active tab"),
            newConnection = T(context, "New connection"),
            ready = T(context, "Ready"),
            chooseConnection = Language(context) == "de" ? "Verbindung auswaehlen" : "Choose a connection",
            starting = Language(context) == "de" ? "Startet" : "Starting",
            website = T(context, "Website"),
            websiteBeta = T(context, "Website (Beta)"),
            websiteProxy = T(context, "Website proxy"),
            websiteOpening = T(context, "Opening website"),
            websiteLoaded = T(context, "Website loaded"),
            forward = T(context, "Forward"),
            openInNewTab = T(context, "Open in new tab"),
            waiting = Language(context) == "de" ? "Wartet" : "Waiting",
            connecting = Language(context) == "de" ? "Verbindet" : "Connecting",
            connected = Language(context) == "de" ? "Verbunden" : "Connected",
            disconnecting = Language(context) == "de" ? "Trennt" : "Disconnecting",
            disconnected = Language(context) == "de" ? "Beendet" : "Ended",
            open = Language(context) == "de" ? "Offen" : "Open",
            unstable = Language(context) == "de" ? "Instabil" : "Unstable",
            closed = Language(context) == "de" ? "Geschlossen" : "Closed",
            unknown = Language(context) == "de" ? "Unbekannt" : "Unknown",
            failed = Language(context) == "de" ? "Verbindung fehlgeschlagen" : "Connection failed",
            connectionUnavailable = Language(context) == "de" ? "Verbindung nicht moeglich" : "Connection unavailable",
            guacClientMissing = Language(context) == "de" ? "Der Guacamole-Webclient konnte nicht geladen werden." : "The Guacamole web client could not be loaded.",
            opening = Language(context) == "de" ? "Verbindung wird aufgebaut" : "Opening connection",
            preparing = Language(context) == "de" ? "Matgate bereitet die Sitzung vor." : "Matgate is preparing the session.",
            isOpening = Language(context) == "de" ? "wird geoeffnet" : "is opening",
            remoteConnected = Language(context) == "de" ? "Remote-Sitzung ist verbunden." : "Remote session is connected.",
            connectionEnded = Language(context) == "de" ? "Verbindung beendet" : "Connection ended",
            sessionClosed = Language(context) == "de" ? "Die Sitzung wurde geschlossen." : "The session was closed.",
            connectionStartFailed = Language(context) == "de" ? "Die Verbindung konnte nicht gestartet werden." : "The connection could not be started.",
            tunnelClosed = Language(context) == "de" ? "Der Guacamole-Tunnel wurde geschlossen." : "The Guacamole tunnel was closed.",
            tunnelInterrupted = Language(context) == "de" ? "Der Guacamole-Tunnel wurde unterbrochen." : "The Guacamole tunnel was interrupted.",
            connectionFailedDetail = Language(context) == "de" ? "Die Verbindung ist fehlgeschlagen." : "The connection failed.",
            reconnect = Language(context) == "de" ? "Neu verbinden" : "Reconnect",
            closeTab = Language(context) == "de" ? "Tab schliessen" : "Close tab",
            loading = Language(context) == "de" ? "Lade" : "Loading",
            fileManagerOpening = Language(context) == "de" ? "Dateimanager wird geoeffnet" : "Opening file manager",
            fileManagerPreparing = Language(context) == "de" ? "Dateimanager wird vorbereitet." : "Preparing file manager.",
            fileApi = Language(context) == "de" ? "Datei-API" : "File API",
            fileListLoading = Language(context) == "de" ? "Dateiliste wird geladen." : "Loading file list.",
            fileListLoadFailed = Language(context) == "de" ? "Dateiliste konnte nicht geladen werden." : "Could not load file list.",
            fileListUpdated = Language(context) == "de" ? "Dateiliste aktualisiert." : "File list updated.",
            fileAccessFailed = Language(context) == "de" ? "Dateizugriff fehlgeschlagen" : "File access failed",
            error = Language(context) == "de" ? "Fehler" : "Error",
            isLoading = Language(context) == "de" ? "wird geladen" : "is loading",
            create = Language(context) == "de" ? "Anlegen" : "Create",
            directory = Language(context) == "de" ? "Verzeichnis" : "Directory",
            file = Language(context) == "de" ? "Datei" : "File",
            upload = Language(context) == "de" ? "Upload" : "Upload",
            fileName = Language(context) == "de" ? "Dateiname" : "File name",
            download = Language(context) == "de" ? "Download" : "Download",
            downloadZip = Language(context) == "de" ? "ZIP" : "ZIP",
            view = Language(context) == "de" ? "Ansehen" : "View",
            unzip = Language(context) == "de" ? "Entpacken" : "Unzip",
            copy = Language(context) == "de" ? "Kopieren" : "Copy",
            move = Language(context) == "de" ? "Verschieben" : "Move",
            delete = Language(context) == "de" ? "Loeschen" : "Delete",
            deleteSelected = Language(context) == "de" ? "Auswahl loeschen" : "Delete selected",
            selected = Language(context) == "de" ? "ausgewaehlt" : "selected",
            selection = Language(context) == "de" ? "Auswahl" : "Selection",
            selectAll = Language(context) == "de" ? "Alles auswaehlen" : "Select all",
            clearSelection = Language(context) == "de" ? "Auswahl aufheben" : "Clear selection",
            archiveName = Language(context) == "de" ? "Archivname" : "Archive name",
            zipCreated = Language(context) == "de" ? "ZIP-Archiv erstellt" : "ZIP archive created",
            destinationPath = Language(context) == "de" ? "Zielpfad" : "Destination path",
            folder = Language(context) == "de" ? "Ordner" : "Folder",
            refresh = Language(context) == "de" ? "Aktualisieren" : "Refresh",
            back = T(context, "Back"),
            name = T(context, "Name"),
            path = Language(context) == "de" ? "Pfad" : "Path",
            size = Language(context) == "de" ? "Groesse" : "Size",
            modified = Language(context) == "de" ? "Geaendert" : "Modified",
            actions = Language(context) == "de" ? "Aktionen" : "Actions",
            emptyFolder = Language(context) == "de" ? "Dieser Ordner ist leer." : "This folder is empty.",
            folderName = Language(context) == "de" ? "Ordnername" : "Folder name",
            deleteConfirm = Language(context) == "de" ? "wirklich loeschen?" : "delete?",
            actionDone = Language(context) == "de" ? "Dateiaktion abgeschlossen." : "File action completed.",
            actionFailed = Language(context) == "de" ? "Dateiaktion fehlgeschlagen." : "File action failed.",
            working = Language(context) == "de" ? "Arbeite" : "Working",
            uploadFailed = Language(context) == "de" ? "Upload fehlgeschlagen." : "Upload failed.",
            mkdirFailed = Language(context) == "de" ? "Ordner konnte nicht erstellt werden." : "Could not create folder.",
            deleteFailed = Language(context) == "de" ? "Loeschen fehlgeschlagen." : "Delete failed.",
            downloadStarted = Language(context) == "de" ? "Download gestartet" : "Download started",
            zipDownloadStarted = Language(context) == "de" ? "ZIP-Download gestartet" : "ZIP download started",
            copyFailed = Language(context) == "de" ? "Kopieren fehlgeschlagen." : "Copy failed.",
            moveFailed = Language(context) == "de" ? "Verschieben fehlgeschlagen." : "Move failed.",
            viewStarted = Language(context) == "de" ? "Ansicht geoeffnet" : "View opened",
            close = T(context, "Close"),
            clipboardSent = Language(context) == "de" ? "Zwischenablage gesendet" : "Clipboard sent",
            clipboardReceived = Language(context) == "de" ? "Zwischenablage empfangen" : "Clipboard received",
            remoteClipboardReady = Language(context) == "de" ? "Remote-Zwischenablage bereit" : "Remote clipboard ready",
            connectionContinues = Language(context) == "de" ? "Verbindung wird fortgesetzt" : "Connection continues",
            credentialsSubmitted = Language(context) == "de" ? "Die Zugangsdaten wurden uebergeben." : "Credentials were submitted."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var connectionChoices = ConnectionChoiceSections(context, user, sharedServers, false);
        var ownConnectionChoices = ConnectionChoiceSections(context, user, ownServers, true);
        var ownServersSection = user.CanCreateServers
            ? $$"""
                <section class="home-management">
                    <div class="row split">
                        <div>
                            <p class="eyebrow">{{T(context, "Own servers")}}</p>
                            <h2>{{T(context, "Own servers")}}</h2>
                        </div>
                        <a class="button primary" href="/admin/servers/new">{{Icon("plus")}}{{T(context, "Create own server")}}</a>
                    </div>
                    {{ownConnectionChoices}}
                </section>
                """
            : "";
        var body = $$"""
            <div id="matgate-shell" class="matgate-shell">
            <div class="shell-page-row">
                <div id="session-tabs" class="session-tabs shell-page-tabs" role="tablist">
                    <div id="new-connection-tab" class="session-tab session-tab--add" role="tab" data-tab-kind="add" aria-label="{{A(T(context, "New connection"))}}">
                        <button type="button" class="session-tab-main">
                            <span class="session-tab-title">{{Icon("plus")}}</span>
                            <small class="session-tab-description">&nbsp;</small>
                        </button>
                    </div>
                </div>
                <div id="connection-tab-actions" class="tab-actions" aria-label="{{A(T(context, "Actions for the active tab"))}}"></div>
            </div>
            <div id="shell-page-panels" class="shell-page-panels">
            <section id="home-view" class="app-view session-page multi-session-page active">
                <div id="session-deck" class="session-deck">
                    <div id="new-connection-panel" class="connection-picker-panel">
                        <div class="connection-picker-inner">
                            <section class="connection-picker-head">
                                <p class="eyebrow">{{T(context, "Home")}}</p>
                                <h1>{{T(context, "New Connection")}}</h1>
                            </section>
                            {{connectionChoices}}
                            {{ownServersSection}}
                        </div>
                    </div>
                </div>
                <form id="credential-dialog" class="credential-dialog hidden">
                    <h2>{{T(context, "Credentials")}}</h2>
                    <div id="credential-fields" class="stack"></div>
                    <div class="actions">
                        <button type="submit" class="primary">{{Icon("save")}}{{T(context, "Send")}}</button>
                        <button id="credential-cancel" type="button">{{T(context, "Cancel")}}</button>
                    </div>
                </form>
                <form id="clipboard-dialog" class="credential-dialog clipboard-dialog hidden">
                    <h2>{{T(context, "Send clipboard")}}</h2>
                    <label>{{T(context, "Text")}}
                        <textarea id="clipboard-text" required></textarea>
                    </label>
                    <div class="actions">
                        <button type="submit" class="primary">{{Icon("save")}}{{T(context, "Send to active tab")}}</button>
                        <button id="clipboard-close" type="button">{{T(context, "Close")}}</button>
                    </div>
                </form>
                <dialog id="file-viewer-dialog" class="matgate-dialog file-viewer-dialog"></dialog>
            </section>
            </div>
            <div id="session-statusbar" class="session-statusbar">
                <div class="status-primary">
                    <strong id="status-title">{{T(context, "No active tab")}}</strong>
                    <span id="status-target">-</span>
                </div>
                <div class="status-secondary">
                    <div class="status-metrics">
                        <span id="status-state">{{T(context, "Ready")}}</span>
                        <span id="status-latency">{{T(context, "Gateway: -")}}</span>
                        <span id="status-tunnel">{{T(context, "Tunnel: -")}}</span>
                        <span id="status-sync">{{T(context, "Sync: -")}}</span>
                        <span id="status-message">-</span>
                    </div>
                    <div class="status-actions">
                        <button id="status-info-button" type="button" class="status-info-button" title="{{A(aboutTitle)}}" aria-label="{{A(aboutTitle)}}">{{Icon("info")}}</button>
                    </div>
                </div>
            </div>
            </div>
            <script src="/guacamole/guacamole-common-js/all.min.js"></script>
            <script>
            (() => {
                const availableServers = {{availableServers}};
                const initialOpenServerId = {{initialOpenServerId}};
                const csrfToken = {{csrfToken}};
                const uiText = {{uiText}};
                const fileIcons = {{fileIcons}};
                const actionIcons = {{actionIcons}};
                const websiteIcons = {{websiteIcons}};
                const archiveExtensions = {{archiveExtensions}};
                const homeView = document.getElementById('home-view');
                const tabsRoot = document.getElementById('session-tabs');
                const newConnectionTab = document.getElementById('new-connection-tab');
                const newConnectionPanel = document.getElementById('new-connection-panel');
                const deck = document.getElementById('session-deck');
                const connectionTabActions = document.getElementById('connection-tab-actions');
                const statusTitle = document.getElementById('status-title');
                const statusTarget = document.getElementById('status-target');
                const statusState = document.getElementById('status-state');
                const statusLatency = document.getElementById('status-latency');
                const statusTunnel = document.getElementById('status-tunnel');
                const statusSync = document.getElementById('status-sync');
                const statusMessage = document.getElementById('status-message');
                const aboutInfoButton = document.getElementById('status-info-button');
                const fileViewerDialog = document.getElementById('file-viewer-dialog');
                const credentialDialog = document.getElementById('credential-dialog');
                const credentialFields = document.getElementById('credential-fields');
                const credentialCancel = document.getElementById('credential-cancel');
                const clipboardDialog = document.getElementById('clipboard-dialog');
                const clipboardText = document.getElementById('clipboard-text');
                const clipboardClose = document.getElementById('clipboard-close');
                const shellPagePanels = document.getElementById('shell-page-panels');
                const tabs = new Map();
                const workspaceStorageKey = 'matgate.workspace.tabs.v2';
                const shellTabStorageKey = 'matgate.shell.tabs.v1';
                const tabOrderStorageKey = 'matgate.tab.order.v1';
                const shellTabs = new Map();

                let activeTabId = null;
                let activeShellTabId = '';
                let draggedTabId = null;
                let suppressTabClicks = false;
                let transparentDragImage = null;
                let credentialTab = null;
                let resizeTimer = null;
                let gatewayLatencyMs = null;
                let fileViewerLoadToken = 0;

                window.MatgateOpenAboutTab = (event) => {
                    if (event) {
                        event.preventDefault();
                    }

                    openShellTab('/about', ui('About'), aboutInfoButton?.innerHTML || '');
                    return false;
                };
                if (aboutInfoButton) {
                    aboutInfoButton.addEventListener('click', window.MatgateOpenAboutTab);
                }
                if (fileViewerDialog) {
                    fileViewerDialog.addEventListener('click', (event) => {
                        if (event.target === fileViewerDialog) {
                            closeFileViewerDialog();
                        }
                    });
                    fileViewerDialog.addEventListener('close', () => {
                        fileViewerLoadToken += 1;
                        fileViewerDialog.replaceChildren();
                    });
                }

                function closeFileViewerDialog() {
                    fileViewerLoadToken += 1;
                    if (fileViewerDialog && typeof fileViewerDialog.close === 'function' && fileViewerDialog.open) {
                        fileViewerDialog.close();
                        return;
                    }

                    if (fileViewerDialog) {
                        fileViewerDialog.replaceChildren();
                    }
                }

                async function openFileViewerDialog(tab, path) {
                    const pageUrl = `/files/${tab.serverId}/view?path=${encodeURIComponent(path)}&embedded=1`;
                    if (!fileViewerDialog || typeof fileViewerDialog.showModal !== 'function') {
                        window.location.href = `/files/${tab.serverId}/view?path=${encodeURIComponent(path)}`;
                        return;
                    }

                    const loadToken = ++fileViewerLoadToken;
                    fileViewerDialog.replaceChildren();
                    const loading = document.createElement('div');
                    loading.className = 'file-viewer-dialog-loading';
                    loading.textContent = `${ui('loading')}...`;
                    fileViewerDialog.appendChild(loading);
                    if (!fileViewerDialog.open) {
                        fileViewerDialog.showModal();
                    }

                    try {
                        const response = await fetch(pageUrl, { cache: 'no-store' });
                        if (loadToken !== fileViewerLoadToken) {
                            return;
                        }

                        if (!response.ok) {
                            const errorText = await response.text().catch(() => '');
                            fileViewerDialog.innerHTML = `
                                <div class="file-viewer-dialog-loading error">
                                    <div>
                                        <h2>${escapeHtml(ui('fileAccessFailed'))}</h2>
                                        <p>${escapeHtml(errorText || ui('fileAccessFailed'))}</p>
                                    </div>
                                </div>`;
                            return;
                        }

                        const html = await response.text();
                        if (loadToken !== fileViewerLoadToken) {
                            return;
                        }

                        fileViewerDialog.innerHTML = normalizeFileViewerMarkup(html);
                    }
                    catch (error) {
                        if (loadToken !== fileViewerLoadToken) {
                            return;
                        }

                        const message = error instanceof Error ? error.message : ui('fileAccessFailed');
                        fileViewerDialog.innerHTML = `
                            <div class="file-viewer-dialog-loading error">
                                <div>
                                    <h2>${escapeHtml(ui('error') || 'Error')}</h2>
                                    <p>${escapeHtml(message)}</p>
                                </div>
                            </div>`;
                    }
                }

                function normalizeFileViewerMarkup(html) {
                    try {
                        const doc = new DOMParser().parseFromString(html, 'text/html');
                        const embeddedViewer = doc.querySelector('section.file-viewer-page');
                        if (embeddedViewer) {
                            return embeddedViewer.outerHTML;
                        }

                        const main = doc.querySelector('main');
                        if (main) {
                            return main.innerHTML.trim() || html;
                        }
                    }
                    catch {
                        // Fall back to the raw response below.
                    }

                    return html;
                }

                function showView(view, updateHistory) {
                    activateNewConnectionTab();
                    document.body.dataset.view = 'home';
                    document.title = `${ui('dashboard')} - Matgate`;

                    if (updateHistory && location.pathname !== '/') {
                        history.pushState({ view: 'home' }, '', '/');
                    }
                }

                function setWorkspaceVisible(active) {
                    if (homeView) {
                        homeView.classList.toggle('hidden', !active);
                    }

                    if (shellPagePanels) {
                        shellPagePanels.classList.toggle('hidden', false);
                    }
                }

                function shellEmbeddedUrl(url) {
                    try {
                        const parsed = new URL(url, window.location.origin);
                        parsed.searchParams.set('embed', '1');
                        return `${parsed.pathname}${parsed.search}${parsed.hash}`;
                    }
                    catch {
                        return `${url}${url.includes('?') ? '&' : '?'}embed=1`;
                    }
                }

                function saveShellTabs() {
                    try {
                        localStorage.setItem(shellTabStorageKey, JSON.stringify({
                            tabs: Array.from(shellTabs.values()).map(tab => ({
                                id: tab.id,
                                title: tab.title,
                                url: tab.url,
                                iconHtml: tab.iconHtml || '',
                                description: shellTabDescription(tab)
                            })),
                            activeTabId: activeShellTabId
                        }));
                    }
                    catch {
                        // Ignore storage failures.
                    }
                }

                function createShellTab(tab) {
                    if (!tabsRoot || !shellPagePanels || shellTabs.has(tab.id)) {
                        return shellTabs.get(tab.id) || null;
                    }

                    const resolvedTitle = tab.url === '/about'
                        ? (uiText.about || ui('About'))
                        : tab.title;
                    const resolvedTab = { ...tab, title: resolvedTitle };

                    const button = document.createElement('div');
                    button.className = 'session-tab session-tab--page';
                    button.setAttribute('data-shell-tab-id', resolvedTab.id);
                    button.setAttribute('data-tab-id', resolvedTab.id);
                    button.setAttribute('data-tab-kind', 'page');

                    const main = document.createElement('button');
                    main.type = 'button';
                    main.className = 'session-tab-main';
                    main.draggable = true;

                    const title = document.createElement('span');
                    title.className = 'session-tab-title';
                    title.innerHTML = `${resolvedTab.iconHtml || ''}<span>${escapeHtml(resolvedTab.title)}</span>`;

                    const subtitle = document.createElement('small');
                    subtitle.className = 'session-tab-description';
                    subtitle.textContent = shellTabDescription(resolvedTab);
                    main.append(title, subtitle);

                    const close = document.createElement('button');
                    close.type = 'button';
                    close.className = 'session-tab-close';
                    close.setAttribute('aria-label', ui('close'));
                    close.innerHTML = '&times;';
                    close.addEventListener('click', (event) => {
                        event.stopPropagation();
                        closeShellTab(resolvedTab.id);
                    });
                    close.addEventListener('mousedown', event => {
                        event.stopPropagation();
                    });
                    close.addEventListener('dragstart', event => {
                        event.preventDefault();
                        event.stopPropagation();
                    });

                    const panel = document.createElement('section');
                    panel.className = 'shell-page-panel hidden';
                    panel.setAttribute('data-shell-panel-id', resolvedTab.id);
                    const iframe = document.createElement('iframe');
                    iframe.title = resolvedTab.title;
                    iframe.src = shellEmbeddedUrl(resolvedTab.url);
                    panel.appendChild(iframe);

                    button.append(main, close);
                    main.addEventListener('click', event => {
                        if (suppressTabClicks) {
                            event.preventDefault();
                            return;
                        }

                        activateShellTab(resolvedTab.id);
                    });
                    insertTabButton(button);
                    shellPagePanels.appendChild(panel);
                    wireTabDrag(button, resolvedTab.id, main);

                    const entry = { ...resolvedTab, button, panel, iframe };
                    shellTabs.set(resolvedTab.id, entry);
                    return entry;
                }

                function shellTabDescription(tab) {
                    const fallback = uiText.page || 'Page';
                    if (tab.url === '/tools') {
                        return fallback;
                    }

                    const description = typeof tab.description === 'string' ? tab.description.trim() : '';
                    if (!description) {
                        return fallback;
                    }

                    if (description.startsWith('/') || description.includes('://') || /^[a-zA-Z]:[\\/]/.test(description)) {
                        return fallback;
                    }

                    return description;
                }

                function activateShellTab(tabId) {
                    activeShellTabId = tabId;
                    activeTabId = null;
                    newConnectionTab.classList.remove('active');
                    newConnectionPanel.classList.add('hidden');
                    for (const tab of tabs.values()) {
                        tab.tabButton.classList.remove('active');
                        tab.panel.classList.add('hidden');
                    }
                    setWorkspaceVisible(false);
                    document.title = `${(shellTabs.get(tabId)?.title || ui('dashboard'))} - Matgate`;

                    shellTabs.forEach(tab => {
                        tab.button.classList.toggle('active', tab.id === tabId);
                        tab.panel.classList.toggle('hidden', tab.id !== tabId);
                    });

                    shellTabs.get(tabId)?.button.scrollIntoView({ block: 'nearest', inline: 'nearest' });

                    updateTabActions();
                    saveTabOrder();
                    saveShellTabs();
                }

                function closeShellTab(tabId) {
                    const tab = shellTabs.get(tabId);
                    if (!tab) {
                        return;
                    }

                    tab.button.remove();
                    tab.panel.remove();
                    shellTabs.delete(tabId);

                    if (activeShellTabId === tabId) {
                        activeShellTabId = '';
                        activateNewConnectionTab();
                    }

                    saveTabOrder();
                    saveShellTabs();
                }

                function openShellTab(url, title, iconHtml = '', description = '') {
                    if (!url) {
                        activateNewConnectionTab();
                        return;
                    }

                    const existing = Array.from(shellTabs.values()).find(tab => tab.url === url);
                    if (existing) {
                        activateShellTab(existing.id);
                        return;
                    }

                    const id = `shell-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
                    createShellTab({ id, url, title, iconHtml, description });
                    activateShellTab(id);
                }

                function restoreShellTabs() {
                    if (!tabsRoot || !shellPagePanels) {
                        return;
                    }

                    try {
                        const raw = localStorage.getItem(shellTabStorageKey);
                        if (!raw) {
                            activateNewConnectionTab();
                            return;
                        }

                        const state = JSON.parse(raw);
                        const storedTabs = Array.isArray(state.tabs) ? state.tabs : [];
                        storedTabs.forEach(tab => {
                            if (tab && tab.id && tab.url && tab.title) {
                                createShellTab(tab);
                            }
                        });

                        const preferred = state.activeTabId && shellTabs.has(state.activeTabId)
                            ? state.activeTabId
                            : '';
                        if (preferred) {
                            activateShellTab(preferred);
                        }
                        else {
                            activateNewConnectionTab();
                        }
                    }
                    catch {
                        activateNewConnectionTab();
                    }
                }

                function wireShellNavigation() {
                    document.querySelectorAll('.workspace-open-button').forEach(button => {
                        button.addEventListener('click', () => {
                            openServer(button.getAttribute('data-server-id') || '');
                        });
                    });

                    document.querySelectorAll('[data-shell-open-tab="1"]').forEach(anchor => {
                        anchor.addEventListener('click', event => {
                            if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.button !== 0) {
                                return;
                            }

                            const url = anchor.getAttribute('href') || '';
                            const title = anchor.getAttribute('data-shell-title') || anchor.textContent.trim() || '';
                            const description = anchor.getAttribute('data-shell-description') || '';
                            const iconHtml = anchor.querySelector('.icon')?.outerHTML || anchor.getAttribute('data-shell-icon-html') || '';
                            if (!url) {
                                return;
                            }

                            event.preventDefault();
                            openShellTab(url, title, iconHtml, description);
                        });
                    });

                    newConnectionTab.addEventListener('click', event => {
                        if (suppressTabClicks) {
                            event.preventDefault();
                            return;
                        }

                        activateNewConnectionTab();
                    });

                    window.addEventListener('popstate', () => {
                        showView('home', false);
                    });
                }

                function newTabId() {
                    if (crypto && crypto.randomUUID) {
                        return crypto.randomUUID();
                    }

                    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, ch => {
                        const random = Math.floor(Math.random() * 16);
                        const value = ch === 'x' ? random : (random & 0x3) | 0x8;
                        return value.toString(16);
                    });
                }

                function findServer(serverId) {
                    return availableServers.find(server => server.id === serverId);
                }

                function orderedSessionTabs() {
                    return Array.from(tabsRoot.querySelectorAll('.session-tab[data-tab-id][data-tab-kind="connection"]'))
                        .map(tabButton => tabs.get(tabButton.getAttribute('data-tab-id') || ''))
                        .filter(Boolean);
                }

                function ui(key) {
                    return uiText[key] || key;
                }

                function isFileProtocol(protocol) {
                    return ['SFTP', 'FTP', 'SMB'].includes((protocol || '').toUpperCase());
                }

                function isWebsiteProtocol(protocol) {
                    return (protocol || '').toUpperCase() === 'WEBSITE';
                }

                function protocolLabel(protocol) {
                    switch ((protocol || '').toUpperCase()) {
                        case 'WEBSITE': return uiText.websiteBeta || 'Website (Beta)';
                        default: return protocol || '-';
                    }
                }

                function buildWebsiteTabUrl(serverId, tabId, sourceUrl = '') {
                    const fallbackUrl = `/website/${serverId}/${tabId}/proxy/`;
                    if (!sourceUrl) {
                        return fallbackUrl;
                    }

                    try {
                        const url = new URL(sourceUrl, window.location.origin);
                        if (url.origin !== window.location.origin) {
                            return fallbackUrl;
                        }

                        const tabSpecificMatch = url.pathname.match(/^\/website\/[^/]+\/[^/]+\/proxy\/?/i);
                        if (tabSpecificMatch) {
                            return `${url.origin}${url.pathname.replace(/^\/website\/[^/]+\/[^/]+\/proxy\/?/i, `/website/${serverId}/${tabId}/proxy/`)}${url.search}${url.hash}`;
                        }

                        const legacyMatch = url.pathname.match(/^\/website\/[^/]+\/proxy\/?/i);
                        if (legacyMatch) {
                            return `${url.origin}${url.pathname.replace(/^\/website\/[^/]+\/proxy\/?/i, `/website/${serverId}/${tabId}/proxy/`)}${url.search}${url.hash}`;
                        }
                    }
                    catch {
                        // Fall back to a clean proxy URL.
                    }

                    return fallbackUrl;
                }

                function buildWebsiteDisplayUrl(tab, sourceUrl = '') {
                    const fallbackUrl = tab.target || '';
                    if (!sourceUrl) {
                        return fallbackUrl;
                    }

                    try {
                        const url = new URL(sourceUrl, window.location.origin);
                        const proxyMatch = url.pathname.match(/^\/website\/[^/]+\/(?:[^/]+\/)?proxy\/?(.*)$/i);
                        if (!proxyMatch) {
                            return url.origin === window.location.origin ? fallbackUrl : sourceUrl;
                        }

                        const targetBase = new URL(tab.target || fallbackUrl || window.location.origin, window.location.origin);
                        const relativePath = `${proxyMatch[1] || ''}${url.search}${url.hash}`;
                        return new URL(relativePath, targetBase).href;
                    }
                    catch {
                        // Fall back to the configured target URL.
                    }

                    return fallbackUrl || sourceUrl;
                }

                function updateWebsiteLocation(tabId, url) {
                    const tab = tabs.get(tabId);
                    if (!tab || !isWebsiteProtocol(tab.protocol)) {
                        return;
                    }

                    const nextProxyUrl = typeof url === 'string' && url.trim()
                        ? url.trim()
                        : tab.target || '';
                    const nextDisplayUrl = buildWebsiteDisplayUrl(tab, nextProxyUrl);

                    tab.currentUrl = nextProxyUrl;
                    tab.displayUrl = nextDisplayUrl;
                    if (tab.websiteUi?.address) {
                        tab.websiteUi.address.value = nextDisplayUrl;
                    }

                    tab.lastSyncAt = Date.now();
                    tab.lastMessage = uiText.websiteLoaded || 'Website loaded';
                    updateStatusBar();
                }

                function setStatus(tab, status) {
                    tab.status = status;
                    tab.statusUpdatedAt = Date.now();
                    tab.statusLabel.textContent = status;
                    updateStatusBar();
                }

                function setOverlay(tab, headline, text, showActions) {
                    tab.overlayTitle.textContent = headline;
                    tab.overlayMessage.textContent = text;
                    tab.lastMessage = text;
                    tab.overlayActions.classList.toggle('hidden', !showActions);
                    tab.overlay.classList.remove('hidden');
                    updateStatusBar();
                }

                function hideOverlay(tab) {
                    tab.overlay.classList.add('hidden');
                }

                function updateTabActions() {
                    if (!connectionTabActions) {
                        updateStatusBar();
                        return;
                    }

                    connectionTabActions.replaceChildren();
                    const tab = tabs.get(activeTabId);
                    if (!tab) {
                        updateStatusBar();
                        return;
                    }

                    const fullscreenButton = createTabActionButton(
                        actionIcons.fullscreen,
                        uiText.fullscreen || 'Fullscreen',
                        () => {
                            const target = tab.panel || deck;
                            if (!document.fullscreenElement) {
                                target.requestFullscreen().then(scheduleResize).catch(() => {});
                            }
                            else {
                                document.exitFullscreen().then(scheduleResize).catch(() => {});
                            }
                        },
                        '',
                        true);

                    connectionTabActions.appendChild(fullscreenButton);

                    if (tab.client && !tab.terminal) {
                        const clipboardButton = createTabActionButton(
                            actionIcons.clipboard,
                            uiText.clipboard || 'Clipboard',
                            async () => {
                                try {
                                    const text = await readBrowserClipboard();
                                    if (!sendClipboardText(tab, text)) {
                                        openClipboardDialog(tab.remoteClipboard || '');
                                    }
                                }
                                catch {
                                    openClipboardDialog(tab.remoteClipboard || '');
                                }
                            },
                            '',
                            true);
                        connectionTabActions.appendChild(clipboardButton);
                    }

                    const disconnectButton = createTabActionButton(
                        actionIcons.disconnect,
                        uiText.disconnect || 'Disconnect',
                        () => closeTab(tab.id),
                        'danger');
                    connectionTabActions.appendChild(disconnectButton);
                    updateStatusBar();
                }

                function createTabActionButton(iconHtml, label, onClick, className = '', iconOnly = false) {
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = ['tab-action-button', iconOnly ? 'icon-only' : '', className].filter(Boolean).join(' ');
                    button.title = label;
                    button.setAttribute('aria-label', label);
                    button.innerHTML = iconOnly
                        ? `${iconHtml || ''}`
                        : `${iconHtml || ''}<span>${escapeHtml(label)}</span>`;
                    button.addEventListener('click', onClick);
                    return button;
                }

                function getTabEntry(tabId) {
                    return tabs.get(tabId) || shellTabs.get(tabId) || null;
                }

                function insertTabButton(tabButton) {
                    if (!tabsRoot || !tabButton) {
                        return;
                    }

                    if (newConnectionTab && newConnectionTab.parentElement === tabsRoot) {
                        tabsRoot.insertBefore(tabButton, newConnectionTab);
                        return;
                    }

                    tabsRoot.appendChild(tabButton);
                }

                function saveTabOrder() {
                    try {
                        localStorage.setItem(tabOrderStorageKey, JSON.stringify({
                            order: Array.from(tabsRoot.querySelectorAll('.session-tab[data-tab-id]'))
                                .map(tabButton => tabButton.getAttribute('data-tab-id'))
                                .filter(Boolean)
                        }));
                    }
                    catch {
                        // Ignore ordering failures.
                    }
                }

                function restoreTabOrder() {
                    if (!tabsRoot) {
                        return;
                    }

                    try {
                        const raw = localStorage.getItem(tabOrderStorageKey);
                        if (!raw) {
                            return;
                        }

                        const state = JSON.parse(raw);
                        const order = Array.isArray(state.order) ? state.order : [];
                        const anchor = newConnectionTab && newConnectionTab.parentElement === tabsRoot ? newConnectionTab : null;
                        order.forEach(tabId => {
                            const entry = getTabEntry(tabId);
                            const tabButton = entry?.tabButton || entry?.button;
                            if (tabButton && tabButton.parentElement === tabsRoot) {
                                tabsRoot.insertBefore(tabButton, anchor);
                            }
                        });
                    }
                    catch {
                        // Ignore ordering failures.
                    }
                }

                function wireTabDrag(tabButton, tabId) {
                    const dragHandle = tabButton.querySelector('.session-tab-main') || tabButton;
                    dragHandle.draggable = true;
                    dragHandle.addEventListener('dragstart', event => {
                        draggedTabId = tabId;
                        suppressTabClicks = true;
                        tabButton.classList.add('dragging');
                        try {
                            event.dataTransfer.effectAllowed = 'move';
                            event.dataTransfer.setData('text/plain', tabId);
                            event.dataTransfer.setDragImage(getTransparentDragImage(), 0, 0);
                        }
                        catch {
                            // Ignore drag data issues.
                        }
                    });
                    dragHandle.addEventListener('dragend', () => {
                        tabButton.classList.remove('dragging');
                        draggedTabId = null;
                        setTimeout(() => {
                            suppressTabClicks = false;
                        }, 0);
                        saveTabOrder();
                    });
                    tabButton.addEventListener('dragover', event => {
                        if (!draggedTabId || draggedTabId === tabId) {
                            return;
                        }

                        event.preventDefault();
                        const draggingTab = getTabEntry(draggedTabId);
                        const draggingButton = draggingTab?.tabButton || draggingTab?.button;
                        if (!draggingButton) {
                            return;
                        }

                        const rect = tabButton.getBoundingClientRect();
                        const insertBefore = event.clientX < rect.left + (rect.width / 2);
                        const referenceNode = insertBefore ? tabButton : (tabButton.nextSibling || newConnectionTab);
                        if (draggingButton !== referenceNode) {
                            tabsRoot.insertBefore(draggingButton, referenceNode);
                        }
                    });
                    tabButton.addEventListener('drop', event => {
                        if (!draggedTabId) {
                            return;
                        }

                        event.preventDefault();
                        draggedTabId = null;
                        saveTabOrder();
                    });
                }

                function getTransparentDragImage() {
                    if (transparentDragImage) {
                        return transparentDragImage;
                    }

                    transparentDragImage = document.createElement('div');
                    transparentDragImage.style.width = '1px';
                    transparentDragImage.style.height = '1px';
                    transparentDragImage.style.opacity = '0';
                    transparentDragImage.style.position = 'fixed';
                    transparentDragImage.style.pointerEvents = 'none';
                    transparentDragImage.style.top = '-100px';
                    transparentDragImage.style.left = '-100px';
                    document.body.appendChild(transparentDragImage);
                    return transparentDragImage;
                }

                function activateNewConnectionTab() {
                    activeTabId = null;
                    newConnectionTab.classList.add('active');
                    newConnectionPanel.classList.remove('hidden');
                    activeShellTabId = '';
                    setWorkspaceVisible(true);
                    document.title = `${ui('dashboard')} - Matgate`;

                    shellTabs.forEach(tab => {
                        tab.button.classList.remove('active');
                        tab.panel.classList.add('hidden');
                    });
                    for (const tab of tabs.values()) {
                        tab.tabButton.classList.remove('active');
                        tab.panel.classList.add('hidden');
                    }

                    updateTabActions();
                    saveWorkspaceTabs();
                }

                function createTab(server, options = {}) {
                    const tabId = options.tabId || newTabId();
                    const isWebsite = isWebsiteProtocol(server.protocol);
                    const tabButton = document.createElement('div');
                    tabButton.className = 'session-tab session-tab--connection';
                    tabButton.setAttribute('role', 'tab');
                    tabButton.setAttribute('data-tab-id', tabId);
                    tabButton.setAttribute('data-tab-kind', 'connection');

                    const tabMain = document.createElement('button');
                    tabMain.type = 'button';
                    tabMain.className = 'session-tab-main';
                    tabMain.draggable = true;
                    const tabTitle = document.createElement('span');
                    tabTitle.className = 'session-tab-title';
                    tabTitle.innerHTML = `${server.iconHtml || ''}<span>${escapeHtml(server.name)}</span>`;

                    const tabDescription = document.createElement('small');
                    tabDescription.className = 'session-tab-description';
                    tabDescription.textContent = ui('starting');

                    tabMain.append(tabTitle, tabDescription);

                    const closeButton = document.createElement('button');
                    closeButton.type = 'button';
                    closeButton.className = 'session-tab-close';
                    closeButton.setAttribute('aria-label', 'Tab schliessen');
                    closeButton.innerHTML = '&times;';
                    closeButton.addEventListener('mousedown', event => {
                        event.stopPropagation();
                    });
                    closeButton.addEventListener('dragstart', event => {
                        event.preventDefault();
                        event.stopPropagation();
                    });
                    closeButton.addEventListener('click', event => {
                        event.stopPropagation();
                        closeTab(tabId);
                    });

                    tabButton.append(tabMain, closeButton);
                    tabMain.addEventListener('click', event => {
                        if (suppressTabClicks) {
                            event.preventDefault();
                            return;
                        }

                        activateTab(tabId);
                    });
                    insertTabButton(tabButton);
                    wireTabDrag(tabButton, tabId);

                    const panel = document.createElement('div');
                    panel.className = 'connection-panel hidden';
                    panel.tabIndex = 0;

                    const displayRoot = document.createElement('div');
                    displayRoot.className = isWebsite ? 'website-display' : 'guac-display';

                    const overlay = document.createElement('div');
                    overlay.className = 'connection-overlay';

                    const dialog = document.createElement('div');
                    dialog.className = 'connection-dialog';

                    const overlayTitle = document.createElement('h1');
                    overlayTitle.textContent = ui('opening');

                    const overlayMessage = document.createElement('p');
                    overlayMessage.textContent = ui('preparing');

                    const overlayActions = document.createElement('div');
                    overlayActions.className = 'actions hidden';

                    const reconnectButton = document.createElement('button');
                    reconnectButton.type = 'button';
                    reconnectButton.className = 'primary';
                    reconnectButton.textContent = uiText.reconnect || 'Reconnect';

                    const closeOverlayButton = document.createElement('button');
                    closeOverlayButton.type = 'button';
                    closeOverlayButton.textContent = uiText.closeTab || 'Close tab';

                    overlayActions.append(reconnectButton, closeOverlayButton);
                    dialog.append(overlayTitle, overlayMessage, overlayActions);
                    overlay.appendChild(dialog);
                    panel.append(displayRoot, overlay);
                    deck.appendChild(panel);

                    const tab = {
                        id: tabId,
                        serverId: server.id,
                        name: server.name,
                        protocol: server.protocol,
                        iconKey: server.iconKey,
                        iconHtml: server.iconHtml,
                        target: server.target,
                        initialFilePath: options.filePath || '',
                        tabButton,
                        tabMain,
                        closeButton,
                        statusLabel: tabDescription,
                        panel,
                        displayRoot,
                        overlay,
                        overlayTitle,
                        overlayMessage,
                        overlayActions,
                        client: null,
                        keyboard: null,
                        lastError: '',
                        lastMessage: ui('newConnection'),
                        terminal: false,
                        connectedAt: null,
                        lastSyncAt: null,
                        lastSentSize: null,
                        tunnelState: 'Initialisiert',
                        connectionName: '',
                        encryptedData: '',
                        remoteClipboard: '',
                        websiteUi: null,
                        currentUrl: '',
                        displayUrl: '',
                        statusTimer: null,
                        statusUpdatedAt: Date.now(),
                        watchdog: null
                    };

                    if (isWebsite) {
                        const websiteShell = document.createElement('div');
                        websiteShell.className = 'website-shell';
                        websiteShell.innerHTML = `
                            ${Toolbar('website-toolbar',
                                ToolbarGroup('website-toolbar-group',
                                    ToolbarIconButton(uiText.back || 'Back', websiteIcons.back, 'website-tool-button', Attr('data-website-action', 'back') + Attr('title', uiText.back || 'Back')),
                                    ToolbarIconButton(uiText.forward || 'Forward', websiteIcons.forward, 'website-tool-button', Attr('data-website-action', 'forward') + Attr('title', uiText.forward || 'Forward')),
                                    ToolbarIconButton(uiText.refresh || 'Refresh', websiteIcons.refresh, 'website-tool-button', Attr('data-website-action', 'reload') + Attr('title', uiText.refresh || 'Refresh'))
                                ),
                                ToolbarGroup('website-toolbar-group website-toolbar-address toolbar-group--grow',
                                    ToolbarInput('website-address', server.target || '', uiText.website || 'Website', true)
                                ),
                                ToolbarGroup('website-toolbar-group toolbar-group--end',
                                    ToolbarButton(uiText.openInNewTab || 'Open in new tab', websiteIcons.open, 'website-tool-button', Attr('data-website-action', 'open') + Attr('title', uiText.openInNewTab || 'Open in new tab'))
                                )
                            )}
                            <iframe class="website-frame" title="${escapeHtml(server.name)}" allow="clipboard-read; clipboard-write; fullscreen"></iframe>`;
                        displayRoot.appendChild(websiteShell);
                        tab.websiteUi = {
                            shell: websiteShell,
                            frame: websiteShell.querySelector('.website-frame'),
                            address: websiteShell.querySelector('.website-address'),
                            backButton: websiteShell.querySelector('[data-website-action="back"]'),
                            forwardButton: websiteShell.querySelector('[data-website-action="forward"]'),
                            reloadButton: websiteShell.querySelector('[data-website-action="reload"]'),
                            openButton: websiteShell.querySelector('[data-website-action="open"]')
                        };
                        tab.websiteUi.frame.name = tab.id;
                        tab.websiteUi.frame.addEventListener('load', () => {
                            if (!tabs.has(tab.id)) {
                                return;
                            }

                            try {
                                updateWebsiteLocation(tab.id, tab.websiteUi.frame.contentWindow.location.href);
                            }
                            catch {
                                updateWebsiteLocation(tab.id, tab.target || '');
                            }

                            hideOverlay(tab);
                            setStatus(tab, ui('ready'));
                            updateStatusBar();
                        });
                        tab.websiteUi.backButton.addEventListener('click', () => {
                            try {
                                tab.websiteUi.frame.contentWindow.history.back();
                            }
                            catch {
                                // Ignore navigation issues.
                            }
                        });
                        tab.websiteUi.forwardButton.addEventListener('click', () => {
                            try {
                                tab.websiteUi.frame.contentWindow.history.forward();
                            }
                            catch {
                                // Ignore navigation issues.
                            }
                        });
                        tab.websiteUi.reloadButton.addEventListener('click', () => {
                            try {
                                tab.websiteUi.frame.contentWindow.location.reload();
                            }
                            catch {
                                tab.websiteUi.frame.src = tab.websiteUi.frame.src;
                            }
                        });
                        tab.websiteUi.openButton.addEventListener('click', () => {
                            const nextTabId = newTabId();
                            const current = tab.websiteUi.frame?.contentWindow?.location?.href
                                || tab.websiteUi.frame?.src
                                || tab.currentUrl
                                || tab.websiteUi.address.value
                                || tab.target
                                || '';
                            window.open(buildWebsiteTabUrl(tab.serverId, nextTabId, current), '_blank', 'noopener');
                        });
                    }

                    reconnectButton.addEventListener('click', () => restartTab(tab));
                    closeOverlayButton.addEventListener('click', () => closeTab(tab.id));
                    panel.addEventListener('click', () => panel.focus());

                    tabs.set(tab.id, tab);
                    activateTab(tab.id);
                    updateTabActions();
                    startTab(tab);
                    saveWorkspaceTabs();
                    return tab;
                }

                function activateTab(tabId) {
                    if (!tabs.has(tabId)) {
                        return;
                    }

                    activeTabId = tabId;
                    activeShellTabId = '';
                    setWorkspaceVisible(true);
                    newConnectionTab.classList.remove('active');
                    newConnectionPanel.classList.add('hidden');
                    document.title = `${tabs.get(tabId)?.name || ui('dashboard')} - Matgate`;
                    shellTabs.forEach(tab => {
                        tab.button.classList.remove('active');
                        tab.panel.classList.add('hidden');
                    });
                    for (const tab of tabs.values()) {
                        const active = tab.id === tabId;
                        tab.tabButton.classList.toggle('active', active);
                        tab.panel.classList.toggle('hidden', !active);
                    }

                    const activeTab = tabs.get(tabId);
                    fitDisplay(activeTab);
                    activeTab.panel.focus();
                    activeTab.tabButton.scrollIntoView({ block: 'nearest', inline: 'nearest' });
                    updateTabActions();
                    saveTabOrder();
                    saveWorkspaceTabs();
                }

                function closeTab(tabId) {
                    const tab = tabs.get(tabId);
                    if (!tab) {
                        return;
                    }

                    tab.terminal = true;
                    window.clearInterval(tab.watchdog);
                    if (tab.client) {
                        tab.client.disconnect();
                    }
                    if (tab.websiteUi?.frame) {
                        tab.websiteUi.frame.src = 'about:blank';
                    }

                    if (credentialTab && credentialTab.id === tabId) {
                        closeCredentialDialog();
                    }

                    tab.tabButton.remove();
                    tab.panel.remove();
                    tabs.delete(tabId);

                    if (activeTabId === tabId) {
                        const next = tabs.values().next().value;
                        activeTabId = null;
                        if (next) {
                            activateTab(next.id);
                        }
                        else {
                            activateNewConnectionTab();
                        }
                    }

                    updateTabActions();
                    saveTabOrder();
                    saveWorkspaceTabs();
                }

                function openServer(serverId, filePath = '', tabId = '') {
                    const server = findServer(serverId);
                    if (server) {
                        return createTab(server, { filePath, tabId });
                    }

                    return null;
                }

                function CssClasses(baseClass, className = '') {
                    const extra = (className || '').toString().trim();
                    return extra ? `${baseClass} ${extra}` : baseClass;
                }

                function Attr(name, value) {
                    const text = value === null || value === undefined ? '' : value.toString();
                    return text ? ` ${name}="${escapeHtml(text)}"` : '';
                }

                function BoolAttr(name, value) {
                    return value ? ` ${name}` : '';
                }

                function Toolbar(className, ...groups) {
                    return `<div class="${escapeHtml(CssClasses('toolbar', className))}">${groups.filter(Boolean).join('')}</div>`;
                }

                function ToolbarGroup(className, ...items) {
                    return `<div class="${escapeHtml(CssClasses('toolbar-group', className))}">${items.filter(Boolean).join('')}</div>`;
                }

                function ToolbarButton(label, iconHtml, className = '', extraAttributes = '', disabled = false) {
                    const attrs = `${extraAttributes ? ` ${extraAttributes.trim()}` : ''}${BoolAttr('disabled', disabled)}`;
                    return `<button type="button" class="${escapeHtml(CssClasses('toolbar-button', className))}"${attrs}>${iconHtml || ''}<span>${escapeHtml(label || '')}</span></button>`;
                }

                function ToolbarIconButton(label, iconHtml, className = '', extraAttributes = '', disabled = false) {
                    const attrs = `${Attr('aria-label', label)}${extraAttributes ? ` ${extraAttributes.trim()}` : ''}${BoolAttr('disabled', disabled)}`;
                    return `<button type="button" class="${escapeHtml(CssClasses('toolbar-button toolbar-icon-button', className))}"${attrs}>${iconHtml || ''}</button>`;
                }

                function ToolbarInput(className, value, ariaLabel, readOnly = false, extraAttributes = '') {
                    const attrs = `${Attr('aria-label', ariaLabel)}${Attr('value', value)}${BoolAttr('readonly', readOnly)}${extraAttributes ? ` ${extraAttributes.trim()}` : ''}`;
                    return `<input type="text" class="${escapeHtml(CssClasses('toolbar-input', className))}"${attrs}>`;
                }

                function ToolbarMenu(className, summaryClassName, summaryLabel, summaryIconHtml, summaryAttributes = '', ...items) {
                    const attrs = summaryAttributes ? ` ${summaryAttributes.trim()}` : '';
                    const chevron = fileIcon('chevronDown');
                    return `
                        <details class="${escapeHtml(CssClasses('toolbar-menu', className))}">
                            <summary class="${escapeHtml(CssClasses('toolbar-button toolbar-menu-trigger', summaryClassName))}"${attrs}>${summaryIconHtml || ''}<span>${escapeHtml(summaryLabel || '')}</span><span class="menu-caret">${chevron}</span></summary>
                            <div class="toolbar-menu-panel file-menu-panel">${items.filter(Boolean).join('')}</div>
                        </details>`;
                }

                function ToolbarMenuItem(label, iconHtml, className = '', extraAttributes = '', disabled = false) {
                    return ToolbarButton(label, iconHtml, CssClasses('toolbar-menu-item', className), extraAttributes, disabled);
                }

                function ToolbarUploadButton(label, iconHtml, className = '', inputClassName = '', inputAttributes = '') {
                    const attrs = inputAttributes ? ` multiple ${inputAttributes.trim()}` : ' multiple';
                    return `
                        <label class="${escapeHtml(CssClasses('toolbar-button toolbar-button--primary toolbar-upload-button', className))}" title="${escapeHtml(label || '')}">
                            ${iconHtml || ''}<span>${escapeHtml(label || '')}</span>
                            <input type="file" class="${escapeHtml(CssClasses('toolbar-upload-input file-upload-input', inputClassName))}"${attrs}>
                        </label>`;
                }

                function saveWorkspaceTabs() {
                    try {
                        const activeTab = tabs.get(activeTabId);
                        const orderedTabs = orderedSessionTabs();
                        const state = {
                            tabs: orderedTabs.map(tab => ({
                                tabId: tab.id,
                                serverId: tab.serverId,
                                filePath: isFileProtocol(tab.protocol) ? (tab.filePath || '/') : ''
                            })),
                            activeTabId: activeTab ? activeTab.id : ''
                        };

                        localStorage.setItem(workspaceStorageKey, JSON.stringify(state));
                    }
                    catch {
                        // Local storage is optional; restoring tabs is a convenience.
                    }
                }

                function loadWorkspaceTabs() {
                    try {
                        const raw = localStorage.getItem(workspaceStorageKey);
                        if (!raw) {
                            return { tabs: [], activeTabId: '', activeServerId: '' };
                        }

                        const state = JSON.parse(raw);
                        const tabStates = Array.isArray(state.tabs) ? state.tabs : [];
                        return {
                            tabs: tabStates
                                .map(tabState => typeof tabState === 'string'
                                    ? { serverId: tabState, filePath: '', tabId: '' }
                                    : {
                                        tabId: tabState.tabId || tabState.tabID || tabState.id || '',
                                        serverId: tabState.serverId || tabState.id || '',
                                        filePath: tabState.filePath || tabState.path || ''
                                    })
                                .filter(tabState => Boolean(findServer(tabState.serverId))),
                            activeTabId: typeof state.activeTabId === 'string' ? state.activeTabId : '',
                            activeServerId: findServer(state.activeServerId) ? state.activeServerId : ''
                        };
                    }
                    catch {
                        return { tabs: [], activeTabId: '', activeServerId: '' };
                    }
                }

                async function fetchLaunch(tab) {
                    const response = await fetch(`/api/connections/${tab.serverId}/launch`, {
                        method: 'POST',
                        headers: { 'X-Matgate-Csrf': csrfToken }
                    });

                    if (!response.ok) {
                        let error = ui('connectionStartFailed');
                        try {
                            const payload = await response.json();
                            error = payload.error || error;
                        }
                        catch {
                            // Keep generic error.
                        }

                        throw new Error(error);
                    }

                    return await response.json();
                }

                async function authenticate(tab) {
                    const response = await fetch('/guacamole/api/tokens', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                        body: new URLSearchParams({ data: tab.encryptedData })
                    });

                    if (!response.ok) {
                        throw new Error('Guacamole hat den Starttoken abgelehnt.');
                    }

                    return await response.json();
                }

                function viewport(tab) {
                    const rect = tab.panel.getBoundingClientRect();

                    return {
                        width: Math.max(320, Math.floor(rect.width)),
                        height: Math.max(240, Math.floor(rect.height)),
                        dpi: 96
                    };
                }

                function fitDisplay(tab) {
                    if (!tab || !tab.client) {
                        return;
                    }

                    const display = tab.client.getDisplay();
                    const width = display.getWidth();
                    const height = display.getHeight();
                    if (!width || !height) {
                        return;
                    }

                    const rect = tab.panel.getBoundingClientRect();
                    const scale = Math.min(1, rect.width / width, rect.height / height);
                    display.scale(Math.max(0.1, scale));
                }

                function sendDisplaySize(tab) {
                    if (!tab || !tab.client) {
                        return;
                    }

                    const size = viewport(tab);
                    if (tab.lastSentSize
                        && tab.lastSentSize.width === size.width
                        && tab.lastSentSize.height === size.height
                        && tab.lastSentSize.dpi === size.dpi) {
                        fitDisplay(tab);
                        return;
                    }

                    tab.lastSentSize = size;
                    tab.client.sendSize(size.width, size.height);
                    fitDisplay(tab);
                }

                function scheduleResize() {
                    window.clearTimeout(resizeTimer);
                    resizeTimer = window.setTimeout(() => sendDisplaySize(tabs.get(activeTabId)), 200);
                }

                async function measureGatewayLatency() {
                    const startedAt = performance.now();
                    try {
                        const response = await fetch(`/api/ping?t=${Date.now()}`, { cache: 'no-store' });
                        if (!response.ok) {
                            throw new Error('Ping failed');
                        }

                        gatewayLatencyMs = Math.round(performance.now() - startedAt);
                    }
                    catch {
                        gatewayLatencyMs = null;
                    }

                    updateStatusBar();
                }

                function tunnelStateName(state) {
                    if (!window.Guacamole) {
                        return 'Unbekannt';
                    }

                    switch (state) {
                        case Guacamole.Tunnel.State.CONNECTING: return ui('connecting');
                        case Guacamole.Tunnel.State.OPEN: return uiText.open || 'Open';
                        case Guacamole.Tunnel.State.UNSTABLE: return uiText.unstable || 'Unstable';
                        case Guacamole.Tunnel.State.CLOSED: return uiText.closed || 'Closed';
                        default: return uiText.unknown || 'Unknown';
                    }
                }

                function formatAge(timestamp) {
                    if (!timestamp) {
                        return '-';
                    }

                    const seconds = Math.max(0, Math.round((Date.now() - timestamp) / 1000));
                    return seconds < 2 ? 'jetzt' : `vor ${seconds}s`;
                }

                function normalizeStatus(status, fallback) {
                    if (!status) {
                        return fallback;
                    }

                    if (typeof status === 'string') {
                        return status;
                    }

                    const message = status.message || fallback;
                    return status.code ? `${message} (${status.code})` : message;
                }

                function updateStatusBar() {
                    const tab = tabs.get(activeTabId);
                    if (!tab) {
                        statusTitle.textContent = ui('newConnection');
                        statusTarget.textContent = '-';
                        statusState.textContent = ui('ready');
                        statusLatency.textContent = gatewayLatencyMs === null ? 'Gateway: -' : `Gateway: ${gatewayLatencyMs} ms`;
                        statusTunnel.textContent = 'Tunnel: -';
                        statusSync.textContent = 'Sync: -';
                        statusMessage.textContent = ui('chooseConnection');
                        return;
                    }

                    statusTitle.textContent = tab.name;
                    statusTarget.textContent = isWebsiteProtocol(tab.protocol)
                        ? `${protocolLabel(tab.protocol)} ${tab.displayUrl || tab.target || tab.currentUrl}`
                        : `${protocolLabel(tab.protocol)} ${tab.target}`;
                    statusState.textContent = tab.status || ui('ready');
                    statusLatency.textContent = gatewayLatencyMs === null ? 'Gateway: -' : `Gateway: ${gatewayLatencyMs} ms`;
                    statusTunnel.textContent = isFileProtocol(tab.protocol)
                        ? `Tunnel: ${ui('fileApi')}`
                        : isWebsiteProtocol(tab.protocol)
                            ? `Tunnel: ${uiText.websiteProxy || 'Website proxy'}`
                        : `Tunnel: ${tab.tunnelState || '-'}`;
                    statusSync.textContent = `Sync: ${formatAge(tab.lastSyncAt)}`;
                    statusMessage.textContent = tab.lastError || tab.lastMessage || '-';
                }

                window.MatgateWebsiteLocationChanged = (tabId, url) => {
                    updateWebsiteLocation(tabId, url);
                };

                function statusName(state) {
                    switch (state) {
                        case Guacamole.Client.State.CONNECTING: return ui('starting');
                        case Guacamole.Client.State.WAITING: return uiText.waiting || 'Waiting';
                        case Guacamole.Client.State.CONNECTED: return ui('connected');
                        case Guacamole.Client.State.DISCONNECTING: return uiText.disconnecting || 'Disconnecting';
                        case Guacamole.Client.State.DISCONNECTED: return ui('disconnected');
                        default: return ui('ready');
                    }
                }

                async function startTab(tab) {
                    if (isFileProtocol(tab.protocol)) {
                        startFileTab(tab);
                        return;
                    }

                    if (isWebsiteProtocol(tab.protocol)) {
                        startWebsiteTab(tab);
                        return;
                    }

                    if (!window.Guacamole) {
                        finishTab(tab, uiText.connectionUnavailable || 'Connection unavailable', uiText.guacClientMissing || 'The Guacamole web client could not be loaded.');
                        return;
                    }

                    tab.terminal = false;
                    tab.lastError = '';
                    tab.lastMessage = ui('preparing');
                    tab.connectedAt = null;
                    tab.lastSyncAt = null;
                    tab.tunnelState = 'Initialisiert';
                    tab.displayRoot.className = 'guac-display';
                    tab.displayRoot.replaceChildren();
                    setStatus(tab, ui('starting'));
                    setOverlay(tab, ui('opening'), `${tab.name} ${uiText.isOpening || 'is opening'}.`, false);

                    try {
                        const launch = await fetchLaunch(tab);
                        tab.connectionName = launch.connectionName;
                        tab.encryptedData = launch.encryptedData;

                        const auth = await authenticate(tab);
                        const dataSource = auth.dataSource || 'json';
                        const websocketTunnel = new Guacamole.WebSocketTunnel('/guacamole/websocket-tunnel');
                        websocketTunnel.receiveTimeout = 8000;
                        websocketTunnel.unstableThreshold = 2500;
                        const httpTunnel = new Guacamole.HTTPTunnel('/guacamole/tunnel');
                        httpTunnel.receiveTimeout = 8000;
                        httpTunnel.unstableThreshold = 2500;
                        const tunnel = new Guacamole.ChainedTunnel(websocketTunnel, httpTunnel);

                        const client = new Guacamole.Client(tunnel);
                        tab.client = client;
                        const display = client.getDisplay();
                        tab.displayRoot.appendChild(display.getElement());
                        display.onresize = () => fitDisplay(tab);
                        updateTabActions();

                        tunnel.onstatechange = state => {
                            tab.tunnelState = tunnelStateName(state);
                            tab.lastMessage = `Tunnel ${tab.tunnelState.toLowerCase()}.`;
                            updateStatusBar();

                            if (state === Guacamole.Tunnel.State.UNSTABLE) {
                                setStatus(tab, uiText.unstable || 'Unstable');
                            }

                            if (state === Guacamole.Tunnel.State.CLOSED && !tab.terminal) {
                                finishTab(
                                    tab,
                                    tab.lastError ? ui('failed') : ui('connectionEnded'),
                                    tab.lastError || (uiText.tunnelClosed || 'The Guacamole tunnel was closed.')
                                );
                            }
                        };

                        tunnel.onerror = status => {
                            tab.lastError = normalizeStatus(status, uiText.tunnelInterrupted || 'The Guacamole tunnel was interrupted.');
                            tab.lastMessage = tab.lastError;
                            updateStatusBar();
                        };

                        client.onerror = status => {
                            tab.lastError = normalizeStatus(status, uiText.connectionFailedDetail || 'The connection failed.');
                            finishTab(tab, ui('failed'), tab.lastError);
                        };

                        client.onrequired = names => promptForRequiredArguments(tab, names);
                        client.onclipboard = (stream, mimetype) => receiveRemoteClipboard(tab, stream, mimetype);
                        client.onsync = () => {
                            tab.lastSyncAt = Date.now();
                            updateStatusBar();
                        };
                        client.onstatechange = state => {
                            setStatus(tab, statusName(state));
                            if (state === Guacamole.Client.State.CONNECTED) {
                                tab.connectedAt ??= Date.now();
                                tab.lastMessage = uiText.remoteConnected || 'Remote session is connected.';
                                hideOverlay(tab);
                                if (activeTabId === tab.id) {
                                    tab.panel.focus();
                                }
                                fitDisplay(tab);
                            }

                            if (state === Guacamole.Client.State.DISCONNECTED && !tab.terminal) {
                                finishTab(
                                    tab,
                                    tab.lastError ? ui('failed') : (uiText.connectionEnded || 'Connection ended'),
                                    tab.lastError || (uiText.sessionClosed || 'The session was closed.')
                                );
                            }
                        };

                        const mouse = new Guacamole.Mouse(display.getElement());
                        mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = state => client.sendMouseState(state, true);

                        if (Guacamole.Mouse.Touchscreen) {
                            const touchscreen = new Guacamole.Mouse.Touchscreen(display.getElement());
                            touchscreen.onmousedown = touchscreen.onmouseup = touchscreen.onmousemove = state => client.sendMouseState(state, true);
                        }

                        tab.keyboard = new Guacamole.Keyboard(tab.panel);
                        tab.keyboard.onkeydown = keysym => {
                            client.sendKeyEvent(1, keysym);
                            return false;
                        };
                        tab.keyboard.onkeyup = keysym => {
                            client.sendKeyEvent(0, keysym);
                            return false;
                        };

                        const size = viewport(tab);
                        tab.lastSentSize = size;
                        const parameters = new URLSearchParams({
                            token: auth.authToken,
                            GUAC_DATA_SOURCE: dataSource,
                            GUAC_ID: tab.connectionName,
                            GUAC_TYPE: 'c',
                            GUAC_WIDTH: size.width.toString(),
                            GUAC_HEIGHT: size.height.toString(),
                            GUAC_DPI: size.dpi.toString()
                        });

                        setStatus(tab, ui('connecting'));
                        client.connect(parameters.toString());
                        tab.watchdog = window.setInterval(() => {
                            if (!tabs.has(tab.id) || tab.terminal) {
                                return;
                            }

                            updateStatusBar();
                        }, 1000);
                    }
                    catch (error) {
                        finishTab(tab, ui('failed'), error instanceof Error ? error.message : (uiText.connectionStartFailed || 'The connection could not be started.'));
                    }
                }

                function startFileTab(tab) {
                    tab.terminal = false;
                    tab.client = null;
                    tab.keyboard = null;
                    tab.tunnelState = ui('fileApi');
                    tab.lastError = '';
                    tab.lastMessage = ui('fileManagerPreparing');
                    tab.connectedAt ??= Date.now();
                    tab.filePath = tab.initialFilePath || tab.filePath || '/';
                    tab.initialFilePath = '';
                    tab.displayRoot.className = 'file-display';
                    tab.displayRoot.replaceChildren();
                    setStatus(tab, ui('loading'));
                    setOverlay(tab, ui('fileManagerOpening'), `${tab.name} ${uiText.isLoading || 'is loading'}.`, false);

                    const manager = document.createElement('div');
                    manager.className = 'file-manager';
                    manager.innerHTML = `
                        ${Toolbar('file-toolbar',
                            ToolbarGroup('file-toolbar-main toolbar-group--grow',
                                ToolbarIconButton(ui('refresh'), fileIcon('refresh'), 'file-tool-button', Attr('data-file-action', 'refresh') + Attr('title', ui('refresh'))),
                                ToolbarInput('file-path-input', '/', ui('path')),
                                ToolbarMenu(
                                    'file-menu file-create-menu',
                                    'file-tool-button file-menu-trigger',
                                    ui('create'),
                                    fileIcon('plus'),
                                    Attr('title', ui('create')),
                                    ToolbarMenuItem(ui('directory'), fileIcon('mkdir'), 'file-action-button file-menu-item', Attr('data-file-action', 'create-directory') + Attr('title', ui('directory'))),
                                    ToolbarMenuItem(ui('file'), fileIcon('file'), 'file-action-button file-menu-item', Attr('data-file-action', 'create-file') + Attr('title', ui('file')))
                                ),
                                ToolbarMenu(
                                    'file-menu file-actions-menu',
                                    'file-tool-button file-menu-trigger',
                                    ui('actions'),
                                    fileIcon('menu'),
                                    Attr('title', ui('actions')),
                                    ToolbarMenuItem(ui('move'), fileIcon('move'), 'file-action-button file-menu-item', Attr('data-file-action', 'move') + Attr('title', ui('move')), true),
                                    ToolbarMenuItem(ui('copy'), fileIcon('copy'), 'file-action-button file-menu-item', Attr('data-file-action', 'copy') + Attr('title', ui('copy')), true),
                                    ToolbarMenuItem(ui('downloadZip'), fileIcon('archive'), 'file-action-button file-menu-item', Attr('data-file-action', 'zip') + Attr('title', ui('downloadZip')), true),
                                    ToolbarMenuItem(ui('delete'), fileIcon('delete'), 'file-action-button danger file-menu-item', Attr('data-file-action', 'delete-selected') + Attr('title', ui('deleteSelected')), true)
                                )
                            ),
                            ToolbarGroup('file-toolbar-transfer toolbar-group--end',
                                ToolbarUploadButton(ui('upload'), fileIcon('upload'), 'file-upload-button')
                            )
                        )}
                        <div class="file-message hidden"></div>
                        <div class="file-table-wrap">
                            <table class="file-table">
                                <thead>
                                    <tr><th class="file-select-heading" title="${escapeHtml(ui('selection'))}"></th><th>${escapeHtml(ui('name') || 'Name')}</th><th>${escapeHtml(ui('size'))}</th><th>${escapeHtml(ui('modified'))}</th><th class="file-actions-heading">${escapeHtml(ui('actions'))}</th></tr>
                                </thead>
                                <tbody></tbody>
                            </table>
                        </div>`;

                    tab.selectedFilePaths = new Set();
                    tab.fileUi = {
                        root: manager,
                        pathInput: manager.querySelector('.file-path-input'),
                        message: manager.querySelector('.file-message'),
                        tbody: manager.querySelector('tbody'),
                        uploadInput: manager.querySelector('.file-upload-input'),
                        createMenu: manager.querySelector('.file-create-menu'),
                        actionsMenu: manager.querySelector('.file-actions-menu'),
                        selectAllButton: null,
                        batchButtons: Array.from(manager.querySelectorAll('[data-file-action="zip"], [data-file-action="copy"], [data-file-action="move"], [data-file-action="delete-selected"]'))
                    };

                    manager.querySelector('[data-file-action="refresh"]').addEventListener('click', () => {
                        loadFilePath(tab, tab.filePath || '/');
                    });
                    manager.querySelector('[data-file-action="create-directory"]').addEventListener('click', async () => {
                        closeFileMenus(tab);
                        const name = window.prompt(ui('folderName'));
                        if (!name) {
                            return;
                        }

                        await runFileMutation(tab, async () => {
                            const response = await fetch(`/api/files/${tab.serverId}/mkdir`, {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    'X-Matgate-Csrf': csrfToken
                                },
                                body: JSON.stringify({ path: tab.filePath || '/', name })
                            });
                            await ensureFileResponse(response, ui('mkdirFailed'));
                        });
                    });
                    manager.querySelector('[data-file-action="create-file"]').addEventListener('click', async () => {
                        closeFileMenus(tab);
                        const name = window.prompt(ui('fileName'));
                        if (!name) {
                            return;
                        }

                        await runFileMutation(tab, async () => {
                            const response = await fetch(`/api/files/${tab.serverId}/create-file`, {
                                method: 'POST',
                                headers: {
                                    'Content-Type': 'application/json',
                                    'X-Matgate-Csrf': csrfToken
                                },
                                body: JSON.stringify({ path: tab.filePath || '/', name })
                            });
                            await ensureFileResponse(response, ui('actionFailed'));
                        });
                    });
                    tab.fileUi.pathInput.addEventListener('keydown', event => {
                        if (event.key === 'Enter') {
                            event.preventDefault();
                            loadFilePath(tab, tab.fileUi.pathInput.value || '/');
                        }
                    });
                    tab.fileUi.uploadInput.addEventListener('change', async () => {
                        const selectedFiles = Array.from(tab.fileUi.uploadInput.files || []);
                        if (!selectedFiles.length) {
                            return;
                        }

                        await runFileMutation(tab, async () => {
                            for (const file of selectedFiles) {
                                const formData = new FormData();
                                formData.append('path', tab.filePath || '/');
                                formData.append('file', file);
                                const response = await fetch(`/api/files/${tab.serverId}/upload`, {
                                    method: 'POST',
                                    headers: { 'X-Matgate-Csrf': csrfToken },
                                    body: formData
                                });
                                await ensureFileResponse(response, ui('uploadFailed'));
                            }

                            tab.fileUi.uploadInput.value = '';
                        });
                    });
                    manager.querySelector('[data-file-action="move"]').addEventListener('click', () => {
                        closeFileMenus(tab);
                        copyOrMoveSelected(tab, 'move');
                    });
                    manager.querySelector('[data-file-action="copy"]').addEventListener('click', () => {
                        closeFileMenus(tab);
                        copyOrMoveSelected(tab, 'copy');
                    });
                    manager.querySelector('[data-file-action="zip"]').addEventListener('click', () => {
                        closeFileMenus(tab);
                        createZipArchive(tab);
                    });
                    manager.querySelector('[data-file-action="delete-selected"]').addEventListener('click', () => {
                        closeFileMenus(tab);
                        deleteSelectedEntries(tab);
                    });
                    tab.displayRoot.appendChild(manager);
                    hideOverlay(tab);
                    loadFilePath(tab, tab.filePath);
                }

                function startWebsiteTab(tab) {
                    tab.terminal = false;
                    tab.client = null;
                    tab.keyboard = null;
                    tab.tunnelState = uiText.websiteProxy || 'Website proxy';
                    tab.lastError = '';
                    tab.lastMessage = uiText.websiteOpening || 'Opening website';
                    tab.connectedAt ??= Date.now();
                    tab.lastSyncAt = null;
                    tab.displayRoot.className = 'website-display';
                    setStatus(tab, ui('loading'));
                    setOverlay(tab, uiText.websiteOpening || 'Opening website', `${tab.name} ${uiText.isLoading || 'is loading'}.`, false);

                    if (!tab.websiteUi) {
                        return;
                    }

                    const proxyUrl = `/website/${tab.serverId}/${tab.id}/proxy/`;
                    tab.currentUrl = proxyUrl;
                    tab.displayUrl = buildWebsiteDisplayUrl(tab, proxyUrl);
                    tab.websiteUi.address.value = tab.displayUrl;
                    tab.websiteUi.frame.src = proxyUrl;
                }

                async function loadFilePath(tab, path) {
                    if (!tab || !tab.fileUi) {
                        return false;
                    }

                    setStatus(tab, ui('loading'));
                    tab.lastError = '';
                    tab.lastMessage = ui('fileListLoading');
                    updateStatusBar();
                    setFileMessage(tab, '');

                    try {
                        const response = await fetch(`/api/files/${tab.serverId}/list?path=${encodeURIComponent(path || '/')}`, {
                            cache: 'no-store'
                        });
                        await ensureFileResponse(response, uiText.fileListLoadFailed || 'Could not load file list.');
                        const payload = await response.json();
                        tab.filePath = payload.path || payload.Path || '/';
                        tab.fileParentPath = payload.parentPath || payload.ParentPath || '/';
                        tab.lastSyncAt = Date.now();
                        tab.lastMessage = ui('fileListUpdated');
                        tab.fileUi.pathInput.value = tab.filePath;
                        tab.selectedFilePaths.clear();
                        renderFileEntries(tab, payload.entries || payload.Entries || []);
                        updateFileSelectionActions(tab);
                        saveWorkspaceTabs();
                        setStatus(tab, ui('ready'));
                        return true;
                    }
                    catch (error) {
                        const message = error instanceof Error ? error.message : ui('fileAccessFailed');
                        tab.lastError = message;
                        setStatus(tab, uiText.error || 'Error');
                        setFileMessage(tab, message, 'error');
                        if (!tab.fileUi.tbody.children.length) {
                            setOverlay(tab, ui('fileAccessFailed'), message, true);
                        }
                        return false;
                    }
                }

                function renderFileEntries(tab, entries) {
                    tab.fileUi.tbody.replaceChildren();

                    const parentRow = document.createElement('tr');
                    parentRow.className = 'is-directory parent-directory';
                    const hasParent = (tab.filePath || '/') !== '/';
                    if (!hasParent) {
                        parentRow.classList.add('is-root-directory');
                    }

                    const selectCell = document.createElement('td');
                    selectCell.className = 'file-select-cell';

                    const nameCell = document.createElement('td');
                    const nameButton = document.createElement('button');
                    nameButton.type = 'button';
                    nameButton.className = 'file-name-button';
                    nameButton.innerHTML = `${fileIcon('parent')}<span>..</span>`;
                    nameButton.disabled = !hasParent;
                    nameButton.title = hasParent ? (tab.fileParentPath || '/') : (tab.filePath || '/');
                    nameButton.addEventListener('click', () => {
                        if (!hasParent) {
                            return;
                        }

                        loadFilePath(tab, tab.fileParentPath || '/');
                    });
                    nameCell.appendChild(nameButton);

                    const sizeCell = document.createElement('td');
                    sizeCell.textContent = '-';
                    const modifiedCell = document.createElement('td');
                    modifiedCell.textContent = '-';
                    const { actionCell, actions } = createFileActionCell();
                    const selectAllButton = fileActionButton('check', ui('selectAll'), '', () => toggleFileSelectionAll(tab));
                    tab.fileUi.selectAllButton = selectAllButton;
                    actions.appendChild(selectAllButton);
                    parentRow.append(selectCell, nameCell, sizeCell, modifiedCell, actionCell);
                    tab.fileUi.tbody.appendChild(parentRow);

                    if (!entries.length) {
                        const row = document.createElement('tr');
                        row.innerHTML = `<td colspan="5" class="file-empty">${escapeHtml(ui('emptyFolder'))}</td>`;
                        tab.fileUi.tbody.appendChild(row);
                        return;
                    }

                    for (const entry of entries) {
                        const name = entry.name || entry.Name || '';
                        const entryPath = entry.path || entry.Path || '/';
                        const isDirectory = Boolean(entry.isDirectory ?? entry.IsDirectory);
                        const size = entry.size ?? entry.Size;
                        const modifiedAt = entry.modifiedAt || entry.ModifiedAt || '';
                        const row = document.createElement('tr');
                        row.className = isDirectory ? 'is-directory' : 'is-file';

                        const selectCell = document.createElement('td');
                        selectCell.className = 'file-select-cell';
                        if (!isSmbShareRootEntry(tab, isDirectory)) {
                            const checkbox = document.createElement('input');
                            checkbox.type = 'checkbox';
                            checkbox.className = 'file-select-entry';
                            checkbox.value = entryPath;
                            checkbox.addEventListener('change', () => setFileSelection(tab, entryPath, checkbox.checked));
                            selectCell.appendChild(checkbox);
                        }

                        const nameCell = document.createElement('td');
                        const nameButton = document.createElement('button');
                        nameButton.type = 'button';
                        nameButton.className = 'file-name-button';
                        nameButton.innerHTML = `${fileIcon(isDirectory ? 'folder' : 'file')}<span>${escapeHtml(name)}</span>`;
                        nameButton.addEventListener('click', () => {
                            if (isDirectory) {
                                loadFilePath(tab, entryPath);
                            }
                            else {
                                viewFileEntry(tab, entryPath);
                            }
                        });
                        nameCell.appendChild(nameButton);

                        const sizeCell = document.createElement('td');
                        sizeCell.textContent = isDirectory ? '-' : formatFileSize(size);

                        const modifiedCell = document.createElement('td');
                        modifiedCell.textContent = formatFileDate(modifiedAt);

                        const { actionCell, actions } = createFileActionCell();
                        if (isDirectory) {
                            actions.appendChild(fileActionButton('folder', ui('open'), '', () => loadFilePath(tab, entryPath)));
                        }
                        else {
                            actions.append(
                                fileActionButton('view', ui('view'), '', () => viewFileEntry(tab, entryPath)),
                                fileActionButton('download', ui('download'), '', () => downloadFileEntry(tab, entryPath)));
                            if (isArchiveFileName(name)) {
                                actions.appendChild(fileActionButton('archive', ui('unzip'), '', () => unzipFileEntry(tab, entryPath)));
                            }
                        }

                        if (!isSmbShareRootEntry(tab, isDirectory)) {
                            actions.appendChild(fileActionButton('delete', ui('delete'), 'danger', () => deleteFileEntry(tab, entryPath, name)));
                        }

                        row.append(selectCell, nameCell, sizeCell, modifiedCell, actionCell);
                        tab.fileUi.tbody.appendChild(row);
                    }
                }

                function isSmbShareRootEntry(tab, isDirectory) {
                    return isDirectory && tab.protocol === 'SMB' && (tab.filePath || '/') === '/';
                }

                function isArchiveFileName(name) {
                    const normalized = (name || '').toString().trim().toLowerCase();
                    return archiveExtensions.some(extension => normalized.endsWith(extension));
                }

                function closeFileMenus(tab) {
                    tab.fileUi?.createMenu?.removeAttribute('open');
                    tab.fileUi?.actionsMenu?.removeAttribute('open');
                }

                function setFileSelection(tab, path, selected, refresh = true) {
                    if (selected) {
                        tab.selectedFilePaths.add(path);
                    }
                    else {
                        tab.selectedFilePaths.delete(path);
                    }

                    if (refresh) {
                        updateFileSelectionActions(tab);
                    }
                }

                function setAllFileSelections(tab, selected) {
                    const checkboxes = Array.from(tab.fileUi.tbody.querySelectorAll('.file-select-entry'));
                    for (const checkbox of checkboxes) {
                        checkbox.checked = selected;
                        setFileSelection(tab, checkbox.value, selected, false);
                    }

                    updateFileSelectionActions(tab);
                }

                function toggleFileSelectionAll(tab) {
                    const checkboxes = Array.from(tab.fileUi.tbody.querySelectorAll('.file-select-entry'));
                    if (!checkboxes.length) {
                        return;
                    }

                    const allSelected = checkboxes.every(checkbox => checkbox.checked);
                    setAllFileSelections(tab, !allSelected);
                }

                function selectedFilePaths(tab) {
                    return Array.from(tab.selectedFilePaths || []);
                }

                function updateFileSelectionActions(tab) {
                    if (!tab.fileUi) {
                        return;
                    }

                    const count = selectedFilePaths(tab).length;
                    const selectableCheckboxes = Array.from(tab.fileUi.tbody.querySelectorAll('.file-select-entry'));
                    const selectableCount = selectableCheckboxes.length;
                    const allSelected = selectableCount > 0 && count === selectableCount;
                    if (tab.fileUi.selectAllButton) {
                        tab.fileUi.selectAllButton.disabled = selectableCount === 0;
                        tab.fileUi.selectAllButton.classList.toggle('is-active', allSelected);
                        tab.fileUi.selectAllButton.innerHTML = `${fileIcon('check')}<span>${escapeHtml(allSelected ? ui('clearSelection') : ui('selectAll'))}</span>`;
                        tab.fileUi.selectAllButton.title = allSelected ? ui('clearSelection') : ui('selectAll');
                    }

                    for (const button of tab.fileUi.batchButtons) {
                        button.disabled = count === 0;
                    }

                    tab.lastMessage = count > 0 ? `${count} ${ui('selected')}` : ui('ready');
                    updateStatusBar();
                }

                function suggestArchiveName(paths) {
                    const firstPath = (paths[0] || '').toString().replace(/\/+$/, '');
                    const leaf = firstPath.split('/').filter(Boolean).pop() || 'matgate-archive';
                    const dotIndex = leaf.lastIndexOf('.');
                    const baseName = dotIndex > 0 ? leaf.slice(0, dotIndex) : leaf;
                    return `${baseName || 'matgate-archive'}.zip`;
                }

                async function createZipArchive(tab) {
                    const paths = selectedFilePaths(tab);
                    if (!paths.length) {
                        return;
                    }

                    const defaultName = suggestArchiveName(paths);
                    const archiveName = window.prompt(ui('archiveName'), defaultName);
                    if (archiveName === null) {
                        return;
                    }

                    const trimmedName = archiveName.trim();
                    if (!trimmedName) {
                        return;
                    }

                    const created = await runFileMutation(tab, async () => {
                        const response = await fetch(`/api/files/${tab.serverId}/zip`, {
                            method: 'POST',
                            headers: {
                                'Content-Type': 'application/json',
                                'X-Matgate-Csrf': csrfToken
                            },
                            body: JSON.stringify({
                                destinationPath: tab.filePath || '/',
                                archiveName: trimmedName,
                                paths
                            })
                        });
                        await ensureFileResponse(response, ui('actionFailed'));
                    });
                    if (created) {
                        flashStatus(tab, ui('zipCreated'));
                    }
                }

                function hiddenInput(name, value) {
                    const input = document.createElement('input');
                    input.type = 'hidden';
                    input.name = name;
                    input.value = value;
                    return input;
                }

                async function copyOrMoveSelected(tab, action) {
                    const paths = selectedFilePaths(tab);
                    if (!paths.length) {
                        return;
                    }

                    const destinationPath = window.prompt(ui('destinationPath'), tab.filePath || '/');
                    if (!destinationPath) {
                        return;
                    }

                    await runFileMutation(tab, async () => {
                        const response = await fetch(`/api/files/${tab.serverId}/${action}`, {
                            method: 'POST',
                            headers: {
                                'Content-Type': 'application/json',
                                'X-Matgate-Csrf': csrfToken
                            },
                            body: JSON.stringify({ paths, destinationPath })
                        });
                        await ensureFileResponse(response, action === 'copy' ? ui('copyFailed') : ui('moveFailed'));
                    });
                }

                async function deleteSelectedEntries(tab) {
                    const paths = selectedFilePaths(tab);
                    if (!paths.length || !window.confirm(`${paths.length} ${ui('deleteConfirm')}`)) {
                        return;
                    }

                    await runFileMutation(tab, async () => {
                        const response = await fetch(`/api/files/${tab.serverId}/delete`, {
                            method: 'POST',
                            headers: {
                                'Content-Type': 'application/json',
                                'X-Matgate-Csrf': csrfToken
                            },
                            body: JSON.stringify({ paths })
                        });
                        await ensureFileResponse(response, ui('deleteFailed'));
                    });
                }

                function createFileActionCell() {
                    const actionCell = document.createElement('td');
                    actionCell.className = 'file-actions-cell';
                    const actions = document.createElement('div');
                    actions.className = 'file-row-actions';
                    actionCell.appendChild(actions);
                    return { actionCell, actions };
                }

                function fileIcon(name) {
                    return fileIcons[name] || '';
                }

                function fileActionButton(iconName, label, className, onClick) {
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = ['file-action-button', className].filter(Boolean).join(' ');
                    button.title = label;
                    button.innerHTML = `${fileIcon(iconName)}<span>${escapeHtml(label)}</span>`;
                    button.addEventListener('click', onClick);
                    return button;
                }

                function downloadFileEntry(tab, path) {
                    window.location.href = `/api/files/${tab.serverId}/download?path=${encodeURIComponent(path)}`;
                    flashStatus(tab, ui('downloadStarted'));
                }

                function viewFileEntry(tab, path) {
                    openFileViewerDialog(tab, path);
                    flashStatus(tab, ui('viewStarted'));
                }

                async function unzipFileEntry(tab, path) {
                    await runFileMutation(tab, async () => {
                        const response = await fetch(`/api/files/${tab.serverId}/extract`, {
                            method: 'POST',
                            headers: {
                                'Content-Type': 'application/json',
                                'X-Matgate-Csrf': csrfToken
                            },
                            body: JSON.stringify({ path, destinationPath: tab.filePath || '/' })
                        });
                        await ensureFileResponse(response, ui('actionFailed'));
                    });
                }

                async function deleteFileEntry(tab, path, name) {
                    if (!window.confirm(`${name} ${ui('deleteConfirm')}`)) {
                        return;
                    }

                    await runFileMutation(tab, async () => {
                        const response = await fetch(`/api/files/${tab.serverId}?path=${encodeURIComponent(path)}`, {
                            method: 'DELETE',
                            headers: { 'X-Matgate-Csrf': csrfToken }
                        });
                        await ensureFileResponse(response, ui('deleteFailed'));
                    });
                }

                async function runFileMutation(tab, action) {
                    setStatus(tab, uiText.working || 'Working');
                    setFileMessage(tab, '');
                    try {
                        await action();
                        tab.lastMessage = ui('actionDone');
                        tab.lastSyncAt = Date.now();
                        return await loadFilePath(tab, tab.filePath || '/');
                    }
                    catch (error) {
                        const message = error instanceof Error ? error.message : ui('actionFailed');
                        tab.lastError = message;
                        setStatus(tab, uiText.error || 'Error');
                        setFileMessage(tab, message, 'error');
                        return false;
                    }
                }

                async function ensureFileResponse(response, fallback) {
                    if (response.ok) {
                        return;
                    }

                    let message = fallback;
                    try {
                        const payload = await response.json();
                        message = payload.error || payload.Error || message;
                    }
                    catch {
                        // Keep fallback.
                    }

                    throw new Error(message);
                }

                function setFileMessage(tab, message, kind = '') {
                    if (!tab.fileUi) {
                        return;
                    }

                    tab.fileUi.message.textContent = message;
                    tab.fileUi.message.classList.toggle('hidden', !message);
                    tab.fileUi.message.classList.toggle('error', Boolean(message) && kind === 'error');
                    updateStatusBar();
                }

                function formatFileSize(value) {
                    if (value === null || value === undefined || Number.isNaN(Number(value))) {
                        return '-';
                    }

                    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
                    let size = Number(value);
                    let unit = 0;
                    while (size >= 1024 && unit < units.length - 1) {
                        size /= 1024;
                        unit += 1;
                    }

                    return `${size.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
                }

                function formatFileDate(value) {
                    if (!value) {
                        return '-';
                    }

                    const date = new Date(value);
                    return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString();
                }

                function restartTab(tab) {
                    tab.terminal = true;
                    window.clearInterval(tab.watchdog);
                    if (tab.client) {
                        tab.client.disconnect();
                    }
                    tab.client = null;
                    startTab(tab);
                }

                function finishTab(tab, headline, text) {
                    tab.terminal = true;
                    window.clearInterval(tab.watchdog);
                    setStatus(tab, ui('disconnected'));
                    setOverlay(tab, headline, text, true);
                    updateTabActions();
                }

                function promptForRequiredArguments(tab, names) {
                    credentialTab = tab;
                    activateTab(tab.id);
                    credentialFields.replaceChildren();
                    credentialDialog.classList.remove('hidden');

                    for (const name of names) {
                        const label = document.createElement('label');
                        label.textContent = name === 'username' ? ui('username')
                            : name === 'password' ? ui('password')
                            : name === 'domain' ? 'Domain'
                            : name;

                        const input = document.createElement('input');
                        input.name = name;
                        input.required = true;
                        input.autocomplete = 'off';
                        if (name.toLowerCase().includes('password')) {
                            input.type = 'password';
                        }

                        label.appendChild(input);
                        credentialFields.appendChild(label);
                    }

                    const firstInput = credentialFields.querySelector('input');
                    if (firstInput) {
                        firstInput.focus();
                    }
                }

                function sendArgument(tab, name, value) {
                    const stream = tab.client.createArgumentValueStream('text/plain', name);
                    const writer = new Guacamole.StringWriter(stream);
                    writer.sendText(value);
                    writer.sendEnd();
                }

                function sendClipboardText(tab, text) {
                    if (!tab || !tab.client || !text) {
                        return false;
                    }

                    const stream = tab.client.createClipboardStream('text/plain');
                    const writer = new Guacamole.StringWriter(stream);
                    writer.sendText(text);
                    writer.sendEnd();
                    flashStatus(tab, uiText.clipboardSent || 'Clipboard sent');
                    return true;
                }

                function receiveRemoteClipboard(tab, stream, mimetype) {
                    if (!mimetype || !mimetype.toLowerCase().startsWith('text/')) {
                        return;
                    }

                    const reader = new Guacamole.StringReader(stream);
                    let value = '';

                    reader.ontext = text => {
                        value += text;
                    };

                    reader.onend = async () => {
                        tab.remoteClipboard = value;
                        if (await tryWriteBrowserClipboard(value)) {
                            flashStatus(tab, uiText.clipboardReceived || 'Clipboard received');
                        }
                        else {
                            flashStatus(tab, uiText.remoteClipboardReady || 'Remote clipboard ready');
                            if (tab.id === activeTabId) {
                                clipboardText.value = value;
                            }
                        }
                    };
                }

                async function tryWriteBrowserClipboard(text) {
                    if (!navigator.clipboard || !navigator.clipboard.writeText) {
                        return false;
                    }

                    try {
                        await navigator.clipboard.writeText(text);
                        return true;
                    }
                    catch {
                        return false;
                    }
                }

                async function readBrowserClipboard() {
                    if (!navigator.clipboard || !navigator.clipboard.readText) {
                        throw new Error('Clipboard API unavailable');
                    }

                    return await navigator.clipboard.readText();
                }

                function openClipboardDialog(prefill = '') {
                    clipboardText.value = prefill;
                    clipboardDialog.classList.remove('hidden');
                    clipboardText.focus();
                    clipboardText.select();
                }

                function closeClipboardDialog() {
                    clipboardDialog.classList.add('hidden');
                }

                function flashStatus(tab, text) {
                    window.clearTimeout(tab.statusTimer);
                    const previous = tab.status;
                    setStatus(tab, text);
                    tab.statusTimer = window.setTimeout(() => {
                        if (tabs.has(tab.id) && tab.status === text) {
                            setStatus(tab, previous || 'Verbunden');
                        }
                    }, 1800);
                }

                function closeCredentialDialog() {
                    credentialDialog.classList.add('hidden');
                    credentialFields.replaceChildren();
                    credentialTab = null;
                }

                function escapeHtml(value) {
                    const div = document.createElement('div');
                    div.textContent = value;
                    return div.innerHTML;
                }

                credentialDialog.addEventListener('submit', event => {
                    event.preventDefault();
                    if (!credentialTab || !credentialTab.client) {
                        closeCredentialDialog();
                        return;
                    }

                    const formData = new FormData(credentialDialog);
                    for (const [name, value] of formData.entries()) {
                        sendArgument(credentialTab, name, value.toString());
                    }

                    const tab = credentialTab;
                    closeCredentialDialog();
                    setOverlay(tab, uiText.connectionContinues || 'Connection continues', uiText.credentialsSubmitted || 'Credentials were submitted.', false);
                    tab.panel.focus();
                });

                credentialCancel.addEventListener('click', closeCredentialDialog);
                clipboardDialog.addEventListener('submit', event => {
                    event.preventDefault();
                    const activeTab = tabs.get(activeTabId);
                    if (sendClipboardText(activeTab, clipboardText.value)) {
                        closeClipboardDialog();
                        activeTab.panel.focus();
                    }
                });
                clipboardClose.addEventListener('click', closeClipboardDialog);
                window.addEventListener('resize', scheduleResize);
                window.addEventListener('beforeunload', () => {
                    for (const tab of tabs.values()) {
                        if (tab.client) {
                            tab.client.disconnect();
                        }
                    }
                });

                wireShellNavigation();
                activateNewConnectionTab();
                showView('home', false);
                restoreShellTabs();
                measureGatewayLatency();
                window.setInterval(measureGatewayLatency, 5000);

                const restoredWorkspace = loadWorkspaceTabs();
                const tabStatesToOpen = [...restoredWorkspace.tabs];
                if (initialOpenServerId && !tabStatesToOpen.some(tab => tab.serverId === initialOpenServerId)) {
                    tabStatesToOpen.push({ serverId: initialOpenServerId, filePath: '' });
                }

                const openedTabs = [];
                for (const tabState of tabStatesToOpen) {
                    const tab = openServer(tabState.serverId, tabState.filePath || '', tabState.tabId || '');
                    if (tab) {
                        openedTabs.push(tab);
                    }
                }

                const preferredTab = openedTabs.find(tab => tab.id === restoredWorkspace.activeTabId)
                    || openedTabs.find(tab => tab.serverId === (initialOpenServerId || restoredWorkspace.activeServerId))
                    || openedTabs[0];
                if (preferredTab) {
                    activateTab(preferredTab.id);
                }
                else {
                    activateNewConnectionTab();
                }

                restoreTabOrder();
                saveTabOrder();

                if (initialOpenServerId) {
                    history.replaceState({ view: 'home' }, '', '/');
                }
            })();
            </script>
            """;

        return Layout(context, user, T(context, "Connections"), body, "session-main");
    }

    public string ConnectionSession(
        HttpContext context,
        MatgateUser user,
        ServerEndpoint server,
        GuacamoleLaunchResult launch)
    {
        var encryptedData = JsonSerializer.Serialize(launch.EncryptedData ?? "");
        var connectionName = JsonSerializer.Serialize(launch.ConnectionName ?? "");
        var reconnectUrl = JsonSerializer.Serialize($"/connect/{server.Id}");
        var title = $"{server.Name} - Verbindung";

        var body = $$"""
            <section class="session-page">
                <div class="session-bar">
                    <div>
                        <span class="badge">{{E(ServerProtocolLabel(server.Protocol))}}</span>
                        <strong>{{E(server.Name)}}</strong>
                        <span class="muted">{{E(ServerTargetValue(server))}}</span>
                    </div>
                    <div class="session-actions">
                        <a class="button" href="/">Dashboard</a>
                        <button id="fullscreen-button" type="button">Vollbild</button>
                        <button id="disconnect-button" type="button" class="danger">Trennen</button>
                    </div>
                </div>
                <div id="guac-stage" class="guac-stage" tabindex="0">
                    <div id="guac-display" class="guac-display"></div>
                    <div id="connection-overlay" class="connection-overlay">
                        <div class="connection-dialog">
                            <h1 id="connection-title">Verbindung wird aufgebaut</h1>
                            <p id="connection-message">Matgate bereitet die Sitzung vor.</p>
                            <div id="connection-actions" class="actions hidden">
                                <a class="button primary" href="{{A($"/connect/{server.Id}")}}">Neu verbinden</a>
                                <a class="button" href="/">Dashboard</a>
                            </div>
                        </div>
                    </div>
                    <form id="credential-dialog" class="credential-dialog hidden">
                        <h2>Zugangsdaten</h2>
                        <div id="credential-fields" class="stack"></div>
                        <div class="actions">
                            <button type="submit" class="primary">Senden</button>
                            <a class="button" href="/">Abbrechen</a>
                        </div>
                    </form>
                </div>
            </section>
            <script src="/guacamole/guacamole-common-js/all.min.js"></script>
            <script>
            (() => {
                const encryptedData = {{encryptedData}};
                const connectionName = {{connectionName}};
                const reconnectUrl = {{reconnectUrl}};
                const stage = document.getElementById('guac-stage');
                const displayRoot = document.getElementById('guac-display');
                const overlay = document.getElementById('connection-overlay');
                const title = document.getElementById('connection-title');
                const message = document.getElementById('connection-message');
                const actions = document.getElementById('connection-actions');
                const credentialDialog = document.getElementById('credential-dialog');
                const credentialFields = document.getElementById('credential-fields');
                const fullscreenButton = document.getElementById('fullscreen-button');
                const disconnectButton = document.getElementById('disconnect-button');

                let client = null;
                let keyboard = null;
                let resizeTimer = null;
                let terminalState = false;
                let lastError = '';

                function viewport() {
                    const rect = stage.getBoundingClientRect();
                    return {
                        width: Math.max(320, Math.floor(rect.width)),
                        height: Math.max(240, Math.floor(rect.height)),
                        dpi: 96
                    };
                }

                function setOverlay(headline, text, showActions) {
                    title.textContent = headline;
                    message.textContent = text;
                    actions.classList.toggle('hidden', !showActions);
                    overlay.classList.remove('hidden');
                }

                function hideOverlay() {
                    overlay.classList.add('hidden');
                }

                function finish(headline, text) {
                    terminalState = true;
                    setOverlay(headline, text, true);
                }

                function fitDisplay() {
                    if (!client) {
                        return;
                    }

                    const display = client.getDisplay();
                    const width = display.getWidth();
                    const height = display.getHeight();
                    if (!width || !height) {
                        return;
                    }

                    const rect = stage.getBoundingClientRect();
                    const scale = Math.min(1, rect.width / width, rect.height / height);
                    display.scale(Math.max(0.1, scale));
                }

                function sendDisplaySize() {
                    if (!client) {
                        return;
                    }

                    const size = viewport();
                    client.sendSize(size.width, size.height);
                    fitDisplay();
                }

                function scheduleResize() {
                    window.clearTimeout(resizeTimer);
                    resizeTimer = window.setTimeout(sendDisplaySize, 200);
                }

                function sendArgument(name, value) {
                    const stream = client.createArgumentValueStream('text/plain', name);
                    const writer = new Guacamole.StringWriter(stream);
                    writer.sendText(value);
                    writer.sendEnd();
                }

                function promptForRequiredArguments(names) {
                    credentialFields.replaceChildren();
                    credentialDialog.classList.remove('hidden');
                    overlay.classList.add('hidden');

                    for (const name of names) {
                        const label = document.createElement('label');
                        label.textContent = name === 'username' ? 'Benutzername'
                            : name === 'password' ? 'Passwort'
                            : name === 'domain' ? 'Domain'
                            : name;

                        const input = document.createElement('input');
                        input.name = name;
                        input.required = true;
                        input.autocomplete = 'off';
                        if (name.toLowerCase().includes('password')) {
                            input.type = 'password';
                        }

                        label.appendChild(input);
                        credentialFields.appendChild(label);
                    }

                    const firstInput = credentialFields.querySelector('input');
                    if (firstInput) {
                        firstInput.focus();
                    }
                }

                credentialDialog.addEventListener('submit', event => {
                    event.preventDefault();
                    const formData = new FormData(credentialDialog);
                    for (const [name, value] of formData.entries()) {
                        sendArgument(name, value.toString());
                    }

                    credentialDialog.classList.add('hidden');
                    setOverlay('Verbindung wird fortgesetzt', 'Die Zugangsdaten wurden uebergeben.', false);
                    stage.focus();
                });

                async function authenticate() {
                    const response = await fetch('/guacamole/api/tokens', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                        body: new URLSearchParams({ data: encryptedData })
                    });

                    if (!response.ok) {
                        throw new Error('Guacamole hat den Starttoken abgelehnt.');
                    }

                    return await response.json();
                }

                async function start() {
                    if (!window.Guacamole) {
                        finish('Verbindung nicht moeglich', 'Der Guacamole-Webclient konnte nicht geladen werden.');
                        return;
                    }

                    terminalState = false;
                    lastError = '';
                    credentialDialog.classList.add('hidden');
                    setOverlay('Verbindung wird aufgebaut', 'Matgate bereitet die Sitzung vor.', false);
                    displayRoot.replaceChildren();

                    try {
                        const auth = await authenticate();
                        const dataSource = auth.dataSource || 'json';
                        const size = viewport();
                        const tunnel = new Guacamole.ChainedTunnel(
                            new Guacamole.WebSocketTunnel('/guacamole/websocket-tunnel'),
                            new Guacamole.HTTPTunnel('/guacamole/tunnel')
                        );

                        client = new Guacamole.Client(tunnel);
                        const display = client.getDisplay();
                        displayRoot.appendChild(display.getElement());
                        display.onresize = fitDisplay;

                        tunnel.onerror = status => {
                            lastError = status && status.message ? status.message : 'Der Guacamole-Tunnel wurde unterbrochen.';
                        };

                        client.onerror = status => {
                            lastError = status && status.message ? status.message : 'Die Verbindung ist fehlgeschlagen.';
                        };

                        client.onrequired = promptForRequiredArguments;
                        client.onstatechange = state => {
                            if (state === Guacamole.Client.State.CONNECTED) {
                                hideOverlay();
                                stage.focus();
                                fitDisplay();
                            }

                            if (state === Guacamole.Client.State.DISCONNECTED && !terminalState) {
                                finish(
                                    lastError ? 'Verbindung fehlgeschlagen' : 'Verbindung beendet',
                                    lastError || 'Die Sitzung wurde geschlossen.'
                                );
                            }
                        };

                        const mouse = new Guacamole.Mouse(display.getElement());
                        mouse.onmousedown = mouse.onmouseup = mouse.onmousemove = state => {
                            client.sendMouseState(state, true);
                        };

                        if (Guacamole.Mouse.Touchscreen) {
                            const touchscreen = new Guacamole.Mouse.Touchscreen(display.getElement());
                            touchscreen.onmousedown = touchscreen.onmouseup = touchscreen.onmousemove = state => {
                                client.sendMouseState(state, true);
                            };
                        }

                        keyboard = new Guacamole.Keyboard(stage);
                        keyboard.onkeydown = keysym => {
                            client.sendKeyEvent(1, keysym);
                            return false;
                        };
                        keyboard.onkeyup = keysym => {
                            client.sendKeyEvent(0, keysym);
                            return false;
                        };

                        const parameters = new URLSearchParams({
                            token: auth.authToken,
                            GUAC_DATA_SOURCE: dataSource,
                            GUAC_ID: connectionName,
                            GUAC_TYPE: 'c',
                            GUAC_WIDTH: size.width.toString(),
                            GUAC_HEIGHT: size.height.toString(),
                            GUAC_DPI: size.dpi.toString()
                        });

                        client.connect(parameters.toString());
                        window.addEventListener('resize', scheduleResize);
                    }
                    catch (error) {
                        finish('Verbindung fehlgeschlagen', error instanceof Error ? error.message : 'Die Verbindung konnte nicht gestartet werden.');
                    }
                }

                fullscreenButton.addEventListener('click', () => {
                    if (!document.fullscreenElement) {
                        stage.requestFullscreen().then(scheduleResize).catch(() => {});
                    }
                    else {
                        document.exitFullscreen().then(scheduleResize).catch(() => {});
                    }
                });

                disconnectButton.addEventListener('click', () => {
                    if (client) {
                        client.disconnect();
                    }
                    window.location.href = '/';
                });

                stage.addEventListener('click', () => stage.focus());
                window.addEventListener('beforeunload', () => {
                    if (client) {
                        client.disconnect();
                    }
                });

                start();
            })();
            </script>
            """;

        return Layout(context, user, title, body, "session-main");
    }

    private static string Layout(HttpContext context, MatgateUser? user, string title, string body, string mainClass = "")
    {
        var language = Language(context);
        var theme = Theme(context, user);
        var requestPath = context.Request.Path.Value ?? "/";
        var displayName = user is null ? "" : string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
        var canManageAdminArea = user is not null && (user.IsAdmin || user.CanManageServers);
        var serversActive = requestPath.StartsWith("/admin/servers", StringComparison.OrdinalIgnoreCase);
        var usersActive = requestPath.StartsWith("/admin/users", StringComparison.OrdinalIgnoreCase);
        var toolsActive = requestPath.StartsWith("/tools", StringComparison.OrdinalIgnoreCase);
        var accountActive = requestPath.StartsWith("/account", StringComparison.OrdinalIgnoreCase);
        var serversClass = serversActive ? " active" : "";
        var usersClass = usersActive ? " active" : "";
        var toolsClass = toolsActive ? " active" : "";
        var accountClass = accountActive ? " active" : "";
        var shellTabs = user is null ? "" : $$"""
            <nav class="shell-tabs" aria-label="Primary">
                {{(canManageAdminArea ? $"""<a class="shell-tab{serversClass}" href="/admin/servers" data-shell-open-tab="1" data-shell-title="{A(T(context, "Servers"))}">{Icon("server")}<span>{T(context, "Servers")}</span></a>""" : "")}}
                {{(user!.IsAdmin ? $"""<a class="shell-tab{usersClass}" href="/admin/users" data-shell-open-tab="1" data-shell-title="{A(T(context, "Users"))}">{Icon("users")}<span>{T(context, "Users")}</span></a>""" : "")}}
                <a class="shell-tab{{toolsClass}}" href="/tools" data-shell-open-tab="1" data-shell-title="{{A(T(context, "Tools"))}}">{{Icon("wrench")}}<span>{{T(context, "Tools")}}</span></a>
                <a class="shell-tab{{accountClass}}" href="/account" data-shell-open-tab="1" data-shell-title="{{A(T(context, "Account"))}}">{{Icon("user")}}<span class="account-name">{{E(displayName)}}</span></a>
            </nav>
            """;
        var shellActions = user is null ? "" : $$"""
            <div class="shell-actions">
                <form method="post" action="/logout" class="inline">
                    {{Csrf(context)}}
                    <button type="submit" class="shell-action">{{Icon("logout")}}<span>{{T(context, "Logout")}}</span></button>
                </form>
            </div>
            """;
        var navigation = user is null ? "" : shellTabs + shellActions;
        return $$"""
            <!doctype html>
            <html lang="{{language}}" data-theme="{{theme}}" data-embedded="{{(string.Equals(context.Request.Query["embed"].ToString(), "1", StringComparison.OrdinalIgnoreCase) ? "1" : "0")}}">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="theme-color" content="#176b5b">
                <link rel="icon" type="image/svg+xml" href="/favicon.svg">
                <link rel="shortcut icon" href="/favicon.ico">
                <title>{{E(title)}} - Matgate</title>
                <style>
                    :root {
                        color-scheme: light;
                        --radius: 2px;
                        --shell-height: 34px;
                        --bg: #f4f6f4;
                        --panel: #ffffff;
                        --surface: #ffffff;
                        --surface-2: #eef2ef;
                        --surface-3: #dfe7e3;
                        --hover-bg: #f5f8f6;
                        --hover-strong-bg: #eef4f1;
                        --active-bg: #eef7f1;
                        --text: #1f2725;
                        --muted: #67706c;
                        --line: #dce2de;
                        --accent: #176b5b;
                        --accent-2: #2b5876;
                        --danger: #a63a3a;
                        --primary-hover: #145d4f;
                        --danger-hover: #923232;
                        --shadow: 0 10px 24px rgb(31 39 37 / 8%);
                        --shadow-strong: 0 12px 28px rgb(31 39 37 / 14%);
                    }
                    :root[data-theme="dark"] {
                        color-scheme: dark;
                        --bg: #0f1412;
                        --panel: #161c19;
                        --surface: #171d1a;
                        --surface-2: #1d2421;
                        --surface-3: #232c28;
                        --hover-bg: #202823;
                        --hover-strong-bg: #26312c;
                        --active-bg: #1f352f;
                        --text: #edf2ef;
                        --muted: #a0aca6;
                        --line: #2f3d37;
                        --accent: #5bc2a8;
                        --accent-2: #8cb8e0;
                        --danger: #d46f6f;
                        --primary-hover: #4aa78f;
                        --danger-hover: #bd5f5f;
                        --shadow: 0 10px 24px rgb(0 0 0 / 32%);
                        --shadow-strong: 0 12px 28px rgb(0 0 0 / 42%);
                    }
                    @media (prefers-color-scheme: dark) {
                        :root[data-theme="system"],
                        :root:not([data-theme="light"]):not([data-theme="dark"]) {
                            color-scheme: dark;
                            --bg: #0f1412;
                            --panel: #161c19;
                            --surface: #171d1a;
                            --surface-2: #1d2421;
                            --surface-3: #232c28;
                            --hover-bg: #202823;
                            --hover-strong-bg: #26312c;
                            --active-bg: #1f352f;
                            --text: #edf2ef;
                            --muted: #a0aca6;
                            --line: #2f3d37;
                            --accent: #5bc2a8;
                            --accent-2: #8cb8e0;
                            --danger: #d46f6f;
                            --primary-hover: #4aa78f;
                            --danger-hover: #bd5f5f;
                            --shadow: 0 10px 24px rgb(0 0 0 / 32%);
                            --shadow-strong: 0 12px 28px rgb(0 0 0 / 42%);
                        }
                    }
                    * { box-sizing: border-box; }
                    body {
                        margin: 0;
                        background: var(--bg);
                        color: var(--text);
                        font-family: Segoe UI, system-ui, -apple-system, sans-serif;
                        line-height: 1.5;
                    }
                    html[data-embedded="1"] header {
                        display: none;
                    }
                    header {
                        background: var(--surface);
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        align-items: center;
                        justify-content: space-between;
                        gap: 16px;
                        padding: 6px clamp(12px, 3vw, 24px);
                        position: sticky;
                        top: 0;
                        z-index: 5;
                    }
                    .brand {
                        align-items: center;
                        color: var(--text);
                        display: inline-flex;
                        font-size: 18px;
                        font-weight: 800;
                        gap: 10px;
                        letter-spacing: 0;
                        text-decoration: none;
                        white-space: nowrap;
                    }
                    .brand-mark {
                        align-items: center;
                        background: linear-gradient(135deg, #176b5b, #2b5876);
                        border-radius: var(--radius);
                        box-shadow: inset 0 0 0 1px rgb(255 255 255 / 28%);
                        color: #ffffff;
                        display: inline-flex;
                        height: 30px;
                        justify-content: center;
                        overflow: hidden;
                        position: relative;
                        width: 30px;
                    }
                    .brand-gate {
                        border: 2px solid rgb(255 255 255 / 80%);
                        border-bottom: 0;
                        border-radius: 7px 7px 0 0;
                        height: 17px;
                        left: 8px;
                        position: absolute;
                        top: 7px;
                        width: 18px;
                    }
                    .brand-core {
                        font-size: 15px;
                        font-weight: 900;
                        position: relative;
                        top: 3px;
                    }
                    .brand-word span { color: var(--accent); }
                    .shell-page-row {
                        align-items: center;
                        background: var(--surface);
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        gap: 8px;
                        min-height: 40px;
                        overflow: visible;
                        padding: 2px 12px;
                    }
                    .shell-page-tabs {
                        flex: 1 1 auto;
                        min-width: 0;
                    }
                    .shell-page-panels {
                        display: flex;
                        flex: 1 1 auto;
                        min-height: 0;
                        position: relative;
                    }
                    .shell-page-panel {
                        inset: 0;
                        position: absolute;
                    }
                    .shell-page-panel iframe {
                        border: 0;
                        display: block;
                        height: 100%;
                        width: 100%;
                    }
                    .icon {
                        fill: none;
                        flex: 0 0 auto;
                        height: 17px;
                        stroke: currentColor;
                        stroke-linecap: round;
                        stroke-linejoin: round;
                        stroke-width: 2;
                        width: 17px;
                    }
                    nav { display: flex; align-items: center; gap: 6px; flex-wrap: nowrap; overflow: visible; min-width: 0; }
                    .shell-tabs {
                        flex: 1 1 auto;
                        min-width: 0;
                        overflow-x: auto;
                        overflow-y: visible;
                        justify-content: flex-end;
                        padding-right: 2px;
                        scrollbar-width: thin;
                    }
                    .shell-tab {
                        align-items: center;
                        background: transparent;
                        border: 0;
                        border-radius: 0;
                        color: var(--muted);
                        cursor: pointer;
                        display: inline-flex;
                        gap: 7px;
                        min-height: 28px;
                        padding: 4px 7px;
                        text-decoration: none;
                        white-space: nowrap;
                    }
                    .shell-tabs .shell-tab,
                    .shell-tabs .shell-tab:link,
                    .shell-tabs .shell-tab:visited,
                    .shell-tabs .shell-tab:hover,
                    .shell-tabs .shell-tab:focus,
                    .shell-tabs .shell-tab:focus-visible,
                    .shell-tabs .shell-tab.active,
                    .shell-tabs .shell-tab.active:hover,
                    .shell-tabs .shell-tab.active:focus,
                    .shell-tabs .shell-tab.active:focus-visible {
                        text-decoration: none !important;
                        text-decoration-line: none !important;
                    }
                    .shell-tab.active {
                        background: transparent;
                        color: var(--muted);
                        font-weight: inherit;
                        box-shadow: none;
                    }
                    .shell-tabs .shell-tab.active,
                    .shell-tabs .shell-tab.active:hover,
                    .shell-tabs .shell-tab.active:focus,
                    .shell-tabs .shell-tab.active:focus-visible {
                        color: var(--muted);
                    }
                    .shell-tab-main {
                        background: transparent;
                        border: 0;
                        border-radius: 0;
                        display: grid;
                        gap: 0;
                        justify-items: start;
                        min-height: 40px;
                        min-width: 140px;
                        padding: 4px 9px;
                        text-align: left;
                        width: 100%;
                    }
                    .shell-tab-title {
                        max-width: none;
                        justify-content: flex-start;
                        width: 100%;
                    }
                    .shell-tab-description {
                        color: var(--muted);
                        display: block;
                        font-size: 11px;
                        line-height: 1.2;
                        min-height: 1.2em;
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                        width: 100%;
                    }
                    .shell-actions {
                        display: flex;
                        flex: 0 0 auto;
                        align-items: center;
                        gap: 6px;
                        margin-left: 8px;
                    }
                    .shell-action {
                        align-items: center;
                        background: transparent;
                        border: 0;
                        border-radius: 0;
                        color: var(--muted);
                        cursor: pointer;
                        display: inline-flex;
                        gap: 7px;
                        min-height: 28px;
                        padding: 4px 8px;
                        text-decoration: none;
                        white-space: nowrap;
                    }
                    .shell-tab:hover,
                    .shell-tab:focus-visible,
                    .shell-action:hover,
                    .shell-action:focus-visible {
                        background: transparent;
                        color: var(--accent);
                    }
                    .button, button {
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        background: var(--surface);
                        color: var(--text);
                        cursor: pointer;
                        display: inline-flex;
                        align-items: center;
                        min-height: 30px;
                        padding: 4px 9px;
                        text-decoration: none;
                        font: inherit;
                        gap: 7px;
                    }
                    .menu-panel {
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        box-shadow: var(--shadow-strong);
                        display: grid;
                        gap: 4px;
                        min-width: 220px;
                        padding: 6px;
                        position: absolute;
                        right: 0;
                        top: calc(100% + 8px);
                        z-index: 30;
                    }
                    .menu-panel a,
                    .menu-panel button {
                        justify-content: flex-start;
                        min-width: 0;
                        width: 100%;
                    }
                    .menu-panel a:hover,
                    .menu-panel a:focus-visible,
                    .menu-panel button:hover,
                    .menu-panel button:focus-visible {
                        background: var(--hover-bg);
                        border-color: var(--surface-3);
                        color: var(--text);
                    }
                    .account-trigger {
                        max-width: 260px;
                    }
                    .account-name {
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                        max-width: 180px;
                    }
                    button:disabled { cursor: not-allowed; opacity: .55; }
                    .primary { background: var(--accent); border-color: var(--accent); color: #ffffff; }
                    .danger { background: var(--danger); border-color: var(--danger); color: #ffffff; }
                    .primary:hover,
                    .primary:focus-visible {
                        background: var(--primary-hover);
                        border-color: var(--primary-hover);
                        color: #ffffff;
                    }
                    .danger:hover,
                    .danger:focus-visible {
                        background: var(--danger-hover);
                        border-color: var(--danger-hover);
                        color: #ffffff;
                    }
                    main { width: min(1180px, calc(100% - 24px)); margin: 22px auto 44px; }
                    main.shell-main {
                        display: flex;
                        flex-direction: column;
                        gap: 0;
                        height: calc(100vh - 54px);
                        margin: 0;
                        width: 100%;
                    }
                    main.session-main { width: 100%; height: calc(100vh - 54px); margin: 0; }
                    h1, h2 { line-height: 1.15; margin: 0; }
                    h1 { font-size: clamp(30px, 4vw, 52px); }
                    h2 { font-size: 20px; margin-bottom: 18px; }
                    .eyebrow { color: var(--accent-2); font-weight: 700; margin: 0 0 6px; text-transform: uppercase; }
                    .muted { color: var(--muted); }
                    .target {
                        font-family: Consolas, ui-monospace, monospace;
                        overflow-wrap: anywhere;
                        word-break: break-word;
                    }
                    .page-head { align-items: center; display: flex; justify-content: space-between; gap: 18px; margin-bottom: 22px; }
                    .panel, .card, .auth-panel {
                        background: var(--panel);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        padding: 18px;
                        box-shadow: var(--shadow);
                    }
                    .panel + .panel { margin-top: 18px; }
                    .grid { display: grid; gap: 16px; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); }
                    .row { display: flex; align-items: center; gap: 14px; }
                    .split { justify-content: space-between; }
                    .stack { display: grid; gap: 14px; }
                    .form-grid { display: grid; gap: 14px; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); align-items: end; }
                    label { display: grid; gap: 6px; font-weight: 600; }
                    .check {
                        align-items: center;
                        display: flex;
                        gap: 8px;
                        min-height: 30px;
                        padding-top: 1px;
                    }
                    .check input[type="checkbox"],
                    .check input[type="radio"] {
                        flex: 0 0 auto;
                        height: 16px;
                        margin: 0;
                        width: 16px;
                    }
                    input:not([type="checkbox"]):not([type="radio"]),
                    select,
                    textarea {
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        font: inherit;
                        min-height: 34px;
                        padding: 7px 9px;
                        width: 100%;
                    }
                    input[type="checkbox"],
                    input[type="radio"] {
                        accent-color: var(--accent);
                        border: 0;
                        box-shadow: none;
                        min-height: 0;
                        padding: 0;
                        width: auto;
                    }
                    textarea { min-height: 76px; resize: vertical; }
                    .actions { align-items: end; display: flex; gap: 10px; }
                    .tool-panel {
                        display: grid;
                        gap: 16px;
                    }
                    .tool-form {
                        display: grid;
                        gap: 16px;
                    }
                    .tool-select-row {
                        align-items: end;
                        display: flex;
                        flex-wrap: wrap;
                        gap: 12px;
                        justify-content: space-between;
                    }
                    .tool-select-label {
                        display: grid;
                        gap: 6px;
                        flex: 1 1 320px;
                        min-width: min(100%, 320px);
                    }
                    .tool-select-label select {
                        width: 100%;
                    }
                    .tool-summary {
                        align-items: center;
                        background: var(--surface-2);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        display: flex;
                        gap: 12px;
                        padding: 12px;
                    }
                    .tool-summary-icon-stack {
                        align-items: center;
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--accent);
                        display: inline-flex;
                        flex: 0 0 auto;
                        height: 44px;
                        justify-content: center;
                        width: 44px;
                    }
                    .tool-summary-icon {
                        align-items: center;
                        display: inline-flex;
                        justify-content: center;
                    }
                    .tool-summary-icon[hidden] {
                        display: none;
                    }
                    .tool-summary-icon .icon {
                        height: 20px;
                        width: 20px;
                    }
                    .tool-summary-copy {
                        min-width: 0;
                    }
                    .tool-summary-copy .eyebrow {
                        margin-bottom: 4px;
                    }
                    .tool-summary-copy h2 {
                        margin: 0;
                    }
                    .tool-summary-copy p {
                        margin: 4px 0 0;
                    }
                    .tool-fields {
                        display: grid;
                        gap: 14px;
                    }
                    .tool-field-group {
                        display: grid;
                        gap: 14px;
                    }
                    .tool-field-group[hidden] {
                        display: none;
                    }
                    .tool-form-grid {
                        align-items: start;
                    }
                    .tool-actions {
                        justify-content: flex-end;
                    }
                    .tool-actions button {
                        min-width: 132px;
                    }
                    .tool-output {
                        background: var(--surface-2);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        font-family: Consolas, ui-monospace, monospace;
                        font-size: 12px;
                        line-height: 1.45;
                        margin: 0;
                        min-height: 220px;
                        overflow: auto;
                        padding: 12px;
                        white-space: pre-wrap;
                    }
                    @media (max-width: 720px) {
                        .tool-select-row {
                            align-items: stretch;
                        }
                        .tool-actions {
                            justify-content: stretch;
                        }
                        .tool-actions button {
                            width: 100%;
                        }
                    }
                    .server-form-footer {
                        align-items: center;
                        display: flex;
                        flex-wrap: wrap;
                        gap: 16px;
                        justify-content: space-between;
                        margin-top: 18px;
                    }
                    .server-form-actions {
                        align-items: center;
                        justify-content: flex-end;
                        margin-left: auto;
                    }
                    .server-delete-form {
                        display: inline;
                        margin: 0;
                    }
                    .wide { grid-column: 1 / -1; }
                    .inline { display: inline; margin: 0; }
                    .auth-panel { display: grid; gap: 26px; grid-template-columns: minmax(0, 1fr) minmax(280px, 380px); margin: 10vh auto 0; max-width: 880px; }
                    .notice { border-radius: var(--radius); padding: 10px 12px; }
                    .error { background: #fff1f1; border: 1px solid #f0caca; color: #7d2424; }
                    .badge { background: var(--surface-2); border-radius: var(--radius); color: var(--accent); display: inline-block; font-size: 12px; font-weight: 800; padding: 3px 7px; }
                    .server-title {
                        align-items: center;
                        display: flex;
                        gap: 12px;
                        min-width: 0;
                    }
                    .server-title h2 {
                        margin: 8px 0 0;
                    }
                    .server-icon {
                        align-items: center;
                        background: var(--surface-2);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--accent);
                        display: inline-flex;
                        flex: 0 0 auto;
                        height: 42px;
                        justify-content: center;
                        width: 42px;
                    }
                    .server-icon.small {
                        height: 30px;
                        width: 30px;
                    }
                    .server-icon.small .icon {
                        height: 15px;
                        width: 15px;
                    }
                    .server-name-cell {
                        align-items: center;
                        display: inline-flex;
                        gap: 9px;
                    }
                    .empty { border: 1px dashed var(--line); border-radius: var(--radius); color: var(--muted); padding: 18px; }
                    .table-wrap { overflow-x: auto; }
                    table { border-collapse: collapse; width: 100%; }
                    th, td { border-bottom: 1px solid var(--line); padding: 12px 8px; text-align: left; vertical-align: top; }
                    .danger-zone { border-color: #efc7c7; }
                    .matgate-shell {
                        background: var(--bg);
                        display: flex;
                        flex-direction: column;
                        height: 100%;
                        min-height: calc(100vh - 54px);
                        overflow: hidden;
                        position: relative;
                        width: 100%;
                    }
                    .app-view {
                        inset: 0;
                        pointer-events: none;
                        position: absolute;
                        visibility: hidden;
                        z-index: 0;
                    }
                    .app-view.active {
                        pointer-events: auto;
                        visibility: visible;
                        z-index: 1;
                    }
                    .dashboard-view {
                        overflow: auto;
                        padding: 28px clamp(16px, 4vw, 44px) 56px;
                    }
                    .dashboard-inner {
                        margin: 0 auto;
                        max-width: 1180px;
                    }
                    .home-management {
                        margin-top: 22px;
                    }
                    .server-list-actions {
                        justify-content: flex-end;
                        margin-top: 16px;
                    }
                    .session-page { display: flex; flex-direction: column; height: 100%; min-height: 0; }
                    .session-bar {
                        align-items: center;
                        background: var(--surface);
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        justify-content: space-between;
                        gap: 14px;
                        min-height: 38px;
                        padding: 5px clamp(10px, 2vw, 18px);
                    }
                    .session-bar > div:first-child { align-items: center; display: flex; flex-wrap: wrap; gap: 10px; min-width: 0; }
                    .session-actions { align-items: center; display: flex; gap: 8px; flex-wrap: wrap; }
                    .session-tab-row {
                        align-items: stretch;
                        background: var(--surface-2);
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        min-height: 40px;
                    }
                    .session-tabs {
                        align-items: stretch;
                        background: transparent;
                        display: flex;
                        flex: 0 1 auto;
                        gap: 0;
                        min-height: 40px;
                        min-width: 0;
                        overflow-x: auto;
                    }
                    .tab-actions {
                        align-items: center;
                        background: var(--surface);
                        border-left: 1px solid var(--line);
                        display: flex;
                        flex: 0 0 auto;
                        gap: 8px;
                        justify-content: flex-end;
                        margin-left: auto;
                        padding: 3px clamp(8px, 2vw, 12px);
                    }
                    #connection-tab-actions {
                        flex: 0 0 auto;
                        min-width: 0;
                    }
                    .tab-actions button {
                        align-items: center;
                        display: inline-flex;
                        gap: 6px;
                        min-height: 28px;
                        padding: 4px 9px;
                        white-space: nowrap;
                    }
                    .tab-action-button .icon {
                        height: 15px;
                        width: 15px;
                    }
                    .tab-action-button.icon-only {
                        justify-content: center;
                        min-width: 28px;
                        padding: 0;
                        width: 28px;
                    }
                    .session-tab {
                        align-items: stretch;
                        background: var(--surface-3);
                        border-right: 1px solid var(--line);
                        display: flex;
                        flex: 0 0 auto;
                        max-width: 280px;
                        cursor: grab;
                    }
                    .session-tab.active { background: var(--surface); }
                    .session-tab.dragging { opacity: .65; }
                    .session-tab--page,
                    .session-tab--connection {
                        align-items: stretch;
                    }
                    .session-tab--add {
                        align-items: stretch;
                        border-right: 0;
                        color: var(--accent);
                        flex: 0 0 auto;
                        margin-left: auto;
                        max-width: none;
                        min-width: 0;
                        padding: 0;
                        width: auto;
                    }
                    .session-tab-main,
                    .shell-tab-main {
                        background: transparent;
                        border: 0;
                        border-radius: 0;
                        display: grid;
                        gap: 0;
                        align-content: center;
                        justify-items: start;
                        min-height: 40px;
                        padding: 4px 9px;
                        text-align: left;
                        width: 100%;
                    }
                    .session-tab-main { min-width: 150px; }
                    .shell-tab-main { min-width: 140px; }
                    .session-tab--page .session-tab-main,
                    .session-tab--connection .session-tab-main {
                        min-width: 150px;
                    }
                    .session-tab--add .session-tab-main {
                        align-items: center;
                        display: flex;
                        justify-content: center;
                        min-width: 0;
                        padding: 0 9px;
                        width: 34px;
                    }
                    .session-tab--add .session-tab-title {
                        justify-content: center;
                        width: auto;
                    }
                    .session-tab-title {
                        align-items: center;
                        display: flex;
                        gap: 7px;
                        max-width: none;
                        justify-content: flex-start;
                        min-width: 0;
                        width: 100%;
                    }
                    .session-tab-title .icon {
                        height: 15px;
                        width: 15px;
                    }
                    .session-tab-title span {
                        display: block;
                        flex: 1 1 auto;
                        min-width: 0;
                        max-width: none;
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                    }
                    .shell-tab-title span {
                        display: block;
                        flex: 1 1 auto;
                        min-width: 0;
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                    }
                    .session-tab-description {
                        color: var(--muted);
                        display: block;
                        font-size: 11px;
                        line-height: 1.2;
                        min-height: 1.2em;
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                        width: 100%;
                    }
                    .session-tab--add .session-tab-description {
                        color: transparent;
                        display: none;
                        min-width: 0;
                        overflow: hidden;
                        padding: 0;
                        width: 0;
                    }
                    .session-tab-close {
                        background: transparent;
                        border: 0;
                        border-radius: 0;
                        align-items: center;
                        display: flex;
                        justify-content: center;
                        min-height: 40px;
                        padding: 0 8px;
                    }
                    .session-deck {
                        background: #111614;
                        flex: 1;
                        min-height: 0;
                        position: relative;
                    }
                    .session-statusbar {
                        align-items: center;
                        background: var(--surface);
                        border-top: 1px solid var(--line);
                        color: var(--text);
                        display: flex;
                        gap: 12px;
                        justify-content: space-between;
                        min-height: 28px;
                        padding: 3px clamp(10px, 2vw, 18px);
                    }
                    .status-primary, .status-secondary, .status-metrics, .status-actions {
                        align-items: center;
                        display: flex;
                        gap: 10px;
                        min-width: 0;
                    }
                    .status-secondary {
                        margin-left: auto;
                    }
                    .status-primary strong, .status-primary span, .status-metrics span {
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                    }
                    .status-actions {
                        flex: 0 0 auto;
                    }
                    .status-info-button {
                        align-items: center;
                        appearance: none;
                        background: transparent;
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--muted);
                        display: inline-flex;
                        flex: 0 0 auto;
                        height: 24px;
                        justify-content: center;
                        padding: 0;
                        text-decoration: none;
                        transition: background-color .15s ease, border-color .15s ease, color .15s ease;
                        width: 24px;
                    }
                    .status-info-button:hover {
                        background: var(--hover-strong-bg);
                        border-color: var(--surface-3);
                        color: var(--accent);
                    }
                    .status-primary span, .status-metrics span {
                        color: var(--muted);
                        font-size: 13px;
                    }
                    #status-state {
                        background: var(--surface-2);
                        border-radius: var(--radius);
                        color: var(--accent);
                        font-weight: 700;
                        padding: 2px 7px;
                    }
                    #status-message { max-width: 360px; }
                    .connection-panel {
                        background: #111614;
                        height: 100%;
                        inset: 0;
                        outline: none;
                        overflow: hidden;
                        position: absolute;
                        width: 100%;
                    }
                    .connection-picker-panel {
                        background: var(--bg);
                        height: 100%;
                        inset: 0;
                        overflow: auto;
                        padding: clamp(14px, 3vw, 28px);
                        position: absolute;
                        width: 100%;
                    }
                    .connection-picker-inner {
                        margin: 0 auto;
                        max-width: 980px;
                    }
                    .connection-picker-head {
                        margin-bottom: 18px;
                    }
                    .connection-picker-head h1 {
                        font-size: 28px;
                    }
                    .connection-picker-grid {
                        display: grid;
                        gap: 12px;
                        grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
                    }
                    .connection-choice-section {
                        display: grid;
                        gap: 10px;
                    }
                    .connection-choice-section + .connection-choice-section {
                        margin-top: 18px;
                    }
                    .connection-choice-section-head {
                        align-items: center;
                    }
                    .connection-choice-section-head h2 {
                        align-items: center;
                        display: flex;
                        gap: 8px;
                        margin: 6px 0 0;
                    }
                    .connection-choice-section-head h2 .icon {
                        height: 16px;
                        width: 16px;
                    }
                    .connection-choice {
                        align-items: stretch;
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        display: grid;
                        gap: 12px;
                        min-height: 150px;
                        padding: 14px;
                    }
                    .connection-choice h2 {
                        margin: 8px 0 8px;
                    }
                    .connection-choice-copy {
                        display: grid;
                        gap: 6px;
                        min-width: 0;
                    }
                    .connection-choice-copy h2 {
                        margin: 0;
                    }
                    .connection-choice .target {
                        margin: 0;
                    }
                    .connection-choice .muted {
                        margin: 8px 0 0;
                    }
                    .connection-choice-actions {
                        align-items: flex-end;
                        display: flex;
                        flex-wrap: wrap;
                        gap: 8px;
                        justify-content: flex-end;
                    }
                    .connection-choice-actions button,
                    .connection-choice-actions a {
                        align-self: end;
                        justify-content: center;
                    }
                    .favorite-toggle-form {
                        display: inline-flex;
                        margin: 0;
                    }
                    .favorite-toggle {
                        align-items: center;
                        display: inline-flex;
                        justify-content: center;
                        height: 32px;
                        min-height: 32px;
                        min-width: 32px;
                        padding: 0;
                        width: 32px;
                    }
                    .favorite-toggle.active {
                        background: var(--surface-2);
                        border-color: var(--accent);
                        color: var(--accent);
                    }
                    .favorite-toggle .icon {
                        height: 15px;
                        width: 15px;
                    }
                    .server-folder-badge {
                        align-items: center;
                        display: inline-flex;
                        gap: 6px;
                    }
                    .server-folder-badge .icon {
                        height: 13px;
                        width: 13px;
                    }
                    .table-actions {
                        text-align: right;
                    }
                    .guac-stage {
                        background: #111614;
                        flex: 1;
                        min-height: 0;
                        overflow: hidden;
                        position: relative;
                    }
                    .guac-display {
                        align-items: center;
                        display: flex;
                        height: 100%;
                        justify-content: center;
                        overflow: hidden;
                        width: 100%;
                    }
                    .guac-display > div { transform-origin: center center; }
                    .website-display {
                        background: var(--bg);
                        color: var(--text);
                        height: 100%;
                        overflow: hidden;
                        width: 100%;
                    }
                    .website-shell {
                        display: flex;
                        flex-direction: column;
                        height: 100%;
                        min-height: 0;
                        width: 100%;
                    }
                    .toolbar,
                    .website-toolbar,
                    .file-toolbar {
                        align-items: center;
                        display: flex;
                        flex-wrap: wrap;
                        gap: 6px;
                    }
                    .toolbar-group,
                    .website-toolbar-group,
                    .file-toolbar-group {
                        align-items: center;
                        display: flex;
                        flex-wrap: wrap;
                        gap: 6px;
                        min-width: 0;
                    }
                    .toolbar-group--grow,
                    .website-toolbar-address,
                    .file-toolbar-main {
                        flex: 1 1 560px;
                    }
                    .toolbar-group--end,
                    .file-toolbar-transfer {
                        margin-left: auto;
                        justify-content: flex-end;
                    }
                    .toolbar-button,
                    .toolbar-menu-trigger,
                    .toolbar-upload-button,
                    .website-tool-button,
                    .file-tool-button,
                    .file-menu > summary,
                    .file-upload-button {
                        align-items: center;
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--text);
                        cursor: pointer;
                        display: inline-flex;
                        font: inherit;
                        gap: 7px;
                        justify-content: center;
                        min-height: 32px;
                        padding: 4px 8px;
                        text-decoration: none;
                    }
                    .toolbar-button:hover,
                    .toolbar-button:focus-visible,
                    .toolbar-menu-trigger:hover,
                    .toolbar-menu-trigger:focus-visible,
                    .toolbar-upload-button:hover,
                    .toolbar-upload-button:focus-visible,
                    .website-tool-button:hover,
                    .website-tool-button:focus-visible,
                    .file-tool-button:hover,
                    .file-tool-button:focus-visible,
                    .file-menu[open] > summary,
                    .file-menu > summary:hover,
                    .file-menu > summary:focus-visible {
                        background: var(--hover-bg);
                        border-color: var(--surface-3);
                        color: var(--text);
                    }
                    .toolbar-button .icon,
                    .toolbar-menu-trigger .icon,
                    .toolbar-upload-button .icon,
                    .website-tool-button .icon,
                    .file-tool-button .icon,
                    .file-upload-button .icon {
                        height: 15px;
                        width: 15px;
                    }
                    .toolbar-icon-button {
                        flex: 0 0 auto;
                        min-width: 32px;
                        padding: 0;
                        width: 32px;
                    }
                    .toolbar-button--primary,
                    .file-upload-button {
                        background: var(--accent);
                        border-color: var(--accent);
                        color: #ffffff;
                    }
                    .toolbar-button--primary:hover,
                    .toolbar-button--primary:focus-visible,
                    .toolbar-upload-button:hover,
                    .toolbar-upload-button:focus-visible,
                    .file-upload-button:hover,
                    .file-upload-button:focus-visible {
                        background: var(--primary-hover);
                        border-color: var(--primary-hover);
                        color: #ffffff;
                    }
                    .toolbar-button--danger {
                        background: var(--danger);
                        border-color: var(--danger);
                        color: #ffffff;
                    }
                    .toolbar-button--danger:hover,
                    .toolbar-button--danger:focus-visible {
                        background: var(--danger-hover);
                        border-color: var(--danger-hover);
                        color: #ffffff;
                    }
                    .toolbar-input,
                    .website-address,
                    .file-path-input {
                        flex: 1 1 220px;
                        font-family: Consolas, ui-monospace, monospace;
                        min-height: 32px;
                    }
                    .toolbar-menu,
                    .file-menu {
                        position: relative;
                    }
                    .toolbar-menu-panel,
                    .file-menu-panel {
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        box-shadow: var(--shadow-strong);
                        display: grid;
                        gap: 4px;
                        min-width: 240px;
                        padding: 6px;
                        position: absolute;
                        left: 0;
                        top: calc(100% + 8px);
                        z-index: 12;
                    }
                    .toolbar-menu-item,
                    .file-menu-item {
                        align-items: center;
                        justify-content: flex-start;
                        width: 100%;
                    }
                    .website-toolbar {
                        align-items: center;
                        background: var(--surface);
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        flex-wrap: wrap;
                        gap: 8px;
                        padding: 6px 8px;
                    }
                    .website-toolbar-group {
                        align-items: center;
                        display: flex;
                        gap: 8px;
                        min-width: 0;
                    }
                    .website-toolbar-address {
                        flex: 1 1 340px;
                    }
                    .website-address {
                        font-family: Consolas, ui-monospace, monospace;
                        min-height: 32px;
                    }
                    .website-tool-button {
                        align-items: center;
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--text);
                        cursor: pointer;
                        display: inline-flex;
                        gap: 7px;
                        justify-content: center;
                        min-height: 32px;
                        padding: 4px 8px;
                    }
                    .website-tool-button:hover {
                        background: var(--hover-bg);
                        border-color: var(--surface-3);
                        color: var(--text);
                    }
                    .website-frame {
                        border: 0;
                        flex: 1;
                        min-height: 0;
                        width: 100%;
                    }
                    .file-display {
                        background: var(--surface);
                        color: var(--text);
                        height: 100%;
                        overflow: hidden;
                        width: 100%;
                    }
                    .file-manager {
                        display: flex;
                        flex-direction: column;
                        gap: 0;
                        height: 100%;
                        padding: 0;
                    }
                    .file-toolbar {
                        align-items: center;
                        background: var(--surface);
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        flex-wrap: wrap;
                        gap: 8px;
                        padding: 6px 8px;
                    }
                    .file-toolbar-group {
                        align-items: center;
                        display: flex;
                        flex-wrap: wrap;
                        gap: 8px;
                    }
                    .file-toolbar-main {
                        flex: 1 1 340px;
                    }
                    .file-toolbar-transfer {
                        margin-left: auto;
                        justify-content: flex-end;
                    }
                    .file-toolbar button,
                    .file-menu > summary,
                    .file-upload-button {
                        gap: 7px;
                        min-height: 32px;
                        padding: 4px 8px;
                    }
                    .file-path-input {
                        flex: 1 1 220px;
                        font-family: Consolas, ui-monospace, monospace;
                        min-height: 32px;
                    }
                    .file-upload-button {
                        align-items: center;
                        background: var(--accent);
                        border: 1px solid var(--accent);
                        border-radius: var(--radius);
                        color: #ffffff;
                        cursor: pointer;
                        display: inline-flex;
                        font-weight: 600;
                        gap: 7px;
                        justify-content: center;
                    }
                    .file-menu {
                        position: relative;
                    }
                    .file-menu > summary {
                        align-items: center;
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--text);
                        cursor: pointer;
                        display: inline-flex;
                        list-style: none;
                        text-decoration: none;
                        font: inherit;
                        justify-content: center;
                    }
                    .file-menu > summary::-webkit-details-marker {
                        display: none;
                    }
                    .file-menu[open] > summary,
                    .file-menu > summary:hover {
                        background: var(--hover-bg);
                        border-color: var(--surface-3);
                        color: var(--text);
                    }
                    .file-menu-panel {
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        box-shadow: var(--shadow-strong);
                        display: grid;
                        gap: 4px;
                        min-width: 240px;
                        padding: 6px;
                        position: absolute;
                        left: 0;
                        top: calc(100% + 8px);
                        z-index: 12;
                    }
                    .file-menu-item {
                        align-items: center;
                        justify-content: flex-start;
                        width: 100%;
                    }
                    .toolbar-button.is-active,
                    .file-tool-button.is-active {
                        background: var(--active-bg);
                        border-color: var(--accent);
                        color: var(--accent-2);
                    }
                    .file-upload-input { display: none; }
                    .file-message {
                        align-items: center;
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--text);
                        display: flex;
                        gap: 12px;
                        margin: 8px 8px 0;
                        min-height: 28px;
                        padding: 3px 10px;
                    }
                    .file-message.error {
                        border-color: color-mix(in srgb, var(--danger) 24%, var(--line));
                        color: var(--danger);
                    }
                    .file-table-wrap {
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        flex: 1;
                        margin: 8px;
                        min-height: 0;
                        overflow: auto;
                    }
                    .file-table th,
                    .file-table td {
                        padding: 6px 10px;
                        vertical-align: middle;
                        white-space: nowrap;
                    }
                    .file-select-heading,
                    .file-select-cell {
                        padding-left: 4px;
                        padding-right: 4px;
                        text-align: center;
                        vertical-align: middle;
                        width: 30px;
                    }
                    .file-select-heading {
                        color: var(--muted);
                        font-size: 12px;
                        font-weight: 600;
                    }
                    .file-select-cell {
                        line-height: 1;
                    }
                    .file-select-cell input[type="checkbox"] {
                        accent-color: var(--accent);
                        cursor: pointer;
                        height: 14px;
                        margin: 0;
                        width: 14px;
                    }
                    .file-table th:first-child,
                    .file-table td:first-child {
                        min-width: 30px;
                        width: 30px;
                    }
                    .file-table th:nth-child(2),
                    .file-table td:nth-child(2) {
                        min-width: 260px;
                        white-space: normal;
                        width: 100%;
                    }
                    .file-table th:last-child {
                        text-align: right;
                    }
                    .file-actions-heading {
                        min-width: 210px;
                    }
                    .file-actions-cell {
                        min-width: 210px;
                        text-align: right;
                    }
                    .file-name-button {
                        align-items: center;
                        background: transparent;
                        border: 0;
                        display: inline-flex;
                        gap: 9px;
                        justify-content: flex-start;
                        min-height: 30px;
                        padding: 2px 0;
                        text-align: left;
                        width: 100%;
                    }
                    .file-name-button .icon {
                        color: var(--accent-2);
                        height: 20px;
                        width: 20px;
                    }
                    .is-directory .file-name-button .icon { color: var(--accent); }
                    .is-directory .file-name-button { font-weight: 700; }
                    .parent-directory .file-name-button:disabled {
                        color: var(--muted);
                        cursor: not-allowed;
                        opacity: .6;
                    }
                    .parent-directory .file-name-button:disabled .icon {
                        color: var(--muted);
                    }
                    .parent-directory {
                        background: var(--surface-2);
                    }
                    .file-row-actions {
                        display: inline-flex;
                        gap: 6px;
                        justify-content: flex-end;
                    }
                    .file-row-actions button,
                    .file-action-button {
                        gap: 6px;
                        min-height: 30px;
                        padding: 4px 8px;
                    }
                    .server-form {
                        gap: 16px;
                    }
                    .server-form-section .form-grid {
                        align-items: start;
                    }
                    .access-table th,
                    .access-table td {
                        padding: 10px 8px;
                        vertical-align: middle;
                    }
                    .access-select-cell {
                        padding-left: 4px;
                        padding-right: 4px;
                        text-align: center;
                        width: 44px;
                    }
                    .access-select-cell input[type="checkbox"] {
                        accent-color: var(--accent);
                        cursor: pointer;
                        height: 14px;
                        margin: 0;
                        width: 14px;
                    }
                    .access-table th:first-child,
                    .access-table td:first-child {
                        min-width: 44px;
                        width: 44px;
                    }
                    .file-empty {
                        color: var(--muted);
                        text-align: center;
                    }
                    main.viewer-main {
                        min-height: calc(100vh - 54px);
                        padding: 0;
                    }
                    .file-viewer-page {
                        display: flex;
                        flex-direction: column;
                        min-height: calc(100vh - 54px);
                        padding: 0;
                    }
                    .viewer-tab-row {
                        align-items: stretch;
                        background: var(--surface-2);
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        min-height: 36px;
                    }
                    .viewer-tabs {
                        flex: 1 1 auto;
                        min-width: 0;
                    }
                    .viewer-tab {
                        flex: 1 1 auto;
                        max-width: none;
                    }
                    .viewer-tab-main {
                        min-width: 0;
                        width: 100%;
                    }
                    .viewer-tab-main small {
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                    }
                    .viewer-body {
                        display: flex;
                        flex: 1;
                        min-height: 0;
                        padding: clamp(14px, 3vw, 28px);
                    }
                    .viewer-body .viewer-stage {
                        flex: 1;
                    }
                    .viewer-stage {
                        align-items: center;
                        background: #111614;
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        display: flex;
                        flex: 1;
                        justify-content: center;
                        min-height: 360px;
                        overflow: hidden;
                        padding: clamp(12px, 3vw, 28px);
                    }
                    .audio-stage {
                        background: linear-gradient(135deg, #f8faf9, #e8f1ee);
                        color: var(--text);
                        flex-direction: column;
                        gap: 20px;
                    }
                    .viewer-audio-mark {
                        align-items: center;
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--accent);
                        display: inline-flex;
                        height: 86px;
                        justify-content: center;
                        width: 86px;
                    }
                    .viewer-audio-mark .icon {
                        height: 44px;
                        width: 44px;
                    }
                    .audio-stage audio {
                        max-width: 820px;
                        width: min(100%, 820px);
                    }
                    .video-stage video {
                        background: #000000;
                        max-height: calc(100vh - 220px);
                        max-width: 100%;
                        width: 100%;
                    }
                    .image-stage img {
                        max-height: calc(100vh - 220px);
                        max-width: 100%;
                        object-fit: contain;
                    }
                    .document-stage {
                        background: var(--surface);
                        padding: 0;
                    }
                    .document-stage iframe {
                        border: 0;
                        height: calc(100vh - 190px);
                        width: 100%;
                    }
                    .empty-viewer {
                        background: var(--surface);
                        color: var(--text);
                        flex-direction: column;
                    }
                    .embedded-viewer {
                        background: var(--panel);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        box-shadow: var(--shadow-strong);
                        display: flex;
                        flex-direction: column;
                        gap: 14px;
                        height: min(88vh, 920px);
                        overflow: hidden;
                        padding: clamp(14px, 3vw, 24px);
                        width: 100%;
                    }
                    .embedded-viewer .viewer-head {
                        align-items: flex-start;
                        flex-wrap: wrap;
                    }
                    .embedded-viewer .viewer-head .actions {
                        flex-wrap: wrap;
                    }
                    .embedded-viewer .viewer-head .actions > * {
                        flex: 1 1 auto;
                        justify-content: center;
                    }
                    .embedded-viewer .viewer-stage {
                        flex: 1;
                        min-height: 0;
                    }
                    .embedded-viewer .video-stage video {
                        max-height: 100%;
                    }
                    .embedded-viewer .image-stage img {
                        max-height: 100%;
                    }
                    .embedded-viewer .document-stage iframe {
                        height: 100%;
                    }
                    .embedded-viewer .audio-stage audio {
                        max-width: 100%;
                    }
                    .matgate-dialog,
                    .file-viewer-dialog {
                        background: transparent;
                        border: 0;
                        max-width: none;
                        margin: auto;
                        overflow: visible;
                        padding: 0;
                        width: min(1160px, calc(100vw - 32px));
                    }
                    .matgate-dialog::backdrop,
                    .file-viewer-dialog::backdrop {
                        background: rgb(10 14 12 / 72%);
                        backdrop-filter: blur(2px);
                    }
                    .about-page {
                        display: grid;
                        gap: 18px;
                        margin: 0 auto;
                        max-width: 1120px;
                        padding: clamp(14px, 3vw, 28px);
                        width: 100%;
                    }
                    .about-head {
                        margin-bottom: 0;
                    }
                    .about-brand {
                        align-items: center;
                        display: inline-flex;
                    }
                    .about-copy {
                        display: flex;
                        flex-direction: column;
                        gap: 8px;
                        max-width: 760px;
                        min-width: 0;
                    }
                    .about-copy .version-number {
                        margin: 0;
                    }
                    .about-card {
                        display: grid;
                        gap: 10px;
                        max-width: 760px;
                    }
                    .about-meta {
                        align-items: center;
                    }
                    .file-viewer-dialog-content {
                        width: 100%;
                    }
                    .file-viewer-dialog-loading {
                        align-items: center;
                        background: var(--panel);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        color: var(--muted);
                        display: flex;
                        justify-content: center;
                        min-height: 220px;
                        padding: 22px;
                        text-align: center;
                    }
                    .file-viewer-dialog-loading.error {
                        color: var(--danger);
                    }
                    .connection-overlay {
                        align-items: center;
                        background: rgb(17 22 20 / 90%);
                        color: #ffffff;
                        display: flex;
                        inset: 0;
                        justify-content: center;
                        padding: 24px;
                        position: absolute;
                        z-index: 3;
                    }
                    .connection-overlay.hidden, .hidden { display: none; }
                    .connection-dialog {
                        max-width: 520px;
                        text-align: center;
                    }
                    .connection-dialog h1 { font-size: 28px; }
                    .credential-dialog {
                        background: var(--surface);
                        border: 1px solid var(--line);
                        border-radius: var(--radius);
                        box-shadow: var(--shadow-strong);
                        left: 50%;
                        padding: 18px;
                        position: absolute;
                        top: 50%;
                        transform: translate(-50%, -50%);
                        width: min(420px, calc(100% - 32px));
                        z-index: 4;
                    }
                    @media (max-width: 720px) {
                        header, .page-head, .auth-panel { align-items: stretch; flex-direction: column; grid-template-columns: 1fr; }
                        .shell-tabs {
                            align-items: center;
                            flex: 1 1 auto;
                            flex-direction: row;
                            overflow-x: auto;
                            overflow-y: hidden;
                            width: 100%;
                        }
                        .shell-tabs > * {
                            width: auto;
                        }
                        .shell-actions {
                            margin-left: 0;
                            width: 100%;
                        }
                        .shell-action {
                            justify-content: center;
                            width: 100%;
                        }
                        .shell-page-row {
                            align-items: stretch;
                            flex-direction: column;
                        }
                        #connection-tab-actions {
                            flex: 0 0 auto;
                            min-width: 0;
                            width: 100%;
                        }
                        .shell-page-tabs {
                            width: 100%;
                        }
                        .shell-page-tabs > * {
                            width: auto;
                        }
                        .shell-page-panel {
                            position: relative;
                            width: 100%;
                        }
                        .shell-tab,
                        nav a,
                        nav button,
                        .button,
                        .shell-action { justify-content: center; }
                        main.session-main { height: auto; min-height: calc(100vh - 54px); }
                        .session-bar { align-items: stretch; flex-direction: column; }
                        .session-actions > * { flex: 1; justify-content: center; }
                        .session-tab-row { flex-direction: column; }
                        .tab-actions {
                            border-left: 0;
                            border-top: 1px solid var(--line);
                            flex-wrap: wrap;
                            justify-content: stretch;
                            margin-left: 0;
                        }
                        .tab-actions > * { flex: 1; justify-content: center; }
                        .session-deck { min-height: 60vh; }
                        .session-statusbar { align-items: stretch; flex-direction: column; }
                        .status-primary, .status-secondary { flex-wrap: wrap; }
                        .status-secondary { margin-left: 0; width: 100%; justify-content: space-between; }
                        .server-form-footer { align-items: stretch; flex-direction: column; }
                        .server-form-actions { margin-left: 0; justify-content: stretch; width: 100%; }
                        .server-form-actions > * { flex: 1; justify-content: center; }
                        .server-delete-form { width: 100%; }
                        .server-delete-form button { width: 100%; }
                        .file-toolbar-group { flex: 1 1 100%; }
                        .file-toolbar-group > * { flex: 1 1 calc(50% - 8px); justify-content: center; }
                        .file-path-input { flex-basis: 100%; }
                        .file-upload-button { width: 100%; }
                        .file-menu { width: 100%; }
                        .file-menu > summary { justify-content: center; width: 100%; }
                        .file-menu-panel { position: static; width: 100%; }
                        .file-row-actions { flex-wrap: wrap; }
                        .viewer-tab-row { align-items: stretch; flex-direction: column; }
                        .viewer-actions { flex-wrap: wrap; }
                        .viewer-actions > * { flex: 1; justify-content: center; }
                        .tool-head { align-items: stretch; flex-direction: column; }
                        .tool-actions { width: 100%; }
                        .tool-actions > * { flex: 1; justify-content: center; }
                        .viewer-body { padding: 10px; }
                        .viewer-stage { min-height: 240px; }
                        .embedded-viewer { height: calc(100vh - 16px); width: calc(100vw - 16px); }
                        .matgate-dialog,
                        .file-viewer-dialog { width: calc(100vw - 16px); }
                    }
                </style>
            </head>
            <body>
                <header>
                    <a class="brand" href="/">{{Logo()}}</a>
                    {{navigation}}
                </header>
                <main class="{{A(mainClass)}}">{{body}}</main>
                <script>
                    (() => {
                        const closeOpenMenus = (keepMenu) => {
                            document.querySelectorAll('details.toolbar-menu[open], details.file-menu[open]').forEach((menu) => {
                                if (menu !== keepMenu) {
                                    menu.removeAttribute('open');
                                }
                            });
                        };

                        document.addEventListener('click', (event) => {
                            const target = event.target instanceof Element ? event.target : null;
                            if (!target) {
                                return;
                            }

                            const keepMenu = target.closest('details.toolbar-menu, details.file-menu');
                            closeOpenMenus(keepMenu);
                        });

                        document.addEventListener('keydown', (event) => {
                            if (event.key === 'Escape') {
                                closeOpenMenus(null);
                            }
                        });

                        window.MatgateCloseFileViewer = (event, source) => {
                            const element = source instanceof Element
                                ? source
                                : (event && event.currentTarget instanceof Element ? event.currentTarget : null);
                            const dialog = element && typeof element.closest === 'function' ? element.closest('dialog') : null;
                            if (dialog && typeof dialog.close === 'function') {
                                dialog.close();
                                return false;
                            }

                            const referrer = document.referrer || '';
                            try {
                                if (referrer) {
                                    const refUrl = new URL(referrer, window.location.href);
                                    if (refUrl.origin === window.location.origin) {
                                        window.history.back();
                                        return false;
                                    }
                                }
                            }
                            catch {
                                // Use fallback below.
                            }

                            window.location.href = '/sessions';
                            return false;
                        };

                        window.MatgateCloseAboutWindow = () => false;
                    })();
                </script>
            </body>
            </html>
            """;
    }

    private static string ServerFields(HttpContext context, MatgateUser currentUser, ServerEndpoint? server = null)
    {
        var selectedRdp = Selected(server?.Protocol is null or ServerProtocol.Rdp or ServerProtocol.LegacyBrowser);
        var selectedVnc = Selected(server?.Protocol == ServerProtocol.Vnc);
        var selectedSsh = Selected(server?.Protocol == ServerProtocol.Ssh);
        var selectedSftp = Selected(server?.Protocol == ServerProtocol.Sftp);
        var selectedFtp = Selected(server?.Protocol == ServerProtocol.Ftp);
        var selectedSmb = Selected(server?.Protocol == ServerProtocol.Smb);
        var selectedWebsite = Selected(server?.Protocol == ServerProtocol.Website);
        var iconKey = ServerEndpoint.NormalizeIconKey(server?.IconKey);
        var folderName = Clean(server?.FolderName, "");
        var folderIconKey = string.IsNullOrWhiteSpace(folderName)
            ? ""
            : ServerEndpoint.NormalizeIconKey(server?.FolderIconKey);
        var port = server?.Port.ToString() ?? "";
        var websiteUrl = string.IsNullOrWhiteSpace(server?.WebsiteUrl)
            ? (string.IsNullOrWhiteSpace(server?.Host) ? "" : server.Host)
            : server.WebsiteUrl;
        var keyboardLayout = string.IsNullOrWhiteSpace(server?.KeyboardLayout)
            ? ServerEndpoint.DefaultKeyboardLayout
            : server.KeyboardLayout;
        var terminalFontSize = ServerEndpoint.NormalizeTerminalFontSize(
            server?.TerminalFontSize ?? ServerEndpoint.DefaultTerminalFontSize);
        var passwordHelp = server is null ? "" : $"""<p class="muted">{T(context, "Leave password empty to keep it unchanged.")}</p>""";
        var clearPassword = server is null ? "" : $"""<label class="check" data-protocols="rdp,vnc,ssh,sftp,ftp,smb"><input type="checkbox" name="clearPassword"> {T(context, "Clear saved target password")}</label>""";
        var canManageGlobal = currentUser.IsAdmin || currentUser.CanManageServers;
        var canCreatePrivate = currentUser.IsAdmin || currentUser.CanCreateServers;
        var scopeValue = server?.OwnerUserId is not null ? "private" : "global";

        if (server is null)
        {
            if (!canManageGlobal && canCreatePrivate)
            {
                scopeValue = "private";
            }
            else if (canManageGlobal && !canCreatePrivate)
            {
                scopeValue = "global";
            }
        }

        var canChangeScope = currentUser.IsAdmin || (currentUser.CanManageServers && currentUser.CanCreateServers);
        var scopeHelp = canChangeScope
            ? ""
            : $"""<p class="muted">{T(context, scopeValue == "private" ? "Own server" : "Global")}</p>""";
        var scopeHidden = canChangeScope ? "" : $"""<input type="hidden" name="scope" value="{A(scopeValue)}">""";

        var basicSection = $$"""
            <section class="panel server-form-section">
                <h2>{{T(context, "Basics")}}</h2>
                <div class="form-grid">
                    <label>{{T(context, "Name")}}
                        <input name="name" value="{{A(server?.Name)}}" required>
                    </label>
                    <label>{{T(context, "Scope")}}
                        <select name="scope"{{(canChangeScope ? "" : " disabled")}}>
                            <option value="global"{{Selected(scopeValue == "global")}}>{{T(context, "Global")}}</option>
                            <option value="private"{{Selected(scopeValue == "private")}}>{{T(context, "Own server")}}</option>
                        </select>
                        {{scopeHidden}}
                        {{scopeHelp}}
                    </label>
                    <label>{{T(context, "Protocol")}}
                        <select name="protocol">
                            <option value="Rdp"{{selectedRdp}}>RDP</option>
                            <option value="Vnc"{{selectedVnc}}>VNC</option>
                            <option value="Ssh"{{selectedSsh}}>SSH</option>
                            <option value="Sftp"{{selectedSftp}}>SFTP</option>
                            <option value="Ftp"{{selectedFtp}}>FTP</option>
                            <option value="Smb"{{selectedSmb}}>SMB</option>
                            <option value="Website"{{selectedWebsite}}>{{T(context, "Website (Beta)")}}</option>
                        </select>
                    </label>
                    <label>{{T(context, "Server icon")}}
                        <select name="iconKey">
                            <option value=""{{Selected(string.IsNullOrWhiteSpace(iconKey))}}>{{T(context, "Default by connection type")}}</option>
                            {{ServerIconOptions(iconKey)}}
                        </select>
                    </label>
                </div>
            </section>
            <section class="panel server-form-section">
                <h2>{{T(context, "Folder")}}</h2>
                <p class="muted">{{T(context, "Optional. Used for grouping in lists.")}}</p>
                <div class="form-grid">
                    <label>{{T(context, "Folder name")}}
                        <input name="folderName" value="{{A(folderName)}}" placeholder="NAS, Work, Lab">
                    </label>
                    <label>{{T(context, "Folder icon")}}
                        <select name="folderIconKey">
                            <option value=""{{Selected(string.IsNullOrWhiteSpace(folderIconKey))}}>{{T(context, "Default folder icon")}}</option>
                            {{ServerIconOptions(folderIconKey)}}
                        </select>
                    </label>
                </div>
            </section>
            <section class="panel server-form-section" data-protocols="rdp,vnc,ssh,sftp,ftp,smb">
                <h2>{{T(context, "Target")}}</h2>
                <div class="form-grid">
                    <label>{{T(context, "Host or IP")}}
                        <input name="host" value="{{A(server?.Host)}}" placeholder="PC-Terminal / Host" required>
                    </label>
                    <label>Port
                        <input name="port" type="number" min="1" max="65535" value="{{A(port)}}" placeholder="3389 / 5900 / 22 / 21 / 445">
                    </label>
                </div>
            </section>
            <section class="panel server-form-section" data-protocols="website">
                <h2>{{T(context, "Website settings")}}</h2>
                <div class="form-grid">
                    <label>{{T(context, "Website URL")}}
                        <input name="websiteUrl" value="{{A(websiteUrl)}}" placeholder="https://nas.local/admin/" required>
                    </label>
                    <label class="check"><input type="checkbox" name="ignoreCertificate"{{Checked(server?.IgnoreCertificate ?? true)}}> {{T(context, "Ignore certificate")}}</label>
                </div>
            </section>
            <section class="panel server-form-section" data-protocols="rdp,vnc,ssh,sftp,ftp,smb">
                <h2>{{T(context, "Credentials")}}</h2>
                <div class="form-grid">
                    <label data-protocols="rdp,ssh,sftp,ftp,smb">{{T(context, "Target user")}}
                        <input name="targetUserName" value="{{A(server?.UserName)}}" autocomplete="off">
                    </label>
                    <label data-protocols="rdp,vnc,ssh,sftp,ftp,smb">{{T(context, "Connection password")}}
                        <input name="targetPassword" type="password" autocomplete="new-password">
                        {{passwordHelp}}
                    </label>
                    {{clearPassword}}
                </div>
            </section>
            <section class="panel server-form-section" data-protocols="rdp,smb">
                <h2>{{T(context, "Domain (RDP/SMB)")}}</h2>
                <div class="form-grid">
                    <label>{{T(context, "Domain")}}
                        <input name="domain" value="{{A(server?.Domain)}}">
                    </label>
                </div>
            </section>
            <section class="panel server-form-section" data-protocols="rdp">
                <h2>{{T(context, "RDP settings")}}</h2>
                <div class="form-grid">
                    <label>{{T(context, "RDP keyboard layout")}}
                        <input name="keyboardLayout" list="keyboardLayouts" value="{{A(keyboardLayout)}}" autocomplete="off">
                        <datalist id="keyboardLayouts">
                            <option value="de-de-qwertz">Deutsch (QWERTZ)</option>
                            <option value="de-ch-qwertz">Schweiz Deutsch (QWERTZ)</option>
                            <option value="en-us-qwerty">English US (QWERTY)</option>
                            <option value="en-gb-qwerty">English UK (QWERTY)</option>
                            <option value="fr-fr-azerty">Francais (AZERTY)</option>
                            <option value="it-it-qwerty">Italiano (QWERTY)</option>
                            <option value="es-es-qwerty">Espanol (QWERTY)</option>
                        </datalist>
                    </label>
                    <label class="check"><input type="checkbox" name="ignoreCertificate"{{Checked(server?.IgnoreCertificate ?? true)}}> {{T(context, "Ignore RDP certificate")}}</label>
                </div>
            </section>
            <section class="panel server-form-section" data-protocols="ssh">
                <h2>{{T(context, "SSH settings")}}</h2>
                <div class="form-grid">
                    <label>{{T(context, "SSH font size")}}
                        <input name="terminalFontSize" type="number" min="8" max="24" value="{{terminalFontSize}}">
                    </label>
                </div>
            </section>
            <section class="panel server-form-section" data-protocols="sftp,ftp,smb">
                <h2>{{T(context, "File access")}}</h2>
                <div class="form-grid">
                    <label>{{T(context, "File start path / SMB share")}}
                        <input name="fileRootPath" value="{{A(server?.FileRootPath)}}" placeholder="/home/user oder Share/Ordner">
                    </label>
                </div>
            </section>
            <section class="panel server-form-section">
                <h2>{{T(context, "Status")}}</h2>
                <div class="form-grid">
                    <label class="check"><input type="checkbox" name="isEnabled"{{Checked(server?.IsEnabled ?? true)}}> {{T(context, "Enabled")}}</label>
                    <label class="wide">{{T(context, "Notes")}}
                        <textarea name="notes">{{E(server?.Notes)}}</textarea>
                    </label>
                </div>
            </section>
            """;
        return basicSection;
    }

    private static string ServerFormScript()
    {
        return """
            <script>
                (() => {
                    const form = document.querySelector('form[data-server-form]');
                    if (!form) {
                        return;
                    }

                    const protocolSelect = form.querySelector('select[name="protocol"]');
                    if (!protocolSelect) {
                        return;
                    }

                    const protocolTargets = Array.from(form.querySelectorAll('[data-protocols]'));
                    const normalize = (value) => (value || '').toString().trim().toLowerCase();

                    const update = () => {
                        const current = normalize(protocolSelect.value);
                        protocolTargets.forEach((element) => {
                            const allowed = (element.dataset.protocols || '')
                                .split(',')
                                .map(normalize)
                                .filter(Boolean);
                            const shouldHide = allowed.length > 0 && !allowed.includes(current);
                            element.hidden = shouldHide;
                            element.querySelectorAll('input, select, textarea, button').forEach((control) => {
                                control.disabled = shouldHide;
                            });
                        });
                    };

                    protocolSelect.addEventListener('change', update);
                    update();
                })();
            </script>
            """;
    }

    private static string RoleLabels(HttpContext context, MatgateUser user)
    {
        var labels = new List<string>();
        if (user.IsAdmin)
        {
            labels.Add("""<span class="badge">ADMIN</span>""");
        }

        if (user.CanManageServers)
        {
            labels.Add("""<span class="badge">MANAGE</span>""");
        }

        if (user.CanCreateServers)
        {
            labels.Add("""<span class="badge">CREATE</span>""");
        }

        return labels.Count == 0 ? $"""<span class="badge">{T(context, "User")}</span>""" : string.Join(" ", labels);
    }

    private static string ServerIcon(ServerEndpoint server, string size = "")
    {
        var iconKey = ServerEndpoint.EffectiveIconKey(server.Protocol, server.IconKey);
        var sizeClass = string.IsNullOrWhiteSpace(size) ? "" : $" {A(size)}";
        return $"""<span class="server-icon{sizeClass}" title="{A(iconKey.ToUpperInvariant())}">{Icon(iconKey)}</span>""";
    }

    private static string ServerScopeText(HttpContext context, ServerEndpoint server, IReadOnlyList<MatgateUser>? users = null, MatgateUser? currentUser = null)
    {
        if (server.OwnerUserId is null)
        {
            return T(context, "Global");
        }

        var owner = users?.FirstOrDefault(candidate => candidate.Id == server.OwnerUserId)
            ?? (currentUser is not null && currentUser.Id == server.OwnerUserId ? currentUser : null);

        var ownerName = owner is null
            ? T(context, "Unknown user")
            : string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.UserName : owner.DisplayName;

        return $"{T(context, "User")}: {ownerName}";
    }

    private static string ServerScopeBadge(HttpContext context, ServerEndpoint server, IReadOnlyList<MatgateUser>? users = null, MatgateUser? currentUser = null)
    {
        return $"""<span class="badge">{ServerScopeText(context, server, users, currentUser)}</span>""";
    }

    private static string ServerTargetValue(ServerEndpoint server)
    {
        if (ServerEndpoint.IsWebsiteProtocol(server.Protocol))
        {
            return string.IsNullOrWhiteSpace(server.WebsiteUrl)
                ? (string.IsNullOrWhiteSpace(server.Host) ? "-" : server.Host)
                : server.WebsiteUrl;
        }

        if (string.IsNullOrWhiteSpace(server.Host))
        {
            return "-";
        }

        return $"{server.Host}:{server.Port}";
    }

    private static string ServerProtocolLabel(ServerProtocol protocol)
    {
        return protocol == ServerProtocol.Website
            ? "Website (Beta)"
            : protocol.ToString().ToUpperInvariant();
    }

    private static string ServerIconOptions(string selectedIconKey)
    {
        return string.Join("", ServerEndpoint.IconKeys.Select(iconKey =>
        {
            var label = iconKey switch
            {
                "rdp" => "RDP / Desktop",
                "vnc" => "VNC / Desktop",
                "ssh" => "SSH / Terminal",
                "sftp" => "SFTP / Secure files",
                "ftp" => "FTP / Transfer",
                "smb" => "SMB / Share",
                "server" => "Server",
                "desktop" => "Desktop",
                "terminal" => "Terminal",
                "folder" => "Folder",
                "database" => "Database",
                "cloud" => "Cloud",
                "shield" => "Shield",
                "home" => "Home",
                "globe" => "Website",
                _ => iconKey
            };

            return $"""<option value="{A(iconKey)}"{Selected(string.Equals(selectedIconKey, iconKey, StringComparison.OrdinalIgnoreCase))}>{A(label)}</option>""";
        }));
    }

    private static string Csrf(HttpContext context)
    {
        return $"""<input type="hidden" name="_csrf" value="{A(context.User.FindFirstValue("csrf"))}">""";
    }

    public static string Language(HttpContext context)
    {
        var claimLanguage = context.User.FindFirstValue("lang");
        if (claimLanguage is "de" or "en")
        {
            return claimLanguage;
        }

        var requested = context.Request.Query["lang"].ToString();
        var selected = string.IsNullOrWhiteSpace(requested)
            ? context.Request.Cookies[LanguageCookieName]
            : requested;

        return NormalizeLanguageCode(selected);
    }

    public static string LanguageCookie => LanguageCookieName;

    public static string ThemeCookie => ThemeCookieName;

    private static string NormalizeLanguageCode(string? value)
    {
        return string.Equals((value ?? "").Trim(), "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
    }

    private static string NormalizeThemeCode(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is "light" or "dark" or "system" ? normalized : "system";
    }

    public static string Theme(HttpContext context, MatgateUser? user = null)
    {
        if (user is not null)
        {
            return NormalizeThemeCode(user.PreferredTheme);
        }

        var requested = context.Request.Cookies[ThemeCookieName];
        return NormalizeThemeCode(requested);
    }

    private static string T(HttpContext context, string key)
    {
        return Language(context) == "de" && GermanText.TryGetValue(key, out var translated)
            ? translated
            : key;
    }

    public static string Translate(HttpContext context, string key) => T(context, key);

    private static string LanguageOptions(HttpContext context, string selectedLanguage)
    {
        var normalized = NormalizeLanguageCode(selectedLanguage);
        return $$"""
            <option value="en"{{Selected(normalized == "en")}}>{{T(context, "English")}}</option>
            <option value="de"{{Selected(normalized == "de")}}>{{T(context, "German")}}</option>
            """;
    }

    private static string ThemeOptions(HttpContext context, string selectedTheme)
    {
        var normalized = NormalizeThemeCode(selectedTheme);
        return $$"""
            <option value="system"{{Selected(normalized == "system")}}>{{T(context, "System")}}</option>
            <option value="light"{{Selected(normalized == "light")}}>{{T(context, "Light")}}</option>
            <option value="dark"{{Selected(normalized == "dark")}}>{{T(context, "Dark")}}</option>
            """;
    }

    private static string ApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            return plusIndex >= 0 ? informational[..plusIndex].Trim() : informational.Trim();
        }

        var version = assembly.GetName().Version;
        return version is null ? "dev" : version.ToString(3);
    }

    private static string Icon(string name)
    {
        var path = name switch
        {
            "home" => """<path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V21h14V9.5"/><path d="M9 21v-6h6v6"/>""",
            "workspace" => """<rect x="3" y="4" width="18" height="14" rx="2"/><path d="M8 21h8"/><path d="M12 18v3"/><path d="m8 9 3 3-3 3"/><path d="M13 15h4"/>""",
            "server" => """<rect x="4" y="4" width="16" height="6" rx="2"/><rect x="4" y="14" width="16" height="6" rx="2"/><path d="M8 7h.01"/><path d="M8 17h.01"/>""",
            "wrench" => """<path d="M14.7 6.3a4 4 0 0 0-5.4 5.4L3 18v3h3l6.3-6.3a4 4 0 0 0 5.4-5.4l-2 2-2.3-.6-.6-2.3z"/>""",
            "user" => """<circle cx="12" cy="8" r="4"/><path d="M4 21v-1a8 8 0 0 1 16 0v1"/>""",
            "chevron-down" => """<path d="m6 9 6 6 6-6"/>""",
            "info" => """<circle cx="12" cy="12" r="9"/><path d="M12 17v-6"/><path d="M12 8h.01"/>""",
            "rdp" => """<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/><path d="M8 8h3v3H8z"/><path d="M13 8h3v3h-3z"/><path d="M8 13h3v1H8z"/><path d="M13 13h3v1h-3z"/>""",
            "vnc" => """<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/>""",
            "ssh" => """<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3"/><path d="M12 15h5"/>""",
            "sftp" => """<path d="M3 8a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><rect x="9" y="12" width="6" height="5" rx="1"/><path d="M10.5 12v-1.5a1.5 1.5 0 0 1 3 0V12"/>""",
            "ftp" => """<path d="M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/><path d="M7 9l5-5 5 5"/><path d="M12 4v12"/><path d="m8 13 4 4 4-4"/>""",
            "smb" => """<rect x="4" y="4" width="16" height="6" rx="2"/><rect x="4" y="14" width="16" height="6" rx="2"/><path d="M8 7h.01"/><path d="M8 17h.01"/><path d="M12 10v4"/><path d="M9 12h6"/>""",
            "desktop" => """<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/>""",
            "terminal" => """<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3"/><path d="M13 15h4"/>""",
            "database" => """<ellipse cx="12" cy="5" rx="7" ry="3"/><path d="M5 5v14c0 1.7 3.1 3 7 3s7-1.3 7-3V5"/><path d="M5 12c0 1.7 3.1 3 7 3s7-1.3 7-3"/>""",
            "cloud" => """<path d="M17.5 19H8a5 5 0 1 1 1.5-9.8 6 6 0 0 1 11 3.8 3.5 3.5 0 0 1-3 6z"/>""",
            "shield" => """<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="m9 12 2 2 4-4"/>""",
            "star" => """<path d="M12 3l2.9 5.9 6.5.9-4.7 4.6 1.1 6.5L12 17l-5.8 3.9 1.1-6.5-4.7-4.6 6.5-.9z"/>""",
            "users" => """<path d="M16 21v-2a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v2"/><circle cx="9.5" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>""",
            "arrow-left" => """<path d="M11 19 4 12l7-7"/><path d="M20 12H5"/>""",
            "arrow-right" => """<path d="m13 5 7 7-7 7"/><path d="M4 12h15"/>""",
            "external-link" => """<path d="M14 3h7v7"/><path d="M10 14 21 3"/><path d="M21 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5"/>""",
            "logout" => """<path d="M10 17l5-5-5-5"/><path d="M15 12H3"/><path d="M21 3v18"/>""",
            "play" => """<path d="m8 5 11 7-11 7z"/>""",
            "x" => """<path d="M6 6l12 12"/><path d="M18 6 6 18"/>""",
            "music" => """<path d="M9 18V5l10-2v13"/><circle cx="7" cy="18" r="3"/><circle cx="17" cy="16" r="3"/>""",
            "plus" => """<path d="M12 5v14"/><path d="M5 12h14"/>""",
            "save" => """<path d="M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z"/><path d="M17 21v-8H7v8"/><path d="M7 3v5h8"/>""",
            "trash" => """<path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 15H6L5 6"/>""",
            "key" => """<circle cx="7.5" cy="15.5" r="3.5"/><path d="M10 13 21 2"/><path d="m15 7 2 2"/><path d="m18 4 2 2"/>""",
            "folder" => """<path d="M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>""",
            "folder-up" => """<path d="M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="m9 14 3-3 3 3"/><path d="M12 17v-6"/>""",
            "folder-plus" => """<path d="M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><path d="M12 11v6"/><path d="M9 14h6"/>""",
            "file" => """<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/><path d="M8 13h8"/><path d="M8 17h6"/>""",
            "clipboard" => """<rect x="8" y="4" width="8" height="4" rx="1"/><path d="M16 6h2a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h2"/>""",
            "maximize" => """<path d="M8 3H5a2 2 0 0 0-2 2v3"/><path d="M16 3h3a2 2 0 0 1 2 2v3"/><path d="M8 21H5a2 2 0 0 1-2-2v-3"/><path d="M16 21h3a2 2 0 0 0 2-2v-3"/>""",
            "refresh" => """<path d="M21 12a9 9 0 0 1-15.5 6.2"/><path d="M3 12A9 9 0 0 1 18.5 5.8"/><path d="M18 2v4h4"/><path d="M6 22v-4H2"/>""",
            "eye" => """<path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>""",
            "edit" => """<path d="M4 20h4l10-10-4-4L4 16z"/><path d="m14 6 4 4"/>""",
            "copy" => """<rect x="8" y="8" width="12" height="12" rx="2"/><rect x="4" y="4" width="12" height="12" rx="2"/>""",
            "move" => """<path d="M12 2v20"/><path d="m8 6 4-4 4 4"/><path d="m8 18 4 4 4-4"/><path d="M2 12h20"/><path d="m6 8-4 4 4 4"/><path d="m18 8 4 4-4 4"/>""",
            "archive" => """<rect x="3" y="4" width="18" height="4" rx="1"/><path d="M5 8v11a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8"/><path d="M10 12h4"/>""",
            "check" => """<path d="m20 6-11 11-5-5"/>""",
            "upload" => """<path d="M12 3v12"/><path d="m7 8 5-5 5 5"/><path d="M5 21h14"/>""",
            "download" => """<path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M5 21h14"/>""",
            "globe" => """<circle cx="12" cy="12" r="10"/><path d="M2 12h20"/><path d="M12 2a15 15 0 0 1 0 20"/><path d="M12 2a15 15 0 0 0 0 20"/>""",
            _ => """<circle cx="12" cy="12" r="9"/>"""
        };

        return $"""<svg class="icon" viewBox="0 0 24 24" aria-hidden="true">{path}</svg>""";
    }

    private static string Logo()
    {
        return """
            <span class="brand-mark" aria-hidden="true">
                <span class="brand-gate"></span>
                <span class="brand-core">M</span>
            </span>
            <span class="brand-word"><span>MAT</span>GATE</span>
            """;
    }

    private static string FormatFileSize(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(0, value);
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string Checked(bool isChecked) => isChecked ? " checked" : "";

    private static string Selected(bool isSelected) => isSelected ? " selected" : "";

    private static string CssClasses(string baseClass, string? className = null)
    {
        return string.IsNullOrWhiteSpace(className) ? baseClass : $"{baseClass} {className}";
    }

    private static string Attr(string name, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : $" {name}=\"{A(value)}\"";
    }

    private static string BoolAttr(string name, bool value)
    {
        return value ? $" {name}" : "";
    }

    private static string Toolbar(string className, params string[] groups)
    {
        var content = string.Join("", groups.Where(group => !string.IsNullOrWhiteSpace(group)));
        return $"""<div class="{A(CssClasses("toolbar", className))}">{content}</div>""";
    }

    private static string ToolbarGroup(string className, params string[] items)
    {
        var content = string.Join("", items.Where(item => !string.IsNullOrWhiteSpace(item)));
        return $"""<div class="{A(CssClasses("toolbar-group", className))}">{content}</div>""";
    }

    private static string ToolbarButton(string label, string iconHtml, string className = "", string extraAttributes = "", bool disabled = false)
    {
        var attrs = string.Concat(
            string.IsNullOrWhiteSpace(extraAttributes) ? "" : $" {extraAttributes.Trim()}",
            BoolAttr("disabled", disabled));
        return $"""<button type="button" class="{A(CssClasses("toolbar-button", className))}"{attrs}>{iconHtml}<span>{E(label)}</span></button>""";
    }

    private static string ToolbarInput(string className, string value, string ariaLabel, bool readOnly = false, string extraAttributes = "")
    {
        var attrs = string.Concat(
            Attr("aria-label", ariaLabel),
            Attr("value", value),
            BoolAttr("readonly", readOnly),
            string.IsNullOrWhiteSpace(extraAttributes) ? "" : $" {extraAttributes.Trim()}");
        return $"""<input type="text" class="{A(CssClasses("toolbar-input", className))}"{attrs}>""";
    }

    private static string ToolbarMenu(string className, string summaryClassName, string summaryLabel, string summaryIconHtml, string summaryAttributes = "", params string[] items)
    {
        var content = string.Join("", items.Where(item => !string.IsNullOrWhiteSpace(item)));
        var attrs = string.IsNullOrWhiteSpace(summaryAttributes) ? "" : $" {summaryAttributes.Trim()}";
        return $"""
            <details class="{A(CssClasses("toolbar-menu", className))}">
                <summary class="{A(CssClasses("toolbar-button toolbar-menu-trigger", summaryClassName))}"{attrs}>{summaryIconHtml}<span>{E(summaryLabel)}</span><span class="menu-caret">{Icon("chevron-down")}</span></summary>
                <div class="toolbar-menu-panel file-menu-panel">{content}</div>
            </details>
            """;
    }

    private static string ToolbarMenuItem(string label, string iconHtml, string className = "", string extraAttributes = "", bool disabled = false)
    {
        return ToolbarButton(label, iconHtml, CssClasses("toolbar-menu-item", className), extraAttributes, disabled);
    }

    private static string ToolbarUploadButton(string label, string iconHtml, string className = "", string inputClassName = "", string inputAttributes = "")
    {
        var inputAttrs = string.IsNullOrWhiteSpace(inputAttributes) ? " multiple" : $" multiple {inputAttributes.Trim()}";
        return $"""
            <label class="{A(CssClasses("toolbar-button toolbar-button--primary toolbar-upload-button", className))}" title="{A(label)}">
                {iconHtml}<span>{E(label)}</span>
                <input type="file" class="{A(CssClasses("toolbar-upload-input file-upload-input", inputClassName))}"{inputAttrs}>
            </label>
            """;
    }

    private static string Clean(string? value, string fallback)
    {
        var cleaned = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string E(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");

    private static string A(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");
}
