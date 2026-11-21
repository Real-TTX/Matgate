using Matgate.Models;

namespace Matgate.Web;

// Groups of users, in the administration: a tab with the list, and a page per group with its members and what
// it is given - folders (with everything filed in them, now and later) and single connections.
public sealed partial class HtmlViews
{
    private static string GroupsPanel(HttpContext context, IReadOnlyList<UserGroup> groups, IReadOnlyList<MatgateUser> users)
    {
        var de = Language(context) == "de";
        var editLabel = de ? "Bearbeiten" : "Edit";
        var names = users.ToDictionary(user => user.Id, user => string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName);
        var rows = string.Join("", groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Select(group => $$"""
            <tr>
                <td><a href="/admin/groups/{{group.Id}}"><strong>{{E(group.Name)}}</strong></a>{{(string.IsNullOrWhiteSpace(group.Description) ? "" : $"""<br><small class="muted">{E(group.Description)}</small>""")}}</td>
                <td data-sort-value="{{group.MemberIds.Count}}" class="muted">{{E(string.Join(", ", group.MemberIds.Where(names.ContainsKey).Take(5).Select(id => names[id])) + (group.MemberIds.Count > 5 ? ", ..." : ""))}}</td>
                <td class="muted">{{E(string.Join(", ", group.Folders.Take(4)) + (group.Folders.Count > 4 ? ", ..." : ""))}}</td>
                <td data-sort-value="{{group.ServerIds.Count}}">{{group.ServerIds.Count}}</td>
                <td class="table-actions"><div class="row-actions"><a class="icon-button" href="/admin/groups/{{group.Id}}" title="{{A(editLabel)}}" aria-label="{{A(editLabel)}}">{{Icon("edit")}}</a></div></td>
            </tr>
            """));
        var headers = $$"""<th data-sortable>{{T(context, "Name")}}</th><th data-sortable>{{(de ? "Mitglieder" : "Members")}}</th><th>{{(de ? "Ordner" : "Folders")}}</th><th data-sortable>{{(de ? "Einzelne Verbindungen" : "Single connections")}}</th><th class="table-actions">{{T(context, "Actions")}}</th>""";
        var table = DataTable(
            de ? "Gruppen suchen..." : "Search groups...",
            headers,
            rows,
            de ? "Noch keine Gruppe." : "No group yet.",
            5,
            15,
            $$"""<a class="button primary" href="/admin/groups/new">{{Icon("plus")}}{{(de ? "Neue Gruppe" : "New group")}}</a>""");
        return $$"""
            <section class="panel" data-groups>
                <p class="muted settings-lead">{{(de
                    ? "Gruppen geben ihren Mitgliedern Verbindungen - ganze Ordner (mit Unterordnern und allem, was später hineinkommt) oder einzelne. Was ein Benutzer selbst freigegeben bekommen hat, bleibt daneben bestehen."
                    : "Groups give their members connections - whole folders (with their subfolders and whatever is filed there later) or single ones. What a user was given on their own stays as well.")}}</p>
                {{table}}
            </section>
            """;
    }

    public string GroupPage(
        HttpContext context,
        MatgateUser currentUser,
        UserGroup? group,
        IReadOnlyList<MatgateUser> users,
        IReadOnlyList<ServerEndpoint> servers,
        IReadOnlyList<string> folders,
        string? error = null)
    {
        var de = Language(context) == "de";
        var isNew = group is null;
        string Check(string name, string value, string label, string note, bool on) => $$"""
            <label class="group-check"><input type="checkbox" name="{{name}}" value="{{A(value)}}"{{(on ? " checked" : "")}}><span><strong>{{E(label)}}</strong>{{(note.Length == 0 ? "" : $"""<small class="muted">{E(note)}</small>""")}}</span></label>
            """;
        var members = string.Join("", users
            .Where(user => !user.IsAdmin)
            .OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(user => Check("member", user.Id.ToString(), string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName, user.UserName, group?.MemberIds.Contains(user.Id) == true)));
        var folderChecks = string.Join("", folders.Select(name => Check("folder", name, name, de ? $"{servers.Count(server => server.FolderName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase) || server.FolderName.Trim().StartsWith(name + "/", StringComparison.OrdinalIgnoreCase))} Verbindung(en)" : $"{servers.Count(server => server.FolderName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase) || server.FolderName.Trim().StartsWith(name + "/", StringComparison.OrdinalIgnoreCase))} connection(s)", group?.Folders.Any(chosen => ServerFolders.Same(chosen, name)) == true)));
        var serverChecks = string.Join("", servers
            .Where(server => server.OwnerUserId is null && server.Protocol != ServerProtocol.Local)
            .OrderBy(server => server.FolderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .Select(server => Check("server", server.Id.ToString(), server.Name, (string.IsNullOrWhiteSpace(server.FolderName) ? "" : server.FolderName + " · ") + ServerProtocolLabel(server.Protocol), group?.ServerIds.Contains(server.Id) == true)));
        var empty = $"""<p class="muted">{E(de ? "Keine." : "None.")}</p>""";
        var delete = isNew
            ? ""
            : $$"""
                <span class="spacer"></span>
                <form method="post" action="/admin/groups/{{group!.Id}}/delete" data-confirm="{{A(de ? $"Gruppe „{group.Name}“ löschen? Ihre Mitglieder verlieren, was sie nur über diese Gruppe hatten." : $"Delete the group \"{group.Name}\"? Its members lose what they had only through it.")}}">
                    {{Csrf(context)}}
                    <button type="submit" class="danger">{{Icon("trash")}}{{(de ? "Löschen" : "Delete")}}</button>
                </form>
                """;
        var body = $$"""
            <div class="conn-page">
                <section class="page-head conn-head">
                    <span class="conn-head-icon" aria-hidden="true">{{Icon("users")}}</span>
                    <div>
                        <p class="eyebrow">{{(de ? "Gruppe" : "Group")}}</p>
                        <h1>{{(isNew ? (de ? "Neue Gruppe" : "New group") : E(group!.Name))}}</h1>
                    </div>
                </section>
                {{(error is null ? "" : $"""<p class="error-box" role="alert">{E(error)}</p>""")}}
                <form id="group-form" method="post" action="{{(isNew ? "/admin/groups" : $"/admin/groups/{group!.Id}")}}" class="stack">
                    {{Csrf(context)}}
                    <section class="panel">
                        <div class="conn-grid">
                            <label>{{T(context, "Name")}}<input name="name" required maxlength="80" value="{{A(group?.Name)}}" placeholder="{{A(de ? "z. B. Technik" : "e.g. Engineering")}}"></label>
                            <label>{{(de ? "Beschreibung" : "Description")}} <small class="muted">(optional)</small><input name="description" maxlength="200" value="{{A(group?.Description)}}"></label>
                        </div>
                    </section>
                    <section class="panel">
                        <h2>{{(de ? "Mitglieder" : "Members")}}</h2>
                        <p class="muted">{{(de ? "Administratoren sehen ohnehin alles und stehen deshalb nicht in der Liste." : "Administrators see everything anyway and are therefore not listed.")}}</p>
                        <div class="group-checks">{{(members.Length == 0 ? empty : members)}}</div>
                    </section>
                    <section class="panel">
                        <h2>{{(de ? "Ordner" : "Folders")}}</h2>
                        <p class="muted">{{(de ? "Alles, was in einem dieser Ordner oder darunter liegt - auch was später hinzukommt." : "Everything filed in one of these folders or below - also what is added later.")}}</p>
                        <div class="group-checks">{{(folderChecks.Length == 0 ? empty : folderChecks)}}</div>
                    </section>
                    <section class="panel">
                        <h2>{{(de ? "Einzelne Verbindungen" : "Single connections")}}</h2>
                        <div class="group-checks">{{(serverChecks.Length == 0 ? empty : serverChecks)}}</div>
                    </section>
                </form>
                <section class="panel conn-actions">
                    <div class="form-actions">
                        <button type="submit" form="group-form" class="primary">{{Icon("save")}}{{T(context, "Save")}}</button>
                        <a class="button" href="/admin?tab=groups">{{Icon("arrow-left")}}{{T(context, "Back")}}</a>
                        {{delete}}
                    </div>
                </section>
            </div>
            <style>
                .group-checks { display: grid; gap: 6px; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); margin-top: 8px; }
                .group-check { align-items: flex-start; border: 1px solid var(--line); border-radius: 10px; cursor: pointer; display: flex; gap: 10px; padding: 8px 10px; }
                .group-check:has(input:checked) { background: color-mix(in srgb, var(--accent) 10%, var(--surface)); border-color: var(--accent); }
                .group-check span { display: grid; min-width: 0; }
                .group-check strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .group-check small { font-size: 12px; }
            </style>
            """;

        return Layout(context, currentUser, isNew ? (de ? "Neue Gruppe" : "New group") : group!.Name, body);
    }
}
