using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// Administration -> Users and Administration -> Groups, built like Administration -> Connections: a list on the
// left (search, new), beside it what is chosen there. A user: who it is, how it signs in, its groups (added and
// taken away right there) and everything it may use, each with the reason. A group: its members (added and taken
// away right there), the groups of a sign-in service it stands for, and what it may do - each entry with a level
// that is saved as soon as it is changed. The choice is part of the address (?tab=users&sel=<id>).
public sealed partial class HtmlViews
{
    public static string AdminUsersUrl(Guid? id = null) => "/admin?tab=users" + (id is null ? "" : "&sel=" + id);

    public static string AdminGroupsUrl(Guid? id = null) => "/admin?tab=groups" + (id is null ? "" : "&sel=" + id);

    private static string PersonName(MatgateUser user) => string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;

    private static string Initials(string name)
    {
        var parts = name.Split([' ', '.', '_', '-', '@'], StringSplitOptions.RemoveEmptyEntries);
        var letters = parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)],
            _ => $"{parts[0][0]}{parts[1][0]}",
        };
        return letters.ToUpperInvariant();
    }

    private static string PersonAvatar(MatgateUser user, string size = "") =>
        $"""<span class="admp-avatar{(size.Length == 0 ? "" : " admp-avatar--" + size)}{(user.IsAdmin ? " is-admin" : "")}" aria-hidden="true">{E(Initials(PersonName(user)))}</span>""";

    private static string PeopleSide(string searchLabel, string top, string newButton, string listLabel, string items, bool de) => $$"""
        <div class="admtree-side">
            <div class="admtree-tools">
                {{top}}
                <label class="admtree-search">{{Icon("search")}}<input type="search" data-admp-filter placeholder="{{A(searchLabel)}}" autocomplete="off" spellcheck="false"></label>
                {{newButton}}
            </div>
            <div class="admtree" role="navigation" aria-label="{{A(listLabel)}}">
                {{items}}
                <p class="admtree-none muted" data-admp-none hidden>{{(de ? "Nichts gefunden." : "Nothing found.")}}</p>
            </div>
        </div>
        """;

    private static string ReturnField(string url) => $"""<input type="hidden" name="return" value="{A(url)}">""";

    // ---------------------------------------------------------------- users

    private static string UsersAdminPanel(
        HttpContext context,
        MatgateUser currentUser,
        IReadOnlyList<MatgateUser> users,
        IReadOnlyList<UserGroup> groups,
        IReadOnlyList<ServerEndpoint> servers,
        IReadOnlyList<FolderDefinition> folders,
        string listHtml)
    {
        var de = Language(context) == "de";
        var selection = context.Request.Query["sel"].ToString();
        var asList = context.Request.Query["view"] == "list";
        var viewSwitch = $$"""
            <div class="view-switch admconn-views" role="group" aria-label="{{A(de ? "Ansicht" : "View")}}">
                <a class="view-switch-btn{{(asList ? "" : " is-active")}}" href="{{A(AdminUsersUrl())}}" data-admp-view="detail">{{Icon("user")}}<span>{{(de ? "Übersicht" : "Overview")}}</span></a>
                <a class="view-switch-btn{{(asList ? " is-active" : "")}}" href="/admin?tab=users&amp;view=list" data-admp-view="list">{{Icon("list")}}<span>{{(de ? "Liste" : "List")}}</span></a>
            </div>
            """;
        if (asList)
        {
            return $$"""
                <section class="panel admconn-list" data-admin-users-list>
                    <div class="admconn-list-bar">{{viewSwitch}}</div>
                    {{listHtml}}
                </section>
                {{AdminTreeStyles}}
                """;
        }

        var ordered = users
            .OrderByDescending(user => user.IsAdmin)
            .ThenBy(user => PersonName(user), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var chosen = Guid.TryParse(selection, out var chosenId) ? users.FirstOrDefault(user => user.Id == chosenId) : null;
        string Tags(MatgateUser user)
        {
            var tags = new List<string>();
            if (user.IsAdmin)
            {
                tags.Add($"""<span class="admtree-tag admp-tag-admin">{E("Admin")}</span>""");
            }

            if (!user.IsEnabled)
            {
                tags.Add($"""<span class="admtree-tag admp-tag-off">{E(de ? "gesperrt" : "locked")}</span>""");
            }

            return string.Join("", tags);
        }

        var items = string.Join("", ordered.Select(user => $$"""
            <a class="admtree-node admp-node{{(chosen?.Id == user.Id ? " is-selected" : "")}}{{(user.IsEnabled ? "" : " is-off")}}" href="{{A(AdminUsersUrl(user.Id))}}" style="--depth: 0" data-admp-user="{{A(user.UserName)}}" data-admp-search="{{A($"{user.UserName} {user.DisplayName} {user.Email}".ToLowerInvariant())}}">
                {{PersonAvatar(user)}}
                <span class="admtree-text"><strong>{{E(PersonName(user))}}</strong><small>{{E(string.IsNullOrWhiteSpace(user.Email) ? user.UserName : user.UserName + " · " + user.Email)}}</small></span>
                {{Tags(user)}}
            </a>
            """));
        var side = PeopleSide(
            de ? "Benutzer suchen ..." : "Search users ...",
            viewSwitch,
            $$"""<a class="button primary admtree-new" href="/admin/users/new">{{Icon("plus")}}<span>{{(de ? "Neuer Benutzer" : "New user")}}</span></a>""",
            de ? "Benutzer" : "Users",
            items,
            de);

        string detail;
        var back = $$"""<a class="button admdetail-back" href="{{A(AdminUsersUrl())}}">{{Icon("arrow-left")}}{{(de ? "Alle Benutzer" : "All users")}}</a>""";
        if (chosen is null)
        {
            detail = $$"""
                <div class="admdetail-head">
                    <span class="admdetail-icon">{{Icon("users")}}</span>
                    <div><p class="eyebrow">{{(de ? "Benutzer" : "Users")}}</p><h2>{{(de ? "Alle Benutzer" : "All users")}}</h2></div>
                </div>
                <div class="admdetail-stats" data-admp-stats>
                    <div><strong>{{users.Count}}</strong><span>{{(de ? "Benutzer" : "users")}}</span></div>
                    <div><strong>{{users.Count(user => user.IsAdmin)}}</strong><span>{{(de ? "Administratoren" : "administrators")}}</span></div>
                    <div><strong>{{users.Count(user => !user.IsEnabled)}}</strong><span>{{(de ? "gesperrt" : "locked")}}</span></div>
                    <div><strong>{{users.Count(user => user.TotpEnabled)}}</strong><span>{{(de ? "mit Zwei-Faktor" : "with two-factor")}}</span></div>
                    <div><strong>{{users.Count(user => user.ExternalLogins.Count > 0)}}</strong><span>{{(de ? "mit SSO" : "with SSO")}}</span></div>
                </div>
                <p class="muted admdetail-hint">{{(de
                    ? "Links einen Benutzer wählen: dort steht, wie er sich anmeldet, in welchen Gruppen er ist und was er alles benutzen darf - mit dem Grund dafür. Rechte gibt man am besten Gruppen, auf Ordner oder Verbindungen (Verbindungen → Zugriff)."
                    : "Pick a user on the left: it shows how they sign in, which groups they are in and everything they may use - with the reason why. Rights are best given to groups, on folders or connections (Connections → Access).")}}</p>
                """;
        }
        else
        {
            detail = back + UserDetailPane(context, currentUser, chosen, groups, servers, folders, de);
        }

        return $$"""
            <section class="admconn admpeople{{(chosen is null ? "" : " has-selection")}}" data-admin-users>
                {{side}}
                <div class="admdetail" data-admdetail>{{detail}}</div>
            </section>
            {{AdminTreeStyles}}
            {{AccessStyles}}
            {{AdminPeopleStyles}}
            <script>{{AdminPeopleScript}}</script>
            """;
    }

    private static string UserDetailPane(
        HttpContext context,
        MatgateUser currentUser,
        MatgateUser user,
        IReadOnlyList<UserGroup> groups,
        IReadOnlyList<ServerEndpoint> servers,
        IReadOnlyList<FolderDefinition> folders,
        bool de)
    {
        var self = AdminUsersUrl(user.Id);
        var badges = new List<string>();
        if (user.IsAdmin)
        {
            badges.Add($"""<span class="admp-badge is-accent">{Icon("shield")}{E("Administrator")}</span>""");
        }

        badges.Add(user.IsEnabled
            ? $"""<span class="admp-badge is-ok">{Icon("check")}{E(de ? "Aktiv" : "Active")}</span>"""
            : $"""<span class="admp-badge is-off">{Icon("lock")}{E(de ? "Gesperrt" : "Locked")}</span>""");
        if (user.TotpEnabled)
        {
            badges.Add($"""<span class="admp-badge">{Icon("key")}{E(de ? "Zwei-Faktor" : "Two-factor")}</span>""");
        }

        foreach (var provider in user.ExternalLogins.Select(login => login.Provider).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            badges.Add($"""<span class="admp-badge">{Icon("globe")}{E(SsoProviderLabel(provider))}</span>""");
        }

        var rights = new List<string>();
        if (user.IsAdmin)
        {
            rights.Add(de ? "Alles (Administrator)" : "Everything (administrator)");
        }
        else
        {
            if (user.CanManageServers)
            {
                rights.Add(de ? "Server verwalten" : "Manage servers");
            }

            if (user.CanCreateServers)
            {
                rights.Add(de ? "Eigene Server anlegen" : "Create own servers");
            }

            if (user.CanQuickConnect)
            {
                rights.Add(de ? "Quick-Connect" : "Quick connect");
            }
        }

        var signIn = new List<string>();
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            signIn.Add(de ? "Passwort" : "Password");
        }

        signIn.AddRange(user.ExternalLogins.Select(login => SsoProviderLabel(login.Provider) + (string.IsNullOrWhiteSpace(login.Email) ? "" : " (" + login.Email + ")")));
        var facts = new List<(string Label, string Value)>
        {
            (de ? "Benutzername" : "User name", user.UserName),
            (de ? "E-Mail" : "Email", string.IsNullOrWhiteSpace(user.Email) ? "-" : user.Email),
            (de ? "Anmeldung" : "Sign-in", signIn.Count == 0 ? "-" : string.Join(", ", signIn)),
            (de ? "Zwei-Faktor" : "Two-factor", user.TotpEnabled ? (de ? "Eingeschaltet" : "On") : (de ? "Aus" : "Off")),
            (de ? "Darf außerdem" : "May also", rights.Count == 0 ? (de ? "Nur, was freigegeben ist" : "Only what is given") : string.Join(", ", rights)),
            (de ? "Sprache" : "Language", user.PreferredLanguage == "de" ? "Deutsch" : "English"),
        };

        // ---- groups: each one a row, away with it right there; a row to add one
        var mine = groups.Where(group => group.MemberIds.Contains(user.Id)).OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var others = groups.Where(group => !group.MemberIds.Contains(user.Id)).OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToList();
        string MemberForm(UserGroup group, string op, string inner, string extra = "") => $$"""
            <form method="post" action="/admin/groups/members"{{extra}}>{{Csrf(context)}}{{ReturnField(self)}}<input type="hidden" name="group" value="{{group.Id}}"><input type="hidden" name="user" value="{{user.Id}}"><input type="hidden" name="op" value="{{op}}">{{inner}}</form>
            """;
        var groupRows = string.Join("", mine.Select(group => $$"""
            <li class="acc-entry" data-admp-group-row="{{A(group.Name)}}">
                <span class="acc-avatar acc-avatar--group" aria-hidden="true">{{Icon("users")}}</span>
                <span class="acc-who"><a href="{{A(AdminGroupsUrl(group.Id))}}"><strong>{{E(group.Name)}}</strong></a><small>{{E(GroupNote(group, de))}}</small></span>
                {{MemberForm(group, "remove", $"""<button type="submit" class="icon-button acc-remove" title="{A(de ? "Aus der Gruppe nehmen" : "Remove from the group")}" aria-label="{A(de ? "Aus der Gruppe nehmen" : "Remove from the group")}" data-admp-leave>{Icon("x")}</button>""")}}
            </li>
            """));
        var addGroup = others.Count == 0
            ? ""
            : $$"""
                <form method="post" action="/admin/groups/members" class="acc-add" data-admp-join>
                    {{Csrf(context)}}{{ReturnField(self)}}<input type="hidden" name="user" value="{{user.Id}}"><input type="hidden" name="op" value="add">
                    <select name="group" required aria-label="{{A(de ? "Gruppe" : "Group")}}"><option value="">{{(de ? "In eine Gruppe aufnehmen ..." : "Add to a group ...")}}</option>{{string.Join("", others.Select(group => $"""<option value="{group.Id}">{E(group.Name)}</option>"""))}}</select>
                    <button type="submit" class="primary">{{Icon("plus")}}{{(de ? "Aufnehmen" : "Add")}}</button>
                </form>
                """;
        var groupsCard = user.IsAdmin
            ? $"""<p class="muted">{E(de ? "Administratoren dürfen ohnehin alles - Gruppen ändern für sie nichts." : "Administrators may do everything anyway - groups change nothing for them.")}</p>"""
            : $"""<ul class="acc-list" data-admp-groups>{(groupRows.Length == 0 ? $"""<li class="acc-empty muted">{E(de ? "In keiner Gruppe." : "In no group.")}</li>""" : groupRows)}</ul>{addGroup}"""
                + (user.ExternalLogins.Count > 0 && groups.Any(group => group.ExternalGroups.Count > 0)
                    ? $"""<p class="muted admp-note">{E(de ? "Gruppen mit SSO-Zuordnung setzt der Anmeldedienst bei jeder Anmeldung neu." : "Groups mapped to SSO are set again by the sign-in service at every sign-in.")}</p>"""
                    : "");

        // ---- what this user may use: every target something reaches them on, with the level and the reasons
        var shared = servers.Where(server => server.OwnerUserId is null && server.Protocol != ServerProtocol.Local).ToList();
        var usable = shared.Count(server => AccessResolver.Level(user, server) > AccessLevel.None);
        var own = servers.Count(server => server.OwnerUserId == user.Id);
        var folderById = folders.ToDictionary(folder => folder.Id);
        var rows = new List<(int Order, string Sort, string Html)>();
        string Row(string iconHtml, string name, string note, string href, IEnumerable<AccessReason> reasons, string kind)
        {
            var list = reasons.ToList();
            return $$"""
                <li class="acc-person admp-reach" data-admp-reach="{{A(kind + ":" + name)}}" data-effective-level="{{AccessResolver.Highest(list)}}">
                    <span class="acc-avatar acc-avatar--group" aria-hidden="true">{{iconHtml}}</span>
                    <span class="acc-who"><a href="{{A(href)}}"><strong>{{E(name)}}</strong></a><small>{{E(note)}}</small></span>
                    {{AccessLevelBadge(AccessResolver.Highest(list), de)}}
                    <span class="acc-reasons access-reasons">{{string.Join("", list.OrderByDescending(reason => reason.Level).Select(reason => $"""<span class="acc-reason access-reason">{E(AccessReasonText(reason, de))}</span>"""))}}</span>
                </li>
                """;
        }

        if (!user.IsAdmin)
        {
            var reaching = user.Access.Grants;
            if (reaching.Any(entry => entry.Grant.TargetKind == AccessTargetKind.All))
            {
                rows.Add((0, "", Row(Icon("server"), de ? "Alle Verbindungen" : "All connections", de ? $"{shared.Count} gemeinsame Verbindung(en)" : $"{shared.Count} shared connection(s)", AdminConnectionsUrl("all"), AccessResolver.AllReasons(user).Where(reason => reason.Kind == AccessReasonKind.Grant), "all")));
            }

            foreach (var folderId in reaching.Where(entry => entry.Grant.TargetKind == AccessTargetKind.Folder).Select(entry => entry.Grant.TargetId).Distinct())
            {
                if (!folderById.TryGetValue(folderId, out var folder))
                {
                    continue;
                }

                var inside = shared.Count(server => ServerFolders.Same(server.FolderName, folder.Name) || server.FolderName.Trim().StartsWith(folder.Name + "/", StringComparison.OrdinalIgnoreCase));
                rows.Add((1, folder.Name, Row(FolderIcon(folder.IconKey), folder.Name, (de ? $"Ordner · {inside} Verbindung(en)" : $"Folder · {inside} connection(s)") + (folder.InheritanceBroken ? (de ? " · erbt nicht" : " · does not inherit") : ""), AdminConnectionsUrl("folder:" + folder.Name), AccessResolver.FolderReasons(user, folder.Name).Where(reason => reason.Kind == AccessReasonKind.Grant), "folder")));
            }

            foreach (var serverId in reaching.Where(entry => entry.Grant.TargetKind == AccessTargetKind.Server).Select(entry => entry.Grant.TargetId).Distinct())
            {
                if (shared.FirstOrDefault(server => server.Id == serverId) is not { } server)
                {
                    continue;
                }

                rows.Add((2, server.Name, Row(ServerIcon(server, "small"), server.Name, ServerProtocolLabel(server.Protocol) + (string.IsNullOrWhiteSpace(server.FolderName) ? "" : " · " + server.FolderName), AdminConnectionsUrl("server:" + server.Id), AccessResolver.Reasons(user, server).Where(reason => reason.Kind == AccessReasonKind.Grant), "server")));
            }
        }

        var reachHtml = string.Join("", rows.OrderBy(row => row.Order).ThenBy(row => row.Sort, StringComparer.OrdinalIgnoreCase).Select(row => row.Html));
        var summary = user.IsAdmin
            ? (de ? "Darf als Administrator alles." : "May do everything as an administrator.")
            : de
                ? $"Darf {usable} von {shared.Count} gemeinsamen Verbindungen benutzen{(user.CanManageServers ? " (alle bearbeiten: Recht „Server verwalten“)" : "")}{(own > 0 ? $", dazu {own} eigene" : "")}."
                : $"May use {usable} of {shared.Count} shared connections{(user.CanManageServers ? " (edit all of them: right \"Manage servers\")" : "")}{(own > 0 ? $", plus {own} of their own" : "")}.";
        var accessCard = $$"""
            <section class="admdetail-section" data-admp-access>
                <h3>{{Icon("shield")}}{{(de ? "Was darf " + PersonName(user) + "?" : "What may " + PersonName(user) + " do?")}}</h3>
                <p class="admp-summary" data-admp-summary>{{E(summary)}}</p>
                {{(user.IsAdmin ? "" : $"""<ul class="acc-list">{(reachHtml.Length == 0 ? $"""<li class="acc-empty muted">{E(de ? "Nirgends freigegeben - weder direkt noch über eine Gruppe." : "Given nowhere - neither directly nor through a group.")}</li>""" : reachHtml)}</ul>""")}}
                <p class="muted admp-note">{{(de
                    ? "Freigaben gibt und nimmt man am Ordner oder an der Verbindung (Verbindungen → Zugriff) - oder einer Gruppe. Einzelne direkte Rechte und Regeln für Dateien stehen unter „Bearbeiten“."
                    : "Entries are given and taken on the folder or the connection (Connections → Access) - or to a group. Single direct rights and file rules are under \"Edit\".")}}</p>
            </section>
            """;

        var delete = currentUser.Id == user.Id
            ? ""
            : $$"""<form method="post" action="/admin/users/{{user.Id}}/delete" data-confirm="{{A(de ? $"Benutzer „{user.UserName}“ wirklich löschen?" : $"Delete the user \"{user.UserName}\"?")}}">{{Csrf(context)}}<button type="submit" class="danger" data-admp-delete>{{Icon("trash")}}{{(de ? "Löschen" : "Delete")}}</button></form>""";
        return $$"""
            <div class="admdetail-head admp-head">
                {{PersonAvatar(user, "large")}}
                <div>
                    <p class="eyebrow">{{(de ? "Benutzer" : "User")}} · {{E(user.UserName)}}</p>
                    <h2>{{E(PersonName(user))}}</h2>
                    <div class="admp-badges">{{string.Join("", badges)}}</div>
                </div>
            </div>
            <div class="admdetail-actions">
                <a class="button primary" href="/admin/users/{{user.Id}}" data-admp-edit>{{Icon("edit")}}{{(de ? "Bearbeiten" : "Edit")}}</a>
                <a class="button" href="/admin/users/{{user.Id}}#password">{{Icon("key")}}{{(de ? "Passwort setzen" : "Set password")}}</a>
                <span class="spacer"></span>
                {{delete}}
            </div>
            <section class="admdetail-section"><h3>{{Icon("list")}}{{(de ? "Übersicht" : "Overview")}}</h3><dl class="admdetail-facts">{{string.Join("", facts.Select(fact => $"<div><dt>{E(fact.Label)}</dt><dd>{E(fact.Value)}</dd></div>"))}}</dl></section>
            <section class="admdetail-section" data-admp-user-groups><h3>{{Icon("users")}}{{(de ? $"Gruppen · {mine.Count}" : $"Groups · {mine.Count}")}}</h3>{{groupsCard}}</section>
            {{accessCard}}
            """;
    }

    private static string GroupNote(UserGroup group, bool de)
    {
        var members = group.MemberIds.Count;
        var note = de ? $"{members} Mitglied{(members == 1 ? "" : "er")}" : $"{members} member{(members == 1 ? "" : "s")}";
        if (group.ExternalGroups.Count > 0)
        {
            note += de ? " · SSO" : " · SSO";
        }

        return note;
    }

    // ---------------------------------------------------------------- groups

    private static string GroupsAdminPanel(
        HttpContext context,
        IReadOnlyList<UserGroup> groups,
        IReadOnlyList<MatgateUser> users,
        IReadOnlyList<AccessGrant> grants,
        IReadOnlyList<ServerEndpoint> servers,
        IReadOnlyList<FolderDefinition> folders)
    {
        var de = Language(context) == "de";
        var selection = context.Request.Query["sel"].ToString();
        var chosen = Guid.TryParse(selection, out var chosenId) ? groups.FirstOrDefault(group => group.Id == chosenId) : null;
        int GrantCount(UserGroup group) => grants.Count(grant => grant.PrincipalKind == AccessPrincipalKind.Group && grant.PrincipalId == group.Id);
        var items = string.Join("", groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Select(group => $$"""
            <a class="admtree-node admp-node{{(chosen?.Id == group.Id ? " is-selected" : "")}}" href="{{A(AdminGroupsUrl(group.Id))}}" style="--depth: 0" data-admp-group="{{A(group.Name)}}" data-admp-search="{{A($"{group.Name} {group.Description} {string.Join(" ", group.ExternalGroups)}".ToLowerInvariant())}}">
                <span class="admp-avatar admp-avatar--group" aria-hidden="true">{{Icon("users")}}</span>
                <span class="admtree-text"><strong>{{E(group.Name)}}</strong><small>{{E(GroupNote(group, de))}}{{(GrantCount(group) == 0 ? "" : de ? $" · {GrantCount(group)} Freigabe(n)" : $" · {GrantCount(group)} entr{(GrantCount(group) == 1 ? "y" : "ies")}")}}</small></span>
            </a>
            """));
        var side = PeopleSide(
            de ? "Gruppen suchen ..." : "Search groups ...",
            "",
            $$"""<a class="button primary admtree-new" href="/admin/groups/new">{{Icon("plus")}}<span>{{(de ? "Neue Gruppe" : "New group")}}</span></a>""",
            de ? "Gruppen" : "Groups",
            items.Length == 0 ? $"""<p class="muted admp-empty-list">{E(de ? "Noch keine Gruppe." : "No group yet.")}</p>""" : items,
            de);

        string detail;
        if (chosen is null)
        {
            var memberCount = groups.SelectMany(group => group.MemberIds).Distinct().Count();
            detail = $$"""
                <div class="admdetail-head">
                    <span class="admdetail-icon">{{Icon("users")}}</span>
                    <div><p class="eyebrow">{{(de ? "Gruppen" : "Groups")}}</p><h2>{{(de ? "Alle Gruppen" : "All groups")}}</h2></div>
                </div>
                <div class="admdetail-stats" data-admp-stats>
                    <div><strong>{{groups.Count}}</strong><span>{{(de ? "Gruppen" : "groups")}}</span></div>
                    <div><strong>{{memberCount}}</strong><span>{{(de ? "Benutzer in Gruppen" : "users in groups")}}</span></div>
                    <div><strong>{{groups.Count(group => group.ExternalGroups.Count > 0)}}</strong><span>{{(de ? "mit SSO-Zuordnung" : "mapped to SSO")}}</span></div>
                    <div><strong>{{grants.Count(grant => grant.PrincipalKind == AccessPrincipalKind.Group)}}</strong><span>{{(de ? "Freigaben" : "entries")}}</span></div>
                </div>
                <p class="muted admdetail-hint">{{(de
                    ? "Gruppen geben ihren Mitgliedern Rechte - auf alle Verbindungen, auf ganze Ordner (mit allem darin, auch später) oder auf einzelne Verbindungen, jeweils mit einer Stufe von „Nur lesen“ bis „Verwalten“. Es zählt das Höchste. Links eine Gruppe wählen, um Mitglieder und Rechte direkt zu ändern."
                    : "Groups give their members rights - on all connections, on whole folders (with everything in them, also later) or on single connections, each with a level from \"Read only\" to \"Manage\". The highest counts. Pick a group on the left to change members and rights right there.")}}</p>
                """;
        }
        else
        {
            detail = $$"""<a class="button admdetail-back" href="{{A(AdminGroupsUrl())}}">{{Icon("arrow-left")}}{{(de ? "Alle Gruppen" : "All groups")}}</a>"""
                + GroupDetailPane(context, chosen, users, grants, servers, folders, de);
        }

        return $$"""
            <section class="admconn admpeople{{(chosen is null ? "" : " has-selection")}}" data-admin-groups>
                {{side}}
                <div class="admdetail" data-admdetail>{{detail}}</div>
            </section>
            {{AdminTreeStyles}}
            {{AccessStyles}}
            {{AdminPeopleStyles}}
            <script>{{AdminPeopleScript}}</script>
            """;
    }

    private static string GroupDetailPane(
        HttpContext context,
        UserGroup group,
        IReadOnlyList<MatgateUser> users,
        IReadOnlyList<AccessGrant> grants,
        IReadOnlyList<ServerEndpoint> servers,
        IReadOnlyList<FolderDefinition> folders,
        bool de)
    {
        var self = AdminGroupsUrl(group.Id);

        // ---- members
        var members = users.Where(user => group.MemberIds.Contains(user.Id)).OrderBy(PersonName, StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = users.Where(user => !user.IsAdmin && !group.MemberIds.Contains(user.Id)).OrderBy(PersonName, StringComparer.OrdinalIgnoreCase).ToList();
        var memberRows = string.Join("", members.Select(user => $$"""
            <li class="acc-entry" data-admp-member="{{A(user.UserName)}}">
                {{PersonAvatar(user)}}
                <span class="acc-who"><a href="{{A(AdminUsersUrl(user.Id))}}"><strong>{{E(PersonName(user))}}</strong></a><small>{{E(user.UserName)}}{{(user.ExternalLogins.Count > 0 ? " · SSO" : "")}}</small></span>
                <form method="post" action="/admin/groups/members">{{Csrf(context)}}{{ReturnField(self)}}<input type="hidden" name="group" value="{{group.Id}}"><input type="hidden" name="user" value="{{user.Id}}"><input type="hidden" name="op" value="remove"><button type="submit" class="icon-button acc-remove" title="{{A(de ? "Aus der Gruppe nehmen" : "Remove from the group")}}" aria-label="{{A(de ? "Aus der Gruppe nehmen" : "Remove from the group")}}" data-admp-remove-member>{{Icon("x")}}</button></form>
            </li>
            """));
        var addMember = candidates.Count == 0
            ? ""
            : $$"""
                <form method="post" action="/admin/groups/members" class="acc-add" data-admp-add-member>
                    {{Csrf(context)}}{{ReturnField(self)}}<input type="hidden" name="group" value="{{group.Id}}"><input type="hidden" name="op" value="add">
                    <select name="user" required aria-label="{{A(de ? "Benutzer" : "User")}}"><option value="">{{(de ? "Mitglied hinzufügen ..." : "Add a member ...")}}</option>{{string.Join("", candidates.Select(user => $"""<option value="{user.Id}">{E(PersonName(user))} ({E(user.UserName)})</option>"""))}}</select>
                    <button type="submit" class="primary">{{Icon("plus")}}{{(de ? "Hinzufügen" : "Add")}}</button>
                </form>
                """;
        var ssoNames = SsoGroupNames(context);
        var external = group.ExternalGroups.Count == 0
            ? ""
            : $$"""
                <div class="admp-sso" data-admp-sso>
                    <span class="admp-sso-label">{{Icon("globe")}}{{(de ? "Steht für die SSO-Gruppen" : "Stands for the SSO groups")}}</span>
                    <span class="admp-chips">{{string.Join("", group.ExternalGroups.Select(value => SsoGroupChip(value, ssoNames)))}}</span>
                    <small class="muted">{{(de ? "Wer sich darüber anmeldet, kommt automatisch hinein - und wieder heraus, wenn der Dienst die Gruppe nicht mehr nennt." : "Whoever signs in through it is put in automatically - and taken out again when the service no longer names the group.")}} <a href="/admin?tab=sso#sso-groups">{{(de ? "Ändern unter Anmeldung" : "Change under Sign-in")}}</a></small>
                </div>
                """;

        // ---- what the group may do: every entry a row with its level, saved at once
        var folderById = folders.ToDictionary(folder => folder.Id);
        var shared = servers.Where(server => server.OwnerUserId is null && server.Protocol != ServerProtocol.Local).ToList();
        var own = grants.Where(grant => grant.PrincipalKind == AccessPrincipalKind.Group && grant.PrincipalId == group.Id).ToList();
        (int Order, string Name, string Note, string Icon, string Href, string Key)? Describe(AccessGrant grant)
        {
            switch (grant.TargetKind)
            {
                case AccessTargetKind.All:
                    return (0, de ? "Alle Verbindungen" : "All connections", de ? $"{shared.Count} gemeinsame, auch künftige" : $"{shared.Count} shared, also future ones", Icon("server"), AdminConnectionsUrl("all"), "all");
                case AccessTargetKind.Folder when folderById.TryGetValue(grant.TargetId, out var folder):
                    var inside = shared.Count(server => ServerFolders.Same(server.FolderName, folder.Name) || server.FolderName.Trim().StartsWith(folder.Name + "/", StringComparison.OrdinalIgnoreCase));
                    return (1, folder.Name, (de ? $"Ordner · {inside} Verbindung(en)" : $"Folder · {inside} connection(s)") + (folder.InheritanceBroken ? (de ? " · erbt nicht" : " · does not inherit") : ""), FolderIcon(folder.IconKey), AdminConnectionsUrl("folder:" + folder.Name), "folder:" + folder.Name);
                case AccessTargetKind.Server when shared.FirstOrDefault(server => server.Id == grant.TargetId) is { } server:
                    return (2, server.Name, ServerProtocolLabel(server.Protocol) + (string.IsNullOrWhiteSpace(server.FolderName) ? "" : " · " + server.FolderName), ServerIcon(server, "small"), AdminConnectionsUrl("server:" + server.Id), "server:" + server.Id);
                default:
                    return null;
            }
        }

        var described = own.Select(grant => (Grant: grant, Info: Describe(grant))).Where(entry => entry.Info is not null).Select(entry => (entry.Grant, Info: entry.Info!.Value)).OrderBy(entry => entry.Info.Order).ThenBy(entry => entry.Info.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var grantRows = string.Join("", described.Select(entry => $$"""
            <li class="acc-entry" data-admp-grant="{{A(entry.Info.Key)}}">
                <span class="acc-avatar acc-avatar--group" aria-hidden="true">{{entry.Info.Icon}}</span>
                <span class="acc-who"><a href="{{A(entry.Info.Href)}}"><strong>{{E(entry.Info.Name)}}</strong></a><small>{{E(entry.Info.Note)}}</small></span>
                <form method="post" action="/admin/groups/{{group.Id}}/grant" class="admp-grant-form" data-admp-autosave>
                    {{Csrf(context)}}{{ReturnField(self)}}<input type="hidden" name="target" value="{{A(entry.Info.Key)}}">
                    {{AccessLevelSelect("level", entry.Grant.Level, de, withNone: false, " data-admp-level")}}
                    <button type="submit" class="icon-button acc-remove" name="level" value="None" title="{{A(de ? "Entfernen" : "Remove")}}" aria-label="{{A(de ? "Entfernen" : "Remove")}}" data-admp-remove-grant>{{Icon("x")}}</button>
                </form>
            </li>
            """));
        var taken = described.Select(entry => entry.Info.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folderNames = ServerFolders.Names(shared)
            .Concat(folders.Where(folder => folder.OwnerUserId is null).Select(folder => folder.Name))
            .SelectMany(name => AccessResolver.PathAndParents(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var targetOptions =
            (taken.Contains("all") ? "" : $"""<option value="all">{E(de ? "Alle Verbindungen" : "All connections")}</option>""")
            + (folderNames.Count == 0 ? "" : $"""<optgroup label="{A(de ? "Ordner" : "Folders")}">{string.Join("", folderNames.Where(name => !taken.Contains("folder:" + name)).Select(name => $"""<option value="folder:{A(name)}">{E(name)}</option>"""))}</optgroup>""")
            + (shared.Count == 0 ? "" : $"""<optgroup label="{A(de ? "Verbindungen" : "Connections")}">{string.Join("", shared.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).Where(server => !taken.Contains("server:" + server.Id)).Select(server => $"""<option value="server:{server.Id}">{E(server.Name)}{(string.IsNullOrWhiteSpace(server.FolderName) ? "" : " · " + E(server.FolderName))}</option>"""))}</optgroup>""");
        var legend = string.Join("", GrantableLevels.Select(level => $"""<span class="acc-legend-item">{AccessLevelBadge(level, de)}<small>{E(AccessLevelHint(level, de))}</small></span>"""));

        return $$"""
            <div class="admdetail-head admp-head">
                <span class="admdetail-icon">{{Icon("users")}}</span>
                <div>
                    <p class="eyebrow">{{(de ? "Gruppe" : "Group")}}</p>
                    <h2>{{E(group.Name)}}</h2>
                    {{(string.IsNullOrWhiteSpace(group.Description) ? "" : $"""<p class="muted admp-description">{E(group.Description)}</p>""")}}
                </div>
            </div>
            <div class="admdetail-actions">
                <a class="button primary" href="/admin/groups/{{group.Id}}" data-admp-edit>{{Icon("edit")}}{{(de ? "Bearbeiten" : "Edit")}}</a>
                <span class="spacer"></span>
                <form method="post" action="/admin/groups/{{group.Id}}/delete" data-confirm="{{A(de ? $"Gruppe „{group.Name}“ löschen? Ihre Mitglieder verlieren, was sie nur über diese Gruppe hatten." : $"Delete the group \"{group.Name}\"? Its members lose what they had only through it.")}}">{{Csrf(context)}}<button type="submit" class="danger" data-admp-delete>{{Icon("trash")}}{{(de ? "Löschen" : "Delete")}}</button></form>
            </div>
            <section class="admdetail-section" data-admp-members>
                <h3>{{Icon("user")}}{{(de ? $"Mitglieder · {members.Count}" : $"Members · {members.Count}")}}</h3>
                {{external}}
                <ul class="acc-list">{{(memberRows.Length == 0 ? $"""<li class="acc-empty muted">{E(de ? "Noch keine Mitglieder." : "No members yet.")}</li>""" : memberRows)}}</ul>
                {{addMember}}
                <p class="muted admp-note">{{(de ? "Administratoren dürfen ohnehin alles und werden deshalb nicht angeboten." : "Administrators may do everything anyway and are therefore not offered.")}}</p>
            </section>
            <section class="admdetail-section" data-admp-grants>
                <h3>{{Icon("shield")}}{{(de ? $"Darf · {described.Count}" : $"May · {described.Count}")}}</h3>
                <p class="muted admp-note">{{(de ? "Ein Ordner gilt mit allem darin, auch was später hineinkommt. Eine geänderte Stufe ist sofort gespeichert." : "A folder holds with everything in it, also what is filed there later. A changed level is saved at once.")}}</p>
                <ul class="acc-list">{{(grantRows.Length == 0 ? $"""<li class="acc-empty muted">{E(de ? "Diese Gruppe darf noch nichts." : "This group may do nothing yet.")}</li>""" : grantRows)}}</ul>
                <form method="post" action="/admin/groups/{{group.Id}}/grant" class="acc-add" data-admp-add-grant>
                    {{Csrf(context)}}{{ReturnField(self)}}
                    <select name="target" required aria-label="{{A(de ? "Worauf" : "On what")}}"><option value="">{{(de ? "Recht geben auf ..." : "Give a right on ...")}}</option>{{targetOptions}}</select>
                    {{AccessLevelSelect("level", AccessLevel.Connect, de, withNone: false)}}
                    <button type="submit" class="primary">{{Icon("plus")}}{{(de ? "Hinzufügen" : "Add")}}</button>
                </form>
                <div class="acc-legend">{{legend}}</div>
            </section>
            """;
    }

    private const string AdminPeopleScript = """
        (() => {
            document.querySelectorAll('.admpeople').forEach(root => {
                if (root.dataset.wired) { return; }
                root.dataset.wired = '1';
                const filter = root.querySelector('[data-admp-filter]');
                if (filter) {
                    filter.addEventListener('input', () => {
                        const wanted = filter.value.trim().toLowerCase();
                        let any = false;
                        root.querySelectorAll('[data-admp-search]').forEach(node => {
                            const hit = !wanted || node.dataset.admpSearch.includes(wanted);
                            node.hidden = !hit;
                            any = any || hit;
                        });
                        root.querySelector('[data-admp-none]').hidden = !wanted || any;
                    });
                }
                // A level changed: saved at once.
                root.querySelectorAll('[data-admp-autosave] [data-admp-level]').forEach(select => select.addEventListener('change', () => select.form.requestSubmit()));
                const chosen = root.querySelector('.admp-node.is-selected');
                if (chosen) { chosen.scrollIntoView({ block: 'nearest' }); }
            });
        })();
        """;

    private const string AdminPeopleStyles = """
        <style>
            .admp-avatar { align-items: center; background: color-mix(in srgb, var(--accent) 16%, var(--surface)); border-radius: 50%; color: var(--accent); display: inline-flex; flex: 0 0 auto; font-size: 12px; font-weight: 700; height: 30px; justify-content: center; letter-spacing: .02em; width: 30px; }
            .admp-avatar.is-admin { background: color-mix(in srgb, #c2410c 20%, var(--surface)); color: #c2410c; }
            .admp-avatar--group { border-radius: 10px; }
            .admp-avatar--group .icon { height: 16px; width: 16px; }
            .admp-avatar--large { font-size: 18px; height: 52px; width: 52px; }
            .acc-entry .admp-avatar { height: 34px; width: 34px; }
            .admp-node { min-height: 44px; }
            .admp-tag-admin { border-color: color-mix(in srgb, #c2410c 40%, var(--line)); color: #c2410c; }
            .admp-tag-off { border-color: color-mix(in srgb, var(--danger, #b3261e) 40%, var(--line)); color: var(--danger, #b3261e); }
            .admp-empty-list { padding: 10px 12px; }
            .admp-head { align-items: flex-start; }
            .admp-badges { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 6px; }
            .admp-badge { align-items: center; background: var(--surface-2, var(--bg)); border: 1px solid var(--line); border-radius: 999px; color: var(--muted); display: inline-flex; font-size: 12px; font-weight: 600; gap: 5px; padding: 3px 10px; }
            .admp-badge .icon { height: 13px; width: 13px; }
            .admp-badge.is-accent { border-color: color-mix(in srgb, #c2410c 40%, var(--line)); color: #c2410c; }
            .admp-badge.is-ok { border-color: color-mix(in srgb, var(--accent) 40%, var(--line)); color: var(--accent); }
            .admp-badge.is-off { border-color: color-mix(in srgb, var(--danger, #b3261e) 40%, var(--line)); color: var(--danger, #b3261e); }
            .admp-description { margin: 4px 0 0; }
            .admp-note { font-size: 12.5px; margin: 0; }
            .admp-summary { font-weight: 600; margin: 0; }
            .admp-grant-form { align-items: center; display: flex; flex: 0 0 auto; gap: 6px; margin: 0; }
            .admp-grant-form .access-level-select { min-height: 34px; width: 150px; }
            .acc-entry form { margin: 0; }
            .acc-add select[name="group"], .acc-add select[name="user"], .acc-add select[name="target"] { flex: 1 1 240px; min-height: 36px; }
            .admp-sso { background: var(--surface-2, var(--bg)); border-radius: 12px; display: grid; gap: 6px; padding: 10px 12px; }
            .admp-sso-label { align-items: center; display: inline-flex; font-size: 13px; font-weight: 600; gap: 6px; }
            .admp-sso-label .icon { color: var(--accent); height: 15px; width: 15px; }
            .admp-chips { display: flex; flex-wrap: wrap; gap: 4px; }
            .admp-sso small { font-size: 12px; }
            .admp-reach .acc-reasons { padding-left: 46px; }
            @media (max-width: 640px) {
                .admp-grant-form { flex: 1 1 100%; }
                .admp-grant-form .access-level-select { flex: 1 1 auto; }
                .admp-reach .acc-reasons { padding-left: 0; }
            }
        </style>
        """;
}
