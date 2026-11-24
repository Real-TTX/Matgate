using Matgate.Models;

namespace Matgate.Web;

// Administration -> Workspaces, in the shape of Users and Groups: the workspaces as a list on the left (search,
// marks for expired, switched off, personal), the details of the one picked on the right - its public link to
// copy, how long it is open, with a password or not, uploads, owner, folder - and right there: open, edit,
// extend (+1 day, +7, +30), end the public access now, delete. Without a pick: the counts. "List" is the table.
public sealed partial class HtmlViews
{
    public static string AdminWorkspacesUrl(Guid? id = null) => "/admin?tab=workspaces" + (id is null ? "" : "&sel=" + id);

    private static string WorkspacesAdminPanel(
        HttpContext context,
        IReadOnlyList<WorkspaceDefinition> workspaces,
        IReadOnlyList<MatgateUser> users,
        string listHtml)
    {
        var de = Language(context) == "de";
        var asList = context.Request.Query["view"] == "list";
        var viewSwitch = $$"""
            <div class="view-switch admconn-views" role="group" aria-label="{{A(de ? "Ansicht" : "View")}}">
                <a class="view-switch-btn{{(asList ? "" : " is-active")}}" href="{{A(AdminWorkspacesUrl())}}" data-admws-view="detail">{{Icon("briefcase")}}<span>{{(de ? "Übersicht" : "Overview")}}</span></a>
                <a class="view-switch-btn{{(asList ? " is-active" : "")}}" href="/admin?tab=workspaces&amp;view=list" data-admws-view="list">{{Icon("list")}}<span>{{(de ? "Liste" : "List")}}</span></a>
            </div>
            """;
        if (asList)
        {
            return $$"""
                <section class="panel admconn-list" data-admin-workspaces-list>
                    <div class="admconn-list-bar">{{viewSwitch}}</div>
                    {{listHtml}}
                </section>
                {{AdminTreeStyles}}
                """;
        }

        var now = DateTimeOffset.UtcNow;
        bool Open(WorkspaceDefinition workspace) => workspace.IsEnabled && WorkspaceIsPublicAccessActive(workspace);
        // Open ones first, those running out soonest at the top; then the closed ones by name.
        var ordered = workspaces
            .OrderByDescending(Open)
            .ThenBy(workspace => Open(workspace) ? workspace.PublicAccessExpiresAt ?? DateTimeOffset.MaxValue : DateTimeOffset.MaxValue)
            .ThenBy(workspace => workspace.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var chosen = Guid.TryParse(context.Request.Query["sel"].ToString(), out var chosenId) ? workspaces.FirstOrDefault(workspace => workspace.Id == chosenId) : null;
        var userById = users.ToDictionary(user => user.Id);
        string OwnerName(WorkspaceDefinition workspace) => workspace.OwnerUserId is { } owner
            ? userById.TryGetValue(owner, out var user) ? PersonName(user) : (de ? "unbekannt" : "unknown")
            : "";

        string Tags(WorkspaceDefinition workspace)
        {
            var tags = new List<string>();
            if (!workspace.IsEnabled)
            {
                tags.Add($"""<span class="admtree-tag admp-tag-off">{E(de ? "aus" : "off")}</span>""");
            }
            else if (!WorkspaceIsPublicAccessActive(workspace))
            {
                tags.Add($"""<span class="admtree-tag admp-tag-off">{E(de ? "abgelaufen" : "expired")}</span>""");
            }
            else
            {
                tags.Add($"""<span class="admtree-tag admws-tag-open">{E(WorkspaceValidityLabel(context, workspace))}</span>""");
            }

            return string.Join("", tags);
        }

        var items = string.Join("", ordered.Select(workspace => $$"""
            <a class="admtree-node admp-node{{(chosen?.Id == workspace.Id ? " is-selected" : "")}}{{(Open(workspace) ? "" : " is-off")}}" href="{{A(AdminWorkspacesUrl(workspace.Id))}}" style="--depth: 0" data-admws-node="{{A(workspace.Name)}}" data-admp-search="{{A($"{workspace.Name} {workspace.Description} {OwnerName(workspace)}".ToLowerInvariant())}}">
                <span class="admtree-icon">{{Icon("briefcase")}}</span>
                <span class="admtree-text"><strong>{{E(workspace.Name)}}</strong><small>{{E(workspace.IsPrivate ? (de ? "Persönlich · " : "Personal · ") + OwnerName(workspace) : (de ? "Gemeinsam" : "Shared"))}}{{(string.IsNullOrEmpty(workspace.AccessPasswordHash) ? "" : " · " + (de ? "Passwort" : "password"))}}</small></span>
                {{Tags(workspace)}}
            </a>
            """));
        if (items.Length == 0)
        {
            items = $"""<p class="admtree-none muted">{E(de ? "Noch keine Workspaces." : "No workspaces yet.")}</p>""";
        }

        var side = PeopleSide(
            de ? "Workspaces suchen ..." : "Search workspaces ...",
            viewSwitch,
            $$"""<a class="button primary admtree-new" href="/workspaces/new">{{Icon("plus")}}<span>{{(de ? "Neuer Workspace" : "New workspace")}}</span></a>""",
            "Workspaces",
            items,
            de);

        string detail;
        if (chosen is null)
        {
            var open = workspaces.Count(Open);
            var soon = workspaces.Count(workspace => Open(workspace) && workspace.PublicAccessExpiresAt is { } until && until - now < TimeSpan.FromDays(1));
            detail = $$"""
                <div class="admdetail-head">
                    <span class="admdetail-icon">{{Icon("briefcase")}}</span>
                    <div><p class="eyebrow">Workspaces</p><h2>{{(de ? "Alle Workspaces" : "All workspaces")}}</h2></div>
                </div>
                <div class="admdetail-stats" data-admws-stats>
                    <div><strong>{{workspaces.Count}}</strong><span>Workspaces</span></div>
                    <div><strong>{{open}}</strong><span>{{(de ? "offen" : "open")}}</span></div>
                    <div><strong>{{soon}}</strong><span>{{(de ? "laufen heute ab" : "end within a day")}}</span></div>
                    <div><strong>{{workspaces.Count - open}}</strong><span>{{(de ? "abgelaufen oder aus" : "expired or off")}}</span></div>
                    <div><strong>{{workspaces.Count(workspace => string.IsNullOrEmpty(workspace.AccessPasswordHash) && Open(workspace))}}</strong><span>{{(de ? "offen ohne Passwort" : "open without password")}}</span></div>
                </div>
                <p class="muted admdetail-hint">{{(de
                    ? "Ein Workspace ist ein Ordner, den man über einen Link nach außen gibt - zum Hoch- und Herunterladen, auf Wunsch mit Passwort und immer nur für eine Zeit. Links einen wählen: dort steht der Link, wie lange er noch gilt, und man kann verlängern oder den Zugang sofort beenden."
                    : "A workspace is a folder given out through a link - to upload and download, with a password if wanted and always for a time only. Pick one on the left: it shows the link and how long it still holds, and it can be extended or ended right away.")}}</p>
                """;
        }
        else
        {
            detail = $$"""<a class="button admdetail-back" href="{{A(AdminWorkspacesUrl())}}">{{Icon("arrow-left")}}{{(de ? "Alle Workspaces" : "All workspaces")}}</a>"""
                + WorkspaceDetailPane(context, chosen, OwnerName(chosen), de);
        }

        return $$"""
            <section class="admconn admpeople admworkspaces{{(chosen is null ? "" : " has-selection")}}" data-admin-workspaces>
                {{side}}
                <div class="admdetail" data-admdetail>{{detail}}</div>
            </section>
            {{AdminTreeStyles}}
            {{AccessStyles}}
            {{AdminPeopleStyles}}
            <style>
                .admws-tag-open { border-color: color-mix(in srgb, var(--accent) 40%, var(--line)); color: var(--accent); }
                .admws-link { align-items: center; background: var(--surface-2, var(--surface)); border: 1px dashed var(--line); border-radius: 10px; display: flex; flex-wrap: wrap; gap: 8px 12px; padding: 8px 10px; }
                .admws-link code { flex: 1 1 auto; font-size: 13px; min-width: 0; overflow-wrap: anywhere; }
                .admws-time { display: flex; flex-wrap: wrap; gap: 8px; }
                .admws-time form { display: contents; }
                .admws-description { margin: 0; white-space: pre-wrap; }
            </style>
            <script>{{AdminPeopleScript}}{{AdminWorkspacesScript}}</script>
            """;
    }

    private static string WorkspaceDetailPane(HttpContext context, WorkspaceDefinition workspace, string ownerName, bool de)
    {
        var self = AdminWorkspacesUrl(workspace.Id);
        var open = WorkspaceIsPublicAccessActive(workspace);
        var link = context.Request.Scheme + "://" + context.Request.Host.Value + "/w/" + workspace.Id;
        var badges = new List<string>
        {
            !workspace.IsEnabled
                ? $"""<span class="admp-badge is-off">{Icon("lock")}{E(de ? "Ausgeschaltet" : "Switched off")}</span>"""
                : open
                    ? $"""<span class="admp-badge is-ok">{Icon("check")}{E(de ? "Offen" : "Open")}</span>"""
                    : $"""<span class="admp-badge is-off">{Icon("clock")}{E(de ? "Abgelaufen" : "Expired")}</span>""",
            workspace.IsPrivate
                ? $"""<span class="admp-badge">{Icon("user")}{E(de ? "Persönlich" : "Personal")}</span>"""
                : $"""<span class="admp-badge">{Icon("users")}{E(de ? "Gemeinsam" : "Shared")}</span>""",
        };
        if (!string.IsNullOrEmpty(workspace.AccessPasswordHash))
        {
            badges.Add($"""<span class="admp-badge">{Icon("key")}{E(de ? "Passwort" : "Password")}</span>""");
        }

        string When(DateTimeOffset at) => at.ToLocalTime().ToString(de ? "dd.MM.yyyy HH:mm" : "yyyy-MM-dd HH:mm");
        var until = workspace.PublicAccessExpiresAt is { } expires
            ? (open ? (de ? $"bis {When(expires)} (noch {WorkspaceValidityLabel(context, workspace)})" : $"until {When(expires)} ({WorkspaceValidityLabel(context, workspace)} left)") : (de ? $"abgelaufen am {When(expires)}" : $"expired on {When(expires)}"))
            : (de ? "ohne Ablauf" : "no expiry");
        var facts = new List<(string Label, string Value)>
        {
            (de ? "Passwort" : "Password", string.IsNullOrEmpty(workspace.AccessPasswordHash) ? (de ? "keins - wer den Link hat, kommt hinein" : "none - whoever has the link gets in") : (de ? "ja" : "yes")),
            (de ? "Hochladen" : "Uploads", workspace.AllowUploads ? (de ? "erlaubt" : "allowed") : (de ? "nur herunterladen" : "download only")),
            (de ? "Gehört" : "Owner", workspace.IsPrivate ? ownerName : (de ? "allen Admins (gemeinsam)" : "all admins (shared)")),
            (de ? "Ordner" : "Folder", workspace.SharesExistingPlace ? (de ? "eine bestehende Ablage" : "an existing place") : (de ? "eigener Ordner" : "a folder of its own")),
            (de ? "Angelegt" : "Created", When(workspace.CreatedAt)),
            (de ? "Geändert" : "Changed", When(workspace.UpdatedAt)),
        };

        string Extend(int hours, string label) => $$"""
            <form method="post" action="/workspaces/{{workspace.Id}}/extend">{{Csrf(context)}}<input type="hidden" name="publicAccessHours" value="{{hours}}"><input type="hidden" name="returnUrl" value="{{A(self)}}"><button type="submit" class="button" data-admws-extend="{{hours}}">{{Icon("plus")}}{{E(label)}}</button></form>
            """;
        var end = open && workspace.IsEnabled
            ? $$"""<form method="post" action="/workspaces/{{workspace.Id}}/expire" data-confirm="{{A(de ? "Den öffentlichen Zugang jetzt beenden? Der Link geht dann sofort nicht mehr; verlängern öffnet ihn wieder." : "End the public access now? The link stops working at once; extending opens it again.")}}">{{Csrf(context)}}<input type="hidden" name="returnUrl" value="{{A(self)}}"><button type="submit" class="button" data-admws-expire>{{Icon("x")}}{{(de ? "Jetzt beenden" : "End now")}}</button></form>"""
            : "";
        var timeCard = $$"""
            <section class="admdetail-section" data-admws-time>
                <h3>{{Icon("clock")}}{{(de ? "Wie lange offen" : "How long open")}}</h3>
                <p class="admp-summary" data-admws-until>{{E(until)}}</p>
                <div class="admws-time">
                    {{Extend(24, de ? "1 Tag" : "1 day")}}
                    {{Extend(24 * 7, de ? "7 Tage" : "7 days")}}
                    {{Extend(24 * 30, de ? "30 Tage" : "30 days")}}
                    {{end}}
                </div>
                <p class="muted admp-note">{{(de ? "Verlängern rechnet ab dem heutigen Ende - bei einem abgelaufenen ab jetzt." : "Extending counts from today's end - for an expired one from now.")}}</p>
            </section>
            """;

        return $$"""
            <div class="admdetail-head admp-head">
                <span class="admdetail-icon">{{Icon("briefcase")}}</span>
                <div>
                    <p class="eyebrow">Workspace</p>
                    <h2>{{E(workspace.Name)}}</h2>
                    <div class="admp-badges">{{string.Join("", badges)}}</div>
                </div>
            </div>
            <div class="admdetail-actions">
                <a class="button primary" href="/workspaces/{{workspace.Id}}" data-shell-open-tab="1" data-shell-title="{{A(workspace.Name)}}" data-shell-description="Workspace" data-admws-open>{{Icon("external-link")}}{{(de ? "Öffnen" : "Open")}}</a>
                <a class="button" href="/workspaces/{{workspace.Id}}?tab=settings" data-admws-edit>{{Icon("edit")}}{{(de ? "Bearbeiten" : "Edit")}}</a>
                <a class="button" href="/workspaces/{{workspace.Id}}?tab=log">{{Icon("list")}}{{(de ? "Protokoll" : "Log")}}</a>
                <span class="spacer"></span>
                <form method="post" action="/workspaces/{{workspace.Id}}/delete" data-confirm="{{A(de ? $"Workspace „{workspace.Name}“ löschen? Ein eigener Ordner wird mit allen Dateien gelöscht." : $"Delete the workspace \"{workspace.Name}\"? A folder of its own is deleted with all its files.")}}">{{Csrf(context)}}<input type="hidden" name="returnUrl" value="{{A(AdminWorkspacesUrl())}}"><button type="submit" class="danger" data-admws-delete>{{Icon("trash")}}{{(de ? "Löschen" : "Delete")}}</button></form>
            </div>
            <section class="admdetail-section" data-admws-link>
                <h3>{{Icon("globe")}}{{(de ? "Öffentlicher Link" : "Public link")}}</h3>
                <div class="admws-link"><code>{{E(link)}}</code><button type="button" class="button" data-admws-copy="{{A(link)}}">{{Icon("copy")}}<span>{{(de ? "Kopieren" : "Copy")}}</span></button></div>
                {{(string.IsNullOrWhiteSpace(workspace.Description) ? "" : $"""<p class="admws-description muted">{E(workspace.Description)}</p>""")}}
            </section>
            {{timeCard}}
            <section class="admdetail-section"><h3>{{Icon("list")}}{{(de ? "Übersicht" : "Overview")}}</h3><dl class="admdetail-facts">{{string.Join("", facts.Select(fact => $"<div><dt>{E(fact.Label)}</dt><dd>{E(fact.Value)}</dd></div>"))}}</dl></section>
            """;
    }

    private const string AdminWorkspacesScript = """
        (() => {
            document.querySelectorAll('[data-admws-copy]').forEach(button => {
                if (button.dataset.wired) { return; }
                button.dataset.wired = '1';
                button.addEventListener('click', async () => {
                    try { await navigator.clipboard.writeText(button.dataset.admwsCopy); } catch { }
                    button.querySelector('span').textContent = document.documentElement.lang === 'de' ? 'Kopiert' : 'Copied';
                });
            });
        })();
        """;
}
