using Matgate.Models;

namespace Matgate.Web;

// Groups of users, in the administration: a tab with the list, and a page per group with its members and what
// it may do - on all connections, on folders (with everything filed in them, now and later) and on single
// connections, each with a level.
public sealed partial class HtmlViews
{
    public string GroupPage(
        HttpContext context,
        MatgateUser currentUser,
        UserGroup? group,
        IReadOnlyList<MatgateUser> users,
        IReadOnlyList<ServerEndpoint> servers,
        IReadOnlyList<string> folders,
        IReadOnlyList<AccessGrant> grants,
        IReadOnlyDictionary<string, FolderDefinition> folderIndex,
        string? error = null)
    {
        var de = Language(context) == "de";
        var isNew = group is null;
        var own = group is null ? [] : grants.Where(grant => grant.PrincipalKind == AccessPrincipalKind.Group && grant.PrincipalId == group.Id).ToList();
        AccessLevel LevelOn(AccessTargetKind kind, Guid id) => own.FirstOrDefault(grant => grant.Targets(kind, id))?.Level ?? AccessLevel.None;
        string Check(string name, string value, string label, string note, bool on) => $$"""
            <label class="group-check"><input type="checkbox" name="{{name}}" value="{{A(value)}}"{{(on ? " checked" : "")}}><span><strong>{{E(label)}}</strong>{{(note.Length == 0 ? "" : $"""<small class="muted">{E(note)}</small>""")}}</span></label>
            """;
        string LevelRow(string label, string note, string select, string attributes) => $$"""
            <div class="group-level"{{attributes}}><span><strong>{{E(label)}}</strong>{{(note.Length == 0 ? "" : $"""<small class="muted">{E(note)}</small>""")}}</span>{{select}}</div>
            """;
        var members = string.Join("", users
            .Where(user => !user.IsAdmin)
            .OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(user => Check("member", user.Id.ToString(), string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName, user.UserName, group?.MemberIds.Contains(user.Id) == true)));
        int InFolder(string name) => servers.Count(server => ServerFolders.Same(server.FolderName, name) || server.FolderName.Trim().StartsWith(name + "/", StringComparison.OrdinalIgnoreCase));
        var folderRows = string.Join("", folders.Select((name, index) =>
        {
            var level = folderIndex.TryGetValue(Services.AccessResolver.FolderKey(name), out var definition) ? LevelOn(AccessTargetKind.Folder, definition.Id) : AccessLevel.None;
            var broken = definition?.InheritanceBroken == true ? (de ? " · erbt nicht" : " · does not inherit") : "";
            return LevelRow(
                name,
                (de ? $"{InFolder(name)} Verbindung(en)" : $"{InFolder(name)} connection(s)") + broken,
                $"""<input type="hidden" name="folder_{index}" value="{A(name)}">""" + AccessLevelSelect("folderlevel_" + index, level, de, withNone: true),
                $" data-group-folder=\"{A(name)}\"");
        }));
        var serverRows = string.Join("", servers
            .Where(server => server.OwnerUserId is null && server.Protocol != ServerProtocol.Local)
            .OrderBy(server => server.FolderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .Select(server => LevelRow(
                server.Name,
                (string.IsNullOrWhiteSpace(server.FolderName) ? "" : server.FolderName + " · ") + ServerProtocolLabel(server.Protocol),
                AccessLevelSelect("level_" + server.Id.ToString("N"), LevelOn(AccessTargetKind.Server, server.Id), de, withNone: true),
                $" data-group-server=\"{A(server.Name)}\"")));
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
                        <p class="muted">{{(de ? "Administratoren dürfen ohnehin alles und stehen deshalb nicht in der Liste." : "Administrators may do everything anyway and are therefore not listed.")}}</p>
                        <div class="group-checks">{{(members.Length == 0 ? empty : members)}}</div>
                    </section>
                    {{ExternalGroupsPanel(context, group)}}
                    <section class="panel">
                        <h2>{{(de ? "Alle Verbindungen" : "All connections")}}</h2>
                        <p class="muted">{{(de ? "Jede gemeinsame Verbindung, auch künftige - außer in Ordnern, die nicht erben." : "Every shared connection, also future ones - except in folders that do not inherit.")}}</p>
                        {{LevelRow(de ? "Alle Verbindungen" : "All connections", "", AccessLevelSelect("level_all", LevelOn(AccessTargetKind.All, Guid.Empty), de, withNone: true), " data-group-all")}}
                    </section>
                    <section class="panel">
                        <h2>{{(de ? "Ordner" : "Folders")}}</h2>
                        <p class="muted">{{(de ? "Alles, was in einem dieser Ordner oder darunter liegt - auch was später hinzukommt." : "Everything filed in one of these folders or below - also what is added later.")}}</p>
                        <div class="group-levels">{{(folderRows.Length == 0 ? empty : folderRows)}}</div>
                    </section>
                    <section class="panel">
                        <h2>{{(de ? "Einzelne Verbindungen" : "Single connections")}}</h2>
                        <div class="group-levels">{{(serverRows.Length == 0 ? empty : serverRows)}}</div>
                    </section>
                    <ul class="access-legend muted panel">{{string.Join("", GrantableLevels.Select(level => $"<li><strong>{E(AccessLevelLabel(level, de))}</strong> - {E(AccessLevelHint(level, de))}</li>"))}}</ul>
                </form>
                <section class="panel conn-actions">
                    <div class="form-actions">
                        <button type="submit" form="group-form" class="primary">{{Icon("save")}}{{T(context, "Save")}}</button>
                        <a class="button" href="{{A(isNew ? AdminGroupsUrl() : AdminGroupsUrl(group!.Id))}}">{{Icon("arrow-left")}}{{T(context, "Back")}}</a>
                        {{delete}}
                    </div>
                </section>
            </div>
            {{AccessStyles}}
            <style>
                .group-checks { display: grid; gap: 6px; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); margin-top: 8px; }
                .group-check { align-items: flex-start; border: 1px solid var(--line); border-radius: 10px; cursor: pointer; display: flex; gap: 10px; padding: 8px 10px; }
                .group-check:has(input:checked) { background: color-mix(in srgb, var(--accent) 10%, var(--surface)); border-color: var(--accent); }
                .group-check span { display: grid; min-width: 0; }
                .group-check strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .group-check small { font-size: 12px; }
                .group-levels { display: grid; gap: 6px; grid-template-columns: repeat(auto-fill, minmax(360px, 1fr)); margin-top: 8px; }
                .group-level { align-items: center; border: 1px solid var(--line); border-radius: 10px; display: flex; gap: 10px; justify-content: space-between; padding: 8px 10px; }
                .group-level:has(select option[value="none"]:not(:checked)) { background: color-mix(in srgb, var(--accent) 10%, var(--surface)); border-color: var(--accent); }
                .group-level span { display: grid; flex: 1 1 auto; min-width: 0; }
                .group-level select { flex: 0 0 150px; min-width: 0; width: 150px; }
                .group-level strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .group-level small { font-size: 12px; }
            </style>
            """;

        return Layout(context, currentUser, isNew ? (de ? "Neue Gruppe" : "New group") : group!.Name, body);
    }
}
