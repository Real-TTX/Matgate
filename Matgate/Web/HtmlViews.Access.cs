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
        string UserName(MatgateUser user) => string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : $"{user.DisplayName} ({user.UserName})";
        string PrincipalName(AccessGrant grant) => grant.PrincipalKind == AccessPrincipalKind.Group
            ? groups.FirstOrDefault(group => group.Id == grant.PrincipalId)?.Name ?? "?"
            : users.FirstOrDefault(user => user.Id == grant.PrincipalId) is { } user ? UserName(user) : "?";

        var kindLabel = target.Kind switch
        {
            AccessTargetKind.Server => de ? "Verbindung" : "Connection",
            AccessTargetKind.Folder => de ? "Ordner" : "Folder",
            _ => de ? "Alle Verbindungen" : "All connections",
        };
        var given = grants
            .OrderBy(grant => grant.PrincipalKind)
            .ThenBy(PrincipalName, StringComparer.OrdinalIgnoreCase)
            .Select(grant => $$"""
                <tr data-grant-row="{{grant.Id}}">
                    <td><span class="server-name-cell">{{Icon(grant.PrincipalKind == AccessPrincipalKind.Group ? "users" : "user")}}<strong>{{E(PrincipalName(grant))}}</strong></span></td>
                    <td class="muted">{{(grant.PrincipalKind == AccessPrincipalKind.Group ? (de ? "Gruppe" : "Group") : (de ? "Benutzer" : "User"))}}</td>
                    <td>{{AccessLevelSelect("level_" + grant.Id.ToString("N"), grant.Level, de, withNone: true)}}</td>
                </tr>
                """);
        var givenRows = string.Join("", given);
        var principalOptions =
            (groups.Count == 0 ? "" : $"""<optgroup label="{A(de ? "Gruppen" : "Groups")}">{string.Join("", groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).Select(group => $"""<option value="g:{group.Id}">{E(group.Name)}</option>"""))}</optgroup>""")
            + $"""<optgroup label="{A(de ? "Benutzer" : "Users")}">{string.Join("", users.Where(user => !user.IsAdmin).OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase).Select(user => $"""<option value="u:{user.Id}">{E(UserName(user))}</option>"""))}</optgroup>""";

        var effective = string.Join("", rows
            .OrderByDescending(row => row.Level)
            .ThenBy(row => row.User.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(row => $$"""
                <tr data-effective-user="{{A(row.User.UserName)}}" data-effective-level="{{row.Level}}">
                    <td><span class="server-name-cell">{{Icon("user")}}<a href="/admin/users/{{row.User.Id}}">{{E(UserName(row.User))}}</a></span></td>
                    <td>{{AccessLevelBadge(row.Level, de)}}</td>
                    <td><div class="access-reasons">{{AccessReasonsHtml(row.Reasons, de)}}</div></td>
                </tr>
                """));

        var inheritance = target.Kind != AccessTargetKind.Folder
            ? ""
            : $$"""
                <label class="check access-inherit"><input type="checkbox" name="inherit" data-access-inherit{{Checked(!target.InheritanceBroken)}}> <span><strong>{{(de ? "Rechte von oben erben" : "Inherit rights from above")}}</strong><small class="muted">{{(de
                    ? "Was auf übergeordneten Ordnern und auf „Alle Verbindungen“ freigegeben ist, gilt auch hier. Ohne Haken gilt hier nur, was auf diesem Ordner und darunter freigegeben ist (Administratoren und „Server verwalten“ ausgenommen)."
                    : "What is given on parent folders and on \"All connections\" holds here as well. Unticked, only what is given on this folder and below holds here (administrators and \"Manage servers\" excepted).")}}</small></span></label>
                """;
        var lead = target.Kind switch
        {
            AccessTargetKind.Server => de ? "Freigaben auf diese eine Verbindung. Dazu kommt, was auf ihrem Ordner (und darüber) freigegeben ist - unten steht, was am Ende für wen gilt." : "Entries on this one connection. What is given on its folder (and above) comes on top - below is what holds for whom in the end.",
            AccessTargetKind.Folder => de ? "Freigaben auf diesen Ordner gelten für alles darin - Verbindungen und Unterordner, auch was später hinzukommt." : "Entries on this folder hold for everything in it - connections and subfolders, also what is added later.",
            _ => de ? "Freigaben hier gelten für alle gemeinsamen Verbindungen, auch künftige - außer in Ordnern, die nicht erben." : "Entries here hold for every shared connection, also future ones - except in folders that do not inherit.",
        };

        var body = $$"""
            <div class="conn-page access-page" data-access-page="{{target.Kind}}">
                <section class="page-head conn-head">
                    <span class="conn-head-icon" aria-hidden="true">{{Icon("shield")}}</span>
                    <div>
                        <p class="eyebrow">{{(de ? "Zugriff" : "Access")}} · {{E(kindLabel)}}</p>
                        <h1>{{E(target.Name)}}</h1>
                    </div>
                </section>
                <form id="access-form" method="post" action="/admin/access" class="stack">
                    {{Csrf(context)}}
                    <input type="hidden" name="kind" value="{{target.Kind}}">
                    <input type="hidden" name="id" value="{{target.Id}}">
                    <input type="hidden" name="folder" value="{{A(target.FolderPath)}}">
                    <section class="panel">
                        <h2>{{(de ? "Freigaben hier" : "Given here")}}</h2>
                        <p class="muted settings-lead">{{E(lead)}}</p>
                        {{inheritance}}
                        <div class="table-wrap">
                            <table class="access-table" data-access-given>
                                <thead><tr><th>{{(de ? "Wer" : "Who")}}</th><th></th><th>{{(de ? "Darf" : "May")}}</th></tr></thead>
                                <tbody>{{(givenRows.Length == 0 ? $"""<tr><td colspan="3" class="muted">{E(de ? "Hier ist noch nichts freigegeben." : "Nothing is given here yet.")}</td></tr>""" : givenRows)}}</tbody>
                            </table>
                        </div>
                        <div class="access-add">
                            <label>{{(de ? "Hinzufügen" : "Add")}}<select name="newPrincipal" data-access-new-principal><option value="">{{(de ? "Gruppe oder Benutzer wählen..." : "Choose a group or user...")}}</option>{{principalOptions}}</select></label>
                            <label>{{(de ? "Darf" : "May")}}{{AccessLevelSelect("newLevel", AccessLevel.Connect, de, withNone: false, " data-access-new-level")}}</label>
                            <button type="submit" name="action" value="add" data-access-add>{{Icon("plus")}}{{(de ? "Hinzufügen" : "Add")}}</button>
                        </div>
                        <ul class="access-legend muted">{{string.Join("", GrantableLevels.Select(level => $"<li><strong>{E(AccessLevelLabel(level, de))}</strong> - {E(AccessLevelHint(level, de))}</li>"))}}</ul>
                    </section>
                </form>
                <section class="panel conn-actions">
                    <div class="form-actions">
                        <button type="submit" form="access-form" name="action" value="save" class="primary" data-access-save>{{Icon("save")}}{{T(context, "Save")}}</button>
                        <a class="button" href="{{A(target.BackUrl)}}">{{Icon("arrow-left")}}{{T(context, "Back")}}</a>
                    </div>
                </section>
                <section class="panel">
                    <h2>{{(de ? "Wer hat Zugriff?" : "Who has access?")}}</h2>
                    <p class="muted settings-lead">{{(de
                        ? "Alle, die das am Ende dürfen - mit jedem Grund. Es gilt der höchste."
                        : "Everyone who may in the end - with every reason. The highest one holds.")}}</p>
                    <div class="table-wrap">
                        <table class="access-table" data-access-effective>
                            <thead><tr><th>{{(de ? "Benutzer" : "User")}}</th><th>{{(de ? "Darf" : "May")}}</th><th>{{(de ? "Warum" : "Why")}}</th></tr></thead>
                            <tbody>{{(effective.Length == 0 ? $"""<tr><td colspan="3" class="muted">{E(de ? "Niemand außer den Administratoren." : "Nobody but the administrators.")}</td></tr>""" : effective)}}</tbody>
                        </table>
                    </div>
                </section>
            </div>
            {{AccessStyles}}
            """;

        return Layout(context, currentUser, (de ? "Zugriff: " : "Access: ") + target.Name, body);
    }

    private const string AccessStyles = """
        <style>
            .access-table td { vertical-align: middle; }
            .access-level-select { max-width: 220px; min-width: 150px; width: auto; }
            .access-add { align-items: end; display: flex; flex-wrap: wrap; gap: 10px; margin-top: 12px; }
            .access-add label { display: grid; gap: 4px; min-width: 200px; }
            .access-inherit { align-items: flex-start; margin: 4px 0 12px; }
            .access-inherit span { display: grid; gap: 2px; }
            .access-legend { display: grid; font-size: 13px; gap: 2px; margin: 14px 0 0; padding-left: 18px; }
            .access-reasons { display: flex; flex-direction: column; gap: 4px; }
            .access-reason { align-items: center; display: inline-flex; flex-wrap: wrap; gap: 6px; font-size: 13px; }
            .access-badge { border-radius: 999px; display: inline-block; font-size: 12px; font-weight: 600; line-height: 1; padding: 4px 9px; white-space: nowrap; }
            .access-readonly { background: color-mix(in srgb, #8a8f98 18%, var(--surface)); color: var(--text); }
            .access-connect { background: color-mix(in srgb, var(--accent) 16%, var(--surface)); color: var(--text); }
            .access-edit { background: color-mix(in srgb, #d08a1a 20%, var(--surface)); color: var(--text); }
            .access-manage { background: color-mix(in srgb, #c2410c 22%, var(--surface)); color: var(--text); }
            .access-none { background: var(--surface-2, var(--surface)); color: var(--muted); }
        </style>
        """;
}
