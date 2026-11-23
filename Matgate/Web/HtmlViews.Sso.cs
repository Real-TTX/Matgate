using System.Text.Json;
using Matgate.Models;

namespace Matgate.Web;

// What the pages show of the sign-in services: the accounts linked to a user (with the groups the service named
// last - the spelling a group of Matgate is linked to), and the field on a group for linking it.
public sealed partial class HtmlViews
{
    public static string SsoProviderLabel(string provider) => provider switch
    {
        Services.SsoProvider.MatIdentity => "MatIdentity",
        Services.SsoProvider.Entra => "Microsoft Entra ID",
        _ => provider,
    };

    private static string ExternalLoginsPanel(HttpContext context, MatgateUser user)
    {
        var de = Language(context) == "de";
        var settings = context.RequestServices.GetService<Services.SsoSettings>();
        if (user.ExternalLogins.Count == 0 && (settings is null || settings.Providers.Count == 0))
        {
            return "";
        }

        var rows = string.Join("", user.ExternalLogins.Select(login => $$"""
            <tr data-external-login="{{A(login.Provider)}}">
                <td><strong>{{E(SsoProviderLabel(login.Provider))}}</strong><small class="muted">{{E(login.Email)}}</small></td>
                <td class="muted">{{(login.LastSignInAt is { } last ? E(last.ToLocalTime().ToString("dd.MM.yyyy HH:mm")) : "-")}}</td>
                <td>{{(login.LastGroups.Count == 0 ? $"""<span class="muted">{E(de ? "keine" : "none")}</span>""" : string.Join(" ", login.LastGroups.Select(group => $"""<code class="sso-group">{E(group)}</code>""")))}}</td>
                <td class="table-actions">
                    <form method="post" action="/admin/users/{{user.Id}}/unlink" data-confirm="{{A(de ? "Verknüpfung lösen? Dieses Konto meldet sich danach nicht mehr als dieser Benutzer an." : "Unlink? This account then no longer signs in as this user.")}}">
                        {{Csrf(context)}}
                        <input type="hidden" name="provider" value="{{A(login.Provider)}}">
                        <input type="hidden" name="subject" value="{{A(login.Subject)}}">
                        <button type="submit" class="danger">{{Icon("x")}}{{(de ? "Lösen" : "Unlink")}}</button>
                    </form>
                </td>
            </tr>
            """));
        var noPassword = string.IsNullOrEmpty(user.PasswordHash)
            ? $"""<p class="muted">{E(de ? "Dieser Benutzer hat kein eigenes Passwort und meldet sich nur über den Dienst an. Mit einem Passwort (unten) geht beides." : "This user has no password of its own and signs in through the service only. With a password (below) both work.")}</p>"""
            : "";
        return $$"""
            <section class="panel" data-external-logins>
                <h2>{{(de ? "Anmeldung über Dienste" : "Sign-in services")}}</h2>
                <p class="muted settings-lead">{{(de
                    ? "Konten von MatIdentity oder Microsoft, mit denen sich dieser Benutzer anmeldet. „Gruppen“ ist, was der Dienst zuletzt genannt hat - genau so schreibt man es in eine Gruppe, um sie zu verknüpfen."
                    : "Accounts of MatIdentity or Microsoft this user signs in with. \"Groups\" is what the service named last - written exactly like that into a group, it links the two.")}}</p>
                {{noPassword}}
                <div class="table-wrap">
                    <table class="access-table">
                        <thead><tr><th>{{(de ? "Dienst" : "Service")}}</th><th>{{(de ? "Zuletzt" : "Last")}}</th><th>{{(de ? "Gruppen" : "Groups")}}</th><th class="table-actions"></th></tr></thead>
                        <tbody>{{(rows.Length == 0 ? $"""<tr><td colspan="4" class="muted">{E(de ? "Noch kein Konto verknüpft." : "No account linked yet.")}</td></tr>""" : rows)}}</tbody>
                    </table>
                </div>
            </section>
            <style>
                [data-external-logins] td small { display: block; }
                .sso-group { background: var(--surface-2, rgba(127,127,127,.12)); border-radius: 6px; display: inline-block; font-size: 12px; margin: 1px 2px; padding: 2px 6px; }
            </style>
            """;
    }

    // The field on a group's page: which groups of the sign-in services it stands for.
    private static string ExternalGroupsPanel(HttpContext context, UserGroup? group)
    {
        var de = Language(context) == "de";
        var settings = context.RequestServices.GetService<Services.SsoSettings>();
        var linked = group?.ExternalGroups ?? [];
        if (linked.Count == 0 && (settings is null || settings.Providers.Count == 0))
        {
            return "";
        }

        var examples = string.Join(", ", (settings?.Providers ?? []).Select(provider => provider.Key == Services.SsoProvider.Entra ? "entra:<Objekt-ID>" : "matidentity:Technik"));
        return $$"""
            <section class="panel" data-group-external>
                <h2>{{(de ? "Verknüpfte Gruppen der Anmelde-Dienste" : "Linked groups of the sign-in services")}}</h2>
                <p class="muted">{{(de
                    ? $"Eine pro Zeile, z. B. {examples}. Wer sich über den Dienst anmeldet und in einer davon ist, kommt in diese Gruppe - und wieder heraus, wenn nicht mehr. Die genaue Schreibweise steht auf der Seite eines Benutzers, der sich schon angemeldet hat."
                    : $"One per line, e.g. {examples}. Whoever signs in through the service and is in one of them is put into this group - and out again once no longer. The exact spelling is on the page of a user who signed in before.")}}</p>
                <textarea name="externalGroups" rows="3" spellcheck="false" data-group-external-field>{{E(string.Join("\n", linked))}}</textarea>
            </section>
            """;
    }

    // Administration -> Sign-in: MatIdentity and Microsoft Entra ID, each on or off, with what the service needs and
    // the address to give the service (to copy); "only through the services". Saved, it applies at once.
    private static string SsoPanel(HttpContext context)
    {
        var de = Language(context) == "de";
        var service = context.RequestServices.GetService<Services.SsoSettingsService>();
        var config = service?.Load() ?? new Services.SsoConfig();
        var origin = context.Request.Scheme + "://" + context.Request.Host.Value;
        var secretNote = de ? "Gespeichert - leer lassen, um es zu behalten" : "Stored - leave empty to keep it";
        string Field(string label, string name, string value, string hint, string type = "text", string placeholder = "", string extra = "") => $$"""
            <label class="sso-field"><span>{{E(label)}}</span><input type="{{type}}" name="{{name}}" value="{{A(value)}}" placeholder="{{A(placeholder)}}" autocomplete="off" spellcheck="false"{{extra}}>{{(hint.Length == 0 ? "" : $"""<small class="muted">{E(hint)}</small>""")}}</label>
            """;
        string Secret(string label, string name, string value) =>
            Field(label, name, "", "", "password", value.Length > 0 ? secretNote : "", " autocomplete=\"new-password\"");
        string Copy(string label, string value) => $$"""
            <div class="sso-copy"><span>{{E(label)}}</span><code>{{E(value)}}</code><button type="button" class="button" data-sso-copy="{{A(value)}}">{{Icon("copy")}}<span>{{E(de ? "Kopieren" : "Copy")}}</span></button></div>
            """;
        string Card(string key, string title, string lead, bool on, string fields, string copies) => $$"""
            <section class="sso-card{{(on ? " is-on" : "")}}" data-sso-card="{{key}}">
                <header class="sso-card-head">
                    <label class="sso-switch"><input type="checkbox" name="{{key}}Enabled"{{Checked(on)}} data-sso-enabled><span class="sso-switch-track" aria-hidden="true"></span></label>
                    <div><h3>{{E(title)}}</h3><p class="muted">{{E(lead)}}</p></div>
                    <span class="sso-state" data-sso-state>{{(on ? (de ? "An" : "On") : (de ? "Aus" : "Off"))}}</span>
                </header>
                <div class="sso-card-body">
                    {{copies}}
                    <div class="sso-fields">{{fields}}</div>
                    <div class="sso-check"><button type="button" class="button" data-sso-test="{{key}}">{{Icon("refresh")}}<span>{{E(de ? "Verbindung prüfen" : "Check the connection")}}</span></button><span class="muted" data-sso-test-result></span></div>
                </div>
            </section>
            """;

        var matIdentity = Card(
            "matIdentity",
            "MatIdentity",
            de ? "Die Anmeldung der Mat-Familie. In MatIdentity unter Apps eine App für Matgate anlegen; für Gruppen dort „Send the person's groups to the app“ ankreuzen."
               : "The sign-in of the Mat family. In MatIdentity, add an app for Matgate under Apps; for groups tick \"Send the person's groups to the app\" there.",
            config.MatIdentityEnabled,
            Field(de ? "Adresse von MatIdentity" : "MatIdentity's address", "matIdentityAuthority", config.MatIdentityAuthority, "", "url", "https://id.example.com")
            + Field("Client-ID", "matIdentityClientId", config.MatIdentityClientId, "", "text", "matgate")
            + Secret("Client-Secret", "matIdentityClientSecret", config.MatIdentityClientSecret)
            + Field(de ? "Interne Adresse (optional)" : "Internal address (optional)", "matIdentityInternalUrl", config.MatIdentityInternalUrl,
                de ? "Wie Matgate selbst MatIdentity erreicht, wenn nicht über die öffentliche Adresse - z. B. http://matidentity:2300." : "How Matgate itself reaches MatIdentity when not through the public address - e.g. http://matidentity:2300.",
                "url", "http://matidentity:2300"),
            Copy(de ? "Umleitungs-URI für MatIdentity" : "Redirect URI for MatIdentity", origin + "/signin-oidc"));
        var entra = Card(
            "entra",
            "Microsoft Entra ID",
            de ? "Anmelden mit dem Microsoft-Konto der Firma. Im Entra-Portal eine App-Registrierung anlegen, unter „Tokenkonfiguration“ einen Gruppen-Claim hinzufügen (bei vielen Gruppen: „Der Anwendung zugewiesene Gruppen“)."
               : "Sign in with the company's Microsoft account. In the Entra portal, add an app registration and, under \"Token configuration\", a groups claim (with many groups: \"Groups assigned to the application\").",
            config.EntraEnabled,
            Field(de ? "Mandanten-ID (Tenant)" : "Tenant ID", "entraTenantId", config.EntraTenantId, "", "text", "00000000-0000-0000-0000-000000000000")
            + Field(de ? "Anwendungs-ID (Client)" : "Application (client) ID", "entraClientId", config.EntraClientId, "", "text", "00000000-0000-0000-0000-000000000000")
            + Secret(de ? "Geheimer Clientschlüssel" : "Client secret", "entraClientSecret", config.EntraClientSecret),
            Copy(de ? "Umleitungs-URI (Web) für Entra" : "Redirect URI (web) for Entra", origin + "/signin-entra"));

        var fromEnvironment = service is { FromFile: false } && (config.MatIdentityEnabled || config.EntraEnabled)
            ? $"""<p class="sso-note">{E(de ? "Diese Werte kommen zurzeit aus Umgebungsvariablen. Speichern übernimmt sie hierher - danach gilt, was hier steht." : "These values come from environment variables at the moment. Saving takes them over - from then on what is here holds.")}</p>"""
            : "";
        return $$"""
            <section class="panel sso-panel" data-sso-panel>
                <p class="muted settings-lead">{{(de
                    ? "Anmelden über einen Dienst statt mit einem Passwort von Matgate. Wer neu kommt, bekommt einen Benutzer ohne Rechte - die kommen über Gruppen, die man unter „Gruppen“ mit Gruppen des Dienstes verknüpft. Ein bestehendes Konto mit derselben Adresse wird erst nach seinem Passwort verknüpft."
                    : "Sign in through a service instead of with a password of Matgate. Someone new gets a user without rights - those come from groups, linked under \"Groups\" to groups of the service. An existing account with the same address is linked only after its password.")}}</p>
                {{fromEnvironment}}
                <form method="post" action="/admin/sso" class="sso-form" data-sso-form>
                    {{Csrf(context)}}
                    {{matIdentity}}
                    {{entra}}
                    <section class="sso-card sso-card--plain">
                        <label class="check"><input type="checkbox" name="ssoOnly"{{Checked(config.SsoOnly)}}> <span><strong>{{(de ? "Nur über die Dienste anmelden" : "Sign in through the services only")}}</strong><small class="muted">{{(de ? "Das Passwortfeld verschwindet von der Anmeldeseite. Notzugang bleibt: " : "The password field leaves the sign-in page. The way in when a service is down stays: ")}}<code>/login?local=1</code></small></span></label>
                    </section>
                    <div class="actions"><button type="submit" class="primary">{{Icon("save")}}{{T(context, "Save")}}</button></div>
                </form>
            </section>
            <style>
                .sso-panel { display: grid; gap: 14px; }
                .sso-form { display: grid; gap: 14px; }
                .sso-note { background: color-mix(in srgb, var(--accent) 10%, var(--surface)); border-radius: 10px; margin: 0; padding: 10px 14px; }
                .sso-card { border: 1px solid var(--line); border-radius: var(--radius, 14px); display: grid; gap: 0; overflow: hidden; }
                .sso-card.is-on { border-color: color-mix(in srgb, var(--accent) 55%, var(--line)); }
                .sso-card--plain { padding: 14px 16px; }
                .sso-card--plain label span { display: grid; gap: 2px; }
                .sso-card-head { align-items: flex-start; display: flex; gap: 14px; padding: 14px 16px; }
                .sso-card-head h3 { font-size: 16px; margin: 0 0 2px; }
                .sso-card-head p { font-size: 13px; margin: 0; }
                .sso-card-head > div { flex: 1 1 auto; }
                .sso-state { border-radius: 999px; font-size: 12px; font-weight: 700; padding: 3px 10px; background: var(--surface-2, var(--line)); }
                .sso-card.is-on .sso-state { background: color-mix(in srgb, var(--accent) 18%, var(--surface)); color: var(--accent); }
                .sso-card-body { border-top: 1px solid var(--line); display: grid; gap: 12px; padding: 14px 16px; }
                .sso-card:not(.is-on) .sso-card-body { display: none; }
                .sso-fields { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); }
                .sso-field { display: grid; gap: 4px; }
                .sso-field > span { font-size: 13px; font-weight: 600; }
                .sso-field small { font-size: 12px; }
                .sso-copy { align-items: center; background: var(--surface-2, var(--surface)); border: 1px dashed var(--line); border-radius: 10px; display: flex; flex-wrap: wrap; gap: 8px 12px; padding: 8px 10px; }
                .sso-copy > span { font-size: 13px; font-weight: 600; }
                .sso-copy code { flex: 1 1 auto; font-size: 13px; overflow-wrap: anywhere; }
                .sso-check { align-items: center; display: flex; flex-wrap: wrap; gap: 10px; }
                .sso-check .ok { color: var(--accent); font-weight: 600; }
                .sso-check .bad { color: var(--danger, #b3261e); }
                .sso-switch { cursor: pointer; flex: 0 0 auto; margin-top: 2px; position: relative; }
                .sso-switch input { height: 1px; opacity: 0; position: absolute; width: 1px; }
                .sso-switch-track { background: var(--line); border-radius: 999px; display: block; height: 24px; position: relative; transition: background .15s; width: 42px; }
                .sso-switch-track::after { background: #fff; border-radius: 50%; box-shadow: 0 1px 3px rgba(0,0,0,.25); content: ""; height: 18px; left: 3px; position: absolute; top: 3px; transition: transform .15s; width: 18px; }
                .sso-switch input:checked + .sso-switch-track { background: var(--accent); }
                .sso-switch input:checked + .sso-switch-track::after { transform: translateX(18px); }
                .sso-switch input:focus-visible + .sso-switch-track { box-shadow: var(--focus-ring); }
            </style>
            <script>
                (() => {
                    const panel = document.querySelector('[data-sso-panel]');
                    if (!panel || panel.dataset.wired) { return; }
                    panel.dataset.wired = '1';
                    const on = {{JsonSerializer.Serialize(de ? "An" : "On")}};
                    const off = {{JsonSerializer.Serialize(de ? "Aus" : "Off")}};
                    panel.querySelectorAll('[data-sso-card]').forEach(card => {
                        const box = card.querySelector('[data-sso-enabled]');
                        box.addEventListener('change', () => {
                            card.classList.toggle('is-on', box.checked);
                            card.querySelector('[data-sso-state]').textContent = box.checked ? on : off;
                        });
                    });
                    panel.querySelectorAll('[data-sso-copy]').forEach(button => button.addEventListener('click', async () => {
                        try { await navigator.clipboard.writeText(button.dataset.ssoCopy); } catch { }
                        button.querySelector('span').textContent = {{JsonSerializer.Serialize(de ? "Kopiert" : "Copied")}};
                    }));
                    panel.querySelectorAll('[data-sso-test]').forEach(button => button.addEventListener('click', async () => {
                        const result = button.parentElement.querySelector('[data-sso-test-result]');
                        result.className = 'muted';
                        result.textContent = '...';
                        const form = panel.querySelector('[data-sso-form]');
                        const body = new FormData(form);
                        body.append('provider', button.dataset.ssoTest);
                        try {
                            const answer = await (await fetch('/admin/sso/test', { method: 'POST', body, credentials: 'same-origin' })).json();
                            result.className = answer.ok ? 'ok' : 'bad';
                            result.textContent = answer.message;
                        }
                        catch (error) {
                            result.className = 'bad';
                            result.textContent = error.message;
                        }
                    }));
                })();
            </script>
            """;
    }
}
