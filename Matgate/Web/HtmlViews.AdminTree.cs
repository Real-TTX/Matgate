using Matgate.Models;

namespace Matgate.Web;

// Administration -> Connections: the connections as a tree, the way the side panel shows them - "All connections"
// at the top, folders with their subfolders, the connections in them - and beside it what is chosen there: a
// folder (its content, what to do with it, who may use it), a connection (what it is, connect, edit, delete, who
// may use it), or all connections (who may use every one of them). A choice is part of the address
// (?tab=connections&sel=folder:Lab/Linux), so a reload, the back button and a link all keep it.
public sealed partial class HtmlViews
{
    private sealed class AdminTreeFolder(string path, string name)
    {
        public string Path { get; } = path;

        public string Name { get; } = name;

        public SortedDictionary<string, AdminTreeFolder> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<ServerEndpoint> Servers { get; } = [];

        public string IconKey { get; set; } = "";

        public int Count => Servers.Count + Children.Values.Sum(child => child.Count);
    }

    public static string AdminConnectionsUrl(string selection) => "/admin?tab=connections&sel=" + Uri.EscapeDataString(selection);

    private static string ConnectionsAdminPanel(
        HttpContext context,
        MatgateUser user,
        IReadOnlyList<ServerEndpoint> servers,
        IReadOnlyList<FolderDefinition>? folderObjects,
        string accessHtml,
        string listHtml)
    {
        var de = Language(context) == "de";
        var selection = context.Request.Query["sel"].ToString();
        // "Tree | List": the list is the table of every connection, to search and sort (where ?tab=servers led before).
        var asList = context.Request.Query["view"] == "list" || context.Request.Query["tab"] == "servers";
        var viewSwitch = $$"""
            <div class="view-switch admconn-views" role="group" aria-label="{{A(de ? "Ansicht" : "View")}}">
                <a class="view-switch-btn{{(asList ? "" : " is-active")}}" href="/admin?tab=connections" data-admconn-view="tree"{{(asList ? "" : " aria-current=\"page\"")}}>{{Icon("folder")}}<span>{{(de ? "Baum" : "Tree")}}</span></a>
                <a class="view-switch-btn{{(asList ? " is-active" : "")}}" href="/admin?tab=connections&amp;view=list" data-admconn-view="list"{{(asList ? " aria-current=\"page\"" : "")}}>{{Icon("list")}}<span>{{(de ? "Liste" : "List")}}</span></a>
            </div>
            """;
        if (asList)
        {
            return $$"""
                <section class="panel admconn-list" data-admin-connections-list>
                    <div class="admconn-list-bar">{{viewSwitch}}<span class="spacer"></span><a class="button primary" href="/admin/servers/new">{{Icon("plus")}}{{(de ? "Neue Verbindung" : "New connection")}}</a></div>
                    {{listHtml}}
                </section>
                {{AdminTreeStyles}}
                """;
        }
        var connections = servers.Where(server => server.Protocol != ServerProtocol.Local).ToList();

        // The tree: every folder of a connection, every folder that exists on its own, and every folder above them.
        var root = new AdminTreeFolder("", "");
        AdminTreeFolder Ensure(string path)
        {
            var node = root;
            var parts = ServerFolders.CleanPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < parts.Length; index++)
            {
                if (!node.Children.TryGetValue(parts[index], out var child))
                {
                    child = new AdminTreeFolder(string.Join("/", parts.Take(index + 1)), parts[index]);
                    node.Children[parts[index]] = child;
                }

                node = child;
            }

            return node;
        }

        foreach (var folder in folderObjects ?? [])
        {
            Ensure(folder.Name).IconKey = folder.IconKey;
        }

        var unfiled = new List<ServerEndpoint>();
        foreach (var server in connections.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(server.FolderName))
            {
                unfiled.Add(server);
                continue;
            }

            var node = Ensure(server.FolderName);
            node.Servers.Add(server);
            if (node.IconKey.Length == 0)
            {
                node.IconKey = ServerEndpoint.NormalizeIconKey(server.FolderIconKey);
            }
        }

        AdminTreeFolder? Find(string path)
        {
            var node = root;
            foreach (var part in ServerFolders.CleanPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(part, out node!))
                {
                    return null;
                }
            }

            return node;
        }

        // ---- the tree
        string ServerNode(ServerEndpoint server, int depth) => $$"""
            <a class="admtree-node admtree-server{{(selection == "server:" + server.Id ? " is-selected" : "")}}{{(server.IsEnabled ? "" : " is-off")}}" href="{{A(AdminConnectionsUrl("server:" + server.Id))}}" style="--depth: {{depth}}; --proto: {{ProtocolAccent(server.Protocol)}}" data-admtree-server="{{server.Id}}" data-admtree-search="{{A((server.Name + " " + ServerTargetValue(server) + " " + ServerProtocolLabel(server.Protocol)).ToLowerInvariant())}}">
                <span class="admtree-caret-space"></span>
                <span class="admtree-icon">{{ServerIcon(server, "small")}}</span>
                <span class="admtree-text"><strong>{{E(server.Name)}}</strong><small>{{E(ServerProtocolLabel(server.Protocol))}} · {{E(ServerTargetValue(server))}}</small></span>
                {{(server.OwnerUserId is null ? "" : $"""<span class="admtree-tag">{E(de ? "privat" : "private")}</span>""")}}
            </a>
            """;
        string FolderNode(AdminTreeFolder folder, int depth)
        {
            var children = string.Join("", folder.Children.Values.Select(child => FolderNode(child, depth + 1)))
                + string.Join("", folder.Servers.Select(server => ServerNode(server, depth + 1)));
            return $$"""
                <div class="admtree-group" data-admtree-folder="{{A(folder.Path)}}">
                    <a class="admtree-node admtree-folder{{(selection == "folder:" + folder.Path ? " is-selected" : "")}}" href="{{A(AdminConnectionsUrl("folder:" + folder.Path))}}" style="--depth: {{depth}}" data-admtree-search="{{A(folder.Path.ToLowerInvariant())}}">
                        <button type="button" class="admtree-caret" data-admtree-toggle aria-label="{{A(de ? "Auf- oder zuklappen" : "Expand or collapse")}}">{{Icon("chevron-down")}}</button>
                        <span class="admtree-icon admtree-folder-icon">{{FolderIcon(folder.IconKey)}}</span>
                        <span class="admtree-text"><strong>{{E(folder.Name)}}</strong></span>
                        <span class="admtree-count">{{folder.Count}}</span>
                    </a>
                    <div class="admtree-children">{{children}}</div>
                </div>
                """;
        }

        var tree = string.Join("", root.Children.Values.Select(folder => FolderNode(folder, 1)))
            + (unfiled.Count == 0 ? "" : $$"""
                <div class="admtree-group" data-admtree-folder="">
                    <div class="admtree-node admtree-folder admtree-unfiled" style="--depth: 1">
                        <button type="button" class="admtree-caret" data-admtree-toggle aria-label="{{A(de ? "Auf- oder zuklappen" : "Expand or collapse")}}">{{Icon("chevron-down")}}</button>
                        <span class="admtree-icon admtree-folder-icon">{{Icon("folder")}}</span>
                        <span class="admtree-text"><strong>{{E(de ? "Ohne Ordner" : "No folder")}}</strong></span>
                        <span class="admtree-count">{{unfiled.Count}}</span>
                    </div>
                    <div class="admtree-children">{{string.Join("", unfiled.Select(server => ServerNode(server, 2)))}}</div>
                </div>
                """);
        var canCreate = user.IsAdmin || user.CanManageServers || user.CanCreateServers;
        var treePane = $$"""
            <div class="admtree-side">
                <div class="admtree-tools">
                    {{viewSwitch}}
                    <label class="admtree-search">{{Icon("search")}}<input type="search" data-admtree-filter placeholder="{{A(de ? "Verbindungen suchen ..." : "Search connections ...")}}" autocomplete="off" spellcheck="false"></label>
                    {{(canCreate ? $$"""<a class="button primary admtree-new" href="/admin/servers/new?return=admin" title="{{A(de ? "Neue Verbindung" : "New connection")}}">{{Icon("plus")}}<span>{{(de ? "Verbindung" : "Connection")}}</span></a><a class="button admtree-new" href="/admin/folders/new" title="{{A(de ? "Neuer Ordner" : "New folder")}}">{{Icon("folder")}}<span>{{(de ? "Ordner" : "Folder")}}</span></a>""" : "")}}
                </div>
                <div class="admtree" data-admtree role="navigation" aria-label="{{A(de ? "Verbindungen" : "Connections")}}">
                    <a class="admtree-node admtree-root{{(selection is "" or "all" ? " is-selected" : "")}}" href="{{A(AdminConnectionsUrl("all"))}}" style="--depth: 0">
                        <span class="admtree-icon">{{Icon("server")}}</span>
                        <span class="admtree-text"><strong>{{(de ? "Alle Verbindungen" : "All connections")}}</strong></span>
                        <span class="admtree-count">{{connections.Count}}</span>
                    </a>
                    {{tree}}
                    <p class="admtree-none muted" data-admtree-none hidden>{{(de ? "Nichts gefunden." : "Nothing found.")}}</p>
                </div>
            </div>
            """;

        // ---- what is chosen
        string Section(string title, string iconName, string inner) => $$"""<section class="admdetail-section"><h3>{{Icon(iconName)}}{{E(title)}}</h3>{{inner}}</section>""";
        var accessPart = accessHtml.Length == 0 ? "" : $$"""<section class="admdetail-section admdetail-access"><h2 class="admdetail-sub">{{Icon("shield")}}{{(de ? "Zugriff" : "Access")}}</h2>{{accessHtml}}</section>""";
        string detail;
        var backToTree = $$"""<a class="button admdetail-back" href="{{A(AdminConnectionsUrl("tree"))}}">{{Icon("arrow-left")}}{{(de ? "Übersicht" : "Overview")}}</a>""";
        if (selection.StartsWith("server:", StringComparison.Ordinal)
            && Guid.TryParse(selection["server:".Length..], out var serverId)
            && connections.FirstOrDefault(server => server.Id == serverId) is { } chosen)
        {
            var facts = new List<(string Label, string Value)>
            {
                (de ? "Typ" : "Type", ServerProtocolLabel(chosen.Protocol)),
                (de ? "Adresse" : "Address", ServerTargetValue(chosen)),
                (de ? "Ordner" : "Folder", string.IsNullOrWhiteSpace(chosen.FolderName) ? "-" : chosen.FolderName),
                (de ? "Anmeldung" : "Sign-in", string.IsNullOrWhiteSpace(chosen.UserName) ? "-" : chosen.UserName),
                (de ? "Status" : "State", chosen.IsEnabled ? (de ? "Aktiv" : "Active") : (de ? "Aus" : "Off")),
                (de ? "Gehört" : "Belongs to", chosen.OwnerUserId is null ? (de ? "Allen (gemeinsam)" : "Everyone (shared)") : (de ? "Einem Benutzer (privat)" : "One user (private)")),
            };
            detail = $$"""
                <div class="admdetail-head" style="--proto: {{ProtocolAccent(chosen.Protocol)}}">
                    {{backToTree}}
                    <span class="admdetail-icon">{{ServerIcon(chosen)}}</span>
                    <div><p class="eyebrow">{{(de ? "Verbindung" : "Connection")}}</p><h2>{{E(chosen.Name)}}</h2></div>
                </div>
                <div class="admdetail-actions">
                    <a class="button primary" href="/connect/{{chosen.Id}}" data-shell-open-tab="1" data-shell-title="{{A(chosen.Name)}}">{{Icon("play")}}{{(de ? "Verbinden" : "Connect")}}</a>
                    <a class="button" href="/admin/servers/{{chosen.Id}}?return=admin" data-admdetail-edit>{{Icon("edit")}}{{(de ? "Bearbeiten" : "Edit")}}</a>
                    <span class="spacer"></span>
                    <form method="post" action="/admin/servers/{{chosen.Id}}/delete" data-confirm="{{A(de ? $"Verbindung „{chosen.Name}“ wirklich löschen?" : $"Delete the connection \"{chosen.Name}\"?")}}">{{Csrf(context)}}<input type="hidden" name="return" value="admin"><button type="submit" class="danger" data-admdetail-delete>{{Icon("trash")}}{{(de ? "Löschen" : "Delete")}}</button></form>
                </div>
                {{Section(de ? "Übersicht" : "Overview", "list", $"""<dl class="admdetail-facts">{string.Join("", facts.Select(fact => $"<div><dt>{E(fact.Label)}</dt><dd>{E(fact.Value)}</dd></div>"))}</dl>""")}}
                {{accessPart}}
                """;
        }
        else if (selection.StartsWith("folder:", StringComparison.Ordinal) && Find(selection["folder:".Length..]) is { } folder)
        {
            var content = string.Join("", folder.Children.Values.Select(child => $$"""
                    <a class="admdetail-item" href="{{A(AdminConnectionsUrl("folder:" + child.Path))}}"><span class="admtree-icon admtree-folder-icon">{{FolderIcon(child.IconKey)}}</span><span class="admtree-text"><strong>{{E(child.Name)}}</strong><small>{{child.Count}} {{(de ? "Verbindung(en)" : "connection(s)")}}</small></span></a>
                    """))
                + string.Join("", folder.Servers.Select(server => $$"""
                    <a class="admdetail-item" href="{{A(AdminConnectionsUrl("server:" + server.Id))}}" style="--proto: {{ProtocolAccent(server.Protocol)}}"><span class="admtree-icon">{{ServerIcon(server, "small")}}</span><span class="admtree-text"><strong>{{E(server.Name)}}</strong><small>{{E(ServerProtocolLabel(server.Protocol))}} · {{E(ServerTargetValue(server))}}</small></span></a>
                    """));
            var parts = folder.Path.Split('/');
            var crumbs = string.Join("<span class=\"admdetail-crumb-sep\">/</span>", parts.Select((part, index) => $"""<a href="{A(AdminConnectionsUrl("folder:" + string.Join("/", parts.Take(index + 1))))}">{E(part)}</a>"""));
            detail = $$"""
                <div class="admdetail-head">
                    {{backToTree}}
                    <span class="admdetail-icon admtree-folder-icon">{{FolderIcon(folder.IconKey)}}</span>
                    <div><p class="eyebrow admdetail-crumbs">{{crumbs}}</p><h2>{{E(folder.Name)}}</h2></div>
                </div>
                <div class="admdetail-actions">
                    {{(canCreate ? $"""<a class="button primary" href="/admin/servers/new?return=admin&folder={A(Uri.EscapeDataString(folder.Path))}">{Icon("plus")}{E(de ? "Verbindung hier" : "Connection here")}</a><a class="button" href="/admin/folders/new?name={A(Uri.EscapeDataString(folder.Path + "/"))}">{Icon("folder")}{E(de ? "Unterordner" : "Subfolder")}</a>""" : "")}}
                    <a class="button" href="{{A(FolderEditUrl(folder.Path))}}">{{Icon("edit")}}{{(de ? "Bearbeiten" : "Edit")}}</a>
                    <span class="spacer"></span>
                    <form method="post" action="/admin/folders/delete" data-confirm="{{A(de ? $"Ordner „{folder.Path}“ auflösen? Seine Verbindungen bleiben, nur ohne Ordner." : $"Dissolve the folder \"{folder.Path}\"? Its connections stay, only without a folder.")}}">{{Csrf(context)}}<input type="hidden" name="name" value="{{A(folder.Path)}}"><button type="submit" class="danger">{{Icon("trash")}}{{(de ? "Auflösen" : "Dissolve")}}</button></form>
                </div>
                {{Section(de ? $"Inhalt · {folder.Count}" : $"Content · {folder.Count}", "folder", content.Length == 0 ? $"""<p class="muted">{E(de ? "Leer." : "Empty.")}</p>""" : $"""<div class="admdetail-items">{content}</div>""")}}
                {{accessPart}}
                """;
        }
        else
        {
            var folderCount = 0;
            void CountFolders(AdminTreeFolder node)
            {
                folderCount += node.Children.Count;
                foreach (var child in node.Children.Values)
                {
                    CountFolders(child);
                }
            }

            CountFolders(root);
            detail = $$"""
                <div class="admdetail-head">
                    {{backToTree}}
                    <span class="admdetail-icon">{{Icon("server")}}</span>
                    <div><p class="eyebrow">{{(de ? "Verbindungen" : "Connections")}}</p><h2>{{(de ? "Alle Verbindungen" : "All connections")}}</h2></div>
                </div>
                <div class="admdetail-stats">
                    <div><strong>{{connections.Count}}</strong><span>{{(de ? "Verbindungen" : "connections")}}</span></div>
                    <div><strong>{{folderCount}}</strong><span>{{(de ? "Ordner" : "folders")}}</span></div>
                    <div><strong>{{connections.Count(server => server.OwnerUserId is not null)}}</strong><span>{{(de ? "privat" : "private")}}</span></div>
                    <div><strong>{{connections.Count(server => !server.IsEnabled)}}</strong><span>{{(de ? "ausgeschaltet" : "switched off")}}</span></div>
                </div>
                <p class="muted admdetail-hint">{{(de
                    ? "Links eine Verbindung oder einen Ordner wählen: dort steht, was er enthält, was man damit tun kann und wer ihn benutzen darf. Rechte auf einen Ordner gelten für alles darin."
                    : "Pick a connection or a folder on the left: it shows what it holds, what can be done with it and who may use it. Rights on a folder hold for everything in it.")}}</p>
                {{accessPart}}
                """;
        }

        var hasSelection = selection.Length > 0 && selection != "tree";
        return $$"""
            <section class="admconn{{(hasSelection ? " has-selection" : "")}}" data-admin-connections>
                {{treePane}}
                <div class="admdetail" data-admdetail>{{detail}}</div>
            </section>
            {{AdminTreeStyles}}
            <script>{{AdminTreeScript}}</script>
            """;
    }

    private const string AdminTreeScript = """
        (() => {
            const root = document.querySelector('[data-admin-connections]');
            if (!root || root.dataset.wired) { return; }
            root.dataset.wired = '1';
            const key = 'matgate.admin.tree.closed';
            const closed = new Set((() => { try { return JSON.parse(localStorage.getItem(key)) || []; } catch { return []; } })());
            const apply = () => root.querySelectorAll('[data-admtree-folder]').forEach(group => group.classList.toggle('is-closed', closed.has(group.dataset.admtreeFolder)));
            apply();
            root.querySelectorAll('[data-admtree-toggle]').forEach(button => button.addEventListener('click', event => {
                event.preventDefault();
                event.stopPropagation();
                const path = button.closest('[data-admtree-folder]').dataset.admtreeFolder;
                if (closed.has(path)) { closed.delete(path); } else { closed.add(path); }
                try { localStorage.setItem(key, JSON.stringify(Array.from(closed))); } catch { }
                apply();
            }));
            // The chosen node in sight.
            const chosen = root.querySelector('.admtree-node.is-selected');
            if (chosen) {
                let group = chosen.closest('[data-admtree-folder]');
                while (group) { group.classList.remove('is-closed'); group = group.parentElement.closest('[data-admtree-folder]'); }
                chosen.scrollIntoView({ block: 'nearest' });
            }
            const filter = root.querySelector('[data-admtree-filter]');
            filter.addEventListener('input', () => {
                const wanted = filter.value.trim().toLowerCase();
                let any = false;
                root.querySelectorAll('.admtree-server').forEach(node => {
                    const hit = !wanted || node.dataset.admtreeSearch.includes(wanted);
                    node.hidden = !hit;
                    any = any || (hit && !!wanted);
                });
                root.querySelectorAll('[data-admtree-folder]').forEach(group => {
                    const folderHit = wanted && (group.querySelector(':scope > .admtree-folder') || {}).dataset?.admtreeSearch?.includes(wanted);
                    const childHit = !!group.querySelector('.admtree-server:not([hidden])');
                    group.hidden = !!wanted && !folderHit && !childHit;
                    group.classList.toggle('is-filtered', !!wanted);
                    if (folderHit) { group.querySelectorAll('.admtree-server').forEach(node => { node.hidden = false; }); any = true; }
                });
                root.querySelector('[data-admtree-none]').hidden = !wanted || any;
            });
        })();
        """;

    private const string AdminTreeStyles = """
        <style>
            .admconn { align-items: start; display: grid; gap: 16px; grid-template-columns: minmax(260px, 340px) minmax(0, 1fr); }
            .admtree-side { background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius, 14px); display: flex; flex-direction: column; max-height: calc(100dvh - 210px); min-height: 360px; overflow: hidden; position: sticky; top: 12px; }
            .admtree-tools { align-items: center; border-bottom: 1px solid var(--line); display: flex; flex-wrap: wrap; gap: 6px; padding: 10px; }
            .admtree-search { align-items: center; display: flex; flex: 1 1 100%; position: relative; }
            .admconn-views { flex: 1 1 100%; }
            .admconn-views .view-switch-btn { flex: 1 1 0; justify-content: center; text-decoration: none; }
            .admconn-views .view-switch-btn.is-active { background: var(--surface); box-shadow: var(--shadow-soft, 0 1px 3px rgba(0,0,0,.12)); color: var(--text); }
            .admconn-list-bar { align-items: center; display: flex; gap: 10px; margin-bottom: 12px; }
            .admconn-list-bar .admconn-views { flex: 0 0 auto; }
            .admconn-list-bar .spacer { flex: 1 1 auto; }
            .admtree-search > .icon { color: var(--muted); height: 15px; left: 10px; pointer-events: none; position: absolute; width: 15px; }
            .admtree-search input[type="search"] { border-radius: 10px; font-size: 13.5px; min-height: 34px; padding: 5px 10px 5px 32px; width: 100%; }
            .admtree-new { flex: 1 1 0; justify-content: center; min-height: 32px; padding: 4px 10px; }
            .admtree { flex: 1 1 auto; overflow-y: auto; padding: 6px; }
            .admtree-node { align-items: center; border-radius: 9px; color: var(--text); display: flex; gap: 8px; min-height: 36px; padding: 4px 8px; padding-left: calc(8px + var(--depth, 0) * 14px); text-decoration: none; }
            .admtree-node:hover { background: var(--hover-bg); }
            .admtree-node.is-selected { background: color-mix(in srgb, var(--accent) 14%, var(--surface)); box-shadow: inset 3px 0 0 var(--accent); }
            .admtree-node.is-off { opacity: .6; }
            .admtree-root { font-weight: 700; }
            .admtree-icon { align-items: center; display: inline-flex; flex: 0 0 auto; }
            .admtree-icon .icon { height: 16px; width: 16px; }
            .admtree-folder-icon { color: var(--accent); }
            .admtree-text { display: grid; flex: 1 1 auto; min-width: 0; }
            .admtree-text strong { font-size: 13.5px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .admtree-text small { color: var(--muted); font-size: 11.5px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .admtree-count { background: var(--surface-2, var(--bg)); border-radius: 999px; color: var(--muted); font-size: 11.5px; padding: 1px 8px; }
            .admtree-tag { border: 1px solid var(--line); border-radius: 999px; color: var(--muted); font-size: 11px; padding: 0 7px; }
            .admtree-caret { align-items: center; background: transparent; border: 0; border-radius: 6px; box-shadow: none; color: var(--muted); display: inline-flex; flex: 0 0 auto; height: 20px; justify-content: center; min-height: 0; padding: 0; transition: transform .15s; width: 20px; }
            .admtree-caret:hover { background: var(--hover-bg); }
            .admtree-caret .icon { height: 14px; width: 14px; }
            .admtree-caret-space { display: inline-block; flex: 0 0 auto; width: 20px; }
            .admtree-group.is-closed:not(.is-filtered) > .admtree-children { display: none; }
            .admtree-group.is-closed:not(.is-filtered) > .admtree-node .admtree-caret { transform: rotate(-90deg); }
            .admtree-group[hidden], .admtree-node[hidden] { display: none; }
            .admtree-none { padding: 10px 12px; }
            .admdetail { display: grid; gap: 16px; min-width: 0; }
            .admdetail-head { align-items: center; display: flex; gap: 14px; }
            .admdetail-head h2 { font-size: 22px; margin: 0; }
            .admdetail-head .eyebrow { margin: 0 0 2px; }
            .admdetail-icon { align-items: center; background: color-mix(in srgb, var(--proto, var(--accent)) 14%, var(--surface)); border-radius: 14px; color: var(--proto, var(--accent)); display: inline-flex; flex: 0 0 auto; height: 48px; justify-content: center; width: 48px; }
            .admdetail-icon .icon, .admdetail-icon svg { height: 24px; width: 24px; }
            .admdetail-back { display: none; }
            .admdetail-crumbs a { color: inherit; text-decoration: none; }
            .admdetail-crumbs a:hover { text-decoration: underline; }
            .admdetail-crumb-sep { margin: 0 4px; opacity: .6; }
            .admdetail-actions { align-items: center; background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius, 14px); display: flex; flex-wrap: wrap; gap: 8px; padding: 10px 12px; }
            .admdetail-actions form { margin: 0; }
            .admdetail-actions .spacer { flex: 1 1 auto; }
            .admdetail-section { background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius, 14px); display: grid; gap: 10px; padding: 16px 18px; }
            .admdetail-section h3, .admdetail-sub { align-items: center; display: flex; font-size: 16px; gap: 8px; margin: 0; }
            .admdetail-section h3 .icon, .admdetail-sub .icon { color: var(--accent); height: 18px; width: 18px; }
            .admdetail-access { background: transparent; border: 0; padding: 0; }
            .admdetail-facts { display: grid; gap: 10px 18px; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); margin: 0; }
            .admdetail-facts dt { color: var(--muted); font-size: 12px; font-weight: 600; letter-spacing: .02em; text-transform: uppercase; }
            .admdetail-facts dd { font-size: 14px; margin: 2px 0 0; overflow-wrap: anywhere; }
            .admdetail-items { display: grid; gap: 6px; grid-template-columns: repeat(auto-fill, minmax(240px, 1fr)); }
            .admdetail-item { align-items: center; border: 1px solid var(--line); border-radius: 12px; color: var(--text); display: flex; gap: 10px; padding: 8px 10px; text-decoration: none; }
            .admdetail-item:hover { border-color: var(--proto, var(--accent)); }
            .admdetail-stats { display: grid; gap: 10px; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); }
            .admdetail-stats > div { background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius, 14px); display: grid; padding: 14px 16px; }
            .admdetail-stats strong { font-size: 26px; line-height: 1.1; }
            .admdetail-stats span { color: var(--muted); font-size: 13px; }
            .admdetail-hint { margin: 0; }
            @media (max-width: 860px) {
                .admconn { grid-template-columns: 1fr; }
                .admtree-side { max-height: none; position: static; }
                .admconn.has-selection .admtree-side { display: none; }
                .admconn:not(.has-selection) .admdetail { display: none; }
                .admdetail-back { display: inline-flex; }
                .admdetail-head { flex-wrap: wrap; }
                .admdetail-facts { grid-template-columns: repeat(2, minmax(0, 1fr)); }
            }
        </style>
        """;
}
