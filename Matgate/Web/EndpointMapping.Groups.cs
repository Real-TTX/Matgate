using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// Groups of users (see UserGroup): administrators only. The list is a tab of the administration. What a group
// may do is kept as entries in grants.json (AccessGrant) - on all connections, on folders and on connections.
public static partial class EndpointMapping
{
    private static void MapGroupEndpoints(WebApplication app)
    {
        app.MapGet("/admin/groups/new", GroupNewAsync).RequireAuthorization();
        app.MapGet("/admin/groups/{id:guid}", GroupEditAsync).RequireAuthorization();
        app.MapPost("/admin/groups", GroupSaveAsync).RequireAuthorization();
        app.MapPost("/admin/groups/{id:guid}", GroupSaveAsync).RequireAuthorization();
        app.MapPost("/admin/groups/{id:guid}/delete", GroupDeleteAsync).RequireAuthorization();
        app.MapPost("/admin/groups/members", GroupMemberAsync).RequireAuthorization();
        app.MapPost("/admin/groups/{id:guid}/grant", GroupGrantAsync).RequireAuthorization();
    }

    private const string GroupList = "/admin?tab=groups";

    private sealed record GroupChoices(IReadOnlyList<MatgateUser> Users, IReadOnlyList<ServerEndpoint> Servers, List<string> Folders, IReadOnlyList<AccessGrant> Grants, IReadOnlyDictionary<string, FolderDefinition> FolderIndex);

    private static async Task<GroupChoices> GroupChoicesAsync(MatgateUser admin, JsonDataStore store, CancellationToken cancellationToken)
    {
        var servers = await store.GetServersAsync(cancellationToken);
        var global = servers.Where(server => server.OwnerUserId is null).ToList();
        // Every shared folder, and every folder above one: "Lab/Linux" offers "Lab" as well.
        var shared = (await store.GetFoldersAsync(cancellationToken)).Where(folder => folder.OwnerUserId is null).Select(folder => folder.Name);
        var names = ServerFolders.Names(global).Concat(shared)
            .SelectMany(name => AccessResolver.PathAndParents(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var users = await store.GetUsersAsync(cancellationToken);
        var folderIndex = users.FirstOrDefault()?.Access.Folders ?? admin.Access.Folders;
        return new(users, global, names, await store.GetGrantsAsync(cancellationToken), folderIndex);
    }

    private static string RenderGroupPage(HttpContext context, HtmlViews views, MatgateUser admin, UserGroup? group, GroupChoices choices, string? error = null) =>
        views.GroupPage(context, admin, group, choices.Users, choices.Servers, choices.Folders, choices.Grants, choices.FolderIndex, error);

    private static async Task<IResult> GroupNewAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        return Results.Content(RenderGroupPage(context, views, admin, null, await GroupChoicesAsync(admin, store, context.RequestAborted)), "text/html");
    }

    private static async Task<IResult> GroupEditAsync(Guid id, HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        var group = (await store.GetGroupsAsync(context.RequestAborted)).FirstOrDefault(candidate => candidate.Id == id);
        if (group is null)
        {
            return Results.Redirect(EmbedAwareRedirect(context, GroupList));
        }

        return Results.Content(RenderGroupPage(context, views, admin, group, await GroupChoicesAsync(admin, store, context.RequestAborted)), "text/html");
    }

    private static async Task<IResult> GroupSaveAsync(HttpContext context, JsonDataStore store, HtmlViews views, Guid? id = null)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, admin, views);
        }

        var name = Clean(form["name"].ToString(), "");
        var ids = (string field) => form[field].Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty).Where(value => value != Guid.Empty).Distinct().ToList();
        var draft = new UserGroup
        {
            Name = name.Length > 80 ? name[..80] : name,
            Description = Clean(form["description"].ToString(), ""),
            MemberIds = ids("member"),
            ExternalGroups = [.. form["externalGroups"].ToString().Split('\n').Select(line => line.Trim()).Where(line => line.Contains(':') && line.Length <= 200).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
        var existing = id is null ? null : (await store.GetGroupsAsync(context.RequestAborted)).FirstOrDefault(candidate => candidate.Id == id);
        if (id is not null && existing is null)
        {
            return Results.Redirect(EmbedAwareRedirect(context, GroupList));
        }

        if (draft.Name.Length == 0)
        {
            if (existing is not null)
            {
                draft.Id = existing.Id;
            }

            return Results.Content(
                RenderGroupPage(context, views, admin, existing is null ? null : draft, await GroupChoicesAsync(admin, store, context.RequestAborted), HtmlViews.Language(context) == "de" ? "Eine Gruppe braucht einen Namen." : "A group needs a name."),
                "text/html");
        }

        var groupId = existing?.Id ?? draft.Id;
        await store.UpdateGroupsAsync(groups =>
        {
            var target = groups.FirstOrDefault(candidate => candidate.Id == groupId);
            if (target is null)
            {
                target = new UserGroup { Id = groupId };
                groups.Add(target);
            }

            target.Name = draft.Name;
            target.Description = draft.Description;
            target.MemberIds = draft.MemberIds;
            target.ExternalGroups = draft.ExternalGroups;
            target.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        // What the group may do: the form names every target it offers, each with a level or "none".
        static AccessLevel Parse(string value) => Enum.TryParse<AccessLevel>(value, out var level) ? level : AccessLevel.None;
        var wanted = new List<(AccessTargetKind Kind, Guid Id, AccessLevel Level)>
        {
            (AccessTargetKind.All, Guid.Empty, Parse(form["level_all"].ToString())),
        };
        var folderLevels = new List<(string Path, AccessLevel Level)>();
        for (var index = 0; form.ContainsKey("folder_" + index); index++)
        {
            folderLevels.Add((ServerFolders.CleanPath(form["folder_" + index]), Parse(form["folderlevel_" + index].ToString())));
        }

        var folders = await store.EnsureSharedFoldersAsync(folderLevels.Where(entry => entry.Level != AccessLevel.None).Select(entry => entry.Path), context.RequestAborted);
        foreach (var (path, level) in folderLevels.Where(entry => entry.Path.Length > 0))
        {
            if (folders.TryGetValue(AccessResolver.FolderKey(path), out var folder))
            {
                wanted.Add((AccessTargetKind.Folder, folder.Id, level));
            }
        }

        foreach (var server in (await store.GetServersAsync(context.RequestAborted)).Where(server => server.OwnerUserId is null))
        {
            var field = form["level_" + server.Id.ToString("N")].ToString();
            if (field.Length > 0)
            {
                wanted.Add((AccessTargetKind.Server, server.Id, Parse(field)));
            }
        }

        await store.UpdateGrantsAsync(grants =>
        {
            foreach (var (kind, targetId, level) in wanted)
            {
                var current = grants.FirstOrDefault(grant => grant.Targets(kind, targetId) && grant.PrincipalKind == AccessPrincipalKind.Group && grant.PrincipalId == groupId);
                if (level == AccessLevel.None)
                {
                    if (current is not null)
                    {
                        grants.Remove(current);
                    }
                }
                else if (current is null)
                {
                    grants.Add(new AccessGrant { TargetKind = kind, TargetId = targetId, PrincipalKind = AccessPrincipalKind.Group, PrincipalId = groupId, Level = level });
                }
                else
                {
                    current.Level = level;
                }
            }
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, HtmlViews.AdminGroupsUrl(groupId)));
    }

    private static async Task<IResult> GroupDeleteAsync(Guid id, HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, admin, views);
        }

        await store.UpdateGroupsAsync(groups => groups.RemoveAll(group => group.Id == id), context.RequestAborted);
        await store.UpdateGrantsAsync(grants => grants.RemoveAll(grant => grant.PrincipalKind == AccessPrincipalKind.Group && grant.PrincipalId == id), context.RequestAborted);
        return Results.Redirect(EmbedAwareRedirect(context, GroupList));
    }

    // Back to the page of the administration the form sat on (only there), else to the groups.
    private static string AdminReturn(IFormCollection form, string fallback)
    {
        var requested = form["return"].ToString();
        return requested.StartsWith("/admin", StringComparison.Ordinal) && !requested.StartsWith("//", StringComparison.Ordinal) ? requested : fallback;
    }

    // One user into a group or out of it - from the group's page in the tab, or from the user's.
    private static async Task<IResult> GroupMemberAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, admin, views);
        }

        if (Guid.TryParse(form["group"], out var groupId)
            && Guid.TryParse(form["user"], out var userId)
            && await store.FindUserByIdAsync(userId, context.RequestAborted) is not null)
        {
            var add = form["op"] == "add";
            await store.UpdateGroupsAsync(groups =>
            {
                if (groups.FirstOrDefault(group => group.Id == groupId) is not { } group)
                {
                    return;
                }

                group.MemberIds.Remove(userId);
                if (add)
                {
                    group.MemberIds.Add(userId);
                }

                group.UpdatedAt = DateTimeOffset.UtcNow;
            }, context.RequestAborted);
        }

        return Results.Redirect(EmbedAwareRedirect(context, AdminReturn(form, GroupList)));
    }

    // One entry of a group: "all", "folder:<path>" or "server:<id>" with a level; "None" takes it away.
    private static async Task<IResult> GroupGrantAsync(Guid id, HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, admin, views);
        }

        var back = AdminReturn(form, HtmlViews.AdminGroupsUrl(id));
        if (!(await store.GetGroupsAsync(context.RequestAborted)).Any(group => group.Id == id))
        {
            return Results.Redirect(EmbedAwareRedirect(context, GroupList));
        }

        // The remove button sends "None" next to the select's level.
        var values = form["level"].Select(value => value ?? "").ToList();
        var level = values.Contains(nameof(AccessLevel.None))
            ? AccessLevel.None
            : values.Select(value => Enum.TryParse<AccessLevel>(value, out var parsed) ? parsed : AccessLevel.None).DefaultIfEmpty(AccessLevel.None).Max();
        var target = form["target"].ToString();
        (AccessTargetKind Kind, Guid Id)? resolved = null;
        if (target == "all")
        {
            resolved = (AccessTargetKind.All, Guid.Empty);
        }
        else if (target.StartsWith("server:", StringComparison.Ordinal)
            && Guid.TryParse(target["server:".Length..], out var serverId)
            && (await store.GetServersAsync(context.RequestAborted)).Any(server => server.Id == serverId && server.OwnerUserId is null))
        {
            resolved = (AccessTargetKind.Server, serverId);
        }
        else if (target.StartsWith("folder:", StringComparison.Ordinal) && ServerFolders.CleanPath(target["folder:".Length..]) is { Length: > 0 } path)
        {
            if (level == AccessLevel.None)
            {
                var existing = (await store.GetFoldersAsync(context.RequestAborted)).FirstOrDefault(folder => folder.OwnerUserId is null && AccessResolver.FolderKey(folder.Name) == AccessResolver.FolderKey(path));
                resolved = existing is null ? null : (AccessTargetKind.Folder, existing.Id);
            }
            else
            {
                var folders = await store.EnsureSharedFoldersAsync([path], context.RequestAborted);
                resolved = (AccessTargetKind.Folder, folders[AccessResolver.FolderKey(path)].Id);
            }
        }

        if (resolved is { } chosen)
        {
            await store.UpdateGrantsAsync(grants =>
            {
                var current = grants.FirstOrDefault(grant => grant.Targets(chosen.Kind, chosen.Id) && grant.PrincipalKind == AccessPrincipalKind.Group && grant.PrincipalId == id);
                if (level == AccessLevel.None)
                {
                    if (current is not null)
                    {
                        grants.Remove(current);
                    }
                }
                else if (current is null)
                {
                    grants.Add(new AccessGrant { TargetKind = chosen.Kind, TargetId = chosen.Id, PrincipalKind = AccessPrincipalKind.Group, PrincipalId = id, Level = level });
                }
                else
                {
                    current.Level = level;
                }
            }, context.RequestAborted);
        }

        return Results.Redirect(EmbedAwareRedirect(context, back));
    }
}
