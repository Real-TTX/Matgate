using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// Groups of users (see UserGroup): administrators only. The list is a tab of the administration.
public static partial class EndpointMapping
{
    private static void MapGroupEndpoints(WebApplication app)
    {
        app.MapGet("/admin/groups/new", GroupNewAsync).RequireAuthorization();
        app.MapGet("/admin/groups/{id:guid}", GroupEditAsync).RequireAuthorization();
        app.MapPost("/admin/groups", GroupSaveAsync).RequireAuthorization();
        app.MapPost("/admin/groups/{id:guid}", GroupSaveAsync).RequireAuthorization();
        app.MapPost("/admin/groups/{id:guid}/delete", GroupDeleteAsync).RequireAuthorization();
    }

    private const string GroupList = "/admin?tab=groups";

    private static async Task<(IReadOnlyList<MatgateUser> Users, IReadOnlyList<ServerEndpoint> Servers, List<string> Folders)> GroupChoicesAsync(MatgateUser admin, JsonDataStore store, CancellationToken cancellationToken)
    {
        var servers = await store.GetServersAsync(cancellationToken);
        var global = servers.Where(server => server.OwnerUserId is null).ToList();
        // Every folder name, and every folder above one: "Lab/Linux" offers "Lab" as well.
        var names = (await VisibleFolderNamesAsync(admin, global, store, cancellationToken))
            .SelectMany(name =>
            {
                var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return Enumerable.Range(1, parts.Length).Select(count => string.Join("/", parts.Take(count)));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (await store.GetUsersAsync(cancellationToken), global, names);
    }

    private static async Task<IResult> GroupNewAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var admin = await RequireAdminAsync(context, store);
        if (admin is null)
        {
            return Results.Redirect("/forbidden");
        }

        var (users, servers, folders) = await GroupChoicesAsync(admin, store, context.RequestAborted);
        return Results.Content(views.GroupPage(context, admin, null, users, servers, folders), "text/html");
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

        var (users, servers, folders) = await GroupChoicesAsync(admin, store, context.RequestAborted);
        return Results.Content(views.GroupPage(context, admin, group, users, servers, folders), "text/html");
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
            ServerIds = ids("server"),
            Folders = [.. form["folder"].Select(value => ServerFolders.CleanPath(value)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
        };
        var existing = id is null ? null : (await store.GetGroupsAsync(context.RequestAborted)).FirstOrDefault(candidate => candidate.Id == id);
        if (id is not null && existing is null)
        {
            return Results.Redirect(EmbedAwareRedirect(context, GroupList));
        }

        if (draft.Name.Length == 0)
        {
            var (users, servers, folders) = await GroupChoicesAsync(admin, store, context.RequestAborted);
            if (existing is not null)
            {
                draft.Id = existing.Id;
            }

            return Results.Content(
                views.GroupPage(context, admin, existing is null ? null : draft, users, servers, folders, HtmlViews.Language(context) == "de" ? "Eine Gruppe braucht einen Namen." : "A group needs a name."),
                "text/html");
        }

        await store.UpdateGroupsAsync(groups =>
        {
            var target = id is null ? null : groups.FirstOrDefault(candidate => candidate.Id == id);
            if (target is null)
            {
                target = new UserGroup();
                groups.Add(target);
            }

            target.Name = draft.Name;
            target.Description = draft.Description;
            target.MemberIds = draft.MemberIds;
            target.ServerIds = draft.ServerIds;
            target.Folders = draft.Folders;
            target.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, GroupList));
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
        return Results.Redirect(EmbedAwareRedirect(context, GroupList));
    }
}
