using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// Stored credentials (see Credential): the list, a page to make one, a page to change one, and deleting it -
// after which the connections that used it have no login and ask for one when connecting.
public static partial class EndpointMapping
{
    private static void MapCredentialEndpoints(WebApplication app)
    {
        app.MapGet("/credentials", CredentialListAsync).RequireAuthorization();
        app.MapGet("/credentials/new", CredentialNewAsync).RequireAuthorization();
        app.MapGet("/credentials/{id:guid}", CredentialEditAsync).RequireAuthorization();
        app.MapPost("/credentials", CredentialSaveAsync).RequireAuthorization();
        app.MapPost("/credentials/{id:guid}", CredentialSaveAsync).RequireAuthorization();
        app.MapPost("/credentials/{id:guid}/delete", CredentialDeleteAsync).RequireAuthorization();
    }

    private static async Task<Dictionary<Guid, int>> CredentialUsageAsync(JsonDataStore store, CancellationToken cancellationToken) =>
        (await store.GetServersAsync(cancellationToken))
            .Where(server => server.CredentialId is not null)
            .GroupBy(server => server.CredentialId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

    private static async Task<IResult> CredentialListAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var visible = (await store.GetCredentialsAsync(context.RequestAborted)).Where(credential => credential.UsableBy(user)).ToList();
        return Results.Content(views.CredentialList(context, user, visible, await CredentialUsageAsync(store, context.RequestAborted)), "text/html");
    }

    private static async Task<IResult> CredentialNewAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        return user is null ? Results.Redirect("/login") : Results.Content(views.CredentialForm(context, user, null, 0), "text/html");
    }

    private static async Task<IResult> CredentialEditAsync(Guid id, HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var credential = (await store.GetCredentialsAsync(context.RequestAborted)).FirstOrDefault(candidate => candidate.Id == id);
        if (credential is null || !credential.EditableBy(user))
        {
            return Results.Redirect(EmbedAwareRedirect(context, "/credentials"));
        }

        var usage = await CredentialUsageAsync(store, context.RequestAborted);
        return Results.Content(views.CredentialForm(context, user, credential, usage.GetValueOrDefault(id)), "text/html");
    }

    private static async Task<IResult> CredentialSaveAsync(HttpContext context, JsonDataStore store, HtmlViews views, Guid? id = null)
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
        var name = Clean(form["name"].ToString(), "");
        var shared = (user.IsAdmin || user.CanManageServers) && form["scope"] == "shared";
        Credential? existing = null;
        if (id is not null)
        {
            existing = (await store.GetCredentialsAsync(context.RequestAborted)).FirstOrDefault(candidate => candidate.Id == id);
            if (existing is null || !existing.EditableBy(user))
            {
                return Results.Redirect(EmbedAwareRedirect(context, "/credentials"));
            }
        }

        if (name.Length == 0)
        {
            var draft = new Credential { Name = name, UserName = Clean(form["userName"].ToString(), ""), Domain = Clean(form["domain"].ToString(), ""), Notes = Clean(form["notes"].ToString(), ""), OwnerUserId = shared ? null : user.Id };
            return Results.Content(views.CredentialForm(context, user, existing is null ? null : draft, 0, de ? "Zugangsdaten brauchen einen Namen." : "Credentials need a name."), "text/html");
        }

        await store.UpdateCredentialsAsync(credentials =>
        {
            var target = id is null ? null : credentials.FirstOrDefault(candidate => candidate.Id == id);
            if (target is null)
            {
                target = new Credential();
                credentials.Add(target);
            }

            target.Name = name.Length > 120 ? name[..120] : name;
            target.UserName = Clean(form["userName"].ToString(), "");
            target.Domain = Clean(form["domain"].ToString(), "");
            target.Notes = Clean(form["notes"].ToString(), "");
            var password = form["password"].ToString();
            if (password.Length > 0 || id is null)
            {
                target.Password = password;
            }

            // The key as the password: never shown again, replaced by a new one, removed only when asked.
            var key = CleanPrivateKey(form["privateKey"].ToString());
            if (IsChecked(form, "clearPrivateKey"))
            {
                target.PrivateKey = "";
            }
            else if (key.Length > 0)
            {
                target.PrivateKey = key;
            }

            target.OwnerUserId = shared ? null : (existing?.OwnerUserId ?? user.Id);
            if (!shared && existing is not null && existing.IsShared)
            {
                // A shared one made personal belongs to whoever did that.
                target.OwnerUserId = user.Id;
            }

            target.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, "/credentials"));
    }

    private static async Task<IResult> CredentialDeleteAsync(Guid id, HttpContext context, JsonDataStore store, HtmlViews views)
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

        var credential = (await store.GetCredentialsAsync(context.RequestAborted)).FirstOrDefault(candidate => candidate.Id == id);
        if (credential is not null && credential.EditableBy(user))
        {
            await store.UpdateCredentialsAsync(credentials => credentials.RemoveAll(candidate => candidate.Id == id), context.RequestAborted);
            // The connections that used it keep nothing of it: they ask for a login when connecting.
            await store.UpdateServersAsync(servers =>
            {
                foreach (var server in servers.Where(server => server.CredentialId == id))
                {
                    server.CredentialId = null;
                    server.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }, context.RequestAborted);
        }

        return Results.Redirect(EmbedAwareRedirect(context, "/credentials"));
    }
}
