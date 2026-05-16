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

    private static readonly IReadOnlyDictionary<string, string> GermanText = new Dictionary<string, string>
    {
        ["Home Network Gateway"] = "Heimnetz-Gateway",
        ["Home"] = "Home",
        ["About"] = "Über",
        ["About Matgate"] = "Über Matgate",
        ["Local login for RDP, SSH, browser and file access in your home network."] = "Lokale Anmeldung fuer RDP-, SSH-, Browser- und Dateizugriffe im Heimnetz.",
        ["Account"] = "Konto",
        ["Admin"] = "Admin",
        ["Version"] = "Version",
        ["Global"] = "Global",
        ["Username"] = "Benutzername",
        ["Password"] = "Passwort",
        ["Sign in"] = "Einloggen",
        ["Dashboard"] = "Dashboard",
        ["Connections"] = "Verbindungen",
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
        ["New password"] = "Neues Passwort",
        ["Set password"] = "Passwort setzen",
        ["Remove"] = "Entfernen",
        ["Cannot delete own user"] = "Eigenen Benutzer nicht loeschen",
        ["Delete user"] = "Benutzer loeschen",
        ["Servers"] = "Server",
        ["Create server"] = "Server anlegen",
        ["Existing servers"] = "Vorhandene Server",
        ["Type"] = "Typ",
        ["Target"] = "Ziel",
        ["Off"] = "Aus",
        ["Connection"] = "Verbindung",
        ["Clear saved target password"] = "Gespeichertes Ziel-Passwort entfernen",
        ["Delete server"] = "Server loeschen",
        ["Domain"] = "Domäne",
        ["Edit"] = "Bearbeiten",
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
        ["Target/VNC password"] = "Ziel-/VNC-Passwort",
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
        ["Open in browser"] = "Im Browser oeffnen",
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
                    <p class="muted">{{T(context, "Local login for RDP, SSH, browser and file access in your home network.")}}</p>
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
                                <span class="badge">{{E(server.Protocol.ToString().ToUpperInvariant())}}</span>
                                {{ServerScopeBadge(context, server, currentUser: user)}}
                                <h2>{{E(server.Name)}}</h2>
                            </div>
                        </div>
                        <a class="button primary" href="/sessions?open={{server.Id}}">{{Icon("play")}}{{T(context, "Connect")}}</a>
                    </div>
                    <p class="target">{{E(server.Host)}}:{{server.Port}}</p>
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
                        <td><span class="badge">{{E(server.Protocol.ToString().ToUpperInvariant())}}</span></td>
                        <td>{{E(server.Host)}}:{{server.Port}}</td>
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
                    <p class="muted">{{ServerScopeText(context, server, users)}}</p>
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

    public string Account(HttpContext context, MatgateUser user)
    {
        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
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
                    <div class="actions"><button type="submit" class="primary">{{Icon("save")}}{{T(context, "Save")}}</button></div>
                </form>
            </section>
            """;

        return Layout(context, user, T(context, "Account"), body);
    }

    public string About(HttpContext context, MatgateUser user)
    {
        var version = ApplicationVersion();
        var closeButton = $$"""
            <a class="button" href="/sessions" data-about-close onclick="return window.MatgateCloseAboutWindow(event, this)">{{Icon("x")}}{{T(context, "Close")}}</a>
            """;
        var body = AboutBody(context, version, closeButton);

        return Layout(context, user, T(context, "About"), body, "viewer-main");
    }

    private static string AboutBody(HttpContext context, string version, string closeButton)
    {
        return $$"""
            <section class="file-viewer-page about-viewer">
                <div class="session-tab-row viewer-tab-row">
                    <div class="session-tabs viewer-tabs">
                        <div class="session-tab active viewer-tab">
                            <div class="session-tab-main viewer-tab-main" aria-current="page">
                                <span class="session-tab-title">{{Icon("info")}}<span>{{T(context, "About Matgate")}}</span></span>
                                <small>MATGATE {{E(version)}}</small>
                            </div>
                        </div>
                    </div>
                    <div class="tab-actions viewer-actions">
                        {{closeButton}}
                    </div>
                </div>
                <div class="viewer-body">
                    <section class="viewer-stage about-stage">
                        <div class="about-window">
                            <div class="about-brand">{{Logo()}}</div>
                            <div class="about-copy">
                                <p class="eyebrow">{{T(context, "About")}}</p>
                                <h1>{{T(context, "About Matgate")}}</h1>
                                <p class="version-number">{{E(version)}}</p>
                                <p class="muted">{{T(context, "Local login for RDP, SSH, browser and file access in your home network.")}}</p>
                            </div>
                        </div>
                    </section>
                </div>
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
            ? $"""<tr><td colspan="5" class="muted">{E(emptyMessage)}</td></tr>"""
            : string.Join("", servers.Select(server => $$"""
                <tr>
                    <td><span class="server-name-cell">{{ServerIcon(server, "small")}}<a href="/admin/servers/{{server.Id}}">{{E(server.Name)}}</a></span></td>
                    <td>{{ServerScopeBadge(context, server, users)}}</td>
                    <td><span class="badge">{{E(server.Protocol.ToString().ToUpperInvariant())}}</span></td>
                    <td>{{E(server.Host)}}:{{server.Port}}</td>
                    <td>{{(server.IsEnabled ? T(context, "Active") : T(context, "Off"))}}</td>
                </tr>
                """));

        return $$"""
            <section class="home-management">
                <section class="panel">
                    <h2>{{E(title)}}</h2>
                    <div class="table-wrap">
                        <table>
                            <thead><tr><th>{{T(context, "Name")}}</th><th>{{T(context, "Scope")}}</th><th>{{T(context, "Type")}}</th><th>{{T(context, "Target")}}</th><th>{{T(context, "Status")}}</th></tr></thead>
                            <tbody>{{rows}}</tbody>
                        </table>
                    </div>
                </section>
            </section>
            """;
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
            target = $"{server.Host}:{server.Port}"
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
        var archiveExtensions = JsonSerializer.Serialize(FileArchiveFormats.SupportedExtensions, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var uiText = JsonSerializer.Serialize(new
        {
            dashboard = T(context, "Home"),
            connections = T(context, "Connections"),
            username = T(context, "Username"),
            password = T(context, "Password"),
            noActiveTab = T(context, "No active tab"),
            newConnection = T(context, "New connection"),
            ready = T(context, "Ready"),
            chooseConnection = Language(context) == "de" ? "Verbindung auswaehlen" : "Choose a connection",
            starting = Language(context) == "de" ? "Startet" : "Starting",
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
            disconnect = T(context, "Disconnect"),
            clipboardSent = Language(context) == "de" ? "Zwischenablage gesendet" : "Clipboard sent",
            clipboardReceived = Language(context) == "de" ? "Zwischenablage empfangen" : "Clipboard received",
            remoteClipboardReady = Language(context) == "de" ? "Remote-Zwischenablage bereit" : "Remote clipboard ready",
            connectionContinues = Language(context) == "de" ? "Verbindung wird fortgesetzt" : "Connection continues",
            credentialsSubmitted = Language(context) == "de" ? "Die Zugangsdaten wurden uebergeben." : "Credentials were submitted."
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var connectionChoices = sharedServers.Count == 0
            ? $"""<div class="empty">{T(context, "No shared connections.")}</div>"""
            : string.Join("", sharedServers.Select(server => $$"""
                <article class="connection-choice">
                    <div>
                        <div class="server-title">
                            {{ServerIcon(server)}}
                            <div>
                                <span class="badge">{{E(server.Protocol.ToString().ToUpperInvariant())}}</span>
                                <h2>{{E(server.Name)}}</h2>
                            </div>
                        </div>
                        <p class="target">{{E(server.Host)}}:{{server.Port}}</p>
                        <p class="muted">{{E(server.Notes)}}</p>
                    </div>
                    <button type="button" class="primary workspace-open-button" data-server-id="{{server.Id}}">{{Icon("play")}}{{T(context, "Open")}}</button>
                </article>
                """));
        var ownConnectionChoices = ownServers.Count == 0
            ? $"""<div class="empty">{T(context, "No own servers created yet.")}</div>"""
            : string.Join("", ownServers.Select(server => $$"""
                <article class="connection-choice">
                    <div>
                        <div class="server-title">
                            {{ServerIcon(server)}}
                            <div>
                                <span class="badge">{{E(server.Protocol.ToString().ToUpperInvariant())}}</span>
                                {{ServerScopeBadge(context, server, currentUser: user)}}
                                <h2>{{E(server.Name)}}</h2>
                            </div>
                        </div>
                        <p class="target">{{E(server.Host)}}:{{server.Port}}</p>
                        <p class="muted">{{E(server.Notes)}}</p>
                    </div>
                    <div class="actions">
                        <button type="button" class="primary workspace-open-button" data-server-id="{{server.Id}}">{{Icon("play")}}{{T(context, "Open")}}</button>
                        <a class="button" href="/admin/servers/{{server.Id}}">{{Icon("edit")}}{{T(context, "Edit")}}</a>
                    </div>
                </article>
                """));
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
                    <section class="connection-picker-grid">
                        {{ownConnectionChoices}}
                    </section>
                </section>
                """
            : "";
        var aboutVersion = ApplicationVersion();
        var aboutModalClose = $$"""
            <button type="button" class="button" onclick="return window.MatgateCloseAboutModal(event, this)">{{Icon("x")}}{{T(context, "Close")}}</button>
            """;

        var body = $$"""
            <div id="matgate-shell" class="matgate-shell">
            <section id="home-view" class="app-view session-page multi-session-page active">
                <div class="session-tab-row">
                    <div id="session-tabs" class="session-tabs" role="tablist"></div>
                    <button id="new-connection-tab" type="button" class="session-tab session-new-tab" role="tab" aria-label="{{A(T(context, "New connection"))}}">{{Icon("plus")}}</button>
                    <div class="tab-actions" aria-label="{{A(T(context, "Actions for the active tab"))}}">
                        <button id="clipboard-send-button" type="button">{{Icon("clipboard")}}{{T(context, "Clipboard")}}</button>
                        <button id="fullscreen-button" type="button">{{Icon("maximize")}}{{T(context, "Fullscreen")}}</button>
                        <button id="disconnect-active-button" type="button" class="danger">{{Icon("trash")}}{{T(context, "Disconnect")}}</button>
                    </div>
                </div>
                <div id="session-deck" class="session-deck">
                    <div id="new-connection-panel" class="connection-picker-panel">
                        <div class="connection-picker-inner">
                            <section class="connection-picker-head">
                                <p class="eyebrow">{{T(context, "Home")}}</p>
                                <h1>{{T(context, "New Connection")}}</h1>
                            </section>
                            <section class="connection-picker-grid">
                                {{connectionChoices}}
                            </section>
                            {{ownServersSection}}
                        </div>
                    </div>
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
                <dialog id="about-dialog" class="file-viewer-dialog about-dialog">
                    {{AboutBody(context, aboutVersion, aboutModalClose)}}
                </dialog>
            </section>
            </div>
            <script src="/guacamole/guacamole-common-js/all.min.js"></script>
            <script>
            (() => {
                const availableServers = {{availableServers}};
                const initialOpenServerId = {{initialOpenServerId}};
                const csrfToken = {{csrfToken}};
                const uiText = {{uiText}};
                const fileIcons = {{fileIcons}};
                const archiveExtensions = {{archiveExtensions}};
                const homeView = document.getElementById('home-view');
                const tabsRoot = document.getElementById('session-tabs');
                const newConnectionTab = document.getElementById('new-connection-tab');
                const newConnectionPanel = document.getElementById('new-connection-panel');
                const deck = document.getElementById('session-deck');
                const statusTitle = document.getElementById('status-title');
                const statusTarget = document.getElementById('status-target');
                const statusState = document.getElementById('status-state');
                const statusLatency = document.getElementById('status-latency');
                const statusTunnel = document.getElementById('status-tunnel');
                const statusSync = document.getElementById('status-sync');
                const statusMessage = document.getElementById('status-message');
                const clipboardSendButton = document.getElementById('clipboard-send-button');
                const fullscreenButton = document.getElementById('fullscreen-button');
                const disconnectActiveButton = document.getElementById('disconnect-active-button');
                const aboutInfoButton = document.getElementById('status-info-button');
                const aboutDialog = document.getElementById('about-dialog');
                const credentialDialog = document.getElementById('credential-dialog');
                const credentialFields = document.getElementById('credential-fields');
                const credentialCancel = document.getElementById('credential-cancel');
                const clipboardDialog = document.getElementById('clipboard-dialog');
                const clipboardText = document.getElementById('clipboard-text');
                const clipboardClose = document.getElementById('clipboard-close');
                const tabs = new Map();
                const workspaceStorageKey = 'matgate.workspace.tabs.v1';

                let activeTabId = null;
                let credentialTab = null;
                let resizeTimer = null;
                let gatewayLatencyMs = null;

                const closeAboutDialog = () => {
                    if (aboutDialog && typeof aboutDialog.close === 'function' && aboutDialog.open) {
                        aboutDialog.close();
                    }
                };

                window.MatgateCloseAboutModal = (event) => {
                    if (event) {
                        event.preventDefault();
                    }

                    closeAboutDialog();
                    return false;
                };
                window.MatgateOpenAboutModal = (event) => {
                    if (event) {
                        event.preventDefault();
                    }

                    if (!aboutDialog || typeof aboutDialog.showModal !== 'function') {
                        window.open('/about', '_blank', 'noopener');
                        return false;
                    }

                    if (typeof aboutDialog.showModal === 'function' && !aboutDialog.open) {
                        aboutDialog.showModal();
                    }

                    return false;
                };
                if (aboutInfoButton) {
                    aboutInfoButton.addEventListener('click', window.MatgateOpenAboutModal);
                }
                if (aboutDialog) {
                    aboutDialog.addEventListener('click', (event) => {
                        if (event.target === aboutDialog) {
                            closeAboutDialog();
                        }
                    });
                }

                function showView(view, updateHistory) {
                    homeView.classList.add('active');
                    document.body.dataset.view = 'home';
                    document.title = `${ui('dashboard')} - Matgate`;

                    if (updateHistory && location.pathname !== '/') {
                        history.pushState({ view: 'home' }, '', '/');
                    }

                    const activeTab = tabs.get(activeTabId);
                    if (activeTab) {
                        fitDisplay(activeTab);
                        activeTab.panel.focus();
                    }
                }

                function wireShellNavigation() {
                    document.querySelectorAll('a[href="/"], a[href="/sessions"]').forEach(anchor => {
                        anchor.addEventListener('click', event => {
                            if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || event.button !== 0) {
                                return;
                            }

                            event.preventDefault();
                            showView('home', true);
                        });
                    });

                    document.querySelectorAll('.workspace-open-button').forEach(button => {
                        button.addEventListener('click', () => {
                            openServer(button.getAttribute('data-server-id') || '');
                        });
                    });

                    newConnectionTab.addEventListener('click', activateNewConnectionTab);

                    window.addEventListener('popstate', () => {
                        showView('home', false);
                    });
                }

                function newTabId() {
                    if (crypto && crypto.randomUUID) {
                        return crypto.randomUUID();
                    }

                    return `${Date.now()}-${Math.random().toString(16).slice(2)}`;
                }

                function findServer(serverId) {
                    return availableServers.find(server => server.id === serverId);
                }

                function ui(key) {
                    return uiText[key] || key;
                }

                function isFileProtocol(protocol) {
                    return ['SFTP', 'FTP', 'SMB'].includes((protocol || '').toUpperCase());
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
                    const activeTab = tabs.get(activeTabId);
                    const hasActiveConnection = Boolean(activeTab);
                    disconnectActiveButton.disabled = !hasActiveConnection;
                    fullscreenButton.disabled = !hasActiveConnection;
                    clipboardSendButton.disabled = !activeTab || isFileProtocol(activeTab.protocol);
                    disconnectActiveButton.textContent = activeTab && isFileProtocol(activeTab.protocol) ? ui('close') : ui('disconnect');
                    updateStatusBar();
                }

                function activateNewConnectionTab() {
                    activeTabId = null;
                    newConnectionTab.classList.add('active');
                    newConnectionPanel.classList.remove('hidden');

                    for (const tab of tabs.values()) {
                        tab.tabButton.classList.remove('active');
                        tab.panel.classList.add('hidden');
                    }

                    updateTabActions();
                    saveWorkspaceTabs();
                }

                function createTab(server, options = {}) {
                    const tabId = newTabId();
                    const tabButton = document.createElement('div');
                    tabButton.className = 'session-tab';
                    tabButton.setAttribute('role', 'tab');

                    const tabMain = document.createElement('button');
                    tabMain.type = 'button';
                    tabMain.className = 'session-tab-main';
                    tabMain.innerHTML = `<span class="session-tab-title">${server.iconHtml || ''}<span>${escapeHtml(server.name)}</span></span><small>${escapeHtml(ui('starting'))}</small>`;

                    const closeButton = document.createElement('button');
                    closeButton.type = 'button';
                    closeButton.className = 'session-tab-close';
                    closeButton.setAttribute('aria-label', 'Tab schliessen');
                    closeButton.innerHTML = '&times;';

                    tabButton.append(tabMain, closeButton);
                    tabsRoot.appendChild(tabButton);

                    const panel = document.createElement('div');
                    panel.className = 'connection-panel hidden';
                    panel.tabIndex = 0;

                    const displayRoot = document.createElement('div');
                    displayRoot.className = 'guac-display';

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
                        statusLabel: tabMain.querySelector('small'),
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
                        statusTimer: null,
                        statusUpdatedAt: Date.now(),
                        watchdog: null
                    };

                    tabMain.addEventListener('click', () => activateTab(tab.id));
                    closeButton.addEventListener('click', event => {
                        event.stopPropagation();
                        closeTab(tab.id);
                    });
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
                    newConnectionTab.classList.remove('active');
                    newConnectionPanel.classList.add('hidden');
                    for (const tab of tabs.values()) {
                        const active = tab.id === tabId;
                        tab.tabButton.classList.toggle('active', active);
                        tab.panel.classList.toggle('hidden', !active);
                    }

                    const activeTab = tabs.get(tabId);
                    fitDisplay(activeTab);
                    activeTab.panel.focus();
                    updateTabActions();
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
                    saveWorkspaceTabs();
                }

                function openServer(serverId, filePath = '') {
                    const server = findServer(serverId);
                    if (server) {
                        return createTab(server, { filePath });
                    }

                    return null;
                }

                function saveWorkspaceTabs() {
                    try {
                        const activeTab = tabs.get(activeTabId);
                        const state = {
                            tabs: Array.from(tabs.values()).map(tab => ({
                                serverId: tab.serverId,
                                filePath: isFileProtocol(tab.protocol) ? (tab.filePath || '/') : ''
                            })),
                            activeServerId: activeTab ? activeTab.serverId : ''
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
                            return { tabs: [], activeServerId: '' };
                        }

                        const state = JSON.parse(raw);
                        const tabStates = Array.isArray(state.tabs) ? state.tabs : [];
                        return {
                            tabs: tabStates
                                .map(tabState => typeof tabState === 'string'
                                    ? { serverId: tabState, filePath: '' }
                                    : {
                                        serverId: tabState.serverId || tabState.id || '',
                                        filePath: tabState.filePath || tabState.path || ''
                                    })
                                .filter(tabState => Boolean(findServer(tabState.serverId))),
                            activeServerId: findServer(state.activeServerId) ? state.activeServerId : ''
                        };
                    }
                    catch {
                        return { tabs: [], activeServerId: '' };
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
                    statusTarget.textContent = `${tab.protocol} ${tab.target}`;
                    statusState.textContent = tab.status || ui('ready');
                    statusLatency.textContent = gatewayLatencyMs === null ? 'Gateway: -' : `Gateway: ${gatewayLatencyMs} ms`;
                    statusTunnel.textContent = isFileProtocol(tab.protocol)
                        ? `Tunnel: ${ui('fileApi')}`
                        : `Tunnel: ${tab.tunnelState || '-'}`;
                    statusSync.textContent = `Sync: ${formatAge(tab.lastSyncAt)}`;
                    statusMessage.textContent = tab.lastError || tab.lastMessage || '-';
                }

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
                        <div class="file-toolbar">
                            <div class="file-toolbar-group file-toolbar-main">
                                <button type="button" class="file-tool-button" data-file-action="refresh" title="${escapeHtml(ui('refresh'))}">
                                    ${fileIcon('refresh')}<span>${escapeHtml(ui('refresh'))}</span>
                                </button>
                                <input class="file-path-input" aria-label="${escapeHtml(ui('path'))}" value="/">
                                <details class="file-menu file-create-menu">
                                    <summary class="file-tool-button file-menu-trigger" title="${escapeHtml(ui('create'))}">
                                        ${fileIcon('plus')}<span>${escapeHtml(ui('create'))}</span><span class="menu-caret">${fileIcon('chevronDown')}</span>
                                    </summary>
                                    <div class="file-menu-panel">
                                        <button type="button" class="file-action-button file-menu-item" data-file-action="create-directory" title="${escapeHtml(ui('directory'))}">
                                            ${fileIcon('mkdir')}<span>${escapeHtml(ui('directory'))}</span>
                                        </button>
                                        <button type="button" class="file-action-button file-menu-item" data-file-action="create-file" title="${escapeHtml(ui('file'))}">
                                            ${fileIcon('file')}<span>${escapeHtml(ui('file'))}</span>
                                        </button>
                                    </div>
                                </details>
                                <details class="file-menu file-actions-menu">
                                    <summary class="file-tool-button file-menu-trigger" title="${escapeHtml(ui('actions'))}">
                                        ${fileIcon('menu')}<span>${escapeHtml(ui('actions'))}</span><span class="menu-caret">${fileIcon('chevronDown')}</span>
                                    </summary>
                                    <div class="file-menu-panel">
                                        <button type="button" class="file-action-button file-menu-item" data-file-action="move" disabled title="${escapeHtml(ui('move'))}">
                                            ${fileIcon('move')}<span>${escapeHtml(ui('move'))}</span>
                                        </button>
                                        <button type="button" class="file-action-button file-menu-item" data-file-action="copy" disabled title="${escapeHtml(ui('copy'))}">
                                            ${fileIcon('copy')}<span>${escapeHtml(ui('copy'))}</span>
                                        </button>
                                        <button type="button" class="file-action-button file-menu-item" data-file-action="zip" disabled title="${escapeHtml(ui('downloadZip'))}">
                                            ${fileIcon('archive')}<span>${escapeHtml(ui('downloadZip'))}</span>
                                        </button>
                                        <button type="button" class="file-action-button danger file-menu-item" data-file-action="delete-selected" disabled title="${escapeHtml(ui('deleteSelected'))}">
                                            ${fileIcon('delete')}<span>${escapeHtml(ui('delete'))}</span>
                                        </button>
                                    </div>
                                </details>
                            </div>
                            <div class="file-toolbar-group file-toolbar-transfer">
                                <label class="file-upload-button" title="${escapeHtml(ui('upload'))}">
                                    ${fileIcon('upload')}<span>${escapeHtml(ui('upload'))}</span>
                                    <input type="file" class="file-upload-input" multiple>
                                </label>
                                <button type="button" class="file-toggle-button" data-file-action="toggle-unzip" aria-pressed="false" title="${escapeHtml(ui('unzip'))}">
                                    ${fileIcon('archive')}<span>${escapeHtml(ui('unzip'))}</span>
                                </button>
                            </div>
                        </div>
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
                        extractArchiveButton: manager.querySelector('[data-file-action="toggle-unzip"]'),
                        extractArchiveMode: false,
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
                                formData.append('unzip', tab.fileUi.extractArchiveMode ? 'true' : 'false');
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
                        downloadSelectedAsZip(tab);
                    });
                    manager.querySelector('[data-file-action="delete-selected"]').addEventListener('click', () => {
                        closeFileMenus(tab);
                        deleteSelectedEntries(tab);
                    });
                    tab.fileUi.extractArchiveButton.addEventListener('click', () => {
                        tab.fileUi.extractArchiveMode = !tab.fileUi.extractArchiveMode;
                        updateFileUploadMode(tab);
                    });

                    tab.displayRoot.appendChild(manager);
                    updateFileUploadMode(tab);
                    hideOverlay(tab);
                    loadFilePath(tab, tab.filePath);
                }

                async function loadFilePath(tab, path) {
                    if (!tab || !tab.fileUi) {
                        return;
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
                    }
                    catch (error) {
                        const message = error instanceof Error ? error.message : ui('fileAccessFailed');
                        tab.lastError = message;
                        setStatus(tab, uiText.error || 'Error');
                        setFileMessage(tab, message);
                        if (!tab.fileUi.tbody.children.length) {
                            setOverlay(tab, ui('fileAccessFailed'), message, true);
                        }
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

                function updateFileUploadMode(tab) {
                    if (!tab.fileUi?.extractArchiveButton) {
                        return;
                    }

                    const active = Boolean(tab.fileUi.extractArchiveMode);
                    tab.fileUi.extractArchiveButton.classList.toggle('is-active', active);
                    tab.fileUi.extractArchiveButton.setAttribute('aria-pressed', active ? 'true' : 'false');
                }

                function downloadSelectedAsZip(tab) {
                    const paths = selectedFilePaths(tab);
                    if (!paths.length) {
                        return;
                    }

                    const frameName = 'matgate-download-frame';
                    if (!document.querySelector(`iframe[name="${frameName}"]`)) {
                        const frame = document.createElement('iframe');
                        frame.name = frameName;
                        frame.className = 'hidden';
                        document.body.appendChild(frame);
                    }

                    const form = document.createElement('form');
                    form.method = 'post';
                    form.action = `/api/files/${tab.serverId}/zip`;
                    form.target = frameName;
                    form.className = 'hidden';
                    form.appendChild(hiddenInput('_csrf', csrfToken));
                    for (const path of paths) {
                        form.appendChild(hiddenInput('paths', path));
                    }

                    document.body.appendChild(form);
                    form.submit();
                    form.remove();
                    flashStatus(tab, ui('zipDownloadStarted'));
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
                    window.location.href = `/files/${tab.serverId}/view?path=${encodeURIComponent(path)}`;
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
                        await loadFilePath(tab, tab.filePath || '/');
                    }
                    catch (error) {
                        const message = error instanceof Error ? error.message : ui('actionFailed');
                        tab.lastError = message;
                        setStatus(tab, uiText.error || 'Error');
                        setFileMessage(tab, message);
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

                function setFileMessage(tab, message) {
                    if (!tab.fileUi) {
                        return;
                    }

                    tab.fileUi.message.textContent = message;
                    tab.fileUi.message.classList.toggle('hidden', !message);
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

                disconnectActiveButton.addEventListener('click', () => {
                    if (activeTabId) {
                        closeTab(activeTabId);
                    }
                });

                clipboardSendButton.addEventListener('click', async () => {
                    const activeTab = tabs.get(activeTabId);
                    if (!activeTab) {
                        return;
                    }

                    try {
                        const text = await readBrowserClipboard();
                        if (!sendClipboardText(activeTab, text)) {
                            openClipboardDialog(activeTab.remoteClipboard || '');
                        }
                    }
                    catch {
                        openClipboardDialog(activeTab.remoteClipboard || '');
                    }
                });

                fullscreenButton.addEventListener('click', () => {
                    const activeTab = tabs.get(activeTabId);
                    const target = activeTab ? activeTab.panel : deck;
                    if (!document.fullscreenElement) {
                        target.requestFullscreen().then(scheduleResize).catch(() => {});
                    }
                    else {
                        document.exitFullscreen().then(scheduleResize).catch(() => {});
                    }
                });

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
                measureGatewayLatency();
                window.setInterval(measureGatewayLatency, 5000);

                const restoredWorkspace = loadWorkspaceTabs();
                const tabStatesToOpen = [...restoredWorkspace.tabs];
                if (initialOpenServerId && !tabStatesToOpen.some(tab => tab.serverId === initialOpenServerId)) {
                    tabStatesToOpen.push({ serverId: initialOpenServerId, filePath: '' });
                }

                const openedTabs = [];
                for (const tabState of tabStatesToOpen) {
                    const tab = openServer(tabState.serverId, tabState.filePath || '');
                    if (tab) {
                        openedTabs.push(tab);
                    }
                }

                const preferredServerId = initialOpenServerId || restoredWorkspace.activeServerId;
                const preferredTab = openedTabs.find(tab => tab.serverId === preferredServerId) || openedTabs[0];
                if (preferredTab) {
                    activateTab(preferredTab.id);
                }
                else {
                    activateNewConnectionTab();
                }

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
                        <span class="badge">{{E(server.Protocol.ToString().ToUpperInvariant())}}</span>
                        <strong>{{E(server.Name)}}</strong>
                        <span class="muted">{{E(server.Host)}}:{{server.Port}}</span>
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
        var aboutVersion = ApplicationVersion();
        var aboutModalClose = $$"""
            <button type="button" class="button" onclick="return window.MatgateCloseAboutModal(event, this)">{{Icon("x")}}{{T(context, "Close")}}</button>
            """;
        var adminMenu = user is not null && (user.IsAdmin || user.CanManageServers)
            ? $$"""
                <details class="nav-menu admin-menu">
                    <summary class="menu-trigger">{{Icon(user.IsAdmin ? "shield" : "server")}}<span>{{T(context, user.IsAdmin ? "Administration" : "Servers")}}</span><span class="menu-caret">{{Icon("chevron-down")}}</span></summary>
                    <div class="menu-panel">
                        <a href="/admin/servers">{{Icon("server")}}{{T(context, "Servers")}}</a>
                        {{(user.IsAdmin ? $"""<a href="/admin/users">{Icon("users")}{T(context, "Users")}</a>""" : "")}}
                    </div>
                </details>
                """
            : "";
        var accountMenu = user is null ? "" : $$"""
            <details class="nav-menu account-menu">
                <summary class="menu-trigger account-trigger">
                    {{Icon("user")}}<span class="account-name">{{E(string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName)}}</span><span class="menu-caret">{{Icon("chevron-down")}}</span>
                </summary>
                <div class="menu-panel">
                    <a href="/account">{{Icon("user")}}{{T(context, "Account")}}</a>
                    <form method="post" action="/logout" class="inline">
                        {{Csrf(context)}}
                        <button type="submit">{{Icon("logout")}}{{T(context, "Logout")}}</button>
                    </form>
                </div>
            </details>
            """;
        var navigation = user is null ? "" : $$"""
            <nav>
                <a href="/">{{Icon("home")}}{{T(context, "Home")}}</a>
                {{adminMenu}}
                {{accountMenu}}
            </nav>
            """;
        return $$"""
            <!doctype html>
            <html lang="{{language}}">
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
                        --bg: #f4f6f4;
                        --panel: #ffffff;
                        --text: #1f2725;
                        --muted: #67706c;
                        --line: #dce2de;
                        --accent: #176b5b;
                        --accent-2: #2b5876;
                        --danger: #a63a3a;
                    }
                    * { box-sizing: border-box; }
                    body {
                        margin: 0;
                        background: var(--bg);
                        color: var(--text);
                        font-family: Segoe UI, system-ui, -apple-system, sans-serif;
                        line-height: 1.5;
                    }
                    header {
                        background: #ffffff;
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        align-items: center;
                        justify-content: space-between;
                        gap: 24px;
                        padding: 14px clamp(16px, 4vw, 44px);
                        position: sticky;
                        top: 0;
                        z-index: 5;
                    }
                    .brand {
                        align-items: center;
                        color: var(--text);
                        display: inline-flex;
                        font-size: 20px;
                        font-weight: 800;
                        gap: 10px;
                        letter-spacing: 0;
                        text-decoration: none;
                    }
                    .brand-mark {
                        align-items: center;
                        background: linear-gradient(135deg, #176b5b, #2b5876);
                        border-radius: 8px;
                        box-shadow: inset 0 0 0 1px rgb(255 255 255 / 28%);
                        color: #ffffff;
                        display: inline-flex;
                        height: 34px;
                        justify-content: center;
                        overflow: hidden;
                        position: relative;
                        width: 34px;
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
                    .icon {
                        fill: none;
                        flex: 0 0 auto;
                        height: 18px;
                        stroke: currentColor;
                        stroke-linecap: round;
                        stroke-linejoin: round;
                        stroke-width: 2;
                        width: 18px;
                    }
                    nav { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
                    nav a, .button, button {
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        background: #ffffff;
                        color: var(--text);
                        cursor: pointer;
                        display: inline-flex;
                        align-items: center;
                        min-height: 38px;
                        padding: 8px 12px;
                        text-decoration: none;
                        font: inherit;
                        gap: 7px;
                    }
                    .nav-menu {
                        position: relative;
                    }
                    .nav-menu > summary {
                        align-items: center;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        background: #ffffff;
                        color: var(--text);
                        cursor: pointer;
                        display: inline-flex;
                        gap: 7px;
                        min-height: 38px;
                        padding: 8px 12px;
                        text-decoration: none;
                        font: inherit;
                        list-style: none;
                    }
                    .nav-menu > summary::-webkit-details-marker { display: none; }
                    .nav-menu[open] > summary,
                    .nav-menu > summary:hover {
                        background: #f5f8f6;
                    }
                    .menu-caret {
                        display: inline-flex;
                    }
                    .menu-caret .icon {
                        height: 14px;
                        width: 14px;
                    }
                    .menu-panel {
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        box-shadow: 0 16px 32px rgb(31 39 37 / 12%);
                        display: grid;
                        gap: 4px;
                        min-width: 220px;
                        padding: 6px;
                        position: absolute;
                        right: 0;
                        top: calc(100% + 8px);
                        z-index: 12;
                    }
                    .menu-panel a,
                    .menu-panel button {
                        justify-content: flex-start;
                        min-width: 0;
                        width: 100%;
                    }
                    .account-trigger {
                        max-width: 260px;
                    }
                    .account-name {
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                        max-width: 160px;
                    }
                    button:disabled { cursor: not-allowed; opacity: .55; }
                    .primary { background: var(--accent); border-color: var(--accent); color: #ffffff; }
                    .danger { background: var(--danger); border-color: var(--danger); color: #ffffff; }
                    main { width: min(1180px, calc(100% - 32px)); margin: 28px auto 56px; }
                    main.session-main { width: 100%; height: calc(100vh - 67px); margin: 0; }
                    h1, h2 { line-height: 1.15; margin: 0; }
                    h1 { font-size: clamp(30px, 4vw, 52px); }
                    h2 { font-size: 20px; margin-bottom: 18px; }
                    .eyebrow { color: var(--accent-2); font-weight: 700; margin: 0 0 6px; text-transform: uppercase; }
                    .muted { color: var(--muted); }
                    .target { font-family: Consolas, ui-monospace, monospace; }
                    .page-head { align-items: center; display: flex; justify-content: space-between; gap: 18px; margin-bottom: 22px; }
                    .panel, .card, .auth-panel {
                        background: var(--panel);
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        padding: 22px;
                        box-shadow: 0 12px 32px rgb(31 39 37 / 6%);
                    }
                    .panel + .panel { margin-top: 18px; }
                    .grid { display: grid; gap: 16px; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); }
                    .row { display: flex; align-items: center; gap: 14px; }
                    .split { justify-content: space-between; }
                    .stack { display: grid; gap: 14px; }
                    .form-grid { display: grid; gap: 14px; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); align-items: end; }
                    label { display: grid; gap: 6px; font-weight: 600; }
                    .check { align-items: center; display: flex; gap: 8px; min-height: 42px; }
                    input, select, textarea {
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        font: inherit;
                        min-height: 42px;
                        padding: 9px 10px;
                        width: 100%;
                    }
                    textarea { min-height: 84px; resize: vertical; }
                    .actions { align-items: end; display: flex; gap: 10px; }
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
                    .notice { border-radius: 8px; padding: 10px 12px; }
                    .error { background: #fff1f1; border: 1px solid #f0caca; color: #7d2424; }
                    .badge { background: #e8f1ee; border-radius: 999px; color: var(--accent); display: inline-block; font-size: 12px; font-weight: 800; padding: 3px 8px; }
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
                        background: #e8f1ee;
                        border: 1px solid #d1e1dc;
                        border-radius: 8px;
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
                    .empty { border: 1px dashed var(--line); border-radius: 8px; color: var(--muted); padding: 22px; }
                    .table-wrap { overflow-x: auto; }
                    table { border-collapse: collapse; width: 100%; }
                    th, td { border-bottom: 1px solid var(--line); padding: 12px 8px; text-align: left; vertical-align: top; }
                    .danger-zone { border-color: #efc7c7; }
                    .matgate-shell {
                        background: var(--bg);
                        height: 100%;
                        min-height: calc(100vh - 67px);
                        overflow: hidden;
                        position: relative;
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
                        background: #ffffff;
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        justify-content: space-between;
                        gap: 14px;
                        min-height: 54px;
                        padding: 8px clamp(12px, 3vw, 24px);
                    }
                    .session-bar > div:first-child { align-items: center; display: flex; flex-wrap: wrap; gap: 10px; min-width: 0; }
                    .session-actions { align-items: center; display: flex; gap: 8px; flex-wrap: wrap; }
                    .session-tab-row {
                        align-items: stretch;
                        background: #eef2ef;
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        min-height: 44px;
                    }
                    .session-tabs {
                        align-items: stretch;
                        background: transparent;
                        display: flex;
                        flex: 0 1 auto;
                        gap: 1px;
                        min-height: 44px;
                        min-width: 0;
                        overflow-x: auto;
                    }
                    .tab-actions {
                        align-items: center;
                        background: #ffffff;
                        border-left: 1px solid var(--line);
                        display: flex;
                        flex: 0 0 auto;
                        gap: 8px;
                        justify-content: flex-end;
                        margin-left: auto;
                        padding: 4px clamp(8px, 2vw, 14px);
                    }
                    .tab-actions button {
                        min-height: 34px;
                        padding: 6px 10px;
                        white-space: nowrap;
                    }
                    .session-tab {
                        align-items: stretch;
                        background: #dfe7e3;
                        border-right: 1px solid var(--line);
                        display: flex;
                        flex: 0 0 auto;
                        max-width: 280px;
                    }
                    .session-tab.active { background: #ffffff; }
                    .session-new-tab {
                        align-items: center;
                        border: 0;
                        border-radius: 0;
                        border-right: 1px solid var(--line);
                        color: var(--accent);
                        font-size: 24px;
                        font-weight: 800;
                        justify-content: center;
                        max-width: none;
                        min-height: 44px;
                        min-width: 52px;
                        padding: 0 14px;
                    }
                    .session-tab-main {
                        background: transparent;
                        border: 0;
                        border-radius: 0;
                        display: grid;
                        gap: 0;
                        justify-items: start;
                        min-height: 44px;
                        min-width: 160px;
                        padding: 5px 10px;
                    }
                    .session-tab-title {
                        align-items: center;
                        display: flex;
                        gap: 7px;
                        max-width: 190px;
                        min-width: 0;
                    }
                    .session-tab-title .icon {
                        height: 15px;
                        width: 15px;
                    }
                    .session-tab-title span {
                        max-width: 190px;
                        overflow: hidden;
                        text-overflow: ellipsis;
                        white-space: nowrap;
                    }
                    .session-tab-main small { color: var(--muted); font-size: 12px; }
                    .session-tab-close {
                        background: transparent;
                        border: 0;
                        border-radius: 0;
                        min-height: 44px;
                        padding: 0 10px;
                    }
                    .session-deck {
                        background: #111614;
                        flex: 1;
                        min-height: 0;
                        position: relative;
                    }
                    .session-statusbar {
                        align-items: center;
                        background: #ffffff;
                        border-top: 1px solid var(--line);
                        color: var(--text);
                        display: flex;
                        gap: 16px;
                        justify-content: space-between;
                        min-height: 34px;
                        padding: 5px clamp(12px, 3vw, 24px);
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
                        border-radius: 999px;
                        color: var(--muted);
                        display: inline-flex;
                        flex: 0 0 auto;
                        height: 28px;
                        justify-content: center;
                        padding: 0;
                        text-decoration: none;
                        transition: background-color .15s ease, border-color .15s ease, color .15s ease;
                        width: 28px;
                    }
                    .status-info-button:hover {
                        background: #eef4f1;
                        border-color: #cfd8d3;
                        color: var(--accent);
                    }
                    .status-primary span, .status-metrics span {
                        color: var(--muted);
                        font-size: 13px;
                    }
                    #status-state {
                        background: #e8f1ee;
                        border-radius: 999px;
                        color: var(--accent);
                        font-weight: 700;
                        padding: 2px 8px;
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
                        background: #f8faf9;
                        height: 100%;
                        inset: 0;
                        overflow: auto;
                        padding: clamp(18px, 4vw, 40px);
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
                    .connection-choice {
                        align-items: stretch;
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        display: grid;
                        gap: 16px;
                        min-height: 170px;
                        padding: 18px;
                    }
                    .connection-choice h2 {
                        margin: 8px 0 8px;
                    }
                    .connection-choice .target {
                        margin: 0;
                    }
                    .connection-choice .muted {
                        margin: 8px 0 0;
                    }
                    .connection-choice button {
                        align-self: end;
                        justify-content: center;
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
                    .file-display {
                        background: #f8faf9;
                        color: var(--text);
                        height: 100%;
                        overflow: hidden;
                        width: 100%;
                    }
                    .file-manager {
                        display: flex;
                        flex-direction: column;
                        gap: 10px;
                        height: 100%;
                        padding: 12px;
                    }
                    .file-toolbar {
                        align-items: center;
                        display: flex;
                        flex-wrap: wrap;
                        gap: 8px;
                    }
                    .file-toolbar-group {
                        align-items: center;
                        display: flex;
                        flex-wrap: wrap;
                        gap: 8px;
                    }
                    .file-toolbar-main {
                        flex: 1 1 560px;
                    }
                    .file-toolbar-transfer {
                        margin-left: auto;
                        justify-content: flex-end;
                    }
                    .file-toolbar button,
                    .file-menu > summary,
                    .file-upload-button,
                    .file-toggle-button {
                        gap: 7px;
                        min-height: 36px;
                        padding: 6px 10px;
                    }
                    .file-path-input {
                        flex: 1 1 220px;
                        font-family: Consolas, ui-monospace, monospace;
                        min-height: 36px;
                    }
                    .file-upload-button {
                        align-items: center;
                        background: var(--accent);
                        border: 1px solid var(--accent);
                        border-radius: 8px;
                        color: #ffffff;
                        cursor: pointer;
                        display: inline-flex;
                        font-weight: 600;
                        gap: 7px;
                        justify-content: center;
                    }
                    .file-toggle-button {
                        align-items: center;
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        color: var(--text);
                        cursor: pointer;
                        display: inline-flex;
                        font-weight: 600;
                        justify-content: center;
                    }
                    .file-menu {
                        position: relative;
                    }
                    .file-menu > summary {
                        align-items: center;
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
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
                        background: #f5f8f6;
                    }
                    .file-menu-panel {
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        box-shadow: 0 16px 32px rgb(31 39 37 / 12%);
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
                    .file-tool-button.is-active,
                    .file-toggle-button.is-active {
                        background: #eef7f1;
                        border-color: var(--accent);
                        color: var(--accent-2);
                    }
                    .file-upload-input { display: none; }
                    .file-message {
                        background: #fff6df;
                        border: 1px solid #ead6a1;
                        border-radius: 8px;
                        color: #6b5518;
                        padding: 8px 10px;
                    }
                    .file-table-wrap {
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        flex: 1;
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
                        background: #f2f6f4;
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
                        min-height: calc(100vh - 67px);
                        padding: 0;
                    }
                    .file-viewer-page {
                        display: flex;
                        flex-direction: column;
                        min-height: calc(100vh - 67px);
                        padding: 0;
                    }
                    .viewer-tab-row {
                        align-items: stretch;
                        background: #eef2ef;
                        border-bottom: 1px solid var(--line);
                        display: flex;
                        min-height: 44px;
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
                        border-radius: 8px;
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
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
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
                        background: #ffffff;
                        padding: 0;
                    }
                    .document-stage iframe {
                        border: 0;
                        height: calc(100vh - 190px);
                        width: 100%;
                    }
                    .empty-viewer {
                        background: #ffffff;
                        color: var(--text);
                        flex-direction: column;
                    }
                    .about-stage {
                        align-items: center;
                        background: #f3f6f4;
                        justify-content: center;
                    }
                    .about-window {
                        align-items: flex-start;
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        box-shadow: 0 18px 60px rgb(31 39 37 / 10%);
                        display: flex;
                        flex-direction: column;
                        gap: 16px;
                        max-width: 760px;
                        padding: clamp(20px, 4vw, 32px);
                        width: min(760px, 100%);
                    }
                    .about-brand {
                        align-items: center;
                        display: inline-flex;
                    }
                    .about-copy {
                        display: flex;
                        flex-direction: column;
                        gap: 8px;
                        min-width: 0;
                    }
                    .about-copy .version-number {
                        margin: 0;
                    }
                    .embedded-viewer {
                        background: var(--panel);
                        border: 1px solid var(--line);
                        border-radius: 12px;
                        box-shadow: 0 24px 90px rgb(0 0 0 / 32%);
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
                    .file-viewer-dialog {
                        background: transparent;
                        border: 0;
                        max-width: none;
                        margin: auto;
                        overflow: visible;
                        padding: 0;
                        width: min(1160px, calc(100vw - 32px));
                    }
                    .file-viewer-dialog::backdrop,
                    .about-dialog::backdrop {
                        background: rgb(10 14 12 / 72%);
                        backdrop-filter: blur(2px);
                    }
                    .about-dialog {
                        height: min(88vh, 920px);
                    }
                    .about-dialog .about-viewer {
                        height: 100%;
                        min-height: 0;
                    }
                    .about-dialog .viewer-body {
                        min-height: 0;
                    }
                    .about-dialog .about-stage {
                        min-height: 0;
                    }
                    .about-dialog .about-window {
                        max-width: none;
                        width: min(760px, 100%);
                    }
                    .file-viewer-dialog-content {
                        width: 100%;
                    }
                    .file-viewer-dialog-loading {
                        align-items: center;
                        background: var(--panel);
                        border: 1px solid var(--line);
                        border-radius: 12px;
                        color: var(--muted);
                        display: flex;
                        justify-content: center;
                        min-height: 240px;
                        padding: 28px;
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
                        background: #ffffff;
                        border: 1px solid var(--line);
                        border-radius: 8px;
                        box-shadow: 0 18px 60px rgb(0 0 0 / 25%);
                        left: 50%;
                        padding: 22px;
                        position: absolute;
                        top: 50%;
                        transform: translate(-50%, -50%);
                        width: min(420px, calc(100% - 32px));
                        z-index: 4;
                    }
                    @media (max-width: 720px) {
                        header, .page-head, .auth-panel { align-items: stretch; flex-direction: column; grid-template-columns: 1fr; }
                        nav { align-items: stretch; }
                        nav a, nav button, .button, .nav-menu > summary { justify-content: center; width: 100%; }
                        .nav-menu { width: 100%; }
                        .menu-panel {
                            position: static;
                            min-width: 0;
                            width: 100%;
                        }
                        .account-name {
                            max-width: none;
                        }
                        main.session-main { height: auto; min-height: calc(100vh - 67px); }
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
                        .file-upload-button, .file-toggle-button { width: 100%; }
                        .file-menu { width: 100%; }
                        .file-menu > summary { justify-content: center; width: 100%; }
                        .file-menu-panel { position: static; width: 100%; }
                        .file-row-actions { flex-wrap: wrap; }
                        .viewer-tab-row { align-items: stretch; flex-direction: column; }
                        .viewer-actions { flex-wrap: wrap; }
                        .viewer-actions > * { flex: 1; justify-content: center; }
                        .viewer-body { padding: 12px; }
                        .viewer-stage { min-height: 260px; }
                        .embedded-viewer { height: calc(100vh - 16px); width: calc(100vw - 16px); }
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
                            document.querySelectorAll('details.nav-menu[open], details.file-menu[open]').forEach((menu) => {
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

                            const keepMenu = target.closest('details.nav-menu, details.file-menu');
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

                        window.MatgateCloseAboutWindow = (event, source) => {
                            const element = source instanceof Element
                                ? source
                                : (event && event.currentTarget instanceof Element ? event.currentTarget : null);
                            try {
                                if (window.parent && window.parent !== window && typeof window.parent.MatgateCloseAboutModal === 'function') {
                                    window.parent.MatgateCloseAboutModal();
                                    return false;
                                }
                            }
                            catch {
                                // Fall back to local handling below.
                            }

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
                    })();
                </script>
            </body>
            </html>
            """;
    }

    private static string ServerFields(HttpContext context, MatgateUser currentUser, ServerEndpoint? server = null)
    {
        var selectedRdp = Selected(server?.Protocol is null or ServerProtocol.Rdp);
        var selectedSsh = Selected(server?.Protocol == ServerProtocol.Ssh);
        var selectedBrowser = Selected(server?.Protocol == ServerProtocol.Browser);
        var selectedSftp = Selected(server?.Protocol == ServerProtocol.Sftp);
        var selectedFtp = Selected(server?.Protocol == ServerProtocol.Ftp);
        var selectedSmb = Selected(server?.Protocol == ServerProtocol.Smb);
        var iconKey = ServerEndpoint.NormalizeIconKey(server?.IconKey);
        var port = server?.Port.ToString() ?? "";
        var keyboardLayout = string.IsNullOrWhiteSpace(server?.KeyboardLayout)
            ? ServerEndpoint.DefaultKeyboardLayout
            : server.KeyboardLayout;
        var terminalFontSize = ServerEndpoint.NormalizeTerminalFontSize(
            server?.TerminalFontSize ?? ServerEndpoint.DefaultTerminalFontSize);
        var passwordHelp = server is null ? "" : $"""<p class="muted">{T(context, "Leave password empty to keep it unchanged.")}</p>""";
        var clearPassword = server is null ? "" : $"""<label class="check"><input type="checkbox" name="clearPassword"> {T(context, "Clear saved target password")}</label>""";
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
                            <option value="Ssh"{{selectedSsh}}>SSH</option>
                            <option value="Browser"{{selectedBrowser}}>Browser</option>
                            <option value="Sftp"{{selectedSftp}}>SFTP</option>
                            <option value="Ftp"{{selectedFtp}}>FTP</option>
                            <option value="Smb"{{selectedSmb}}>SMB</option>
                        </select>
                    </label>
                    <label>{{T(context, "Server icon")}}
                        <select name="iconKey">
                            <option value=""{{Selected(string.IsNullOrWhiteSpace(iconKey))}}>{{T(context, "Default by connection type")}}</option>
                            {{ServerIconOptions(iconKey)}}
                        </select>
                    </label>
                    <label>{{T(context, "Host or IP")}}
                        <input name="host" value="{{A(server?.Host)}}" placeholder="PC-Terminal / browser" required>
                    </label>
                    <label>Port
                        <input name="port" type="number" min="1" max="65535" value="{{A(port)}}" placeholder="3389 / 22 / 5900 / 21 / 445">
                    </label>
                </div>
            </section>
            <section class="panel server-form-section">
                <h2>{{T(context, "Credentials")}}</h2>
                <div class="form-grid">
                    <label>{{T(context, "Target user")}}
                        <input name="targetUserName" value="{{A(server?.UserName)}}" autocomplete="off">
                    </label>
                    <label>{{T(context, "Connection password")}}
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

        return labels.Count == 0 ? $"""<span class="muted">{T(context, "User")}</span>""" : string.Join(" ", labels);
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

    private static string ServerIconOptions(string selectedIconKey)
    {
        return string.Join("", ServerEndpoint.IconKeys.Select(iconKey =>
        {
            var label = iconKey switch
            {
                "rdp" => "RDP / Desktop",
                "ssh" => "SSH / Terminal",
                "browser" => "Browser",
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

    private static string NormalizeLanguageCode(string? value)
    {
        return string.Equals((value ?? "").Trim(), "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
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
            "user" => """<circle cx="12" cy="8" r="4"/><path d="M4 21v-1a8 8 0 0 1 16 0v1"/>""",
            "chevron-down" => """<path d="m6 9 6 6 6-6"/>""",
            "info" => """<circle cx="12" cy="12" r="9"/><path d="M12 17v-6"/><path d="M12 8h.01"/>""",
            "rdp" => """<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/><path d="M8 8h3v3H8z"/><path d="M13 8h3v3h-3z"/><path d="M8 13h3v1H8z"/><path d="M13 13h3v1h-3z"/>""",
            "ssh" => """<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3"/><path d="M12 15h5"/>""",
            "browser" => """<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18"/><path d="M8 7h.01"/><path d="M11 7h.01"/><circle cx="12" cy="14.5" r="3.5"/><path d="M8.5 14.5h7"/><path d="M12 11a6 6 0 0 1 0 7"/><path d="M12 11a6 6 0 0 0 0 7"/>""",
            "sftp" => """<path d="M3 8a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><rect x="9" y="12" width="6" height="5" rx="1"/><path d="M10.5 12v-1.5a1.5 1.5 0 0 1 3 0V12"/>""",
            "ftp" => """<path d="M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/><path d="M7 9l5-5 5 5"/><path d="M12 4v12"/><path d="m8 13 4 4 4-4"/>""",
            "smb" => """<rect x="4" y="4" width="16" height="6" rx="2"/><rect x="4" y="14" width="16" height="6" rx="2"/><path d="M8 7h.01"/><path d="M8 17h.01"/><path d="M12 10v4"/><path d="M9 12h6"/>""",
            "desktop" => """<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8"/><path d="M12 17v4"/>""",
            "terminal" => """<rect x="3" y="4" width="18" height="16" rx="2"/><path d="m7 9 3 3-3 3"/><path d="M13 15h4"/>""",
            "database" => """<ellipse cx="12" cy="5" rx="7" ry="3"/><path d="M5 5v14c0 1.7 3.1 3 7 3s7-1.3 7-3V5"/><path d="M5 12c0 1.7 3.1 3 7 3s7-1.3 7-3"/>""",
            "cloud" => """<path d="M17.5 19H8a5 5 0 1 1 1.5-9.8 6 6 0 0 1 11 3.8 3.5 3.5 0 0 1-3 6z"/>""",
            "shield" => """<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="m9 12 2 2 4-4"/>""",
            "users" => """<path d="M16 21v-2a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v2"/><circle cx="9.5" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>""",
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

    private static string E(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");

    private static string A(object? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");
}
