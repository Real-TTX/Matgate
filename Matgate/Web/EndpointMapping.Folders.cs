using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The folders of connections: a list (the tab "Folders" of the administration) and one page per folder -
// its name, its icon and which connections lie in it. A folder is nothing but the name its connections carry
// (see ServerFolders), so every change here is a change to those connections; someone who may not edit a
// connection does not move it either.
public static partial class EndpointMapping
{
    private const string FolderList = "/admin?tab=folders";

    private static void MapFolderEndpoints(WebApplication app)
    {
        app.MapGet("/admin/folders", () => Results.Redirect(FolderList)).RequireAuthorization();
        app.MapGet("/admin/folders/new", FolderNewAsync).RequireAuthorization();
        app.MapGet("/admin/folders/edit", FolderEditAsync).RequireAuthorization();
        app.MapPost("/admin/folders/save", FolderSaveAsync).RequireAuthorization();
        app.MapPost("/admin/folders/delete", FolderDeleteAsync).RequireAuthorization();
        app.MapPost("/api/servers/{id:guid}/folder", MoveToFolderAsync).RequireAuthorization();
    }

    // Moves one connection into a folder - or out of every folder with an empty name. What the side panel does
    // when a connection is dropped on a folder. A subfolder is a name with a "/" in it ("Lab/Linux"). It takes
    // the icon the folder has, as the folder page does.
    private static async Task<IResult> MoveToFolderAsync(Guid id, HttpContext context, JsonDataStore store)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (!ValidateCsrfHeader(context))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var name = ServerFolders.CleanPath(form["folder"]);
        var moved = false;
        await store.UpdateServersAsync(servers =>
        {
            var server = servers.FirstOrDefault(candidate => candidate.Id == id);
            if (server is null || !CanEditServer(user, server))
            {
                return;
            }

            server.FolderIconKey = ServerFolders.IconFor(servers, server.Id, name, ServerFolders.Same(server.FolderName, name) ? server.FolderIconKey : "");
            server.FolderName = name;
            server.UpdatedAt = DateTimeOffset.UtcNow;
            moved = true;
        }, context.RequestAborted);

        return moved ? Results.NoContent() : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<List<ServerEndpoint>> EditableServersAsync(MatgateUser user, JsonDataStore store, CancellationToken cancellationToken) =>
    [
        .. (await store.GetServersAsync(cancellationToken))
            .Where(server => CanEditServer(user, server))
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase),
    ];

    private static async Task<IResult> FolderNewAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var servers = await EditableServersAsync(user, store, context.RequestAborted);
        return Results.Content(views.FolderPage(context, user, "", "", "", servers, new HashSet<Guid>()), "text/html");
    }

    private static async Task<IResult> FolderEditAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var servers = await EditableServersAsync(user, store, context.RequestAborted);
        var folder = ServerFolders.List(servers).FirstOrDefault(candidate => ServerFolders.Same(candidate.Name, context.Request.Query["name"]));
        if (folder is null)
        {
            return Results.Redirect(EmbedAwareRedirect(context, FolderList));
        }

        return Results.Content(
            views.FolderPage(context, user, folder.Name, folder.Name, folder.IconKey, servers, folder.Servers.Select(server => server.Id).ToHashSet()),
            "text/html");
    }

    private static async Task<IResult> FolderSaveAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var de = HtmlViews.Language(context) == "de";
        var original = ServerFolders.CleanName(form["original"]);
        var name = ServerFolders.CleanName(form["name"]);
        var iconKey = ServerEndpoint.NormalizeIconKey(form["iconKey"]);
        var chosen = form["server"]
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        string? error = null;
        if (name.Length == 0)
        {
            error = de ? "Ein Ordner braucht einen Namen." : "A folder needs a name.";
        }
        else if (chosen.Count == 0)
        {
            error = de
                ? "Ein Ordner besteht aus den Verbindungen, die in ihm liegen - ohne eine gibt es ihn nicht. Zum Entfernen gibt es \"Ordner auflösen\"."
                : "A folder is the connections that lie in it - without one there is none. To remove it there is \"Dissolve folder\".";
        }

        if (error is not null)
        {
            var servers = await EditableServersAsync(user, store, context.RequestAborted);
            return Results.Content(views.FolderPage(context, user, original, name, iconKey, servers, chosen, error), "text/html");
        }

        await store.UpdateServersAsync(servers =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var server in servers.Where(server => CanEditServer(user, server)))
            {
                var wasIn = original.Length > 0 && ServerFolders.Same(server.FolderName, original);
                if (chosen.Contains(server.Id))
                {
                    server.FolderName = name;
                }
                else if (wasIn)
                {
                    // Unticked: out of the folder.
                    server.FolderName = "";
                    server.FolderIconKey = "";
                    server.UpdatedAt = now;
                    continue;
                }

                // Everything that carries the name - also a folder of that name there was already - is
                // one folder now, with one spelling and one icon.
                if (ServerFolders.Same(server.FolderName, name))
                {
                    server.FolderName = name;
                    server.FolderIconKey = iconKey;
                    server.UpdatedAt = now;
                }
            }
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, FolderList));
    }

    // Dissolves a folder: its connections stay, without a folder.
    private static async Task<IResult> FolderDeleteAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireServerManagerAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/forbidden");
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (!ValidateCsrf(context, form))
        {
            return BadRequest(context, user, views);
        }

        var name = ServerFolders.CleanName(form["name"]);
        if (name.Length > 0)
        {
            await store.UpdateServersAsync(servers =>
            {
                foreach (var server in servers.Where(server => CanEditServer(user, server) && ServerFolders.Same(server.FolderName, name)))
                {
                    server.FolderName = "";
                    server.FolderIconKey = "";
                    server.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }, context.RequestAborted);
        }

        return Results.Redirect(EmbedAwareRedirect(context, FolderList));
    }
}
