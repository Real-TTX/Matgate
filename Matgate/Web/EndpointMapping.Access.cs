using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The access page (HtmlViews.AccessPage) of a shared connection, a shared folder or of all shared connections.
// Administrators, and whoever was given "Manage" there (or above), may open and change it.
public static partial class EndpointMapping
{
    private static void MapAccessEndpoints(WebApplication app)
    {
        app.MapGet("/admin/access", AccessPageAsync).RequireAuthorization();
        app.MapPost("/admin/access", AccessSaveAsync).RequireAuthorization();
    }

    public static string AccessUrlForServer(Guid id) => "/admin/access?server=" + id;

    public static string AccessUrlForFolder(string path) => "/admin/access?folder=" + Uri.EscapeDataString(path);

    public const string AccessUrlForAll = "/admin/access?all=1";

    // What the request is about, and whether this user may manage it; null when not (or not found).
    private static async Task<HtmlViews.AccessPageTarget?> AccessTargetAsync(MatgateUser user, string kind, string id, string folder, JsonDataStore store, bool de, CancellationToken cancellationToken)
    {
        if (kind == nameof(AccessTargetKind.Server) && Guid.TryParse(id, out var serverId))
        {
            var server = await store.FindServerByIdAsync(serverId, cancellationToken);
            // Someone's own connection is theirs: there is nothing to give.
            if (server is null || server.OwnerUserId is not null || AccessResolver.Level(user, server) < AccessLevel.Manage)
            {
                return null;
            }

            return new(AccessTargetKind.Server, server.Id, server.Name, server.FolderName, false, HtmlViews.AdminConnectionsUrl("server:" + server.Id));
        }

        if (kind == nameof(AccessTargetKind.Folder))
        {
            var path = ServerFolders.CleanPath(folder);
            if (path.Length == 0 || !AccessResolver.CanManageFolder(user, path))
            {
                return null;
            }

            user.Access.Folders.TryGetValue(AccessResolver.FolderKey(path), out var definition);
            return new(AccessTargetKind.Folder, definition?.Id ?? Guid.Empty, definition?.Name ?? path, definition?.Name ?? path, definition?.InheritanceBroken == true, HtmlViews.AdminConnectionsUrl("folder:" + (definition?.Name ?? path)));
        }

        if (kind == nameof(AccessTargetKind.All) && AccessResolver.CanManageAll(user))
        {
            return new(AccessTargetKind.All, Guid.Empty, de ? "Alle Verbindungen" : "All connections", "", false, HtmlViews.AdminConnectionsUrl("all"));
        }

        return null;
    }

    private static string FolderEditUrl(string path) => "/admin/folders/edit?name=" + Uri.EscapeDataString(path);

    private static async Task<IResult> AccessPageAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var query = context.Request.Query;
        var kind = query.ContainsKey("server") ? nameof(AccessTargetKind.Server) : query.ContainsKey("folder") ? nameof(AccessTargetKind.Folder) : nameof(AccessTargetKind.All);
        var target = await AccessTargetAsync(user, kind, query["server"].ToString(), query["folder"].ToString(), store, HtmlViews.Language(context) == "de", context.RequestAborted);
        if (target is null)
        {
            return Results.Redirect("/forbidden");
        }

        return Results.Content(await RenderAccessPageAsync(context, user, target, store, views), "text/html");
    }

    // The access editor of a target for the administration's tree; empty when this user may not manage it.
    internal static async Task<string> AccessEditorForAsync(HttpContext context, MatgateUser user, string kind, string id, string folder, JsonDataStore store, HtmlViews views, string returnUrl)
    {
        var target = await AccessTargetAsync(user, kind, id, folder, store, HtmlViews.Language(context) == "de", context.RequestAborted);
        if (target is null)
        {
            return "";
        }

        var (grants, users, groups, rows) = await AccessDataAsync(target, store, context.RequestAborted);
        return views.AccessEditor(context, target, grants, users, groups, rows, returnUrl);
    }

    private static async Task<(List<AccessGrant> Grants, IReadOnlyList<MatgateUser> Users, IReadOnlyList<UserGroup> Groups, List<HtmlViews.AccessPageRow> Rows)> AccessDataAsync(HtmlViews.AccessPageTarget target, JsonDataStore store, CancellationToken cancellationToken)
    {
        var users = await store.GetUsersAsync(cancellationToken);
        var groups = await store.GetGroupsAsync(cancellationToken);
        var grants = (await store.GetGrantsAsync(cancellationToken))
            .Where(grant => grant.TargetKind == target.Kind && (target.Kind == AccessTargetKind.All || (target.Id != Guid.Empty && grant.TargetId == target.Id)))
            .ToList();
        var server = target.Kind == AccessTargetKind.Server ? await store.FindServerByIdAsync(target.Id, cancellationToken) : null;
        var rows = new List<HtmlViews.AccessPageRow>();
        foreach (var person in users.Where(person => person.IsEnabled))
        {
            var reasons = target.Kind switch
            {
                AccessTargetKind.Server => AccessResolver.Reasons(person, server!),
                AccessTargetKind.Folder => AccessResolver.FolderReasons(person, target.FolderPath),
                _ => AccessResolver.AllReasons(person),
            };
            var level = AccessResolver.Highest(reasons);
            if (level > AccessLevel.None)
            {
                rows.Add(new(person, level, reasons));
            }
        }

        return (grants, users, groups, rows);
    }

    private static async Task<string> RenderAccessPageAsync(HttpContext context, MatgateUser user, HtmlViews.AccessPageTarget target, JsonDataStore store, HtmlViews views)
    {
        var (grantsHere, usersAll, groupsAll, rowsAll) = await AccessDataAsync(target, store, context.RequestAborted);
        return views.AccessPage(context, user, target, grantsHere, usersAll, groupsAll, rowsAll);
    }

    private static async Task<IResult> AccessSaveAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var de = HtmlViews.Language(context) == "de";
        var target = await AccessTargetAsync(user, form["kind"].ToString(), form["id"].ToString(), form["folder"].ToString(), store, de, context.RequestAborted);
        if (target is null)
        {
            return Results.Redirect("/forbidden");
        }

        // A folder that so far was only the name its connections carry gets a lasting one now.
        var targetId = target.Id;
        if (target.Kind == AccessTargetKind.Folder)
        {
            var folders = await store.EnsureSharedFoldersAsync([target.FolderPath], context.RequestAborted);
            var definition = folders[AccessResolver.FolderKey(target.FolderPath)];
            targetId = definition.Id;
            var broken = !IsChecked(form, "inherit");
            await store.UpdateFoldersAsync(all =>
            {
                if (all.FirstOrDefault(folder => folder.Id == definition.Id) is { } stored)
                {
                    stored.InheritanceBroken = broken;
                }
            }, context.RequestAborted);
        }

        static AccessLevel? Parse(string value) => Enum.TryParse<AccessLevel>(value, out var level) && level != AccessLevel.None ? level : null;
        var newPrincipal = form["newPrincipal"].ToString();
        var newLevel = Parse(form["newLevel"].ToString());
        (AccessPrincipalKind Kind, Guid Id)? added = null;
        if (newPrincipal.Length > 2 && Guid.TryParse(newPrincipal[2..], out var principalId) && newLevel is not null)
        {
            if (newPrincipal.StartsWith("g:", StringComparison.Ordinal) && (await store.GetGroupsAsync(context.RequestAborted)).Any(group => group.Id == principalId))
            {
                added = (AccessPrincipalKind.Group, principalId);
            }
            else if (newPrincipal.StartsWith("u:", StringComparison.Ordinal) && await store.FindUserByIdAsync(principalId, context.RequestAborted) is not null)
            {
                added = (AccessPrincipalKind.User, principalId);
            }
        }

        await store.UpdateGrantsAsync(grants =>
        {
            var here = grants.Where(grant => grant.Targets(target.Kind, targetId)).ToList();
            foreach (var grant in here)
            {
                var field = form["level_" + grant.Id.ToString("N")].ToString();
                if (field.Length == 0)
                {
                    continue;
                }

                if (Parse(field) is { } level)
                {
                    grant.Level = level;
                }
                else
                {
                    grants.Remove(grant);
                }
            }

            if (added is { } principal)
            {
                var existing = grants.FirstOrDefault(grant => grant.Targets(target.Kind, targetId) && grant.PrincipalKind == principal.Kind && grant.PrincipalId == principal.Id);
                if (existing is null)
                {
                    grants.Add(new AccessGrant { TargetKind = target.Kind, TargetId = target.Kind == AccessTargetKind.All ? Guid.Empty : targetId, PrincipalKind = principal.Kind, PrincipalId = principal.Id, Level = newLevel!.Value });
                }
                else
                {
                    existing.Level = newLevel!.Value;
                }
            }
        }, context.RequestAborted);

        // Back to where the editor sat: its own page or the administration's tree (only a page of the administration).
        var requested = form["return"].ToString();
        var back = requested.StartsWith("/admin", StringComparison.Ordinal) && !requested.StartsWith("//", StringComparison.Ordinal)
            ? requested
            : target.Kind switch
            {
                AccessTargetKind.Server => AccessUrlForServer(target.Id),
                AccessTargetKind.Folder => AccessUrlForFolder(target.FolderPath),
                _ => AccessUrlForAll,
            };
        return Results.Redirect(EmbedAwareRedirect(context, back));
    }
}
