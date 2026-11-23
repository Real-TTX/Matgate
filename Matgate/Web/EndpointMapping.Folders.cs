using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The folders of connections: a list (the tab "Folders" of the administration) and one page per folder -
// its name, its icon and which connections lie in it. A folder is nothing but the name its connections carry
// (see ServerFolders), so every change here is a change to those connections; someone who may not edit a
// connection does not move it either.
public static partial class EndpointMapping
{
    private const string FolderList = "/admin?tab=connections";

    private static void MapFolderEndpoints(WebApplication app)
    {
        app.MapGet("/admin/folders", () => Results.Redirect(FolderList)).RequireAuthorization();
        app.MapGet("/admin/folders/new", FolderNewAsync).RequireAuthorization();
        app.MapGet("/admin/folders/edit", FolderEditAsync).RequireAuthorization();
        app.MapPost("/admin/folders/save", FolderSaveAsync).RequireAuthorization();
        app.MapPost("/admin/folders/delete", FolderDeleteAsync).RequireAuthorization();
        app.MapPost("/api/servers/{id:guid}/folder", MoveToFolderAsync).RequireAuthorization();
        app.MapPost("/api/folders", CreateFolderAsync).RequireAuthorization();
        app.MapPost("/api/order/connections", SaveConnectionOrderAsync).RequireAuthorization();
    }

    private const int MaxConnectionOrder = 2000;

    // The order of some connections, as one user put them (dragging them in the side panel or on the start page):
    // "ids" is what lies in one folder now, top to bottom. Their places in the user's order are taken over by this
    // sequence - where the first of them stood before - and everything else keeps its place. Personal: it changes
    // nothing for anyone else and needs no right beyond seeing the connections.
    private static async Task<IResult> SaveConnectionOrderAsync(HttpContext context, JsonDataStore store)
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
        var ids = form["ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(MaxConnectionOrder)
            .ToList();
        if (ids.Count == 0)
        {
            return Results.NoContent();
        }

        await store.UpdateUsersAsync(users =>
        {
            if (users.FirstOrDefault(candidate => candidate.Id == user.Id) is not { } stored)
            {
                return;
            }

            // "reset": these out of the order again - by name, as at the start.
            stored.ConnectionOrder = form["reset"] == "1"
                ? [.. (stored.ConnectionOrder ?? []).Where(id => !ids.Contains(id))]
                : MergeOrder(stored.ConnectionOrder ?? [], ids);
            stored.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);
        return Results.NoContent();
    }

    // The sequence put where the first of its members stood (at the end when none was there yet), the rest kept.
    internal static List<Guid> MergeOrder(List<Guid> current, List<Guid> sequence)
    {
        var members = sequence.ToHashSet();
        var at = current.FindIndex(members.Contains);
        var rest = current.Where(id => !members.Contains(id)).ToList();
        var insertAt = at < 0 ? rest.Count : current.Take(at).Count(id => !members.Contains(id));
        rest.InsertRange(insertAt, sequence);
        return rest.Count > MaxConnectionOrder ? rest[..MaxConnectionOrder] : rest;
    }

    // A folder made on its own, before anything is filed in it (the side panel's "New folder"). Whoever manages
    // connections makes it for everyone; anyone else for themselves.
    private static async Task<IResult> CreateFolderAsync(HttpContext context, JsonDataStore store)
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
        var name = ServerFolders.CleanPath(form["name"]);
        if (name.Length == 0)
        {
            return Results.BadRequest();
        }

        var shared = user.IsAdmin || user.CanManageServers;
        await store.UpdateFoldersAsync(folders =>
        {
            if (!folders.Any(folder => ServerFolders.Same(folder.Name, name) && (folder.OwnerUserId is null || folder.OwnerUserId == user.Id)))
            {
                folders.Add(new FolderDefinition { Name = name, OwnerUserId = shared ? null : user.Id });
            }
        }, context.RequestAborted);
        return Results.NoContent();
    }

    // The folders a user sees: those of the connections they may use, and those that exist on their own.
    private static async Task<List<string>> VisibleFolderNamesAsync(MatgateUser user, IEnumerable<ServerEndpoint> servers, JsonDataStore store, CancellationToken cancellationToken)
    {
        var names = ServerFolders.Names(servers);
        foreach (var folder in (await store.GetFoldersAsync(cancellationToken)).Where(folder => folder.VisibleTo(user)))
        {
            if (!names.Any(name => ServerFolders.Same(name, folder.Name)))
            {
                names.Add(folder.Name);
            }
        }

        return [.. names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
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

            // A shared connection carries the rights of its folder: whoever puts it into another folder decides who
            // may use it. So that takes "Edit" on the folder it goes into as well (server managers and
            // administrators have it everywhere) - not only on the connection.
            if (server.OwnerUserId is null && name.Length > 0 && AccessResolver.Highest(AccessResolver.FolderReasons(user, name)) < AccessLevel.Edit)
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
        // "Subfolder" in the administration's tree starts with the folder's path ("Lab/").
        return Results.Content(views.FolderPage(context, user, "", ServerFolders.CleanName(context.Request.Query["name"]), "", servers, new HashSet<Guid>()), "text/html");
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
        // A folder that exists on its own may be empty: then it is only its name and icon.
        if (folder is null
            && (await store.GetFoldersAsync(context.RequestAborted)).FirstOrDefault(candidate => candidate.VisibleTo(user) && ServerFolders.Same(candidate.Name, context.Request.Query["name"])) is { } own)
        {
            folder = new ServerFolder(own.Name, own.IconKey, []);
        }

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

        if (error is not null)
        {
            var servers = await EditableServersAsync(user, store, context.RequestAborted);
            return Results.Content(views.FolderPage(context, user, original, name, iconKey, servers, chosen, error), "text/html");
        }

        var renamed = original.Length > 0 && !ServerFolders.Same(original, name);
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
                else if (renamed && Below(server.FolderName, original) is { } rest)
                {
                    // Renamed: its subfolders go with it ("Lab/Linux" becomes "Labor/Linux").
                    server.FolderName = name + "/" + rest;
                    server.UpdatedAt = now;
                }
            }
        }, context.RequestAborted);

        // The folder itself, so that it stays there also while nothing lies in it - for everyone when an
        // administrator or server manager made it.
        // The folder keeps its id through a rename - and with it what was given on it.
        await store.UpdateFoldersAsync(folders =>
        {
            var mine = folders.Where(folder => folder.VisibleTo(user)).ToList();
            var own = mine.FirstOrDefault(folder => original.Length > 0 && ServerFolders.Same(folder.Name, original))
                ?? mine.FirstOrDefault(folder => ServerFolders.Same(folder.Name, name));
            if (own is null)
            {
                own = new FolderDefinition { OwnerUserId = user.IsAdmin || user.CanManageServers ? null : user.Id };
                folders.Add(own);
            }

            own.Name = name;
            own.IconKey = iconKey;
            if (renamed)
            {
                foreach (var below in mine.Where(folder => folder != own && (folder.OwnerUserId == own.OwnerUserId)))
                {
                    if (Below(below.Name, original) is { } rest)
                    {
                        below.Name = name + "/" + rest;
                    }
                }
            }
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, FolderList));
    }

    // The rest of a path below a folder ("Lab/Linux/Web" below "Lab" is "Linux/Web"); null when not below it.
    private static string? Below(string? path, string folder)
    {
        var clean = ServerFolders.CleanPath(path);
        var prefix = ServerFolders.CleanPath(folder) + "/";
        return prefix.Length > 1 && clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? clean[prefix.Length..] : null;
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
            var removed = new HashSet<Guid>();
            await store.UpdateFoldersAsync(folders =>
            {
                foreach (var folder in folders.Where(folder => ServerFolders.Same(folder.Name, name) && (folder.OwnerUserId == user.Id || (folder.OwnerUserId is null && (user.IsAdmin || user.CanManageServers)))).ToList())
                {
                    removed.Add(folder.Id);
                    folders.Remove(folder);
                }
            }, context.RequestAborted);
            // What was given on it goes with it.
            await store.UpdateGrantsAsync(grants => grants.RemoveAll(grant => grant.TargetKind == AccessTargetKind.Folder && removed.Contains(grant.TargetId)), context.RequestAborted);
        }

        return Results.Redirect(EmbedAwareRedirect(context, FolderList));
    }
}
