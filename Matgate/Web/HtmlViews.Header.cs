using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The header's menus: the account menu on a wide screen, and the main menu behind the burger - a sheet over the
// whole screen on a phone, a dropdown in the compact view on a wide one. Which entries there are, who sees them and
// where they stand is decided once, here (HeaderMenuEntries), so the renderings cannot drift apart again: the
// settings page used to go by three names ("Konto", "Einstellungen" and the user's own name), and the phone offered
// "Werkzeuge" to people the page then turned away. Both menus are a button and a panel worked by the menu machinery
// of the layout script ([data-menu-toggle]) - a <details> element did not open in the iPhone app installed to the
// home screen. In the shell nothing here navigates except "Abmelden": pages open as tabs of the shell, the theme is
// saved by fetch. CSS prefix hm-.
public sealed partial class HtmlViews
{
    // Key: what the entry is. Group: its group in the main menu (1 work, 2 one's own, 3 managing); 0 means it is no
    // row there. Kind: "files" (a place, opened as a connection tab), "panel" (shows the side panel), "tab" (a page,
    // opened as a tab of the shell), "view" (opens the choice of the view), "theme", "logout", "about".
    internal sealed record HeaderEntry(string Key, int Group, string IconKey, string LabelDe, string LabelEn, string Kind, string Href);

    // Every entry of the header's menus this user may see, in the order of the main menu.
    internal static IReadOnlyList<HeaderEntry> HeaderMenuEntries(HttpContext context, MatgateUser user, string mainClass)
    {
        var entries = new List<HeaderEntry>();
        // The file manager opens one's own place; the select in its bar leads to every other one. Without a place
        // there is no entry.
        var filesAreaId = user.FileShare?.Personal == true
            ? FileShareService.PersonalAreaId(user.Id)
            : user.FileShare?.Global == true
                ? FileShareService.GlobalAreaId
                : (Guid?)null;
        if (filesAreaId is not null)
        {
            entries.Add(new("files", 1, "folder", "Dateien", "Files", "files", $"/connect/{filesAreaId}"));
        }

        // Only the page that has the side panel has the entry (CSS shows it where the panel is a sheet).
        if (string.Equals(mainClass, "session-main", StringComparison.OrdinalIgnoreCase))
        {
            entries.Add(new("panel", 1, "panel-left", "Verbindungen", "Connections", "panel", ""));
        }

        entries.Add(new("credentials", 1, "key", "Zugangsdaten", "Credentials", "tab", "/credentials"));
        entries.Add(new("settings", 2, "settings", "Einstellungen", "Settings", "tab", "/account"));
        // Shown by CSS only in the compact view on a phone, where the view button of the bar is missing.
        entries.Add(new("view", 2, "chevrons-down-up", "Ansicht ...", "View ...", "view", ""));
        // Whoever the administration lets in (RequireServerManagerAsync): those who may create connections, too.
        if (user.IsAdmin || user.CanManageServers || user.CanCreateServers)
        {
            entries.Add(new("admin", 3, "shield", "Administration", "Administration", "tab", "/admin"));
        }

        // The network tools are for administrators only (ToolsAsync turns everybody else away).
        if (user.IsAdmin)
        {
            entries.Add(new("tools", 3, "wrench", "Werkzeuge", "Tools", "tab", "/tools"));
        }

        entries.Add(new("theme", 0, "", "Darstellung", "Appearance", "theme", ""));
        entries.Add(new("logout", 0, "logout", "Abmelden", "Sign out", "logout", "/logout"));
        entries.Add(new("about", 0, "", "Über Matgate", "About Matgate", "about", "/about"));
        return entries;
    }

    // The entries of the given keys or group as links and buttons, in the order of the list. cssClass is the look:
    // "shell-tab" in the bar, "shell-menu-item hm-item" in a menu. role goes on each of them (menuitem in the account
    // menu). An entry is marked active on its own page; in the shell the address is "/" and nothing is.
    internal static string MenuItems(HttpContext context, IEnumerable<HeaderEntry> entries, string cssClass, string requestPath, string role = "")
    {
        var de = Language(context) == "de";
        var roleAttribute = string.IsNullOrEmpty(role) ? "" : " role=\"" + A(role) + "\"";
        return string.Join("", entries.Select(entry =>
        {
            var label = de ? entry.LabelDe : entry.LabelEn;
            var active = entry.Kind == "tab"
                && entry.Href.Length > 1
                && requestPath.StartsWith(entry.Href, StringComparison.OrdinalIgnoreCase);
            var css = cssClass + (active ? " active" : "");
            switch (entry.Kind)
            {
                case "files":
                    // Three ways to the same place: in the shell wireOpenControls takes the click, in an embedded
                    // page the bridge to the shell does, and on a page of its own the link itself carries.
                    var areaId = entry.Href["/connect/".Length..];
                    return $"""<a class="{A(css)}"{roleAttribute} href="{A(entry.Href)}" data-server-id="{A(areaId)}" data-shell-open-server="{A(areaId)}" data-shell-title="{A(label)}">{Icon(entry.IconKey)}<span>{E(label)}</span></a>""";
                case "panel":
                    // "Side panel on/off" where the panel stands beside the page, what is in it where it is a sheet
                    // (CSS picks the one; from 901 px on the view button chooses the panel and the entry is gone).
                    return $"""<button type="button" class="{A(css)} side-panel-entry"{roleAttribute} data-server-manager-menu>{Icon(entry.IconKey)}<span class="side-panel-entry-wide">{(de ? "Seitenleiste ein/aus" : "Toggle side panel")}</span><span class="side-panel-entry-narrow">{E(label)}</span></button>""";
                case "view":
                    return $"""<button type="button" class="{A(css)}"{roleAttribute} data-view-mode-toggle>{Icon(entry.IconKey)}<span>{E(label)}</span></button>""";
                case "tab":
                    return $"""<a class="{A(css)}"{roleAttribute} href="{A(entry.Href)}" data-shell-open-tab="1" data-shell-title="{A(label)}">{Icon(entry.IconKey)}<span>{E(label)}</span></a>""";
                default:
                    return "";
            }
        }));
    }

    // The bar on a wide screen, right of the logo: Files and Tools, Administration, then the account menu.
    internal static string HeaderNavRow(HttpContext context, MatgateUser user, IReadOnlyList<HeaderEntry> entries, string requestPath)
    {
        string Bar(params string[] keys) => MenuItems(context, keys.SelectMany(key => entries.Where(entry => entry.Key == key)), "shell-tab", requestPath);
        return $$"""
            <div class="shell-nav-row">
                <div class="shell-tabs-scroll">
                    <nav class="shell-tabs" aria-label="Primary">
                        {{Bar("files", "tools")}}
                    </nav>
                </div>
                {{Bar("admin")}}
                {{HeaderAccountMenu(context, user, entries, requestPath)}}
            </div>
            """;
    }

    // The account menu: who is signed in, the settings and the stored credentials, light/dark/system at once, the
    // way out, and the version with the way to "About".
    internal static string HeaderAccountMenu(HttpContext context, MatgateUser user, IReadOnlyList<HeaderEntry> entries, string requestPath)
    {
        var de = Language(context) == "de";
        var name = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName.Trim();
        // On the settings page of its own the button is marked as where one is.
        var accountClass = requestPath.StartsWith("/account", StringComparison.OrdinalIgnoreCase) ? " active" : "";
        var menuTitle = de ? "Konto" : "Account";
        string Items(params string[] keys) => MenuItems(context, keys.SelectMany(key => entries.Where(entry => entry.Key == key)), "shell-menu-item hm-item", requestPath, "menuitem");
        return $$"""
            <div class="toolbar-menu shell-menu account-menu{{accountClass}}">
                <button type="button" class="shell-tab shell-menu-trigger account-trigger hm-trigger{{accountClass}}" data-menu-toggle aria-haspopup="menu" aria-expanded="false" aria-controls="hm-account-items" aria-label="{{A((de ? "Konto: " : "Account: ") + name)}}" title="{{A(de ? "Konto und Einstellungen" : "Account and settings")}}">{{UiAvatar(user, 28)}}<span class="account-name">{{E(name)}}</span><span class="menu-caret">{{Icon("chevron-down")}}</span></button>
                <div class="toolbar-menu-panel account-menu-panel hm-panel" data-menu-title="{{A(menuTitle)}}" hidden>
                    {{UiUserCard(context, user, "menu")}}
                    <div id="hm-account-items" class="hm-items" role="menu" aria-label="{{A(menuTitle)}}">
                        {{Items("settings", "credentials")}}
                        <div class="toolbar-menu-separator" role="separator"></div>
                        {{ThemeSwitch(context, user, withText: false)}}
                        <div class="toolbar-menu-separator" role="separator"></div>
                        {{HeaderLogout(context, inMenu: true)}}
                    </div>
                    {{HeaderFoot(context, "hm-foot")}}
                </div>
            </div>
            """;
    }

    // The main menu behind the burger. On a phone a sheet over the whole screen with a head of its own (the logo and
    // the way back), modal while it is open; in the compact view on a wide screen the same panel drops down below the
    // burger in the look of the account menu. The class names stay what the tests know.
    internal static string HeaderBurger(HttpContext context, MatgateUser user, IReadOnlyList<HeaderEntry> entries, string requestPath)
    {
        var menuLabel = T(context, "Menu");
        string Group(int group)
        {
            var rows = MenuItems(context, entries.Where(entry => entry.Group == group), "shell-menu-item hm-item", requestPath);
            return string.IsNullOrEmpty(rows) ? "" : $"""<div class="hm-group">{rows}</div>""";
        }

        return $$"""
            <div class="toolbar-menu shell-menu shell-burger">
                <button type="button" class="shell-tab shell-menu-trigger shell-burger-trigger" data-menu-toggle aria-haspopup="true" aria-expanded="false" aria-label="{{A(menuLabel)}}" title="{{A(menuLabel)}}">{{Icon("menu")}}</button>
                <div class="toolbar-menu-panel shell-burger-panel hm-panel hm-panel--burger" data-menu-own-head aria-label="{{A(menuLabel)}}" hidden>
                    <div class="shell-burger-sheet-head">
                        <span class="shell-burger-brand">{{Logo()}}</span>
                        <button type="button" class="shell-burger-sheet-close" aria-label="{{A(T(context, "Close"))}}" title="{{A(T(context, "Close"))}}" data-burger-close>&times;</button>
                    </div>
                    {{UiUserCard(context, user, "sheet", extraHtml: ThemeSwitch(context, user, withText: true))}}
                    <nav class="hm-groups" aria-label="{{A(menuLabel)}}">{{Group(1)}}{{Group(2)}}{{Group(3)}}</nav>
                    {{HeaderLogout(context, inMenu: false)}}
                    {{HeaderFoot(context, "hm-foot shell-burger-foot")}}
                </div>
            </div>
            """;
    }

    // Light, dark or system - applied at once and saved in the background (the layout script: a click on a
    // [data-theme-choice] inside [data-theme-switch]; it counts as saved only on 204). Buttons, never part of a form,
    // so a broken handler cannot send anything that navigates. [data-menu-keep]: choosing here leaves the menu open,
    // so the look can be tried. withText: the main menu's version, the words beside the icons - a row of its own on a
    // phone, icons only like the account menu in the dropdown of the compact view (the CSS picks).
    internal static string ThemeSwitch(HttpContext context, MatgateUser user, bool withText)
    {
        var de = Language(context) == "de";
        var current = NormalizeThemeCode(user.PreferredTheme);
        var label = de ? "Darstellung" : "Appearance";
        // In the account menu the choices are entries of the menu; in the main menu (no menu in the ARIA sense) they
        // are a plain radio group.
        var itemRole = withText ? "radio" : "menuitemradio";
        var groupRole = withText ? "radiogroup" : "group";
        string Choice(string mode, string iconKey, string name)
        {
            // The name stays on the button as its label: in the dropdown its words are hidden.
            var text = withText ? $"""<span class="hm-theme-text">{E(name)}</span>""" : "";
            var checkedValue = current == mode ? "true" : "false";
            return $"""<button type="button" role="{itemRole}" aria-checked="{checkedValue}" data-theme-choice="{mode}" aria-label="{A(name)}" title="{A(name)}">{Icon(iconKey)}{text}</button>""";
        }

        var choices = Choice("light", "sun", de ? "Hell" : "Light")
            + Choice("dark", "moon", de ? "Dunkel" : "Dark")
            + Choice("system", "monitor", "System");
        return $$"""
            <div class="hm-theme{{(withText ? " hm-theme--text" : "")}}" role="{{groupRole}}" aria-label="{{A(label)}}" data-menu-keep data-theme-switch>
                <span class="hm-theme-label" aria-hidden="true">{{E(label)}}</span>
                <span class="ui-seg{{(withText ? "" : " ui-seg--icons")}}">{{choices}}</span>
                <p class="hm-theme-error" role="status" hidden>{{E(de ? "Nicht gespeichert – bitte noch einmal." : "Not saved – please try again.")}}</p>
            </div>
            """;
    }

    // "Abmelden": a real form, the one thing in the header that leaves the page. Its _csrf is the first one of every
    // page (scripts and tests take it from there).
    private static string HeaderLogout(HttpContext context, bool inMenu)
    {
        var label = Language(context) == "de" ? "Abmelden" : "Sign out";
        var button = inMenu
            ? $"""<button type="submit" role="menuitem" class="shell-menu-item shell-menu-logout hm-item hm-item--danger">{Icon("logout")}<span>{E(label)}</span></button>"""
            : $"""<button type="submit" class="shell-menu-item shell-menu-logout hm-item hm-item--danger">{Icon("logout")}<span>{E(label)}</span></button>""";
        return $"""<form method="post" action="/logout" class="account-menu-logout">{Csrf(context)}{button}</form>""";
    }

    // The version, and the way to the page about it (until now only the status bar's (i) led there).
    private static string HeaderFoot(HttpContext context, string cssClass)
    {
        var about = Language(context) == "de" ? "Über Matgate" : "About Matgate";
        return $"""<div class="{A(cssClass)}"><span>Matgate {E(ApplicationVersion())}</span><span aria-hidden="true">·</span><a href="/about" data-shell-open-tab="1" data-shell-title="{A(about)}">{E(about)}</a></div>""";
    }
}
