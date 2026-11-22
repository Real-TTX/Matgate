using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The key bar in the account: a list (the tab "Key bar" of /account) and one page per button. Every change
// turns the default into the user's own list first - from then on it is theirs, until they reset it.
public static partial class EndpointMapping
{
    private const string KeyBarList = "/account?tab=keys";

    // After a change that was saved: the list again, with the notice that it was.
    private const string KeyBarSaved = "/account?tab=keys&saved=keys";

    private static void MapKeyBarEndpoints(WebApplication app)
    {
        app.MapGet("/account/keys/new", KeyBarNewAsync).RequireAuthorization();
        app.MapPost("/account/keys", KeyBarCreateAsync).RequireAuthorization();
        app.MapPost("/account/keys/reset", KeyBarResetAsync).RequireAuthorization();
        app.MapGet("/account/keys/{id}", KeyBarEditAsync).RequireAuthorization();
        app.MapPost("/account/keys/{id}", KeyBarUpdateAsync).RequireAuthorization();
        app.MapPost("/account/keys/{id}/move", KeyBarMoveAsync).RequireAuthorization();
        app.MapPost("/account/keys/{id}/delete", KeyBarDeleteAsync).RequireAuthorization();
    }

    private static async Task<IResult> KeyBarNewAsync(HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        return Results.Content(views.KeyBarButtonPage(context, user, new KeyBarButton(), isNew: true), "text/html");
    }

    private static async Task<IResult> KeyBarEditAsync(string id, HttpContext context, JsonDataStore store, HtmlViews views)
    {
        var user = await RequireUserAsync(context, store);
        if (user is null)
        {
            return Results.Redirect("/login");
        }

        var button = KeyBar.For(user).FirstOrDefault(candidate => candidate.Id == id);
        return button is null
            ? Results.Redirect(EmbedAwareRedirect(context, KeyBarList))
            : Results.Content(views.KeyBarButtonPage(context, user, button, isNew: false), "text/html");
    }

    // What the form of a button sent, as it was sent: the page shows it again when it cannot be saved.
    private static KeyBarButton KeyBarFromForm(IFormCollection form, string id)
    {
        var keys = new List<string>();
        foreach (var modifier in KeyBar.Modifiers)
        {
            if (IsChecked(form, "mod" + modifier.Name))
            {
                keys.Add(modifier.Name);
            }
        }

        var key = form["key"].ToString();
        if (key == "char")
        {
            // Not trimmed away to nothing: the page should say that a character is missing.
            keys.Add(form["character"].ToString().Trim() is { Length: > 0 } character ? character : "?missing");
        }
        else if (key.Length > 0)
        {
            keys.Add(key);
        }

        return new KeyBarButton
        {
            Id = id,
            Label = form["label"].ToString(),
            Keys = keys,
            Sticky = IsChecked(form, "sticky"),
            Scope = form["scope"].ToString(),
        };
    }

    private static string KeyBarError(HttpContext context, KeyBarButton sent)
    {
        var de = HtmlViews.Language(context) == "de";
        if (sent.Keys.Count == 0)
        {
            return de ? "Ein Knopf braucht mindestens eine Taste." : "A button needs at least one key.";
        }

        return de
            ? "Als Zeichen geht genau ein Buchstabe, eine Ziffer oder ein Satzzeichen."
            : "The character has to be exactly one letter, digit or punctuation mark.";
    }

    private static async Task<IResult> KeyBarCreateAsync(HttpContext context, JsonDataStore store, HtmlViews views)
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

        var sent = KeyBarFromForm(form, "");
        var button = KeyBar.Clean(sent);
        if (button is null)
        {
            return Results.Content(views.KeyBarButtonPage(context, user, sent, isNew: true, KeyBarError(context, sent)), "text/html");
        }

        var full = false;
        await store.UpdateUsersAsync(users =>
        {
            var current = users.FirstOrDefault(candidate => candidate.Id == user.Id);
            if (current is null)
            {
                return;
            }

            var bar = KeyBar.For(current);
            if (bar.Count >= KeyBar.MaxButtons)
            {
                full = true;
                return;
            }

            while (bar.Any(candidate => candidate.Id == button.Id))
            {
                button.Id = KeyBar.NewId();
            }

            bar.Add(button);
            current.KeyBar = bar;
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        if (full)
        {
            var de = HtmlViews.Language(context) == "de";
            return Results.Content(views.KeyBarButtonPage(context, user, sent, isNew: true,
                de ? $"Mehr als {KeyBar.MaxButtons} Knöpfe fasst die Leiste nicht." : $"The bar does not hold more than {KeyBar.MaxButtons} buttons."), "text/html");
        }

        return Results.Redirect(EmbedAwareRedirect(context, KeyBarSaved));
    }

    private static async Task<IResult> KeyBarUpdateAsync(string id, HttpContext context, JsonDataStore store, HtmlViews views)
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

        if (!KeyBar.IsId(id))
        {
            return Results.Redirect(EmbedAwareRedirect(context, KeyBarList));
        }

        var sent = KeyBarFromForm(form, id);
        var button = KeyBar.Clean(sent);
        if (button is null)
        {
            return Results.Content(views.KeyBarButtonPage(context, user, sent, isNew: false, KeyBarError(context, sent)), "text/html");
        }

        await store.UpdateUsersAsync(users =>
        {
            var current = users.FirstOrDefault(candidate => candidate.Id == user.Id);
            if (current is null)
            {
                return;
            }

            var bar = KeyBar.For(current);
            var at = bar.FindIndex(candidate => candidate.Id == id);
            if (at < 0)
            {
                return;
            }

            bar[at] = button;
            current.KeyBar = bar;
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, KeyBarSaved));
    }

    // One place forward or back in the bar.
    private static async Task<IResult> KeyBarMoveAsync(string id, HttpContext context, JsonDataStore store, HtmlViews views)
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

        var step = form["direction"].ToString() == "up" ? -1 : 1;
        await store.UpdateUsersAsync(users =>
        {
            var current = users.FirstOrDefault(candidate => candidate.Id == user.Id);
            if (current is null)
            {
                return;
            }

            var bar = KeyBar.For(current);
            var at = bar.FindIndex(candidate => candidate.Id == id);
            var to = at + step;
            if (at < 0 || to < 0 || to >= bar.Count)
            {
                return;
            }

            (bar[at], bar[to]) = (bar[to], bar[at]);
            current.KeyBar = bar;
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, KeyBarList));
    }

    private static async Task<IResult> KeyBarDeleteAsync(string id, HttpContext context, JsonDataStore store, HtmlViews views)
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

        await store.UpdateUsersAsync(users =>
        {
            var current = users.FirstOrDefault(candidate => candidate.Id == user.Id);
            if (current is null)
            {
                return;
            }

            var bar = KeyBar.For(current);
            if (bar.RemoveAll(candidate => candidate.Id == id) == 0)
            {
                return;
            }

            current.KeyBar = bar;
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, KeyBarSaved));
    }

    // Back to the default: the user's own list goes, and with it everything they added or changed.
    private static async Task<IResult> KeyBarResetAsync(HttpContext context, JsonDataStore store, HtmlViews views)
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

        await store.UpdateUsersAsync(users =>
        {
            var current = users.FirstOrDefault(candidate => candidate.Id == user.Id);
            if (current is null)
            {
                return;
            }

            current.KeyBar = null;
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }, context.RequestAborted);

        return Results.Redirect(EmbedAwareRedirect(context, KeyBarSaved));
    }
}
