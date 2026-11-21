using Matgate.Models;

namespace Matgate.Web;

// Stored credentials: a list (search above, the table, "New" below) and a page each to make or change one. The
// password is never shown - a field left empty keeps it. And the choice in a connection's form.
public sealed partial class HtmlViews
{
    // The credentials a connection form may offer, put there by the endpoint that shows the form.
    public const string CredentialChoicesKey = "matgate.credentials";

    private static string CredentialChoice(HttpContext context, ServerEndpoint? server)
    {
        var de = Language(context) == "de";
        var choices = context.Items[CredentialChoicesKey] as IReadOnlyList<Credential> ?? [];
        var chosen = server?.CredentialId;
        var options = string.Join("", choices
            .OrderBy(credential => credential.IsShared)
            .ThenBy(credential => credential.Name, StringComparer.OrdinalIgnoreCase)
            .Select(credential => $"""<option value="{credential.Id}"{(credential.Id == chosen ? " selected" : "")}>{E(credential.Name)}{E(string.IsNullOrWhiteSpace(credential.UserName) ? "" : " (" + credential.UserName + ")")}{(credential.IsShared ? E(de ? " - geteilt" : " - shared") : "")}</option>"""));
        return $$"""
            <label class="conn-wide" data-protocols="rdp,vnc,ssh,sftp,ftp,smb,webdav,website,mssql,mysql,postgres">{{(de ? "Zugangsdaten" : "Credentials")}}
                <span class="credential-choice">
                    <select name="credentialId" data-credential-select>
                        <option value="">{{(de ? "Eigene Angaben (hier darunter)" : "Own login (below)")}}</option>
                        {{options}}
                    </select>
                    <a class="button" href="/credentials" data-shell-open-tab="1" data-shell-title="{{A(de ? "Zugangsdaten" : "Credentials")}}">{{Icon("key")}}<span>{{(de ? "Verwalten" : "Manage")}}</span></a>
                </span>
            </label>
            <script>
                (() => {
                    // With stored credentials chosen, the connection's own login fields are not asked for.
                    const select = document.currentScript.previousElementSibling.querySelector('[data-credential-select]');
                    const form = select.closest('form');
                    const sync = () => form.querySelectorAll('[data-credential-own]').forEach(field => field.classList.toggle('credential-hidden', !!select.value));
                    select.addEventListener('change', sync);
                    sync();
                })();
            </script>
            <style>
                .credential-hidden { display: none !important; }
                .credential-choice { align-items: center; display: flex; gap: 8px; }
                .credential-choice select { flex: 1 1 auto; min-width: 0; }
                .credential-choice .button { align-items: center; display: inline-flex; flex: 0 0 auto; gap: 6px; }
            </style>
            """;
    }

    // An SSH private key, pasted or read from a file - for credentials and for SSH/SFTP connections. Like the
    // password it is never shown again: empty keeps the stored one, a box removes it.
    internal static string PrivateKeyField(bool de, bool hasKey, string attributes = "")
    {
        var placeholder = hasKey
            ? (de ? "Ein Schlüssel ist gespeichert - leer lassen: bleibt wie er ist" : "A key is stored - leave empty: stays as it is")
            : "-----BEGIN OPENSSH PRIVATE KEY-----\n...";
        var remove = hasKey
            ? $"""<label class="checkbox-row"><input type="checkbox" name="clearPrivateKey" value="true"> {(de ? "Gespeicherten Schlüssel entfernen" : "Remove the stored key")}</label>"""
            : "";
        return $$"""
            <label class="conn-wide private-key-field"{{attributes}}>{{(de ? "SSH-Schlüssel" : "SSH key")}} <small class="muted">({{(de ? "optional - das Passwort ist dann seine Passphrase" : "optional - the password is then its passphrase")}})</small>
                <textarea name="privateKey" rows="4" spellcheck="false" autocomplete="off" autocapitalize="off" placeholder="{{A(placeholder)}}"></textarea>
                <span class="private-key-actions">
                    <button type="button" class="button" data-private-key-file>{{Icon("upload")}}<span>{{(de ? "Aus Datei" : "From file")}}</span></button>
                    {{remove}}
                </span>
                <input type="file" hidden data-private-key-input>
            </label>
            <script>
                (() => {
                    // A key file (id_ed25519, .pem, .ppk) is read here and put into the box - it goes with the form.
                    const field = document.currentScript.previousElementSibling;
                    const input = field.querySelector('[data-private-key-input]');
                    field.querySelector('[data-private-key-file]').addEventListener('click', () => input.click());
                    input.addEventListener('change', async () => {
                        const file = input.files && input.files[0];
                        if (file && file.size < 64 * 1024) { field.querySelector('textarea').value = await file.text(); }
                        input.value = '';
                    });
                })();
            </script>
            <style>
                .private-key-field textarea { font-family: ui-monospace, Consolas, monospace; font-size: 12px; resize: vertical; width: 100%; }
                .private-key-actions { align-items: center; display: flex; flex-wrap: wrap; gap: 12px; margin-top: 6px; }
                .private-key-actions .button { align-items: center; display: inline-flex; gap: 6px; }
            </style>
            """;
    }

    public string CredentialList(HttpContext context, MatgateUser user, IReadOnlyList<Credential> credentials, IReadOnlyDictionary<Guid, int> usage)
    {
        var de = Language(context) == "de";
        var canShare = user.IsAdmin || user.CanManageServers;
        var rows = string.Join("", credentials
            .OrderBy(credential => credential.IsShared)
            .ThenBy(credential => credential.Name, StringComparer.OrdinalIgnoreCase)
            .Select(credential =>
            {
                var used = usage.TryGetValue(credential.Id, out var count) ? count : 0;
                var editable = credential.EditableBy(user);
                return $$"""
                    <tr data-credential-row data-search="{{A((credential.Name + " " + credential.UserName + " " + credential.Domain).ToLowerInvariant())}}">
                        <td><strong>{{E(credential.Name)}}</strong>{{(string.IsNullOrWhiteSpace(credential.Notes) ? "" : $"""<br><small class="muted">{E(credential.Notes)}</small>""")}}</td>
                        <td>{{E(string.IsNullOrWhiteSpace(credential.Domain) ? credential.UserName : credential.Domain + "\\" + credential.UserName)}}</td>
                        <td><span class="badge">{{(credential.IsShared ? (de ? "Geteilt" : "Shared") : (de ? "Persönlich" : "Personal"))}}</span></td>
                        <td>{{used}}</td>
                        <td class="table-actions">{{(editable ? $"""<a class="button toolbar-button toolbar-icon-button" href="/credentials/{credential.Id}" title="{A(de ? "Bearbeiten" : "Edit")}" aria-label="{A(de ? "Bearbeiten" : "Edit")}">{Icon("edit")}</a>""" : "")}}</td>
                    </tr>
                    """;
            }));
        var empty = credentials.Count == 0
            ? $"""<tr><td colspan="5" class="muted">{E(de ? "Noch keine Zugangsdaten. Mit „Neu“ anlegen und dann in den Verbindungen auswählen." : "No credentials yet. Make some with \"New\", then choose them in the connections.")}</td></tr>"""
            : "";
        var body = $$"""
            <div class="conn-page">
                <section class="page-head conn-head">
                    <span class="conn-head-icon" aria-hidden="true">{{Icon("key")}}</span>
                    <div>
                        <p class="eyebrow">{{(de ? "Verbindungen" : "Connections")}}</p>
                        <h1>{{(de ? "Zugangsdaten" : "Credentials")}}</h1>
                        <p class="muted">{{(de
                            ? "Einmal gespeichert, von beliebig vielen Verbindungen benutzt: ein neues Passwort hier gilt für alle." + (canShare ? " Geteilte stehen allen zur Verfügung." : "")
                            : "Stored once, used by as many connections as you like: a new password here is the new one for all of them." + (canShare ? " Shared ones are there for everyone." : ""))}}</p>
                    </div>
                </section>
                <section class="panel">
                    <div class="credential-toolbar">
                        <label class="credential-search">{{Icon("search")}}<input type="search" data-credential-filter placeholder="{{A(de ? "Suchen ..." : "Search ...")}}" autocomplete="off"></label>
                    </div>
                    <div class="table-wrap">
                        <table>
                            <thead><tr><th>{{T(context, "Name")}}</th><th>{{(de ? "Benutzer" : "User")}}</th><th>{{(de ? "Art" : "Kind")}}</th><th>{{(de ? "Verbindungen" : "Connections")}}</th><th class="table-actions">{{T(context, "Actions")}}</th></tr></thead>
                            <tbody>{{rows}}{{empty}}</tbody>
                        </table>
                    </div>
                    <div class="form-actions">
                        <a class="button primary" href="/credentials/new">{{Icon("plus")}}{{(de ? "Neu" : "New")}}</a>
                    </div>
                </section>
            </div>
            <style>
                .credential-toolbar { display: flex; gap: 8px; margin-bottom: 10px; }
                .credential-search { align-items: center; display: flex; flex: 1 1 auto; max-width: 360px; position: relative; }
                .credential-search > .icon { color: var(--muted); height: 15px; left: 10px; pointer-events: none; position: absolute; width: 15px; }
                .credential-search input:not([type="checkbox"]):not([type="radio"]) { padding-left: 32px; width: 100%; }
            </style>
            <script>
                (() => {
                    const field = document.querySelector('[data-credential-filter]');
                    field.addEventListener('input', () => {
                        const wanted = field.value.trim().toLowerCase();
                        document.querySelectorAll('[data-credential-row]').forEach(row => { row.hidden = !!wanted && !row.dataset.search.includes(wanted); });
                    });
                })();
            </script>
            """;

        return Layout(context, user, de ? "Zugangsdaten" : "Credentials", body);
    }

    public string CredentialForm(HttpContext context, MatgateUser user, Credential? credential, int usedBy, string? error = null)
    {
        var de = Language(context) == "de";
        var isNew = credential is null;
        var canShare = user.IsAdmin || user.CanManageServers;
        var shared = credential?.IsShared ?? false;
        var scope = canShare
            ? $$"""
                <label>{{(de ? "Sichtbar für" : "Visible to")}}
                    <select name="scope">
                        <option value="personal"{{(shared ? "" : " selected")}}>{{(de ? "Nur mich" : "Only me")}}</option>
                        <option value="shared"{{(shared ? " selected" : "")}}>{{(de ? "Alle (geteilt)" : "Everyone (shared)")}}</option>
                    </select>
                </label>
                """
            : "";
        var delete = isNew
            ? ""
            : $$"""
                <span class="spacer"></span>
                <form method="post" action="/credentials/{{credential!.Id}}/delete" data-confirm="{{A(de
                    ? $"Zugangsdaten „{credential.Name}“ löschen?" + (usedBy > 0 ? $" {usedBy} Verbindung(en) haben dann keine Anmeldung mehr und fragen beim Verbinden danach." : "")
                    : $"Delete the credentials \"{credential.Name}\"?" + (usedBy > 0 ? $" {usedBy} connection(s) then have no login and ask for one when connecting." : ""))}}">
                    {{Csrf(context)}}
                    <button type="submit" class="danger">{{Icon("trash")}}{{(de ? "Löschen" : "Delete")}}</button>
                </form>
                """;
        var body = $$"""
            <div class="conn-page">
                <section class="page-head conn-head">
                    <span class="conn-head-icon" aria-hidden="true">{{Icon("key")}}</span>
                    <div>
                        <p class="eyebrow">{{(de ? "Zugangsdaten" : "Credentials")}}</p>
                        <h1>{{(isNew ? (de ? "Neue Zugangsdaten" : "New credentials") : E(credential!.Name))}}</h1>
                        {{(isNew ? "" : $"""<p class="muted">{E(de ? $"Benutzt von {usedBy} Verbindung(en)" : $"Used by {usedBy} connection(s)")}</p>""")}}
                    </div>
                </section>
                {{(error is null ? "" : $"""<p class="error-box" role="alert">{E(error)}</p>""")}}
                <form id="credential-form" method="post" action="{{(isNew ? "/credentials" : $"/credentials/{credential!.Id}")}}" class="panel stack">
                    {{Csrf(context)}}
                    <div class="conn-grid">
                        <label>{{T(context, "Name")}}<input name="name" required maxlength="120" value="{{A(credential?.Name)}}" placeholder="{{A(de ? "z. B. Domänen-Admin" : "e.g. domain admin")}}"></label>
                        {{scope}}
                        <label>{{(de ? "Benutzer" : "User")}}<input name="userName" value="{{A(credential?.UserName)}}" autocomplete="off" autocapitalize="off" spellcheck="false"></label>
                        <label>{{(de ? "Passwort" : "Password")}}<input name="password" type="password" autocomplete="new-password" placeholder="{{A(isNew ? "" : (de ? "leer lassen: bleibt wie es ist" : "leave empty: stays as it is"))}}"></label>
                        <label>{{(de ? "Domäne" : "Domain")}} <small class="muted">(optional)</small><input name="domain" value="{{A(credential?.Domain)}}" autocapitalize="off" spellcheck="false"></label>
                        {{PrivateKeyField(de, !string.IsNullOrEmpty(credential?.PrivateKey))}}
                        <label class="conn-wide">{{(de ? "Notiz" : "Note")}} <small class="muted">(optional)</small><input name="notes" maxlength="300" value="{{A(credential?.Notes)}}"></label>
                    </div>
                </form>
                <section class="panel conn-actions">
                    <div class="form-actions">
                        <button type="submit" form="credential-form" class="primary">{{Icon("save")}}{{T(context, "Save")}}</button>
                        <a class="button" href="/credentials">{{Icon("arrow-left")}}{{T(context, "Back")}}</a>
                        {{delete}}
                    </div>
                </section>
            </div>
            """;

        return Layout(context, user, isNew ? (de ? "Neue Zugangsdaten" : "New credentials") : credential!.Name, body);
    }
}
