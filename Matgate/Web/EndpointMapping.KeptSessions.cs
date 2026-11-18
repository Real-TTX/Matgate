using System.Text.Json;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// Sessions that outlive the browser (Account -> Session): what the shell needs beside the sessions
// themselves. Its list of open tabs is stored on the server, and closing a tab ends its session for good -
// a session that merely lost its browser would otherwise stay until its time runs out.
public static partial class EndpointMapping
{
    private static void MapKeptSessionEndpoints(WebApplication app)
    {
        app.MapPut("/api/shell-state", ShellStatePutAsync).RequireAuthorization();
        app.MapPost("/api/kept-sessions/end", KeptSessionEndAsync).RequireAuthorization();
    }

    // The shell's tabs, as it stores them itself.
    private static async Task<IResult> ShellStatePutAsync(HttpContext context, JsonDataStore store, ShellStateStore states)
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

        if (SessionPreferences.NormalizeKeepSessionMinutes(user.Session?.KeepSessionMinutes ?? 0) == 0)
        {
            // Not this user's wish: the tabs stay the browser's own.
            return Results.NoContent();
        }

        if (context.Request.ContentLength is > ShellStateStore.MaxBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                body.Write(buffer, 0, read);
                if (body.Length > ShellStateStore.MaxBytes)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
            }

            using var document = JsonDocument.Parse(body.ToArray());
            string? Text(string name) =>
                document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            states.Set(user.Id, new ShellStateStore.ShellState(Text("workspace"), Text("shell"), Text("order")));
            return Results.NoContent();
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
    }

    // A tab was closed: its session ends now, not when its time runs out.
    private static async Task<IResult> KeptSessionEndAsync(HttpContext context, JsonDataStore store, DisplayBridge bridge)
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

        var session = context.Request.Query["session"].ToString();
        if (!SessionIdPattern.IsMatch(session))
        {
            return Results.BadRequest();
        }

        var ended = false;
        if (bridge.Held.TryGetValue(session, out var screen) && screen.UserId == user.Id)
        {
            await EndHeldScreenAsync(bridge, session, screen);
            ended = true;
        }

        if (SshShells.TryGetValue(session, out var shell) && shell.UserId == user.Id)
        {
            EndSshShell(session, shell);
            ended = true;
        }

        return Results.Ok(new { ended });
    }
}
