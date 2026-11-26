using System.Globalization;
using Matgate.Models;
using Matgate.Services;

namespace Matgate.Web;

// The pages of the app sign-in: "Allow ThixGate on ANNA-PC?" and the devices in Account -> Security.
public sealed partial class HtmlViews
{
    // The fields that go along from the question to its answer, unchanged.
    public sealed record AppAuthorizeRequest(string Client, string RedirectUri, string CodeChallenge, string State, string Device);

    public string AppAuthorize(HttpContext context, MatgateUser user, AppAuthorizeRequest request, string appName)
    {
        var de = Language(context) == "de";
        var who = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName : user.DisplayName;
        var device = AppDeviceStore.CleanName(request.Device);
        string Hidden(string name, string value) => $"""<input type="hidden" name="{A(name)}" value="{A(value)}">""";
        var fields = Csrf(context)
            + Hidden("client", request.Client)
            + Hidden("redirect_uri", request.RedirectUri)
            + Hidden("code_challenge", request.CodeChallenge)
            + Hidden("state", request.State)
            + Hidden("device", request.Device);
        var body = $$"""
            <div class="conn-page" data-app-authorize>
                <section class="page-head">
                    <div>
                        <p class="eyebrow">{{E(de ? "App-Anmeldung" : "App sign-in")}}</p>
                        <h1>{{E(de ? $"{appName} auf „{device}“ zulassen?" : $"Allow {appName} on \"{device}\"?")}}</h1>
                    </div>
                </section>
                <section class="panel server-form-section">
                    <p>{{E(de
                        ? $"{appName} meldet sich auf diesem Gerät als {who} ({user.UserName}) bei Matgate an. Es darf dann:"
                        : $"{appName} signs in to Matgate on this device as {who} ({user.UserName}). It may then:")}}</p>
                    <ul>
                        <li>{{E(de ? "die Remote-Desktops und Terminals (RDP, VNC, SSH) sehen, mit denen du arbeiten darfst – ohne Passwörter," : "see the remote desktops and terminals (RDP, VNC, SSH) you may work in – without passwords,")}}</li>
                        <li>{{E(de ? "sich über Matgate mit genau diesen Verbindungen verbinden." : "connect to exactly these connections through Matgate.")}}</li>
                    </ul>
                    <p class="muted">{{E(de
                        ? "Unter Einstellungen → Sicherheit → Geräte meldest du das Gerät jederzeit wieder ab."
                        : "Under Settings → Security → Devices you can sign the device out again at any time.")}}</p>
                </section>
                <section class="panel conn-actions">
                    <form method="post" action="/app/authorize" class="form-actions">
                        {{fields}}
                        <button type="submit" name="decision" value="allow" class="primary" data-app-allow>{{Icon("check")}}{{E(de ? "Zulassen" : "Allow")}}</button>
                        <button type="submit" name="decision" value="deny" data-app-deny>{{Icon("x")}}{{E(de ? "Abbrechen" : "Cancel")}}</button>
                    </form>
                </section>
            </div>
            """;
        return Layout(context, user, de ? "App zulassen" : "Allow app", body);
    }

    // After the answer: the browser went back to the app; this page is what stays in the tab.
    public string AppAuthorizeProblem(HttpContext context, MatgateUser user, string message)
    {
        var de = Language(context) == "de";
        var body = $$"""
            <div class="conn-page" data-app-problem>
                <section class="page-head"><div><p class="eyebrow">{{E(de ? "App-Anmeldung" : "App sign-in")}}</p><h1>{{E(de ? "Das hat nicht geklappt" : "That did not work")}}</h1></div></section>
                <section class="panel"><p>{{E(message)}}</p></section>
            </div>
            """;
        return Layout(context, user, de ? "App-Anmeldung" : "App sign-in", body);
    }

    // Account -> Security: the devices an app was allowed on, each with a button that signs it out.
    private static string AppDevicesCard(HttpContext context, MatgateUser user)
    {
        var devices = context.RequestServices.GetService<AppDeviceStore>()?.ForUser(user.Id) ?? [];
        var de = Language(context) == "de";
        string When(DateTimeOffset? when) => when is { } value
            ? value.ToLocalTime().ToString(de ? "dd.MM.yyyy HH:mm" : "d MMM yyyy HH:mm", CultureInfo.InvariantCulture)
            : "–";
        var removeLabel = de ? "Abmelden" : "Sign out";
        var rows = string.Join("", devices.Select(device =>
        {
            var app = AppDeviceStore.Apps.TryGetValue(device.App, out var appName) ? appName : device.App;
            var confirm = de ? $"„{device.Name}“ abmelden? {app} braucht dort danach eine neue Anmeldung." : $"Sign out \"{device.Name}\"? {app} needs a new sign-in there afterwards.";
            return $$"""
                <tr data-app-device="{{A(device.Id)}}">
                    <td>{{E(device.Name)}}</td>
                    <td>{{E(app)}}</td>
                    <td>{{E(When(device.CreatedAt))}}</td>
                    <td>{{E(When(device.LastUsedAt))}}{{(string.IsNullOrEmpty(device.LastAddress) ? "" : $" <span class=\"muted\">{E(device.LastAddress)}</span>")}}</td>
                    <td class="table-actions"><div class="row-actions"><form method="post" action="/account/devices/{{A(device.Id)}}/remove" data-confirm="{{A(confirm)}}" data-confirm-label="{{A(removeLabel)}}">{{Csrf(context)}}<button type="submit" class="icon-button danger-action" title="{{A(removeLabel)}}" aria-label="{{A(removeLabel)}}" data-app-device-remove>{{Icon("x")}}</button></form></div></td>
                </tr>
                """;
        }));
        var headers = $$"""<th>{{(de ? "Gerät" : "Device")}}</th><th>App</th><th>{{(de ? "Zugelassen" : "Allowed")}}</th><th>{{(de ? "Zuletzt" : "Last used")}}</th><th class="table-actions">{{T(context, "Actions")}}</th>""";
        var table = DataTable(
            de ? "Gerät suchen..." : "Search devices...",
            headers,
            rows,
            de ? "Noch kein Gerät. ThixGate erscheint hier, sobald du es zulässt." : "No device yet. ThixGate shows up here once you allow it.",
            5);
        return SettingsCard(
            de ? "Geräte" : "Devices",
            table,
            de
                ? "Apps wie ThixGate, die du zugelassen hast. Sie sehen deine Verbindungen (ohne Passwörter) und verbinden sich über Matgate."
                : "Apps like ThixGate you allowed. They see your connections (without passwords) and connect through Matgate.",
            id: "set-devices",
            attributes: "data-app-devices");
    }
}
