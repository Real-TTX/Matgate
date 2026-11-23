using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// Who may do what: the page of one connection, one folder or of all shared connections, with what is given
// there (to change) and everyone it reaches in the end, each with the reason - so that nobody has to guess.
public sealed partial class HtmlViews
{
    // What the access page is about.
    public sealed record AccessPageTarget(AccessTargetKind Kind, Guid Id, string Name, string FolderPath, bool InheritanceBroken, string BackUrl);

    // One person in "who has access": the level and every reason for it.
    public sealed record AccessPageRow(MatgateUser User, AccessLevel Level, IReadOnlyList<AccessReason> Reasons);

    public static string AccessLevelLabel(AccessLevel level, bool de) => level switch
    {
        AccessLevel.ReadOnly => de ? "Nur lesen" : "Read only",
        AccessLevel.Connect => de ? "Verbinden" : "Connect",
        AccessLevel.Edit => de ? "Bearbeiten" : "Edit",
        AccessLevel.Manage => de ? "Verwalten" : "Manage",
        _ => de ? "Kein Zugriff" : "No access",
    };

    public static string AccessLevelHint(AccessLevel level, bool de) => level switch
    {
        AccessLevel.ReadOnly => de ? "Öffnen, aber nichts ändern: Bildschirm und Terminal nur ansehen, Dateien und Datenbanken nur lesen." : "Open, but change nothing: screen and terminal only to watch, files and databases only to read.",
        AccessLevel.Connect => de ? "Öffnen und darin arbeiten." : "Open and work in it.",
        AccessLevel.Edit => de ? "Dazu die Verbindung selbst ändern (Adresse, Anmeldung, Ordner)." : "Also change the connection itself (address, login, folder).",
        AccessLevel.Manage => de ? "Dazu bestimmen, wer sonst darf." : "Also decide who else may.",
        _ => "",
    };

    private static readonly AccessLevel[] GrantableLevels = [AccessLevel.ReadOnly, AccessLevel.Connect, AccessLevel.Edit, AccessLevel.Manage];

    // A select of the levels; "none" first where the entry can also be taken away.
    public static string AccessLevelSelect(string name, AccessLevel current, bool de, bool withNone, string extra = "")
    {
        var options = (withNone ? $"""<option value="none"{(current == AccessLevel.None ? " selected" : "")}>{E(de ? "—" : "—")}</option>""" : "")
            + string.Join("", GrantableLevels.Select(level => $"""<option value="{level}"{(level == current ? " selected" : "")} title="{A(AccessLevelHint(level, de))}">{E(AccessLevelLabel(level, de))}</option>"""));
        return $"""<select name="{A(name)}" class="access-level-select"{extra}>{options}</select>""";
    }

    public static string AccessLevelBadge(AccessLevel level, bool de) =>
        $"""<span class="access-badge access-{level.ToString().ToLowerInvariant()}" title="{A(AccessLevelHint(level, de))}">{E(AccessLevelLabel(level, de))}</span>""";

    // "through the group IT, on the folder Servers (inherited)" - one reason in words.
    public static string AccessReasonText(AccessReason reason, bool de)
    {
        switch (reason.Kind)
        {
            case AccessReasonKind.Administrator:
                return de ? "Administrator" : "Administrator";
            case AccessReasonKind.Owner:
                return de ? "Eigene Verbindung" : "Own connection";
            case AccessReasonKind.ServerManager:
                return de ? "Recht „Server verwalten“" : "Right \"Manage servers\"";
        }

        var who = reason.Group is null ? (de ? "direkt" : "directly") : (de ? $"Gruppe {reason.Group.Name}" : $"group {reason.Group.Name}");
        var where = reason.Target switch
        {
            AccessTargetKind.Server => de ? "auf dieser Verbindung" : "on this connection",
            AccessTargetKind.Folder => de ? $"auf Ordner {reason.FolderPath}" : $"on folder {reason.FolderPath}",
            _ => de ? "auf alle Verbindungen" : "on all connections",
        };
        return $"{who} · {where}{(reason.Inherited ? (de ? " (geerbt)" : " (inherited)") : "")}";
    }

    public static string AccessReasonsHtml(IEnumerable<AccessReason> reasons, bool de) =>
        string.Join("", reasons
            .OrderByDescending(reason => reason.Level)
            .Select(reason => $"""<span class="access-reason">{AccessLevelBadge(reason.Level, de)} {E(AccessReasonText(reason, de))}</span>"""));

    // The access of one target - on its own page, or inside the administration's tree. Two cards: what is given
    // here (each entry a row: who, its level - changed and saved at once - and away with it; a row to add one; for a
    // folder the switch "inherit from above"), and who has access in the end, with every reason as a small mark.
    public string AccessEditor(
        HttpContext context,
        AccessPageTarget target,
        IReadOnlyList<AccessGrant> grants,
        IReadOnlyList<MatgateUser> users,
        IReadOnlyList<UserGroup> groups,
        IReadOnlyList<AccessPageRow> rows,
        string returnUrl)
    {
        var de = Language(context) == "de";
        string UserName(MatgateUser user) => string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
        string Who(AccessGrant grant)
        {
            if (grant.PrincipalKind == AccessPrincipalKind.Group)
            {
                var group = groups.FirstOrDefault(candidate => candidate.Id == grant.PrincipalId);
                return group is null ? "?" : group.Name;
            }

            var user = users.FirstOrDefault(candidate => candidate.Id == grant.PrincipalId);
            return user is null ? "?" : UserName(user);
        }

        string Note(AccessGrant grant)
        {
            if (grant.PrincipalKind == AccessPrincipalKind.Group)
            {
                var members = groups.FirstOrDefault(candidate => candidate.Id == grant.PrincipalId)?.MemberIds.Count ?? 0;
                return de ? $"Gruppe · {members} Mitglied{(members == 1 ? "" : "er")}" : $"Group · {members} member{(members == 1 ? "" : "s")}";
            }

            var user = users.FirstOrDefault(candidate => candidate.Id == grant.PrincipalId);
            return (de ? "Benutzer" : "User") + (user is null ? "" : " · " + user.UserName);
        }

        var hidden = $"""
            {Csrf(context)}
            <input type="hidden" name="kind" value="{target.Kind}">
            <input type="hidden" name="id" value="{target.Id}">
            <input type="hidden" name="folder" value="{A(target.FolderPath)}">
            <input type="hidden" name="return" value="{A(returnUrl)}">
            """;
        var entries = string.Join("", grants
            .OrderBy(grant => grant.PrincipalKind)
            .ThenBy(Who, StringComparer.OrdinalIgnoreCase)
            .Select(grant => $$"""
                <li class="acc-entry" data-grant-row="{{grant.Id}}">
                    <span class="acc-avatar acc-avatar--{{(grant.PrincipalKind == AccessPrincipalKind.Group ? "group" : "user")}}" aria-hidden="true">{{Icon(grant.PrincipalKind == AccessPrincipalKind.Group ? "users" : "user")}}</span>
                    <span class="acc-who"><strong>{{E(Who(grant))}}</strong><small>{{E(Note(grant))}}</small></span>
                    {{AccessLevelSelect("level_" + grant.Id.ToString("N"), grant.Level, de, withNone: false, " data-access-autosave")}}
                    <button type="submit" class="icon-button acc-remove" name="level_{{grant.Id:N}}" value="none" title="{{A(de ? "Entfernen" : "Remove")}}" aria-label="{{A(de ? "Entfernen" : "Remove")}}" data-access-remove>{{Icon("x")}}</button>
                </li>
                """));
        var principalOptions =
            (groups.Count == 0 ? "" : $"""<optgroup label="{A(de ? "Gruppen" : "Groups")}">{string.Join("", groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Select(group => $"""<option value="g:{group.Id}">{E(group.Name)}</option>"""))}</optgroup>""")
            + $"""<optgroup label="{A(de ? "Benutzer" : "Users")}">{string.Join("", users.Where(user => !user.IsAdmin).OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase).Select(user => $"""<option value="u:{user.Id}">{E(UserName(user))} ({E(user.UserName)})</option>"""))}</optgroup>""";
        var inherit = target.Kind != AccessTargetKind.Folder
            ? ""
            : $$"""
                <label class="acc-inherit">
                    <span class="acc-switch"><input type="checkbox" name="inherit" data-access-inherit data-access-autosave{{Checked(!target.InheritanceBroken)}}><span class="acc-switch-track" aria-hidden="true"></span></span>
                    <span><strong>{{(de ? "Rechte von oben erben" : "Inherit rights from above")}}</strong><small>{{(de
                        ? "Freigaben auf übergeordneten Ordnern und auf „Alle Verbindungen“ gelten auch hier. Aus: hier gilt nur, was auf diesem Ordner und darunter steht."
                        : "Entries on parent folders and on \"All connections\" hold here as well. Off: only what is on this folder and below holds here.")}}</small></span>
                </label>
                """;
        var lead = target.Kind switch
        {
            AccessTargetKind.Server => de ? "Freigaben auf diese Verbindung. Was auf ihrem Ordner steht, gilt zusätzlich." : "Entries on this connection. What is on its folder holds as well.",
            AccessTargetKind.Folder => de ? "Gilt für alles in diesem Ordner - Verbindungen und Unterordner, auch künftige." : "Holds for everything in this folder - connections and subfolders, also future ones.",
            _ => de ? "Gilt für jede gemeinsame Verbindung, auch künftige - außer in Ordnern, die nicht erben." : "Holds for every shared connection, also future ones - except in folders that do not inherit.",
        };
        var legend = string.Join("", GrantableLevels.Select(level => $"""<span class="acc-legend-item">{AccessLevelBadge(level, de)}<small>{E(AccessLevelHint(level, de))}</small></span>"""));
        var effective = string.Join("", rows
            .OrderByDescending(row => row.Level)
            .ThenBy(row => row.User.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(row => $$"""
                <li class="acc-person" data-effective-user="{{A(row.User.UserName)}}" data-effective-level="{{row.Level}}">
                    <span class="acc-avatar acc-avatar--user" aria-hidden="true">{{Icon("user")}}</span>
                    <span class="acc-who"><a href="/admin/users/{{row.User.Id}}"><strong>{{E(UserName(row.User))}}</strong></a><small>{{E(row.User.UserName)}}</small></span>
                    {{AccessLevelBadge(row.Level, de)}}
                    <span class="acc-reasons access-reasons">{{string.Join("", row.Reasons.OrderByDescending(reason => reason.Level).Select(reason => $"""<span class="acc-reason access-reason" title="{A(AccessLevelLabel(reason.Level, de))}">{E(AccessReasonText(reason, de))}</span>"""))}}</span>
                </li>
                """));

        return $$"""
            <div class="acc" data-access-page="{{target.Kind}}">
                <form method="post" action="/admin/access" class="acc-card" id="access-form" data-access-form>
                    {{hidden}}
                    <div class="acc-card-head">
                        <h3>{{Icon("shield")}}{{(de ? "Freigaben" : "Entries")}}</h3>
                        <p class="muted">{{E(lead)}}</p>
                    </div>
                    {{inherit}}
                    <ul class="acc-list" data-access-given>{{(entries.Length == 0 ? $"""<li class="acc-empty muted">{E(de ? "Hier ist nichts freigegeben." : "Nothing is given here.")}</li>""" : entries)}}</ul>
                    <div class="acc-add">
                        <select name="newPrincipal" data-access-new-principal aria-label="{{A(de ? "Gruppe oder Benutzer" : "Group or user")}}"><option value="">{{(de ? "Gruppe oder Benutzer hinzufügen ..." : "Add a group or user ...")}}</option>{{principalOptions}}</select>
                        {{AccessLevelSelect("newLevel", AccessLevel.Connect, de, withNone: false, " data-access-new-level")}}
                        <button type="submit" name="action" value="add" class="primary" data-access-add>{{Icon("plus")}}{{(de ? "Hinzufügen" : "Add")}}</button>
                        <button type="submit" name="action" value="save" hidden data-access-save>{{T(context, "Save")}}</button>
                    </div>
                    <div class="acc-legend">{{legend}}</div>
                </form>
                <section class="acc-card">
                    <div class="acc-card-head">
                        <h3>{{Icon("users")}}{{(de ? "Wer hat Zugriff?" : "Who has access?")}}</h3>
                        <p class="muted">{{(de ? "Alle, die am Ende dürfen, mit jedem Grund. Es gilt der höchste." : "Everyone who may in the end, with every reason. The highest holds.")}}</p>
                    </div>
                    <ul class="acc-list" data-access-effective>{{(effective.Length == 0 ? $"""<li class="acc-empty muted">{E(de ? "Niemand außer den Administratoren." : "Nobody but the administrators.")}</li>""" : effective)}}</ul>
                </section>
            </div>
            {{AccessStyles}}
            <script>
                (() => {
                    // A level changed, or the switch: saved at once.
                    document.querySelectorAll('[data-access-form]').forEach(form => {
                        if (form.dataset.wired) { return; }
                        form.dataset.wired = '1';
                        form.querySelectorAll('[data-access-autosave]').forEach(field => field.addEventListener('change', () => {
                            const save = form.querySelector('[data-access-save]');
                            form.requestSubmit(save);
                        }));
                    });
                })();
            </script>
            """;
    }

    public string AccessPage(
        HttpContext context,
        MatgateUser currentUser,
        AccessPageTarget target,
        IReadOnlyList<AccessGrant> grants,
        IReadOnlyList<MatgateUser> users,
        IReadOnlyList<UserGroup> groups,
        IReadOnlyList<AccessPageRow> rows)
    {
        var de = Language(context) == "de";
        var kindLabel = target.Kind switch
        {
            AccessTargetKind.Server => de ? "Verbindung" : "Connection",
            AccessTargetKind.Folder => de ? "Ordner" : "Folder",
            _ => de ? "Alle Verbindungen" : "All connections",
        };
        var self = context.Request.Path + context.Request.QueryString;
        var body = $$"""
            <div class="conn-page access-page">
                <section class="page-head conn-head">
                    <span class="conn-head-icon" aria-hidden="true">{{Icon("shield")}}</span>
                    <div>
                        <p class="eyebrow">{{(de ? "Zugriff" : "Access")}} · {{E(kindLabel)}}</p>
                        <h1>{{E(target.Name)}}</h1>
                    </div>
                    <span class="spacer"></span>
                    <a class="button" href="{{A(target.BackUrl)}}">{{Icon("arrow-left")}}{{T(context, "Back")}}</a>
                </section>
                {{AccessEditor(context, target, grants, users, groups, rows, self)}}
            </div>
            """;

        return Layout(context, currentUser, (de ? "Zugriff: " : "Access: ") + target.Name, body);
    }

    private const string AccessStyles = """
        <style>
            .acc { display: grid; gap: 16px; }
            .acc-card { background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius, 14px); display: grid; gap: 12px; padding: 16px 18px; }
            .acc-card-head h3 { align-items: center; display: flex; font-size: 16px; gap: 8px; margin: 0 0 2px; }
            .acc-card-head h3 .icon { color: var(--accent); height: 18px; width: 18px; }
            .acc-card-head p { font-size: 13px; margin: 0; }
            .acc-list { display: grid; gap: 6px; list-style: none; margin: 0; padding: 0; }
            .acc-entry, .acc-person { align-items: center; background: var(--surface-2, var(--bg)); border: 1px solid var(--line); border-radius: 12px; display: flex; gap: 12px; padding: 8px 10px; }
            .acc-person { align-items: flex-start; flex-wrap: wrap; }
            .acc-avatar { align-items: center; border-radius: 10px; display: inline-flex; flex: 0 0 auto; height: 34px; justify-content: center; width: 34px; }
            .acc-avatar .icon { height: 17px; width: 17px; }
            .acc-avatar--group { background: color-mix(in srgb, var(--accent) 16%, transparent); color: var(--accent); }
            .acc-avatar--user { background: color-mix(in srgb, #8a8f98 18%, transparent); color: var(--text); }
            .acc-who { display: grid; flex: 1 1 160px; min-width: 0; }
            .acc-who strong, .acc-who a { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .acc-who a { color: inherit; text-decoration: none; }
            .acc-who a:hover strong { text-decoration: underline; }
            .acc-who small { color: var(--muted); font-size: 12px; }
            .acc-entry .access-level-select { flex: 0 0 auto; min-height: 34px; width: 150px; }
            .acc-remove { flex: 0 0 auto; }
            .acc-remove:hover { color: var(--danger, #b3261e); }
            .acc-empty { border: 1px dashed var(--line); border-radius: 12px; padding: 12px; text-align: center; }
            .acc-add { align-items: center; display: flex; flex-wrap: wrap; gap: 8px; }
            .acc-add select[data-access-new-principal] { flex: 1 1 240px; min-height: 36px; }
            .acc-add .access-level-select { min-height: 36px; width: 150px; }
            .acc-add [data-access-save] { display: none !important; }
            .acc-legend { border-top: 1px solid var(--line); display: grid; gap: 8px; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); padding-top: 12px; }
            .acc-legend-item { align-content: start; display: grid; gap: 4px; justify-items: start; }
            .acc-legend-item .access-badge { height: auto; width: fit-content; }
            .acc-legend-item small { color: var(--muted); font-size: 12px; line-height: 1.35; }
            .acc-inherit { align-items: flex-start; background: var(--surface-2, var(--bg)); border-radius: 12px; cursor: pointer; display: flex; gap: 12px; padding: 10px 12px; }
            .acc-inherit > span:last-child { display: grid; gap: 2px; }
            .acc-inherit small { color: var(--muted); font-size: 12.5px; }
            .acc-switch { flex: 0 0 auto; margin-top: 2px; position: relative; }
            .acc-switch input { height: 1px; opacity: 0; position: absolute; width: 1px; }
            .acc-switch-track { background: var(--line); border-radius: 999px; display: block; height: 22px; position: relative; transition: background .15s; width: 40px; }
            .acc-switch-track::after { background: #fff; border-radius: 50%; box-shadow: 0 1px 3px rgba(0,0,0,.25); content: ""; height: 16px; left: 3px; position: absolute; top: 3px; transition: transform .15s; width: 16px; }
            .acc-switch input:checked + .acc-switch-track { background: var(--accent); }
            .acc-switch input:checked + .acc-switch-track::after { transform: translateX(18px); }
            .acc-switch input:focus-visible + .acc-switch-track { box-shadow: var(--focus-ring); }
            .acc-person .access-badge { flex: 0 0 auto; margin-top: 8px; }
            .acc-reasons { display: flex; flex: 1 1 100%; flex-wrap: wrap; gap: 4px; padding-left: 46px; }
            .acc-reason { background: var(--surface); border: 1px solid var(--line); border-radius: 999px; color: var(--muted); font-size: 12px; padding: 2px 9px; }
            .access-badge { border-radius: 999px; display: inline-block; font-size: 12px; font-weight: 600; line-height: 1; padding: 5px 10px; white-space: nowrap; }
            .access-readonly { background: color-mix(in srgb, #8a8f98 18%, var(--surface)); color: var(--text); }
            .access-connect { background: color-mix(in srgb, var(--accent) 16%, var(--surface)); color: var(--text); }
            .access-edit { background: color-mix(in srgb, #d08a1a 20%, var(--surface)); color: var(--text); }
            .access-manage { background: color-mix(in srgb, #c2410c 22%, var(--surface)); color: var(--text); }
            .access-none { background: var(--surface-2, var(--surface)); color: var(--muted); }
            .access-level-select { max-width: 220px; min-width: 130px; width: auto; }
            .access-reasons { display: flex; flex-direction: column; gap: 4px; }
            .access-reason { align-items: center; display: inline-flex; flex-wrap: wrap; font-size: 13px; gap: 6px; }
            .acc-reasons.access-reasons { flex-direction: row; }
            @media (max-width: 640px) {
                .acc-entry { flex-wrap: wrap; }
                .acc-entry .access-level-select { flex: 1 1 auto; }
                .acc-reasons { padding-left: 0; }
            }
        </style>
        """;
}
