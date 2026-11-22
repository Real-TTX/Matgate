using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The settings page (/account) and the pieces it is built from. The pieces are stateless and can be used any number
// of times on a page: ids and names come in as parameters. Their CSS is the block "Part 1 – Settings (prefix set-)"
// in the stylesheet of HtmlViews.cs.
//
// The page has six sections in two groups. From 721 px a rail on the left lists them and the chosen one stands on
// the right; up to 720 px the list of sections is a page of its own (the index) and each section opens as a view of
// its own. Every section is rendered on every address and hidden with the class "hidden"; the generic tab script
// switches between them. Each form ends in a save bar; after a save the server answers with the section, a notice
// and the data the shell around a settings tab takes over at once (window.MatgateSettingsSaved).
public sealed partial class HtmlViews
{
    // One entry of a sortable list: its key, its icon, its name and whether it is switched on.
    internal sealed record OrderItem(string Key, string IconKey, string Label, bool Visible);

    // One section of the settings: its key (?tab=), its group (0 or 1), its icon, its name and the line that says
    // what is set in it (in the list of sections on a phone, as a tooltip in the rail).
    internal sealed record SettingsSection(string Key, int Group, string IconKey, string Label, string Summary);

    // The keys an address may name. "favorites" is another name for the start page's section, scrolled to the
    // favorites.
    private static readonly string[] SettingsKeys = ["profile", "appearance", "security", "home", "session", "keys"];

    // ---- The page ---------------------------------------------------------------------------------------------

    private static string SettingsPage(
        HttpContext context,
        MatgateUser user,
        IReadOnlyList<ServerEndpoint> servers,
        IReadOnlyList<string>? recoveryCodes)
    {
        var de = Language(context) == "de";
        var query = context.Request.Query;
        var asked = query["tab"].ToString().Trim().ToLowerInvariant();
        var scrollTo = "";
        var tab = asked;
        if (tab == "favorites")
        {
            tab = "home";
            scrollTo = "favorites";
        }

        var named = SettingsKeys.Contains(tab);
        if (!named)
        {
            tab = "profile";
        }

        // After the second factor is switched on this page is served directly, as the answer to the code - without
        // this the recovery codes would sit in a hidden section, and nobody would ever see them.
        var showCodes = recoveryCodes is not null && recoveryCodes.Count > 0;
        if (showCodes)
        {
            tab = "security";
            scrollTo = "";
        }

        var sectionView = named || showCodes;
        var saved = query["saved"].ToString().Trim().ToLowerInvariant();
        var favoriteServers = servers
            .Where(server => IsFavoriteServer(user, server.Id))
            .OrderBy(server => string.IsNullOrWhiteSpace(server.FolderName) ? 1 : 0)
            .ThenBy(server => server.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var sections = SettingsSections(context, user, favoriteServers.Count);
        string Title(string key) => sections.First(section => section.Key == key).Label;
        string Saved(params string[] kinds)
        {
            var kind = kinds.FirstOrDefault(candidate => candidate == saved);
            return kind is null ? "" : SetNotice("success", E(SavedText(de, kind)), attributes: "data-settings-notice");
        }

        var panels = string.Join("", new[]
        {
            SettingsPanel(context, "profile", tab == "profile", Title("profile"),
                de ? "Wie du in Matgate heißt und wie man dich erreicht." : "Your name in Matgate and how to reach you.",
                ProfileSection(context, user), Saved("profile")),
            SettingsPanel(context, "appearance", tab == "appearance", Title("appearance"),
                de ? "Hell oder dunkel, Farbschema und eigene Farben – gilt auf jedem Gerät, auf dem du angemeldet bist." : "Light or dark, colour scheme and your own colours – applies on every device you are signed in on.",
                AppearanceSection(context, user), Saved("appearance")),
            SettingsPanel(context, "security", tab == "security", Title("security"),
                de ? "Dein Passwort und die Anmeldung mit zweitem Faktor." : "Your password and signing in with a second factor.",
                SecuritySection(context, user, recoveryCodes), Saved("password", "totp-off") + TotpRecoveryNotice(context, recoveryCodes)),
            SettingsPanel(context, "home", tab == "home", Title("home"),
                de ? "Was die Seite „Neue Verbindung“ zeigt – der Tab mit dem Plus." : "What the “New connection” page shows – the tab with the plus.",
                HomeSection(context, user, servers, favoriteServers), Saved("home", "favorites")),
            SettingsPanel(context, "session", tab == "session", Title("session"),
                de ? "Wie sich deine Sitzungen verhalten – auf jedem Gerät, auf dem du angemeldet bist. Gilt für Sitzungen, die du danach öffnest." : "How your sessions behave – on every device you are signed in on. Applies to sessions you open afterwards.",
                SessionSection(context, user), Saved("session")),
            SettingsPanel(context, "keys", tab == "keys", Title("keys"),
                de ? "Die Knöpfe unter einer Sitzung am Telefon oder Tablet: Tasten, die dessen Tastatur nicht hat, und deine eigenen Tastenkürzel." : "The buttons below a session on a phone or tablet: keys its keyboard does not have, and your own shortcuts.",
                KeyBarPanel(context, user), Saved("keys")),
        });

        return SettingsLayout(context, user, sections, tab, sectionView, panels, interactive: true, scrollTo: scrollTo)
            + $"""<p id="set-order-hint" class="ui-sr">{E(de ? "Mit den Pfeiltasten nach oben oder unten verschieben." : "Move up or down with the arrow keys.")}</p>"""
            + SettingsTexts(de)
            + SettingsSync(context, user, saved)
            + SettingsScript;
    }

    // What the notice after a save says, by what was saved.
    private static string SavedText(bool de, string kind) => kind switch
    {
        "appearance" => de ? "Gespeichert – gilt auf jedem Gerät, auf dem du angemeldet bist." : "Saved – applies on every device you are signed in on.",
        "home" => de ? "Gespeichert – die Startseite nimmt es ab sofort." : "Saved – the start page uses it from now on.",
        "favorites" => de ? "Aus den Favoriten genommen." : "Removed from favorites.",
        "session" or "keys" => de ? "Gespeichert – gilt für Sitzungen, die du ab jetzt öffnest." : "Saved – applies to sessions you open from now on.",
        "password" => de ? "Passwort geändert. Du bleibst angemeldet." : "Password changed. You stay signed in.",
        "totp-off" => de ? "Zwei-Faktor-Anmeldung ausgeschaltet." : "Two-factor sign-in turned off.",
        _ => de ? "Gespeichert." : "Saved.",
    };

    // The six sections with the line that says what is set in each.
    private static IReadOnlyList<SettingsSection> SettingsSections(HttpContext context, MatgateUser user, int favoriteCount)
    {
        var de = Language(context) == "de";
        var themes = context.RequestServices.GetService<ThemeService>();
        var scheme = themes?.Resolve(user.PreferredThemeName).Name ?? "Matgate";
        var mode = NormalizeThemeCode(user.PreferredTheme) switch
        {
            "light" => de ? "Hell" : "Light",
            "dark" => de ? "Dunkel" : "Dark",
            _ => "System",
        };
        var ownColours = !string.IsNullOrWhiteSpace(user.AccentColor)
            || !string.IsNullOrWhiteSpace(user.AccentColor2)
            || !string.IsNullOrWhiteSpace(user.BackgroundColor);
        var twoFactor = user.TotpEnabled
            ? (de ? "Zwei-Faktor an" : "Two-factor on")
            : !string.IsNullOrWhiteSpace(user.TotpSecret)
                ? (de ? "Zwei-Faktor wird eingerichtet" : "Two-factor being set up")
                : (de ? "Zwei-Faktor aus" : "Two-factor off");
        var allSections = HomeLayout.Keys.Length;
        var shownSections = allSections - (user.HiddenHomeSections ?? []).Where(HomeLayout.IsKnown).Distinct(StringComparer.Ordinal).Count();
        var favorites = de
            ? (favoriteCount == 1 ? "1 Favorit" : $"{favoriteCount} Favoriten")
            : (favoriteCount == 1 ? "1 favorite" : $"{favoriteCount} favorites");
        var prefs = user.Session;
        var keep = SessionPreferences.NormalizeKeepSessionMinutes(prefs.KeepSessionMinutes);
        var quality = ScreenQualities(de).FirstOrDefault(choice => choice.Value == SessionPreferences.NormalizeScreenQuality(prefs.ScreenQuality)).Short;
        var session = (de ? "Weiterlaufen: " : "Keep running: ")
            + (keep == 0 ? (de ? "aus" : "off") : KeepSessionLabel(keep, de))
            + (prefs.RestoreAll ? (de ? " · alles wiederherstellen" : " · restore all") : "")
            + (string.IsNullOrEmpty(quality) ? "" : " · " + quality);
        var buttons = KeyBar.For(user).Count;
        var keys = (user.KeyBar is null ? (de ? "Vorgabe · " : "Default · ") : "")
            + (de ? (buttons == 1 ? "1 Knopf" : $"{buttons} Knöpfe") : (buttons == 1 ? "1 button" : $"{buttons} buttons"));
        var who = string.IsNullOrWhiteSpace(user.Email) ? user.UserName : user.Email.Trim();
        var language = NormalizeLanguageCode(user.PreferredLanguage) == "de" ? "Deutsch" : "English";

        return
        [
            new("profile", 0, "user", de ? "Profil" : "Profile", $"{who} · {language}"),
            new("appearance", 0, "palette", de ? "Darstellung" : "Appearance", $"{mode} · {scheme}" + (ownColours ? (de ? " · eigene Farben" : " · own colours") : "")),
            new("security", 0, "lock", de ? "Sicherheit" : "Security", twoFactor),
            new("home", 1, "home", de ? "Startseite" : "Start page", $"{favorites} · " + (de ? $"{shownSections} von {allSections} Bereichen" : $"{shownSections} of {allSections} sections")),
            new("session", 1, "monitor", de ? "Sitzungen" : "Sessions", session),
            new("keys", 1, "keyboard", de ? "Tastenleiste" : "Key bar", keys),
        ];
    }

    // ---- The sections -----------------------------------------------------------------------------------------

    private static string ProfileSection(HttpContext context, MatgateUser user)
    {
        var de = Language(context) == "de";
        var emailError = context.Request.Query["email"].ToString() switch
        {
            "invalid" => de ? "Bitte eine gültige E-Mail-Adresse eingeben." : "Please enter a valid email address.",
            "taken" => de ? "Diese E-Mail-Adresse ist schon vergeben." : "This email address is already in use.",
            _ => "",
        };
        var invalid = emailError.Length > 0 ? " aria-invalid=\"true\" aria-describedby=\"set-email-error\" " : " ";
        var rows = FieldRow("set-display-name",
                de ? "Anzeigename" : "Display name",
                de ? "So erscheinst du in Matgate." : "How you appear in Matgate.",
                $"""<input id="set-display-name" name="displayName" value="{A(user.DisplayName)}">""")
            + FieldRow("set-email",
                de ? "E-Mail (optional)" : "Email (optional)",
                de ? "Steht in der Authenticator-App als Kontoname." : "Shown as the account name in your authenticator app.",
                $"""<input id="set-email" name="email" type="email" maxlength="200"{invalid}value="{A(user.Email)}">""",
                emailError.Length > 0 ? SetNotice("error", E(emailError), "set-email-error") : "")
            + SelectRow("set-language", "preferredLanguage",
                de ? "Sprache" : "Language",
                de ? "Die übrige App wechselt beim nächsten Laden." : "The rest of the app switches the next time it loads.",
                LanguageOptions(context, user.PreferredLanguage))
            + StaticRow(de ? "Benutzername" : "Username",
                $"""<span class="set-mono">{E(user.UserName)}</span>""",
                de ? "Zum Anmelden, lässt sich nicht ändern." : "For signing in; cannot be changed.")
            + StaticRow(de ? "Rolle" : "Role", E(RoleLabel(de, user)));

        return $$"""
            <form id="set-form-profile" method="post" action="/account" class="set-form" data-settings-form>
                {{Csrf(context)}}
                <input type="hidden" name="returnTab" value="profile">
                {{SettingsCard(de ? "Profil" : "Profile", rows)}}
                {{SaveBar(context)}}
            </form>
            """;
    }

    // One form over three cards: the preview script looks up every field of it in the same form, and the server
    // writes the colours only when the colour fields come together.
    private static string AppearanceSection(HttpContext context, MatgateUser user)
    {
        var de = Language(context) == "de";
        var modeTitle = de ? "Hell oder dunkel" : "Light or dark";
        var schemeTitle = de ? "Farbschema" : "Colour scheme";
        var mode = SettingsCard(modeTitle, $$"""
            <div class="ui-seg set-mode-seg" role="radiogroup" aria-label="{{A(modeTitle)}}">{{ThemeModeChooser(context, user.PreferredTheme)}}</div>
            <p class="set-hint">{{E(de ? "System folgt der Einstellung des Geräts." : "System follows the device's setting.")}}</p>
            """);
        var scheme = SettingsCard(schemeTitle,
            $"""<div class="theme-cards set-theme-cards" role="radiogroup" aria-label="{A(schemeTitle)}">{ThemeCards(context, user.PreferredThemeName)}</div>""",
            de ? "Jedes Schema hat einen hellen und einen dunklen Satz – die Wahl oben entscheidet, welcher gilt." : "Every scheme has a light and a dark set – the choice above decides which one applies.");
        var colours = SettingsCard(de ? "Eigene Farben" : "Your own colours",
            ColourGroups(context, user),
            de ? "Statt der Farben des Schemas. Matgate passt sie an, wo sie sonst schlecht lesbar wären." : "Instead of the scheme's colours. Matgate adjusts them where they would be hard to read.");

        return $$"""
            <form id="set-form-appearance" method="post" action="/account" class="set-form" data-settings-form>
                {{Csrf(context)}}
                <input type="hidden" name="returnTab" value="appearance">
                {{mode}}
                {{scheme}}
                {{colours}}
                {{AppearanceScript}}
                {{SaveBar(context, de ? "Vorschau – noch nicht gespeichert" : "Preview – not saved yet")}}
            </form>
            """;
    }

    private static string SecuritySection(HttpContext context, MatgateUser user, IReadOnlyList<string>? recoveryCodes)
    {
        var de = Language(context) == "de";
        var problem = context.Request.Query["pw"].ToString();
        string Error(string code, string id) => problem == code
            ? SetNotice("error", E(code switch
            {
                "current" => de ? "Das aktuelle Passwort stimmt nicht." : "The current password is not right.",
                "short" => de ? "Das neue Passwort braucht mindestens 8 Zeichen." : "The new password needs at least 8 characters.",
                _ => de ? "Die beiden neuen Passwörter sind nicht gleich." : "The two new passwords do not match.",
            }), id + "-error")
            : "";
        var rows = FieldRow("set-pw-current",
                de ? "Aktuelles Passwort" : "Current password", "",
                PasswordField(de, "set-pw-current", "currentPassword", "current-password", problem == "current"),
                Error("current", "set-pw-current"))
            + FieldRow("set-pw-new",
                de ? "Neues Passwort" : "New password", "",
                PasswordField(de, "set-pw-new", "newPassword", "new-password", problem == "short", minLength: 8),
                Error("short", "set-pw-new"))
            + FieldRow("set-pw-confirm",
                de ? "Neues Passwort wiederholen" : "Repeat the new password", "",
                PasswordField(de, "set-pw-confirm", "confirmPassword", "new-password", problem == "mismatch", minLength: 8)
                    + """<p class="set-match" data-password-match aria-live="polite"></p>""",
                Error("mismatch", "set-pw-confirm"));
        var password = SettingsCard(de ? "Passwort ändern" : "Change password", rows,
            de ? "Mindestens 8 Zeichen. Du bleibst danach angemeldet." : "At least 8 characters. You stay signed in afterwards.",
            footHtml: $"""<button type="submit" class="button primary">{Icon("key")}<span>{E(de ? "Passwort ändern" : "Change password")}</span></button>""");

        // The unnamed user name first: a password manager files the new password under the right account.
        return $$"""
            <form id="set-form-password" method="post" action="/account/password" class="set-form">
                <input type="text" autocomplete="username" value="{{A(user.UserName)}}" readonly hidden>
                {{Csrf(context)}}
                {{password}}
            </form>
            {{TotpPanel(context, user, recoveryCodes)}}
            """;
    }

    // A password field with a button beside it that shows what was typed (and hides it again).
    private static string PasswordField(bool de, string id, string name, string autocomplete, bool invalid, int minLength = 0)
    {
        var show = de ? "Passwort zeigen" : "Show password";
        var described = invalid ? " aria-invalid=\"true\" aria-describedby=\"" + A(id) + "-error\" " : " ";
        var min = minLength > 0 ? " minlength=\"" + minLength.ToString(CultureInfo.InvariantCulture) + "\" " : " ";
        return $"""<div class="set-field-eye"><input id="{A(id)}" type="password" name="{A(name)}" autocomplete="{A(autocomplete)}"{min}required{described}spellcheck="false"><button type="button" class="set-eye" aria-pressed="false" aria-controls="{A(id)}" aria-label="{A(show)}" title="{A(show)}"><span class="set-eye-show">{Icon("eye")}</span><span class="set-eye-hide">{Icon("eye-off")}</span></button></div>""";
    }

    // The recovery codes, shown once right after the second factor was switched on - at the top of the section,
    // so they are the first thing seen.
    private static string TotpRecoveryNotice(HttpContext context, IReadOnlyList<string>? recoveryCodes)
    {
        if (recoveryCodes is null || recoveryCodes.Count == 0)
        {
            return "";
        }

        var de = Language(context) == "de";
        return SetNotice("warning", $$"""
            <strong>{{E(de ? "Deine Wiederherstellungs-Codes" : "Your recovery codes")}}</strong>
            <p>{{E(de
                ? "Schreib sie auf und leg sie weg. Jeder gilt einmal und ersetzt den Code aus der App, wenn das Telefon weg ist. Ein zweites Mal kann sie niemand anzeigen - gespeichert sind nur ihre Abdrücke."
                : "Write them down and put them away. Each works once and stands in for the app's code when the phone is gone. Nobody can show them a second time - only their fingerprints are stored.")}}</p>
            <ul class="totp-codes" id="set-totp-codes">{{string.Join("", recoveryCodes.Select(code => $"<li>{E(code)}</li>"))}}</ul>
            <div class="set-notice-actions"><button type="button" class="button" data-copy-from="#set-totp-codes">{{Icon("copy")}}<span>{{E(de ? "Codes kopieren" : "Copy codes")}}</span></button></div>
            """, "set-totp-codes-notice");
    }

    private static string HomeSection(HttpContext context, MatgateUser user, IReadOnlyList<ServerEndpoint> servers, IReadOnlyList<ServerEndpoint> favoriteServers)
    {
        var de = Language(context) == "de";
        var prefs = user.Session;
        // Not the start page's star form: that one is placed on a card's corner, which there is none of here.
        var returnUrl = "/account?tab=favorites&saved=favorites";
        string Favorite(ServerEndpoint server)
        {
            var remove = de ? $"„{server.Name}“ aus den Favoriten nehmen" : $"Remove “{server.Name}” from favorites";
            var where = string.IsNullOrWhiteSpace(server.FolderName)
                ? ServerProtocolLabel(server.Protocol)
                : $"{server.FolderName} · {ServerProtocolLabel(server.Protocol)}";
            return $"""<li class="set-fav"><span class="set-fav-tile" style="--proto: {ProtocolAccent(server.Protocol)}" aria-hidden="true">{Icon(ServerEndpoint.EffectiveIconKey(server.Protocol, server.IconKey))}</span><span class="set-fav-text"><strong>{E(server.Name)}</strong><small>{E(where)}</small></span><form method="post" action="/account/favorites/{server.Id}/toggle">{Csrf(context)}<input type="hidden" name="returnUrl" value="{A(returnUrl)}"><button type="submit" class="set-icon-button is-on" title="{A(remove)}" aria-label="{A(remove)}">{Icon("star")}</button></form></li>""";
        }

        var favorites = favoriteServers.Count == 0
            ? $"""<p class="set-empty">{E(de ? "Noch keine Favoriten – markiere eine Verbindung mit dem Stern." : "No favorites yet – mark a connection with the star.")}</p>"""
            : $"""<ul class="set-fav-list">{string.Join("", favoriteServers.Select(Favorite))}</ul>""";
        var favoritesCard = SettingsCard(de ? "Favoriten" : "Favorites", favorites,
            de ? "Stehen auf der Startseite und in der Seitenleiste oben. Den Stern setzt du an einer Verbindung." : "They come first on the start page and in the side panel. You set the star on a connection.",
            id: "favorites",
            pillHtml: SetPill(de ? "Gilt sofort" : "Applies at once", "accent"));

        // Every list is built once from its items: the rows and the two hidden values come from the same list.
        var sectionsCard = SettingsCard(de ? "Bereiche" : "Sections",
            OrderList("home-order-list", "homeSections", "hiddenHomeSections", "data-order-key", "data-order-visible",
                HomeSectionItems(de, user), string.Join(",", HomeLayout.Keys), de),
            de ? "Ziehen zum Sortieren, mit dem Schalter zeigen oder verbergen. Ein Bereich ohne Inhalt bleibt ohnehin weg." : "Drag to sort, switch to show or hide. A section with nothing in it stays away anyway.");
        // The chips only for someone who may connect quickly; for everyone else the list is not sent, and the stored
        // order stays as it is.
        var quickCard = user.IsAdmin || user.CanQuickConnect
            ? SettingsCard(de ? "Schnell verbinden" : "Quick connect",
                OrderList("quick-order-list", "quickProtocols", "hiddenQuickProtocols", "data-order-key", "data-order-visible",
                    QuickOrderItems(de, prefs), string.Join(",", SessionPreferences.QuickProtocolKeys), de),
                de ? "Welche Arten die Startseite und der Dialog „Schnell verbinden“ anbieten, und in welcher Reihenfolge." : "Which kinds the start page and the quick-connect dialog offer, and in which order.")
            : "";
        // With many connections a filter above the list (no name: it is not sent; Enter does not save).
        var actionBarItems = ActionBarItems(user, servers);
        var filterText = de ? "Verbindungen filtern …" : "Filter connections …";
        var filter = actionBarItems.Count > 8
            ? $"""<input type="search" class="set-filter" data-order-filter placeholder="{A(filterText)}" aria-label="{A(filterText)}" aria-controls="action-bar-list" autocomplete="off">"""
            : "";
        var actionBarCard = SettingsCard(de ? "Knöpfe in der Aktionsleiste" : "Buttons in the action bar",
            filter + OrderList("action-bar-list", "actionBarServers", "hiddenActionBarServers", "data-order-key", "data-order-visible",
                actionBarItems, null, de, de ? "Noch keine Verbindungen." : "No connections yet."),
            de ? "Eingeschaltete Verbindungen stehen als Knopf oben in der Leiste, solange die Startseite vorn ist – ein Druck verbindet." : "Connections switched on stand as a button in the bar at the top while the start page is in front – one press connects.");
        var defaultCard = user.IsAdmin
            ? SettingsCard(de ? "Vorgabe für neue Benutzer" : "Default for new users",
                CheckRow("asDefault",
                    de ? "Beim Speichern als Vorgabe für neue Benutzer übernehmen" : "When saving, also make this the default for new users",
                    de ? "Wer schon da ist, behält seine eigene Anordnung." : "Everyone already here keeps their own arrangement."))
            : "";

        return $$"""
            {{favoritesCard}}
            <form id="set-form-home" method="post" action="/account/home" class="set-form" data-settings-form>
                {{Csrf(context)}}
                {{sectionsCard}}
                {{quickCard}}
                {{actionBarCard}}
                {{defaultCard}}
                {{SaveBar(context)}}
            </form>
            """;
    }

    // One form: the server overwrites every switch of it, so all of them stay in it.
    private static string SessionSection(HttpContext context, MatgateUser user)
    {
        var de = Language(context) == "de";
        var prefs = user.Session;
        var keep = SessionPreferences.NormalizeKeepSessionMinutes(prefs.KeepSessionMinutes);
        var keepOptions = string.Join("", SessionPreferences.KeepSessionChoices.Select(minutes =>
            $"""<option value="{minutes.ToString(CultureInfo.InvariantCulture)}"{Selected(keep == minutes)}>{E(KeepSessionLabel(minutes, de))}</option>"""));

        var picture = SettingsCard(de ? "Bild & Tempo" : "Picture & speed",
            SelectRow("set-screen-quality", "screenQuality",
                de ? "Bildqualität beim Start" : "Picture quality at the start",
                de
                    ? "Für RDP, VNC und Bildschirme über den Agent. „Wie zuletzt“: jedes Gerät merkt sich die Wahl aus dem Fenster „Anzeige“ – gut, wenn Handy und PC an verschieden schnellen Leitungen hängen. Eine feste Wahl gilt für jede neue Sitzung auf jedem Gerät."
                    : "For RDP, VNC and screens through the agent. “As last used”: each device remembers the choice from a session's “Display” dialog – good when phone and PC sit on connections of different speed. A fixed choice applies to every new session on every device.",
                ScreenQualityOptions(de, prefs.ScreenQuality)),
            id: "screen-quality");
        var keyboard = SettingsCard(de ? "Tastatur" : "Keyboard",
            SwitchRow("systemCombos", prefs.SystemCombos,
                de ? "Browser-Tasten durchreichen (Vollbild)" : "Pass browser keys through (fullscreen)",
                de ? "Im Vollbild gehen Windows-Taste, Alt+Tab, Alt+F4, Strg+W/T usw. an die Sitzung statt an den Browser." : "In fullscreen the Windows key, Alt+Tab, Alt+F4, Ctrl+W/T etc. go to the session instead of the browser.")
            + SwitchRow("ctrlAltDelHotkey", prefs.CtrlAltDelHotkey,
                de ? "Strg+Alt+Entf als Knopf" : "Ctrl+Alt+Del button",
                de ? "Zusätzlich zur Bildschirmtastatur als Knopf in der Sitzungsleiste." : "Also as a button in the session bar, besides the on-screen keyboard."),
            de ? "Gilt für alle Sitzungen." : "Applies to all sessions.");
        var browserGone = SettingsCard(de ? "Wenn der Browser zugeht" : "When the browser goes away",
            SelectRow("set-keep-session", "keepSessionMinutes", de ? "Weiterlaufen lassen" : "Keep running for", "", keepOptions)
            + SwitchRow("restoreAll", prefs.RestoreAll,
                de ? "Beim Start immer alles wiederherstellen" : "Always restore everything on start",
                de
                    ? $"Alle Tabs auf jedem Gerät gleich, auch nach einem Update; der zuletzt aktive Tab kommt wieder nach vorn, und die Sitzungen laufen mindestens {SessionPreferences.RestoreAllMinutes} Minuten weiter - die App am Handy schließen und wieder öffnen findet sie, wie sie waren."
                    : $"All tabs the same on every device, also after an update; the tab last in front comes to the front again, and the sessions run on for at least {SessionPreferences.RestoreAllMinutes} minutes - closing the app on the phone and opening it again finds them as they were."),
            de
                ? "Neu geladen, Netz weg, anderes Gerät: so lange laufen RDP-, VNC- und SSH-Sitzungen über Matgates eigene Engine weiter. Ein geschlossener Tab beendet seine Sitzung sofort; Guacamole-Sitzungen verbinden sich neu."
                : "Reloaded, network lost, another device: this is how long RDP, VNC and SSH sessions through Matgate's own engine keep running. Closing a tab ends its session at once; Guacamole sessions reconnect.");
        var bar = SettingsCard(de ? "Sitzungsleiste" : "Session bar",
            OrderList("action-order-list", "actionOrder", "hiddenActions", "data-action", "data-action-visible",
                ActionOrderItems(de, prefs), string.Join(",", SessionPreferences.SortableActions), de),
            de
                ? "Welche Knöpfe über einer Sitzung stehen, und in welcher Reihenfolge. Auf schmalen Schirmen kommt der Rest ins „…“-Menü; Trennen steht immer rechts."
                : "Which buttons sit above a session, and in which order. On narrow screens the rest goes into the “…” menu; Disconnect always stays on the right.");
        var guacamole = SettingsCard(de ? "Nur für Guacamole" : "Guacamole only",
            SwitchRow("edgePanning", prefs.EdgePanning,
                de ? "Ausschnitt am Fensterrand verschieben" : "Pan at the window edge",
                de ? "Bei fester Auflösung: Maus an den Rand, der Ausschnitt wandert mit." : "With a fixed resolution: move the mouse to the edge and the view follows.")
            + SwitchRow("dragPanning", prefs.DragPanning,
                de ? "Mit der mittleren Maustaste verschieben" : "Pan with the middle mouse button",
                de ? "Bei fester Auflösung den Ausschnitt mit gedrückter mittlerer Taste ziehen." : "With a fixed resolution, drag the view while holding the middle button.")
            + SwitchRow("stretchToWindow", prefs.StretchToWindow,
                de ? "Bild aufs Fenster strecken" : "Stretch the picture to the window",
                de ? "Füllt das ganze Fenster, kann leicht verzerren." : "Fills the whole window, may distort slightly.")
            + SwitchRow("functionKeys", prefs.FunctionKeys,
                de ? "F1–F12 auf der Bildschirmtastatur" : "F1–F12 on the on-screen keyboard",
                de ? "Eine zusätzliche Reihe mit Funktionstasten." : "An extra row of function keys.")
            + SwitchRow("pasteAsKeystrokes", prefs.PasteAsKeystrokes,
                de ? "Einfügen als Tastatureingaben" : "Paste as keystrokes",
                de ? "Text wird Zeichen für Zeichen getippt statt über die Zwischenablage geschickt - nötig z. B. bei SSH-Terminals." : "Text is typed character by character instead of sent over the clipboard - needed e.g. for SSH terminals."),
            de ? "Wirkt nur in Verbindungen, die mit Guacamole geöffnet werden." : "Only works in connections opened with Guacamole.",
            pillHtml: SetPill("Guacamole", "neutral"));

        return $$"""
            <form id="set-form-session" method="post" action="/account/session" class="set-form" data-settings-form>
                {{Csrf(context)}}
                {{picture}}
                {{keyboard}}
                {{browserGone}}
                {{bar}}
                {{guacamole}}
                {{SaveBar(context)}}
            </form>
            """;
    }

    // ---- The building blocks ----------------------------------------------------------------------------------

    // The frame of the settings: a rail with the title, the user card and the sections; next to it (on a phone:
    // instead of it) the panels. With interactive false (the pages of one key-bar button) the sections are plain
    // links: the tab script is not wired, so it does not catch them.
    internal static string SettingsLayout(
        HttpContext ctx,
        MatgateUser user,
        IReadOnlyList<SettingsSection> sections,
        string activeKey,
        bool sectionView,
        string panelsHtml,
        bool interactive = true,
        string scrollTo = "")
    {
        var de = Language(ctx) == "de";
        var settings = de ? "Einstellungen" : "Settings";
        string Item(SettingsSection section)
        {
            var active = section.Key == activeKey;
            var target = interactive ? " data-tab-target=\"" + A(section.Key) + "\"" : "";
            var current = active ? " aria-current=\"page\"" : "";
            return $"""<a class="tab-button set-nav-item{(active ? " active" : "")}" href="/account?tab={A(section.Key)}"{target}{current} title="{A(section.Summary)}"><span class="set-nav-tile" aria-hidden="true">{Icon(section.IconKey)}</span><span class="set-nav-text"><span class="set-nav-label">{E(section.Label)}</span><span class="set-nav-summary">{E(section.Summary)}</span></span><span class="set-nav-chevron" aria-hidden="true">{Icon("chevron-right")}</span></a>""";
        }

        var groups = string.Join("", sections
            .GroupBy(section => section.Group)
            .OrderBy(group => group.Key)
            .Select(group => $"""<div class="set-nav-group">{string.Join("", group.Select(Item))}</div>"""));
        var scroll = string.IsNullOrWhiteSpace(scrollTo) ? "" : " data-settings-scroll=\"" + A(scrollTo) + "\"";

        return $$"""
            <div class="set-layout"{{(interactive ? " data-tabs" : "")}} data-settings data-settings-view="{{(sectionView ? "section" : "index")}}"{{scroll}}>
                <aside class="set-aside">
                    <h1 class="set-title">{{E(settings)}}</h1>
                    {{UiUserCard(ctx, user, "settings")}}
                    <nav class="set-nav" aria-label="{{A(settings)}}">{{groups}}</nav>
                </aside>
                <div class="set-main">{{panelsHtml}}</div>
            </div>
            """;
    }

    // One section: the way back to the list (on a phone), its title and lead, a notice, its content, and the way
    // back once more at its end. backHref/backLabel give a way back that is always shown (the key-bar button page).
    internal static string SettingsPanel(
        HttpContext ctx,
        string key,
        bool active,
        string title,
        string lead,
        string bodyHtml,
        string noticeHtml = "",
        string backHref = "",
        string backLabel = "")
    {
        var de = Language(ctx) == "de";
        var always = !string.IsNullOrWhiteSpace(backHref);
        var top = always
            ? $"""<a class="set-back set-back--always" href="{A(backHref)}">{Icon("arrow-left")}<span>{E(backLabel)}</span></a>"""
            : $"""<a class="set-back" href="/account" data-settings-back aria-label="{A(de ? "Zurück zu den Einstellungen" : "Back to the settings")}">{Icon("arrow-left")}<span>{E(de ? "Einstellungen" : "Settings")}</span></a>""";
        var end = always
            ? ""
            : $"""<a class="set-back set-back--end" href="/account" data-settings-back>{Icon("arrow-left")}<span>{E(de ? "Zurück zu den Einstellungen" : "Back to the settings")}</span></a>""";
        var leadHtml = string.IsNullOrWhiteSpace(lead) ? "" : $"""<p class="set-lead">{E(lead)}</p>""";

        return $$"""
            <section class="tab-panel set-panel{{(active ? "" : " hidden")}}" data-tab-panel="{{A(key)}}" aria-labelledby="set-title-{{A(key)}}">
                {{top}}
                <div class="set-head">
                    <h2 class="set-section-title" id="set-title-{{A(key)}}" tabindex="-1">{{E(title)}}</h2>
                    {{leadHtml}}
                </div>
                {{noticeHtml}}
                {{bodyHtml}}
                {{end}}
            </section>
            """;
    }

    // A card of a section: a title (with a pill beside it), a lead, the rows, and a foot for its buttons.
    internal static string SettingsCard(
        string title,
        string bodyHtml,
        string lead = "",
        string id = "",
        string pillHtml = "",
        string footHtml = "",
        string attributes = "")
    {
        var idAttr = string.IsNullOrWhiteSpace(id) ? "" : " id=\"" + A(id) + "\"";
        var extra = string.IsNullOrWhiteSpace(attributes) ? "" : " " + attributes.Trim();
        var leadHtml = string.IsNullOrWhiteSpace(lead) ? "" : $"""<p class="set-card-lead">{E(lead)}</p>""";
        var foot = string.IsNullOrWhiteSpace(footHtml) ? "" : $"""<div class="set-card-foot">{footHtml}</div>""";
        return $$"""
            <section class="set-card"{{idAttr}}{{extra}}>
                <div class="set-card-head"><h3 class="set-card-title">{{E(title)}}</h3>{{pillHtml}}</div>
                {{leadHtml}}
                <div class="set-card-body">{{bodyHtml}}</div>
                {{foot}}
            </section>
            """;
    }

    // Something that is on or off. The checkbox has no value of its own: the session form's server side accepts only
    // "on", and there is no hidden "false" next to it either - a box that is not ticked is simply not sent.
    internal static string SwitchRow(string name, bool on, string title, string desc)
    {
        var small = string.IsNullOrWhiteSpace(desc) ? "" : $"<small>{E(desc)}</small>";
        return $"""<label class="set-row set-row--switch"><input type="checkbox" name="{A(name)}"{Checked(on)}><span class="set-copy"><strong>{E(title)}</strong>{small}</span><span class="set-switch" aria-hidden="true"></span></label>""";
    }

    // A choice out of a list: the copy on the left, the browser's own select on the right (under the copy on a
    // narrow screen). The options come with "selected" from the server, so a form sent without a script carries it.
    internal static string SelectRow(string id, string name, string title, string desc, string optionsHtml)
    {
        var small = string.IsNullOrWhiteSpace(desc) ? "" : $"<small>{E(desc)}</small>";
        return $"""<div class="set-row set-row--select"><label class="set-copy" for="{A(id)}"><strong>{E(title)}</strong>{small}</label><select id="{A(id)}" name="{A(name)}">{optionsHtml}</select></div>""";
    }

    // A field with its copy; errorHtml (a notice) stands above both.
    internal static string FieldRow(string id, string title, string desc, string controlHtml, string errorHtml = "")
    {
        var small = string.IsNullOrWhiteSpace(desc) ? "" : $"<small>{E(desc)}</small>";
        return $"""<div class="set-row set-row--field">{errorHtml}<label class="set-copy" for="{A(id)}"><strong>{E(title)}</strong>{small}</label><div class="set-control">{controlHtml}</div></div>""";
    }

    // Something that is shown, not changed.
    internal static string StaticRow(string title, string valueHtml, string desc = "")
    {
        var small = string.IsNullOrWhiteSpace(desc) ? "" : $"<small>{E(desc)}</small>";
        return $"""<div class="set-row set-row--static"><span class="set-copy"><strong>{E(title)}</strong>{small}</span><span class="set-value">{valueHtml}</span></div>""";
    }

    // A one-off choice made when saving (it is not stored, so a check, not a switch).
    internal static string CheckRow(string name, string text, string desc)
    {
        var small = string.IsNullOrWhiteSpace(desc) ? "" : $"<small>{E(desc)}</small>";
        return $"""<label class="check set-row--check"><input type="checkbox" name="{A(name)}"><span class="set-copy"><strong>{E(text)}</strong>{small}</span></label>""";
    }

    // The end of a form: save, discard (only once something changed) and what is not saved yet.
    internal static string SaveBar(HttpContext ctx, string dirtyText = "")
    {
        var de = Language(ctx) == "de";
        var dirty = string.IsNullOrWhiteSpace(dirtyText) ? (de ? "Ungespeicherte Änderungen" : "Unsaved changes") : dirtyText;
        return $"""<div class="set-savebar" data-savebar><button type="submit" class="button primary">{Icon("save")}<span>{E(T(ctx, "Save"))}</span></button><button type="button" class="button" data-settings-discard disabled>{Icon("refresh")}<span>{E(de ? "Verwerfen" : "Discard")}</span></button><span class="set-savebar-status" aria-live="polite" data-dirty-text="{A(dirty)}"></span></div>""";
    }

    // A message in a section: saved (success), a hint (info), take care (warning), went wrong (error). html is put
    // in as it is - encode what comes from outside.
    internal static string SetNotice(string tone, string html, string id = "", string attributes = "")
    {
        var kind = tone is "success" or "info" or "warning" or "error" ? tone : "info";
        var icon = kind switch
        {
            "success" => "check",
            "error" => "x",
            _ => "info",
        };
        var idAttr = string.IsNullOrWhiteSpace(id) ? "" : " id=\"" + A(id) + "\"";
        var extra = string.IsNullOrWhiteSpace(attributes) ? "" : " " + attributes.Trim();
        return $"""<div class="set-notice set-notice--{kind}" role="{(kind == "error" ? "alert" : "status")}"{idAttr}{extra}>{Icon(icon)}<div class="set-notice-body">{html}</div></div>""";
    }

    // A small label beside a card's title: neutral, accent, success or warning.
    internal static string SetPill(string text, string tone)
    {
        var kind = tone is "neutral" or "accent" or "success" or "warning" ? tone : "neutral";
        return $"""<span class="set-pill set-pill--{kind}">{E(text)}</span>""";
    }

    // A list to sort and to switch entries on and off: the start page's sections, the quick-connect chips, the
    // session bar's actions, the action bar's connections. The hidden fields carry the order and what is switched
    // off exactly as the script writes them - filled here already, so a form sent without the script (or before it
    // ran) sends the real values instead of empty ones, which would reset the list. The grip is the handle for
    // dragging and, with the arrow keys, for moving; the rest of a row scrolls the page on a phone.
    internal static string OrderList(
        string id,
        string valueName,
        string hiddenName,
        string keyAttr,
        string visibleAttr,
        IReadOnlyList<OrderItem> items,
        string? defaultOrder,
        bool de,
        string emptyText = "")
    {
        var rows = items.Count == 0 && emptyText.Length > 0
            ? $"""<li class="action-order-item action-order-empty"><span class="action-order-name muted">{E(emptyText)}</span></li>"""
            : string.Join("", items.Select(item => $$"""
                <li class="action-order-item" {{keyAttr}}="{{A(item.Key)}}"><button type="button" class="action-order-grip" aria-label="{{A(de ? $"„{item.Label}“ verschieben" : $"Move “{item.Label}”")}}" aria-describedby="set-order-hint">{{Icon("menu")}}</button><span class="action-order-icon" aria-hidden="true">{{Icon(item.IconKey)}}</span><span class="action-order-name">{{E(item.Label)}}</span><label class="action-order-show"><input type="checkbox" {{visibleAttr}}{{(item.Visible ? " checked" : "")}} aria-label="{{A(de ? $"„{item.Label}“ zeigen" : $"Show “{item.Label}”")}}"><span class="set-mini-switch" aria-hidden="true"></span></label></li>
                """));
        var defaultAttr = string.IsNullOrWhiteSpace(defaultOrder) ? "" : $" data-default-order=\"{A(defaultOrder)}\"";
        var reset = string.IsNullOrWhiteSpace(defaultOrder)
            ? ""
            : $$"""<button type="button" class="button order-reset" data-order-reset title="{{A(de ? "Setzt die Liste zurück und speichert sofort." : "Resets the list and saves at once.")}}">{{Icon("refresh")}}<span>{{(de ? "Standard wiederherstellen" : "Restore default")}}</span></button>""";
        // The same two values the script's sync() writes: every key in the order shown, and the switched-off ones.
        var value = string.Join(",", items.Select(item => item.Key));
        var hidden = string.Join(",", items.Where(item => !item.Visible).Select(item => item.Key));
        return $$"""
            <div data-order-group>
                <ol id="{{A(id)}}" class="action-order-list" data-order-list{{defaultAttr}}>{{rows}}</ol>
                {{reset}}
                <input type="hidden" name="{{A(valueName)}}" data-order-value value="{{A(value)}}">
                <input type="hidden" name="{{A(hiddenName)}}" data-order-hidden value="{{A(hidden)}}">
                <p class="ui-sr" aria-live="polite" data-order-live></p>
            </div>
            """;
    }

    // ---- After a save: the data the shell takes over -----------------------------------------------------------

    // What the shell around a settings tab takes over without a reload, by what was saved. The theme is worked out
    // exactly as /api/theme/preview does it, from the palette and the colours just saved.
    private static string SettingsSync(HttpContext context, MatgateUser user, string saved)
    {
        var de = Language(context) == "de";
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var actionBar = new JsonArray();
        foreach (var id in user.ActionBarServerIds ?? [])
        {
            actionBar.Add(id.ToString());
        }

        JsonObject? data = saved switch
        {
            "appearance" => new JsonObject { ["theme"] = ThemeSync(context, user) },
            "home" or "favorites" => new JsonObject { ["actionBar"] = actionBar, ["refreshPanel"] = true },
            "session" => new JsonObject { ["sessionPrefs"] = JsonSerializer.SerializeToNode(user.Session, web) },
            "keys" => new JsonObject { ["keyBar"] = JsonNode.Parse(KeyBar.GuacamoleJson(user, de)) },
            "profile" => new JsonObject { ["language"] = NormalizeLanguageCode(user.PreferredLanguage) },
            _ => null,
        };

        // The default encoder escapes "<" and "&", so nothing in the data can end the script element.
        return data is null ? "" : $"""<script type="application/json" id="set-sync">{data.ToJsonString()}</script>""";
    }

    private static JsonObject ThemeSync(HttpContext context, MatgateUser user)
    {
        var themes = context.RequestServices.GetService<ThemeService>();
        var palette = themes?.Resolve(user.PreferredThemeName);
        JsonObject Set(bool dark)
        {
            var set = new JsonObject();
            if (themes is null || palette is null)
            {
                return set;
            }

            foreach (var entry in themes
                .Values(palette, dark, user.AccentColor, user.AccentColor2, user.BackgroundColor)
                .Where(entry => ThemeService.Accept(entry.Key, entry.Value)))
            {
                set["--" + entry.Key] = entry.Value;
            }

            return set;
        }

        return new JsonObject
        {
            ["mode"] = NormalizeThemeCode(user.PreferredTheme),
            ["light"] = Set(false),
            ["dark"] = Set(true),
        };
    }

    // The words the page script needs, in the user's language. %1, %2 ... stand for what is put in.
    private static string SettingsTexts(bool de)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["unsaved"] = de ? "Ungespeicherte Änderungen" : "Unsaved changes",
            ["guardTitle"] = de ? "Ungespeicherte Änderungen" : "Unsaved changes",
            ["guardText"] = de
                ? "In „%1“ ist noch etwas nicht gespeichert. Es geht verloren, wenn du jetzt speicherst."
                : "“%1” has changes that are not saved. They are lost if you save now.",
            ["guardConfirm"] = de ? "Trotzdem speichern" : "Save anyway",
            ["position"] = de ? "„%1“: Platz %2 von %3" : "“%1”: position %2 of %3",
            ["showPassword"] = de ? "Passwort zeigen" : "Show password",
            ["hidePassword"] = de ? "Passwort verbergen" : "Hide password",
            ["copied"] = de ? "Kopiert" : "Copied",
            ["matches"] = de ? "Stimmt überein" : "Matches",
            ["noMatch"] = de ? "Stimmt noch nicht überein" : "Does not match yet",
        };
        return $"""<script type="application/json" id="set-texts">{JsonSerializer.Serialize(texts)}</script>""";
    }

    // ---- Lists and choices -----------------------------------------------------------------------------------

    // The picture quality a new session starts with (Settings > Sessions > Picture & speed), with its label and a
    // short title for the summary. Empty: each device keeps the choice made in a session's "Display" dialog.
    private static (string Value, string Label, string Short)[] ScreenQualities(bool de) =>
    [
        ("", de ? "Wie zuletzt auf diesem Gerät" : "As last used on this device", ""),
        ("auto", de ? "Flüssig (60 Bilder/s)" : "Smooth (60 fps)", de ? "Flüssig" : "Smooth"),
        ("medium", de ? "Mittel (30 Bilder/s)" : "Medium (30 fps)", de ? "Mittel" : "Medium"),
        ("low", de ? "Wenig Daten (20 Bilder/s)" : "Low data (20 fps)", de ? "Wenig Daten" : "Low data"),
        ("fast", de ? "Schnellste Reaktion (weniger Farben)" : "Fastest response (fewer colours)", de ? "Schnellste Reaktion" : "Fastest response"),
    ];

    private static string ScreenQualityOptions(bool de, string? selected)
    {
        var chosen = SessionPreferences.NormalizeScreenQuality(selected);
        return string.Join("", ScreenQualities(de).Select(choice =>
            $"""<option value="{A(choice.Value)}"{(choice.Value == chosen ? " selected" : "")}>{E(choice.Label)}</option>"""));
    }

    // The quick-connect chips in the user's order; switched on means shown on the start page.
    private static List<OrderItem> QuickOrderItems(bool de, SessionPreferences prefs)
    {
        var hidden = prefs.HiddenQuickProtocols ?? [];
        return [.. QuickProtocols(de, null, prefs.QuickProtocolOrder)
            .Select(protocol => new OrderItem(protocol.Protocol, protocol.IconKey, protocol.Name, !hidden.Contains(protocol.Protocol)))];
    }

    // The session bar's actions: the user's own order first, then everything they never moved, in its built-in
    // place - so an action added later shows up at the end instead of being missing because the saved order
    // predates it. The icons are the toolbar's, so a row is recognised at a glance instead of being read.
    private static List<OrderItem> ActionOrderItems(bool de, SessionPreferences prefs)
    {
        var labels = new Dictionary<string, (string Label, string Icon)>(StringComparer.Ordinal)
        {
            ["fullscreen"] = (de ? "Vollbild" : "Fullscreen", "maximize"),
            ["popOut"] = (de ? "In eigenem Fenster öffnen" : "Open in a new window", "external-link"),
            ["reattach"] = (de ? "Fenster zurückholen" : "Re-attach window", "arrow-left"),
            ["pointer"] = (de ? "Zeigermodus" : "Pointer mode", "pointer"),
            ["rightClick"] = (de ? "Rechtsklick" : "Right click", "menu"),
            ["keyboard"] = (de ? "Gerätetastatur" : "Device keyboard", "keyboard"),
            ["osk"] = (de ? "Bildschirmtastatur" : "On-screen keyboard", "grid"),
            ["resolution"] = (de ? "Auflösung" : "Resolution", "monitor"),
            ["autoResize"] = (de ? "Automatisch anpassen" : "Auto-resize", "refresh"),
            ["zoomOut"] = (de ? "Verkleinern" : "Zoom out", "zoom-out"),
            ["zoomIn"] = (de ? "Vergrößern" : "Zoom in", "zoom-in"),
            ["copyUrl"] = (de ? "Adresse kopieren" : "Copy address", "copy"),
            ["clipboard"] = (de ? "Einfügen" : "Paste", "clipboard"),
            ["cad"] = (de ? "Strg+Alt+Entf" : "Ctrl+Alt+Del", "command"),
            ["upload"] = (de ? "Dateien senden" : "Send files", "upload"),
            ["fileArea"] = (de ? "Dateien dieser Verbindung" : "Files for this connection", "folder"),
        };
        var saved = (prefs.ActionOrder ?? []).Where(SessionPreferences.IsKnownAction).ToList();
        var hidden = prefs.HiddenActions ?? [];
        return [.. saved
            .Concat(SessionPreferences.SortableActions.Where(key => !saved.Contains(key)))
            .Distinct(StringComparer.Ordinal)
            .Select(key =>
            {
                var (label, icon) = labels.TryGetValue(key, out var entry) ? entry : (key, "square");
                return new OrderItem(key, icon, label, !hidden.Contains(key));
            })];
    }

    // ---- Scripts ------------------------------------------------------------------------------------------------

    // The appearance form's own script: the colour fields, the application's colour sheet and the live preview. It
    // has to stand inside the form - it finds the form through document.currentScript.
    private const string AppearanceScript = """
        <script>
            (() => {
                // What you set is what you should see - namely what comes out after saving.
                // It is therefore computed on the server (/api/theme/preview): the same
                // function that builds the page, including the correction that keeps a
                // too-pale colour readable.
                const form = document.currentScript.closest("form");
                if (!form) {
                    return;
                }

                form.querySelectorAll("[data-colour-field]").forEach(field => {
                    const toggle = field.querySelector("[data-colour-own]");
                    const valueField = field.querySelector("[data-colour-value]");
                    const swatches = field.querySelector("[data-colour-presets]");
                    if (!toggle || !valueField || !swatches) {
                        return;
                    }

                    // A swatch sets the colour and switches it on in one go - otherwise you
                    // pick a colour and nothing happens.
                    swatches.addEventListener("click", event => {
                        const button = event.target.closest("[data-colour-preset]");
                        if (!button) {
                            return;
                        }

                        event.preventDefault();
                        valueField.value = button.dataset.colourPreset;
                        toggle.checked = true;
                        markChosen(field);
                        schedulePreview();
                    });
                    valueField.addEventListener("input", () => { toggle.checked = true; markChosen(field); schedulePreview(); });
                    toggle.addEventListener("change", () => { markChosen(field); schedulePreview(); });
                    markChosen(field);
                });

                function markChosen(field) {
                    const toggle = field.querySelector("[data-colour-own]");
                    const valueField = field.querySelector("[data-colour-value]");
                    field.classList.toggle("is-own", toggle.checked);
                    const dot = field.querySelector(".colour-chip-dot");
                    if (dot) {
                        dot.style.setProperty("--dot", valueField.value);
                    }
                    field.querySelectorAll("[data-colour-preset]").forEach(button => {
                        button.classList.toggle("is-selected",
                            toggle.checked && button.dataset.colourPreset.toLowerCase() === valueField.value.toLowerCase());
                    });
                }

                // The colour sheet - the application's own picker. It replaces the operating system's, which looked
                // different on every machine, was a small floating box on a phone and could not follow the page.
                // This one fills the screen on a phone, offers the eyedropper where the browser has one, and works
                // the page behind it live: every change goes out as the same "input" event a native picker fired,
                // so the "own colour" switch, the swatches and the server preview follow without knowing that a
                // new control is there.
                const sheet = document.getElementById("colour-dialog");
                if (sheet) {
                    // Out of the form. Enter in the hex field would otherwise send the whole settings form, and a
                    // fixed sheet should not depend on whatever it happens to sit in.
                    document.body.appendChild(sheet);

                    const area = sheet.querySelector("[data-colour-area]");
                    const hue = sheet.querySelector("[data-colour-hue]");
                    const hex = sheet.querySelector("[data-colour-hex]");
                    const preview = sheet.querySelector("[data-colour-preview]");
                    const dropper = sheet.querySelector("[data-colour-eyedropper]");
                    const problem = sheet.querySelector("[data-colour-problem]");
                    const heading = sheet.querySelector("[data-colour-title]");

                    // Hue in degrees, saturation and value from 0 to 1. A colour is kept in this form rather than
                    // as hex, because round-tripping through hex loses the hue of every grey and the slider would
                    // jump to red whenever the square is dragged to the left edge.
                    const state = { h: 0, s: 1, v: 1 };
                    let current = null;
                    let dragging = false;

                    const clamp = (value, low, high) => Math.min(high, Math.max(low, value));

                    const toRgb = () => {
                        const chroma = state.v * state.s;
                        const x = chroma * (1 - Math.abs(((state.h / 60) % 2) - 1));
                        const m = state.v - chroma;
                        const sectors = [[chroma, x, 0], [x, chroma, 0], [0, chroma, x], [0, x, chroma], [x, 0, chroma], [chroma, 0, x]];
                        return sectors[Math.floor(state.h / 60) % 6].map(part => Math.round((part + m) * 255));
                    };

                    const toHex = () => "#" + toRgb().map(part => part.toString(16).padStart(2, "0")).join("");

                    // "#1a2b3c", "1a2b3c" and "#abc" are all colours; anything else is not.
                    const parseHex = text => {
                        let digits = String(text || "").trim().replace(/^#/, "");
                        if (/^[0-9a-f]{3}$/i.test(digits)) {
                            digits = digits.split("").map(digit => digit + digit).join("");
                        }

                        return /^[0-9a-f]{6}$/i.test(digits) ? "#" + digits.toLowerCase() : null;
                    };

                    const fromHex = text => {
                        const value = parseHex(text);
                        if (!value) {
                            return false;
                        }

                        const r = parseInt(value.slice(1, 3), 16) / 255;
                        const g = parseInt(value.slice(3, 5), 16) / 255;
                        const b = parseInt(value.slice(5, 7), 16) / 255;
                        const max = Math.max(r, g, b);
                        const spread = max - Math.min(r, g, b);
                        // A grey has no hue: keep the one the person had instead of falling back to red.
                        let h = state.h;
                        if (spread > 0.0001) {
                            if (max === r) { h = 60 * (((g - b) / spread) % 6); }
                            else if (max === g) { h = 60 * ((b - r) / spread + 2); }
                            else { h = 60 * ((r - g) / spread + 4); }
                            if (h < 0) { h += 360; }
                        }

                        state.h = h;
                        state.s = max === 0 ? 0 : spread / max;
                        state.v = max;
                        return true;
                    };

                    // Paints the sheet from the state. The hex box is only rewritten when asked to: while the
                    // person is typing into it, it must not be overwritten under their fingers.
                    const draw = writeHex => {
                        const value = toHex();
                        area.style.setProperty("--hue", String(Math.round(state.h)));
                        area.style.setProperty("--s", String(state.s));
                        area.style.setProperty("--v", String(state.v));
                        area.setAttribute("aria-valuetext", Math.round(state.s * 100) + " %, " + Math.round(state.v * 100) + " %");
                        hue.value = String(Math.round(state.h));
                        preview.style.setProperty("--dot", value);
                        if (writeHex) {
                            hex.value = value;
                            hex.removeAttribute("aria-invalid");
                            problem.classList.add("hidden");
                        }

                        return value;
                    };

                    const commit = writeHex => {
                        const value = draw(writeHex);
                        if (!current) {
                            return;
                        }

                        current.valueField.value = value;
                        current.valueField.dispatchEvent(new Event("input", { bubbles: true }));
                    };

                    const place = event => {
                        const box = area.getBoundingClientRect();
                        state.s = clamp((event.clientX - box.left) / box.width, 0, 1);
                        state.v = 1 - clamp((event.clientY - box.top) / box.height, 0, 1);
                        commit(true);
                    };

                    area.addEventListener("pointerdown", event => {
                        dragging = true;
                        area.setPointerCapture(event.pointerId);
                        area.focus();
                        place(event);
                        event.preventDefault();
                    });
                    area.addEventListener("pointermove", event => {
                        if (dragging) {
                            place(event);
                        }
                    });
                    const stopDragging = () => { dragging = false; };
                    area.addEventListener("pointerup", stopDragging);
                    area.addEventListener("pointercancel", stopDragging);

                    // The square is a two-axis control, which HTML has no element for - so the arrow keys do it, a
                    // percent at a time and ten with Shift, and the hex box is the accessible way in for anything
                    // more exact.
                    area.addEventListener("keydown", event => {
                        const step = event.shiftKey ? 0.1 : 0.01;
                        const moves = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, step], ArrowDown: [0, -step] };
                        const move = moves[event.key];
                        if (!move) {
                            return;
                        }

                        event.preventDefault();
                        state.s = clamp(state.s + move[0], 0, 1);
                        state.v = clamp(state.v + move[1], 0, 1);
                        commit(true);
                    });

                    hue.addEventListener("input", () => {
                        state.h = Number(hue.value);
                        commit(true);
                    });

                    hex.addEventListener("input", () => {
                        if (fromHex(hex.value)) {
                            hex.removeAttribute("aria-invalid");
                            problem.classList.add("hidden");
                            commit(false);
                        }
                        else {
                            hex.setAttribute("aria-invalid", "true");
                        }
                    });
                    // Leaving the box: what is shown has to be what is chosen. A half-typed or wrong entry says so
                    // once and is put back, instead of leaving a colour on the page that the box does not show.
                    hex.addEventListener("change", () => {
                        if (!parseHex(hex.value)) {
                            problem.classList.remove("hidden");
                        }

                        hex.value = toHex();
                        hex.removeAttribute("aria-invalid");
                    });
                    hex.addEventListener("keydown", event => {
                        if (event.key !== "Enter") {
                            return;
                        }

                        event.preventDefault();
                        if (fromHex(hex.value)) {
                            commit(true);
                            close(true);
                        }
                        else {
                            hex.setAttribute("aria-invalid", "true");
                            problem.classList.remove("hidden");
                        }
                    });

                    // Not every browser has one, and a button that does nothing is worse than none.
                    if (dropper && "EyeDropper" in window) {
                        dropper.hidden = false;
                        dropper.addEventListener("click", async () => {
                            try {
                                const picked = await new window.EyeDropper().open();
                                if (fromHex(picked.sRGBHex)) {
                                    commit(true);
                                }
                            }
                            catch (error) {
                                // Closed without picking anything.
                            }
                        });
                    }

                    const show = field => {
                        if (current) {
                            close(true);
                        }

                        const valueField = field.querySelector("[data-colour-value]");
                        const toggle = field.querySelector("[data-colour-own]");
                        if (!fromHex(valueField.value)) {
                            fromHex("#176b5b");
                        }

                        const legend = field.closest("fieldset") ? field.closest("fieldset").querySelector("legend") : null;
                        heading.textContent = legend ? legend.textContent.trim() : "";
                        draw(true);
                        // Remembered before anything is changed, so "Cancel" can put it all back - including the
                        // "own colour" switch, which the first change turns on.
                        current = {
                            field,
                            valueField,
                            toggle,
                            opener: field.querySelector("[data-colour-open]"),
                            original: { value: valueField.value, own: toggle.checked }
                        };
                        sheet.classList.remove("hidden");
                        area.focus();
                        window.setTimeout(() => area.focus(), 0);
                    };

                    function close(keep) {
                        if (!current) {
                            return;
                        }

                        const done = current;
                        current = null;
                        dragging = false;
                        if (!keep) {
                            done.valueField.value = done.original.value;
                            done.toggle.checked = done.original.own;
                            markChosen(done.field);
                            schedulePreview();
                        }

                        sheet.classList.add("hidden");
                        if (done.opener) {
                            done.opener.focus();
                        }
                    }

                    form.querySelectorAll("[data-colour-open]").forEach(button => {
                        button.addEventListener("click", () => show(button.closest("[data-colour-field]")));
                    });
                    sheet.querySelector("[data-colour-done]").addEventListener("click", () => close(true));
                    sheet.querySelector("[data-colour-cancel]").addEventListener("click", () => close(false));

                    document.addEventListener("keydown", event => {
                        if (current && event.key === "Escape") {
                            event.preventDefault();
                            close(false);
                        }
                    });

                    // Tab stays inside the sheet while it is open - there is no backdrop on a desktop, so nothing
                    // else would stop the focus from wandering off into the page behind it.
                    sheet.addEventListener("keydown", event => {
                        if (event.key !== "Tab") {
                            return;
                        }

                        const items = Array.from(sheet.querySelectorAll("button, input, [tabindex='0']"))
                            .filter(item => !item.disabled && !item.hidden && item.getClientRects().length > 0);
                        if (!items.length) {
                            return;
                        }

                        const first = items[0];
                        const last = items[items.length - 1];
                        if (event.shiftKey && document.activeElement === first) {
                            event.preventDefault();
                            last.focus();
                        }
                        else if (!event.shiftKey && document.activeElement === last) {
                            event.preventDefault();
                            first.focus();
                        }
                    });
                }

                // The theme cards and the light/dark choice belong to the same preview.
                form.querySelectorAll("[name='preferredThemeName'], [name='preferredTheme']")
                    .forEach(el => el.addEventListener("change", () => {
                        form.querySelectorAll(".theme-card").forEach(card =>
                            card.classList.toggle("is-selected", !!card.querySelector("input:checked")));
                        schedulePreview();
                    }));

                let waitTimer = 0;
                let inFlight = null;
                function schedulePreview() {
                    window.clearTimeout(waitTimer);
                    waitTimer = window.setTimeout(fetchPreview, 120);
                }

                function ownColour(name, toggleName) {
                    const field = form.querySelector("[name='" + name + "']");
                    const toggleEl = form.querySelector("[name='" + toggleName + "']");
                    return field && toggleEl && toggleEl.checked ? field.value : "";
                }

                async function fetchPreview() {
                    const mode = (form.querySelector("[name='preferredTheme']:checked") || {}).value || "system";
                    const schema = (form.querySelector("[name='preferredThemeName']:checked") || {}).value || "matgate";
                    const query = new URLSearchParams({
                        theme: schema,
                        accent: ownColour("accentColor", "accentOwn"),
                        accent2: ownColour("accentColor2", "accent2Own"),
                        background: ownColour("backgroundColor", "backgroundOwn"),
                    });

                    if (inFlight) {
                        inFlight.abort();
                    }

                    inFlight = new AbortController();
                    let colours;
                    try {
                        const response = await fetch("/api/theme/preview?" + query, { signal: inFlight.signal });
                        if (!response.ok) {
                            return;
                        }

                        colours = await response.json();
                    }
                    catch (e) {
                        // Aborted or no connection: then what was on screen stays.
                        return;
                    }

                    const isDark = mode === "dark"
                        || (mode === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
                    document.documentElement.dataset.theme = mode;
                    // Set inline it beats both :root blocks - until the next load,
                    // and then the server delivers the same thing again.
                    Object.entries(colours[isDark ? "dark" : "light"] || {}).forEach(([name, colour]) => {
                        document.documentElement.style.setProperty(name, colour);
                    });
                }
            })();
        </script>
        """;

    // The settings page's script: views on a phone, sortable lists, saving, notices, the address and the shell.
    // Idempotent; it waits for the page, so the generic tab script of the layout (after this one) is wired first.
    private const string SettingsScript = """
        <script>
            (() => {
                const start = () => {
                    const root = document.querySelector('.set-layout[data-settings]');
                    if (!root || root.dataset.settingsWired === '1') {
                        return;
                    }
                    root.dataset.settingsWired = '1';

                    let texts = {};
                    try {
                        const source = document.getElementById('set-texts');
                        texts = JSON.parse(source ? source.textContent : '{}') || {};
                    }
                    catch (e) {
                        texts = {};
                    }
                    // texts[name] with %1, %2 ... filled in.
                    const say = (name, ...values) => String(texts[name] || '').replace(/%(\d)/g, (all, at) => String(values[Number(at) - 1] ?? ''));
                    const embedded = document.documentElement.dataset.embedded === '1';
                    const panelOf = element => element && element.closest ? element.closest('[data-tab-panel]') : null;
                    const navItem = key => root.querySelector('.set-nav-item[data-tab-target="' + key + '"]');
                    const activeKey = () => {
                        const panel = root.querySelector('[data-tab-panel]:not(.hidden)');
                        return panel ? panel.dataset.tabPanel : '';
                    };
                    const labelOf = key => {
                        const label = root.querySelector('.set-nav-item[data-tab-target="' + key + '"] .set-nav-label');
                        return label ? label.textContent.trim() : key;
                    };
                    const markCurrent = key => {
                        root.querySelectorAll('.set-nav-item').forEach(item => {
                            if (item.dataset.tabTarget === key) {
                                item.setAttribute('aria-current', 'page');
                            }
                            else {
                                item.removeAttribute('aria-current');
                            }
                        });
                    };
                    // To the top of the page - and of the shell's panel around it: on an iPhone the page tab grows to the
                    // height of its content, and it is that panel that scrolls, not the page.
                    const scrollPageTop = () => {
                        window.scrollTo(0, 0);
                        try {
                            const panel = window.frameElement ? window.frameElement.closest('.shell-page-panel') : null;
                            if (panel) {
                                panel.scrollTo(0, 0);
                            }
                        }
                        catch (e) {
                            // A frame of another origin: nothing of it to scroll.
                        }
                    };
                    const addressOf = (key, hash) => {
                        const url = new URL('/account', location.href);
                        url.searchParams.set('tab', key);
                        if (embedded) {
                            url.searchParams.set('embed', '1');
                        }
                        return url.pathname + url.search + (hash || '');
                    };
                    const replaceAddress = value => {
                        try {
                            history.replaceState(null, '', value);
                        }
                        catch (e) {
                            // Without history the address stays as it is; nothing else depends on it.
                        }
                    };

                    // ---- Lists to sort: by dragging the grip, or with the arrow keys on it.
                    root.querySelectorAll('[data-order-group]').forEach(group => {
                        const list = group.querySelector('[data-order-list]');
                        const field = group.querySelector('[data-order-value]');
                        const hiddenField = group.querySelector('[data-order-hidden]');
                        const live = group.querySelector('[data-order-live]');
                        if (!list || !field || !hiddenField) {
                            return;
                        }

                        const key = item => item.dataset.orderKey || item.dataset.action || '';
                        const items = () => Array.from(list.children).filter(item => key(item));
                        // The order and what is switched off - the server rendered the same values, so this changes
                        // nothing on its own.
                        const sync = () => {
                            const all = items();
                            field.value = all.map(key).join(',');
                            hiddenField.value = all
                                .filter(item => !item.querySelector('[data-order-visible], [data-action-visible]')?.checked)
                                .map(key)
                                .join(',');
                        };
                        const nameOf = item => ((item.querySelector('.action-order-name') || item).textContent || '').trim();
                        const announce = item => {
                            if (live) {
                                const all = items();
                                live.textContent = say('position', nameOf(item), all.indexOf(item) + 1, all.length);
                            }
                        };
                        const moved = item => {
                            sync();
                            list.dispatchEvent(new Event('change', { bubbles: true }));
                            announce(item);
                        };
                        sync();
                        list.addEventListener('change', sync);

                        // Pointer events, not HTML5 drag and drop: that one does not fire at all on iOS Safari. Only the
                        // grip starts a drag - the rest of a row scrolls the page on a phone.
                        let dragged = null;
                        list.addEventListener('pointerdown', event => {
                            const grip = event.target instanceof Element ? event.target.closest('.action-order-grip') : null;
                            const item = grip ? grip.closest('.action-order-item') : null;
                            if (!item || item.parentElement !== list) {
                                return;
                            }

                            dragged = item;
                            item.classList.add('dragging');
                            try {
                                item.setPointerCapture(event.pointerId);
                            }
                            catch (e) {
                                // Without capture the drag still follows the pointer while it stays on the list.
                            }
                        });
                        list.addEventListener('pointermove', event => {
                            if (!dragged) {
                                return;
                            }

                            // The item has the pointer captured, so what is under the finger has to be looked up.
                            const under = document.elementFromPoint(event.clientX, event.clientY);
                            const over = under && under.closest ? under.closest('.action-order-item') : null;
                            if (!over || over === dragged || over.parentElement !== list || !key(over)) {
                                return;
                            }

                            const all = Array.from(list.children);
                            list.insertBefore(dragged, all.indexOf(dragged) < all.indexOf(over) ? over.nextSibling : over);
                            sync();
                        });
                        const endDrag = () => {
                            if (!dragged) {
                                return;
                            }

                            const item = dragged;
                            dragged = null;
                            item.classList.remove('dragging');
                            moved(item);
                        };
                        list.addEventListener('pointerup', endDrag);
                        list.addEventListener('pointercancel', endDrag);

                        // The grip is a button: arrow up and down move its row by one place, Home and End to the ends.
                        list.addEventListener('keydown', event => {
                            const grip = event.target instanceof Element ? event.target.closest('.action-order-grip') : null;
                            const item = grip ? grip.closest('.action-order-item') : null;
                            if (!item || item.parentElement !== list) {
                                return;
                            }

                            const all = items();
                            const at = all.indexOf(item);
                            const to = event.key === 'ArrowUp' ? at - 1
                                : event.key === 'ArrowDown' ? at + 1
                                : event.key === 'Home' ? 0
                                : event.key === 'End' ? all.length - 1
                                : null;
                            if (to === null) {
                                return;
                            }

                            event.preventDefault();
                            if (to < 0 || to >= all.length || to === at) {
                                announce(item);
                                return;
                            }

                            list.insertBefore(item, to < at ? all[to] : all[to].nextSibling);
                            grip.focus();
                            moved(item);
                        });

                        // Back to the built-in order, everything shown - and saved at once, so it is not lost by leaving
                        // the page.
                        const reset = group.querySelector('[data-order-reset]');
                        if (reset && list.dataset.defaultOrder) {
                            reset.addEventListener('click', () => {
                                const wanted = list.dataset.defaultOrder.split(',');
                                const rank = item => {
                                    const at = wanted.indexOf(key(item));
                                    return at < 0 ? wanted.length : at;
                                };
                                Array.from(list.children).sort((a, b) => rank(a) - rank(b)).forEach(item => list.appendChild(item));
                                list.querySelectorAll('[data-order-visible], [data-action-visible]').forEach(box => {
                                    box.checked = true;
                                });
                                sync();
                                const form = group.closest('form');
                                if (form) {
                                    form.requestSubmit();
                                }
                            });
                        }
                    });

                    // ---- The address: what is said once (saved, an error) is not said again on a reload, and a tab the
                    // shell restores comes back without it - the shell takes the address on "load", which is later.
                    const address = new URL(location.href);
                    const justSaved = address.searchParams.has('saved');
                    let tidied = false;
                    ['saved', 'pw', 'email', 'totp'].forEach(name => {
                        if (address.searchParams.has(name)) {
                            address.searchParams.delete(name);
                            tidied = true;
                        }
                    });
                    // The page with the recovery codes is the answer to a POST; its own address cannot be loaded again.
                    if (address.pathname === '/account/totp/confirm') {
                        address.pathname = '/account';
                        address.search = '';
                        address.searchParams.set('tab', 'security');
                        if (embedded) {
                            address.searchParams.set('embed', '1');
                        }
                        tidied = true;
                    }
                    if (tidied) {
                        replaceAddress(address.pathname + address.search + address.hash);
                    }

                    // ---- A save reaches the shell around this tab at once: theme, start page, session settings, key bar.
                    const saved = document.getElementById('set-sync');
                    if (saved) {
                        try {
                            if (window.top !== window && typeof window.top.MatgateSettingsSaved === 'function') {
                                window.top.MatgateSettingsSaved(JSON.parse(saved.textContent));
                            }
                        }
                        catch (e) {
                            // Not inside a shell of this origin: there is nobody to tell.
                        }
                    }

                    // ---- The address names a place (#quick-order-list): its section opens, whatever ?tab= says - old
                    // tabs and bookmarks of the session section land on the start page's list that way.
                    let place = null;
                    try {
                        const id = decodeURIComponent((location.hash || '').slice(1));
                        place = id ? document.getElementById(id) : null;
                    }
                    catch (e) {
                        place = null;
                    }
                    if (place && root.contains(place)) {
                        const panel = panelOf(place);
                        if (panel) {
                            if (panel.classList.contains('hidden') && typeof root.tabsActivate === 'function') {
                                root.tabsActivate(panel.dataset.tabPanel);
                                replaceAddress(addressOf(panel.dataset.tabPanel, location.hash));
                            }
                            root.dataset.settingsView = 'section';
                            markCurrent(panel.dataset.tabPanel);
                        }
                        place.scrollIntoView({ block: 'start' });
                        window.addEventListener('load', () => place.scrollIntoView({ block: 'start' }), { once: true });
                    }
                    else if (root.dataset.settingsScroll) {
                        const spot = document.getElementById(root.dataset.settingsScroll);
                        if (spot) {
                            spot.scrollIntoView({ block: 'start' });
                        }
                    }
                    else if (justSaved) {
                        // The notice stands at the top of the section; after saving at the end of a long one it has to be
                        // in view, also inside the iPhone's grown frame.
                        scrollPageTop();
                    }

                    // ---- On a phone: the list of sections, or one section. Without a script both are plain links and the
                    // server answers with the right view.
                    root.addEventListener('click', event => {
                        const element = event.target instanceof Element ? event.target : null;
                        const item = element ? element.closest('.set-nav-item[data-tab-target]') : null;
                        if (item && root.contains(item)) {
                            // The tab script on the item itself has switched the panel already.
                            const key = item.dataset.tabTarget;
                            root.dataset.settingsView = 'section';
                            markCurrent(key);
                            scrollPageTop();
                            const title = root.querySelector('[data-tab-panel="' + key + '"] .set-section-title');
                            if (title) {
                                title.focus({ preventScroll: true });
                            }
                            return;
                        }

                        const back = element ? element.closest('[data-settings-back]') : null;
                        if (!back || !root.contains(back) || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
                            return;
                        }

                        event.preventDefault();
                        const left = activeKey();
                        root.dataset.settingsView = 'index';
                        replaceAddress(back.getAttribute('href') || '/account');
                        scrollPageTop();
                        const nav = navItem(left);
                        if (nav) {
                            nav.focus({ preventScroll: true });
                        }
                    });

                    // ---- Saving. What a form holds after the page is up is what it holds unsaved: anything else marks the
                    // form, its save bar and its section in the list - until it is the same again.
                    const forms = Array.from(root.querySelectorAll('form[data-settings-form]'));
                    const sectionOf = form => {
                        const panel = panelOf(form);
                        return panel ? panel.dataset.tabPanel : '';
                    };
                    const snapshot = form => new URLSearchParams(new FormData(form)).toString();
                    const kept = new Map();
                    const refresh = form => {
                        if (!kept.has(form)) {
                            return;
                        }

                        const dirty = snapshot(form) !== kept.get(form);
                        if (dirty) {
                            form.setAttribute('data-dirty', '');
                        }
                        else {
                            form.removeAttribute('data-dirty');
                        }
                        const status = form.querySelector('[data-savebar] .set-savebar-status');
                        if (status) {
                            status.textContent = dirty ? (status.dataset.dirtyText || texts.unsaved || '') : '';
                        }
                        const discard = form.querySelector('[data-settings-discard]');
                        if (discard) {
                            discard.disabled = !dirty;
                        }
                        const key = sectionOf(form);
                        const item = navItem(key);
                        if (item) {
                            item.classList.toggle('is-dirty', forms.some(other => sectionOf(other) === key && other.hasAttribute('data-dirty')));
                        }
                    };
                    const refreshAll = () => forms.forEach(refresh);
                    let refreshTimer = 0;
                    const scheduleRefresh = () => {
                        window.clearTimeout(refreshTimer);
                        refreshTimer = window.setTimeout(refreshAll, 100);
                    };
                    ['input', 'change', 'pointerup'].forEach(type => root.addEventListener(type, scheduleRefresh, true));
                    // After "load" and one frame: the lists have written their values and the colour fields are wired.
                    const takeSnapshots = () => {
                        forms.forEach(form => kept.set(form, snapshot(form)));
                        refreshAll();
                    };
                    if (document.readyState === 'complete') {
                        window.requestAnimationFrame(takeSnapshots);
                    }
                    else {
                        window.addEventListener('load', () => window.requestAnimationFrame(takeSnapshots), { once: true });
                    }

                    // Light, dark or system chosen in the account menu is saved already: the choice here follows it, as
                    // the saved value - else the next save of this section would put the old one back unasked.
                    window.addEventListener('matgate:theme', event => {
                        const mode = event.detail && typeof event.detail.mode === 'string' ? event.detail.mode : '';
                        const radio = mode ? root.querySelector("input[name='preferredTheme'][value='" + mode + "']") : null;
                        if (!radio || radio.checked) {
                            return;
                        }

                        radio.checked = true;
                        const form = radio.form;
                        if (form && kept.has(form)) {
                            const saved = new URLSearchParams(kept.get(form));
                            saved.set('preferredTheme', mode);
                            kept.set(form, saved.toString());
                        }
                        refreshAll();
                    });

                    // Discard: the section once more as the server has it - lists, colours and the preview included.
                    root.addEventListener('click', event => {
                        const discard = event.target instanceof Element ? event.target.closest('[data-settings-discard]') : null;
                        if (!discard || !root.contains(discard)) {
                            return;
                        }

                        event.preventDefault();
                        const form = discard.closest('form');
                        const url = new URL(location.href);
                        url.searchParams.delete('saved');
                        url.searchParams.set('tab', (form && sectionOf(form)) || activeKey() || 'profile');
                        url.hash = '';
                        location.replace(url.toString());
                    });

                    // Saving one section while another has unsaved changes loses those: asked first.
                    root.addEventListener('submit', event => {
                        const form = event.target;
                        if (!(form instanceof HTMLFormElement) || form.hasAttribute('data-confirm')) {
                            return;
                        }

                        if (form.dataset.settingsAnyway === '1') {
                            delete form.dataset.settingsAnyway;
                            return;
                        }

                        refreshAll();
                        const other = forms.find(candidate => candidate !== form && candidate.hasAttribute('data-dirty'));
                        if (!other || typeof window.MatgateAskToConfirm !== 'function') {
                            return;
                        }

                        event.preventDefault();
                        const submitter = event.submitter && event.submitter.form === form ? event.submitter : null;
                        window.MatgateAskToConfirm({
                            title: texts.guardTitle || '',
                            text: say('guardText', labelOf(sectionOf(other))),
                            confirm: texts.guardConfirm || '',
                            danger: false,
                        }).then(sure => {
                            if (!sure) {
                                return;
                            }

                            form.dataset.settingsAnyway = '1';
                            if (submitter) {
                                form.requestSubmit(submitter);
                            }
                            else {
                                form.requestSubmit();
                            }
                        });
                    }, true);

                    // The notice "saved" goes after a while, or as soon as something is changed again.
                    const notices = Array.from(root.querySelectorAll('[data-settings-notice]'));
                    if (notices.length) {
                        const quiet = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
                        let gone = false;
                        const collapse = () => {
                            if (gone) {
                                return;
                            }

                            gone = true;
                            notices.forEach(notice => {
                                if (quiet) {
                                    notice.remove();
                                    return;
                                }

                                notice.style.height = notice.offsetHeight + 'px';
                                void notice.offsetHeight;
                                notice.classList.add('is-leaving');
                                notice.style.height = '0px';
                                window.setTimeout(() => notice.remove(), 400);
                            });
                        };
                        const timer = window.setTimeout(collapse, 8000);
                        root.addEventListener('input', () => {
                            window.clearTimeout(timer);
                            collapse();
                        }, { capture: true, once: true });
                    }

                    // A field the server sent back as wrong has the focus.
                    const wrong = root.querySelector('[data-tab-panel]:not(.hidden) [aria-invalid="true"]');
                    if (wrong) {
                        wrong.focus();
                    }

                    // On a touch screen the save bar does not stick to the bottom while something is typed - it would sit
                    // right behind the on-screen keyboard.
                    if (window.matchMedia && window.matchMedia('(pointer: coarse)').matches) {
                        const typing = () => {
                            const active = document.activeElement;
                            const form = active && active.matches && active.matches('input:not([type="checkbox"]):not([type="radio"]):not([type="hidden"]), select, textarea')
                                ? active.closest('form[data-settings-form]')
                                : null;
                            forms.forEach(candidate => {
                                const bar = candidate.querySelector('[data-savebar]');
                                if (bar) {
                                    bar.classList.toggle('is-typing', candidate === form);
                                }
                            });
                        };
                        root.addEventListener('focusin', typing);
                        root.addEventListener('focusout', () => window.setTimeout(typing, 0));
                    }

                    // ---- The place an address named is ringed for a moment, so the eye finds it.
            if (place && root.contains(place)) {
                const card = place.closest('.set-card');
                if (card) {
                    card.classList.add('is-targeted');
                    window.setTimeout(() => card.classList.remove('is-targeted'), 1200);
                }
            }

            // ---- The rail: arrow up and down go from section to section.
            root.addEventListener('keydown', event => {
                const item = event.target instanceof Element ? event.target.closest('.set-nav-item') : null;
                if (!item || (event.key !== 'ArrowDown' && event.key !== 'ArrowUp')) {
                    return;
                }

                const all = Array.from(root.querySelectorAll('.set-nav-item')).filter(candidate => candidate.offsetParent);
                const next = all[all.indexOf(item) + (event.key === 'ArrowDown' ? 1 : -1)];
                if (next) {
                    event.preventDefault();
                    next.focus();
                }
            });

            // ---- Many connections: the filter above the action bar's list narrows what is shown. It is no
            // field of the form - Enter in it does not save.
            root.querySelectorAll('[data-order-filter]').forEach(filter => {
                const list = document.getElementById(filter.getAttribute('aria-controls') || '');
                if (!list) {
                    return;
                }

                filter.addEventListener('keydown', event => {
                    if (event.key === 'Enter') {
                        event.preventDefault();
                    }
                });
                filter.addEventListener('input', event => {
                    event.stopPropagation();
                    const term = filter.value.trim().toLowerCase();
                    Array.from(list.children).forEach(item => {
                        const name = ((item.querySelector('.action-order-name') || item).textContent || '').toLowerCase();
                        item.hidden = !!term && !name.includes(term);
                    });
                });
            });

            // ---- Whether the new password and its repetition match, while they are typed.
            const match = root.querySelector('[data-password-match]');
            const fresh = root.querySelector('#set-form-password [name="newPassword"]');
            const again = root.querySelector('#set-form-password [name="confirmPassword"]');
            if (match && fresh && again) {
                const compare = () => {
                    const same = again.value === fresh.value;
                    match.textContent = again.value ? (same ? texts.matches || '' : texts.noMatch || '') : '';
                    match.classList.toggle('is-ok', !!again.value && same);
                };
                fresh.addEventListener('input', compare);
                again.addEventListener('input', compare);
            }

            // ---- Small controls: show a password, copy a text.
                    root.querySelectorAll('.set-eye').forEach(button => {
                        button.addEventListener('click', () => {
                            const input = document.getElementById(button.getAttribute('aria-controls') || '');
                            if (!input) {
                                return;
                            }

                            const show = input.type === 'password';
                            input.type = show ? 'text' : 'password';
                            const label = show ? (texts.hidePassword || '') : (texts.showPassword || '');
                            button.setAttribute('aria-pressed', show ? 'true' : 'false');
                            button.setAttribute('aria-label', label);
                            button.title = label;
                        });
                    });
                    root.querySelectorAll('[data-copy-from]').forEach(button => {
                        button.addEventListener('click', async () => {
                            const source = document.querySelector(button.dataset.copyFrom || '');
                            if (!source) {
                                return;
                            }

                            const text = Array.from(source.querySelectorAll('li')).map(item => item.textContent.trim()).join('\n')
                                || source.textContent.trim();
                            try {
                                await navigator.clipboard.writeText(text);
                            }
                            catch (e) {
                                // No clipboard here (an address without https): the text is selected instead, ready to copy.
                                const range = document.createRange();
                                range.selectNodeContents(source);
                                const selection = window.getSelection();
                                selection.removeAllRanges();
                                selection.addRange(range);
                                return;
                            }

                            const label = button.querySelector('span');
                            if (label) {
                                const before = label.dataset.before || label.textContent;
                                label.dataset.before = before;
                                label.textContent = texts.copied || before;
                                window.setTimeout(() => {
                                    label.textContent = before;
                                }, 2000);
                            }
                        });
                    });
                };

                if (document.readyState === 'loading') {
                    document.addEventListener('DOMContentLoaded', start, { once: true });
                }
                else {
                    start();
                }
            })();
        </script>
        """;
}
