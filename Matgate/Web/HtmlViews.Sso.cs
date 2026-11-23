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
}
