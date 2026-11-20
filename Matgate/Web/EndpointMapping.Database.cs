using Matgate.Models;
using Matgate.Services;
using Matgate.Services.Database;

namespace Matgate.Web;

// Database connections (SQL Server, MySQL/MariaDB, PostgreSQL): a page with the database browser, and what it
// reads through - the databases, their tables and views, a page of a table's rows, and a statement typed in.
// Everything goes through DatabaseGateway, which is the same for all three; only its dialects differ.
//
// The login is the connection's. Where none is stored, the page asks for it and sends it with every request
// (headers, never the address), as the terminal does for SSH - it is not kept anywhere on the gateway.
public static partial class EndpointMapping
{
    private static readonly DatabaseGateway Databases = new();

    private static void MapDatabaseEndpoints(WebApplication app)
    {
        app.MapGet("/db/{id:guid}/browser", DatabasePageAsync).RequireAuthorization();
        app.MapGet("/api/db/{id:guid}/databases", DatabaseListAsync).RequireAuthorization();
        app.MapGet("/api/db/{id:guid}/objects", DatabaseObjectsAsync).RequireAuthorization();
        app.MapGet("/api/db/{id:guid}/rows", DatabaseRowsAsync).RequireAuthorization();
        app.MapPost("/api/db/{id:guid}/query", DatabaseQueryAsync).RequireAuthorization();
    }

    private sealed record DatabaseAccess(ServerEndpoint Server, MatgateUser User, string Login, string Password);

    private static async Task<(DatabaseAccess? Access, IResult? Result)> RequireDatabaseAsync(Guid id, HttpContext context, JsonDataStore store, bool api)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return (null, Results.Unauthorized());
        }

        var server = await ResolveServerForUserAsync(id, user, context, store);
        if (server is null || !server.IsEnabled || !CanAccessServer(user, server) || !ServerEndpoint.IsDatabaseProtocol(server.Protocol))
        {
            return (null, Results.NotFound(new { error = HtmlViews.Translate(context, "This server is not shared with you.") }));
        }

        if (api && !ValidateCsrfHeader(context))
        {
            return (null, Results.BadRequest(new { error = HtmlViews.Translate(context, "Invalid request") }));
        }

        // What is stored wins; what the page sends fills only what is missing.
        var login = string.IsNullOrWhiteSpace(server.UserName) ? context.Request.Headers["X-Matgate-Db-User"].ToString().Trim() : server.UserName;
        var password = string.IsNullOrEmpty(server.Password) ? context.Request.Headers["X-Matgate-Db-Password"].ToString() : server.Password;
        return (new DatabaseAccess(server, user, login, password), null);
    }

    private static IResult NeedsLogin(HttpContext context, DatabaseAccess access) =>
        Results.Json(new
        {
            needsLogin = true,
            user = access.Server.UserName,
            error = HtmlViews.Language(context) == "de" ? "Anmeldung an der Datenbank nötig." : "Sign in to the database.",
        }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult DatabaseError(HttpContext context, DatabaseException exception)
    {
        var german = HtmlViews.Language(context) == "de";
        var message = exception.Message;
        // The one failure a home lab meets first: a server with a certificate of its own making.
        if (message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            message += german
                ? " - In den Einstellungen der Verbindung „Zertifikat nicht prüfen“ ankreuzen."
                : " - Tick \"Do not check the certificate\" in the connection's settings.";
        }

        return Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> DatabaseRunAsync(Guid id, HttpContext context, JsonDataStore store, Func<DatabaseAccess, Task<object>> work)
    {
        var (access, result) = await RequireDatabaseAsync(id, context, store, true);
        if (result is not null)
        {
            return result;
        }

        if (string.IsNullOrEmpty(access!.Login) || (string.IsNullOrEmpty(access.Password) && string.IsNullOrEmpty(access.Server.Password)))
        {
            return NeedsLogin(context, access);
        }

        try
        {
            return Results.Json(await work(access));
        }
        catch (DatabaseException exception)
        {
            // A wrong login asks again instead of standing there as an error.
            var text = exception.Message;
            if (string.IsNullOrEmpty(access.Server.Password)
                && (text.Contains("Login failed", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase)))
            {
                return Results.Json(new { needsLogin = true, user = access.Login, error = text }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return DatabaseError(context, exception);
        }
    }

    private static async Task<IResult> DatabasePageAsync(Guid id, HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var (access, result) = await RequireDatabaseAsync(id, context, store, false);
        if (result is not null)
        {
            return result;
        }

        return Results.Content(views.DatabaseBrowser(context, access!.User, access.Server), "text/html; charset=utf-8");
    }

    private static Task<IResult> DatabaseListAsync(Guid id, HttpContext context, JsonDataStore store) =>
        DatabaseRunAsync(id, context, store, async access => new
        {
            databases = await Databases.DatabasesAsync(access.Server, access.Login, access.Password, context.RequestAborted),
            current = access.Server.DatabaseName,
        });

    private static Task<IResult> DatabaseObjectsAsync(Guid id, HttpContext context, JsonDataStore store) =>
        DatabaseRunAsync(id, context, store, async access => new
        {
            objects = await Databases.ObjectsAsync(access.Server, access.Login, access.Password, context.Request.Query["db"].ToString(), context.RequestAborted),
        });

    private static Task<IResult> DatabaseRowsAsync(Guid id, HttpContext context, JsonDataStore store) =>
        DatabaseRunAsync(id, context, store, async access =>
        {
            var query = context.Request.Query;
            var order = query["order"].ToString();
            return await Databases.RowsAsync(
                access.Server, access.Login, access.Password,
                query["db"].ToString(), query["schema"].ToString(), query["table"].ToString(),
                int.TryParse(query["offset"], out var offset) ? offset : 0,
                int.TryParse(query["limit"], out var limit) ? limit : 100,
                order.Length > 0 ? order : null,
                query["desc"] == "1",
                context.RequestAborted);
        });

    private static Task<IResult> DatabaseQueryAsync(Guid id, HttpContext context, JsonDataStore store) =>
        DatabaseRunAsync(id, context, store, async access =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var sql = form["sql"].ToString();
            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new DatabaseException(HtmlViews.Language(context) == "de" ? "Keine Abfrage." : "No statement.");
            }

            var database = form["db"].ToString();
            return await Databases.QueryAsync(access.Server, access.Login, access.Password, database.Length > 0 ? database : null, sql, context.RequestAborted);
        });
}
