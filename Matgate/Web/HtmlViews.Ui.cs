using System.Globalization;
using System.Text;
using Matgate.Models;

namespace Matgate.Web;

// Building blocks the settings page, the account menu and the phone menu share, so that all three show the
// same person the same way: the initials in a circle, the name, the login and the role. Their CSS (prefix ui-)
// stands beside the tokens at the top of the stylesheet. Everything that comes from the user is HTML-encoded
// here, so a caller can put the result into a page as it is.
public sealed partial class HtmlViews
{
    // The letters in the circle. The display name is split at white space, dots, hyphens and underscores: two
    // parts or more give the first letter of the first and of the last part ("Matthias Schmoldt" -> MS), one part
    // gives its first letter ("Administrator" -> A), and without a usable display name the user name's first
    // letter counts. A letter is a whole text element, so an accent or a flag stays in one piece; anything that is
    // neither a letter nor a digit is passed over ("Anna (Büro)" -> AB, not "A(").
    internal static string Initials(string? displayName, string userName)
    {
        var parts = new List<string>();
        var part = new StringBuilder();
        foreach (var character in displayName ?? "")
        {
            if (char.IsWhiteSpace(character) || character is '.' or '-' or '_')
            {
                if (part.Length > 0)
                {
                    parts.Add(part.ToString());
                    part.Clear();
                }
            }
            else
            {
                part.Append(character);
            }
        }

        if (part.Length > 0)
        {
            parts.Add(part.ToString());
        }

        var letters = parts.Select(FirstLetter).OfType<string>().ToList();
        var initials = letters.Count switch
        {
            0 => FirstLetter(userName ?? "") ?? FirstTextElement(userName ?? ""),
            1 => letters[0],
            _ => letters[0] + letters[^1],
        };
        return initials.ToUpperInvariant();
    }

    // The first text element of a text that starts with a letter or a digit, or null.
    private static string? FirstLetter(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (Rune.TryGetRuneAt(element, 0, out var rune) && Rune.IsLetterOrDigit(rune))
            {
                return element;
            }
        }

        return null;
    }

    private static string FirstTextElement(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        return elements.MoveNext() ? elements.GetTextElement() : "";
    }

    // One word for what the user may do, for the user card: an administrator, somebody who manages the
    // connections, or a user.
    internal static string RoleLabel(bool de, MatgateUser u) =>
        u.IsAdmin ? "Administrator"
        : u.CanManageServers ? (de ? "Verwalter" : "Manager")
        : de ? "Benutzer" : "User";

    // The initials in a circle of the given size in pixels (the font scales with it). Decoration only: the name
    // always stands next to it or in the label of the control around it.
    internal static string UiAvatar(MatgateUser u, int size) =>
        $"""<span class="ui-avatar" style="--ui-avatar-size:{size.ToString(CultureInfo.InvariantCulture)}px" aria-hidden="true">{E(Initials(u.DisplayName, u.UserName))}</span>""";

    // Who is signed in: avatar, display name (the user name when there is none) and "login · role". Not
    // interactive. variant is "menu" (account menu, avatar 40), "sheet" (phone menu, avatar 44, a card of its
    // own) or "settings" (head of the settings, avatar 40; on a phone a card with avatar 48); anything else counts
    // as "menu". extraHtml, if any, goes under a hairline across the card's full width (the theme switch in the
    // phone menu); it is put in as it is, so it has to be markup already.
    internal static string UiUserCard(HttpContext ctx, MatgateUser u, string variant, string extraHtml = "")
    {
        var de = Language(ctx) == "de";
        var kind = (variant ?? "").Trim().ToLowerInvariant() switch
        {
            "sheet" => "sheet",
            "settings" => "settings",
            _ => "menu",
        };
        var name = string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName.Trim();
        var meta = u.UserName + " · " + RoleLabel(de, u);
        var extra = string.IsNullOrWhiteSpace(extraHtml) ? "" : $"""<div class="ui-user-card-extra">{extraHtml}</div>""";
        // The avatar takes its size from the card (--ui-avatar-size per variant), so a phone can make it larger.
        return $"""<div class="ui-user-card ui-user-card--{kind}"><span class="ui-avatar" aria-hidden="true">{E(Initials(u.DisplayName, u.UserName))}</span><span class="ui-user-card-text"><strong title="{A(name)}">{E(name)}</strong><small>{E(meta)}</small></span>{extra}</div>""";
    }
}
