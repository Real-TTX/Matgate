using Matgate.Services;

namespace Matgate.Web;

// Administration -> Browser farm. The farm is an optional container with real browsers (Chromium, Firefox) that
// websites in the "via Chromium/Firefox VNC" mode open in. The tab is there for every admin:
//   - where the farm is: on/off, its address, the host for VNC, a token (stored encrypted), "Check the
//     connection" - saved, it applies at once; the environment (BrowserFarm__BaseUrl) only until then;
//   - the pool: its places as tiles (free, in use - by whom, with what), how many, at which resolution;
//   - the sessions running (each can be ended) and the ones before.
public sealed partial class HtmlViews
{
    private static string BrowserAdminPanel(HttpContext context, BrowserAdminData browser, bool de)
    {
        static string Duration(DateTimeOffset from, DateTimeOffset to)
        {
            var seconds = Math.Max(0, (to - from).TotalSeconds);
            return seconds < 90 ? $"{(int)seconds} s" : seconds < 5400 ? $"{(int)(seconds / 60)} min" : $"{seconds / 3600:0.#} h";
        }

        string When(DateTimeOffset at) => at.ToLocalTime().ToString(de ? "dd.MM.yyyy HH:mm" : "yyyy-MM-dd HH:mm");
        var status = browser.Status;
        var reachable = browser.Configured && status is not null;
        var (stateClass, stateText) = !browser.Configured
            ? ("", de ? "Aus" : "Off")
            : reachable
                ? ("is-ok", de ? "Verbunden" : "Connected")
                : ("is-bad", de ? "Nicht erreichbar" : "Unreachable");

        // ---- where the farm is
        var secretNote = de ? "Gespeichert - leer lassen, um es zu behalten" : "Stored - leave empty to keep it";
        var fromEnvironment = browser.FromEnvironment && browser.Configured
            ? $"""<p class="bf-note">{E(de ? "Die Adresse kommt zurzeit aus der Umgebungsvariable BrowserFarm__BaseUrl. Speichern übernimmt sie hierher - danach gilt, was hier steht." : "The address comes from the environment variable BrowserFarm__BaseUrl at the moment. Saving takes it over - from then on what is here holds.")}</p>"""
            : "";
        var clearToken = browser.HasToken
            ? $"""<label class="check bf-clear"><input type="checkbox" name="clearToken"> <span>{E(de ? "Gespeichertes Token entfernen" : "Remove the stored token")}</span></label>"""
            : "";
        var connection = $$"""
            <form method="post" action="/admin/browser/connection" class="bf-card{{(browser.Configured ? " is-on" : "")}}" data-bf-connection>
                {{Csrf(context)}}
                <div class="bf-card-head">
                    <label class="bf-switch"><input type="checkbox" name="enabled"{{Checked(browser.Configured)}} data-bf-enabled><span class="bf-switch-track" aria-hidden="true"></span></label>
                    <div>
                        <h3>{{(de ? "Browser-Farm" : "Browser farm")}}</h3>
                        <p class="muted">{{(de
                            ? "Ein eigener Container mit echten Browsern (Chromium, Firefox). Websites mit „Öffnen über Chromium/Firefox (VNC)“ laufen dort und kommen als Bild herüber - für Seiten, die durch den eingebauten Browser nicht gehen."
                            : "A container of its own with real browsers (Chromium, Firefox). Websites with \"Open through Chromium/Firefox (VNC)\" run there and come over as a picture - for pages the built-in browser cannot show.")}}</p>
                    </div>
                    <span class="bf-state {{stateClass}}" data-bf-state>{{E(stateText)}}</span>
                </div>
                <div class="bf-card-body">
                    {{fromEnvironment}}
                    <div class="bf-fields">
                        <label class="bf-field"><span>{{(de ? "Adresse der Farm" : "The farm's address")}}</span><input type="url" name="baseUrl" value="{{A(browser.BaseUrl)}}" placeholder="http://browser-farm:8090" autocomplete="off" spellcheck="false" data-bf-url><small class="muted">{{(de ? "Wie Matgate die Farm erreicht - im selben Compose-Stapel http://browser-farm:8090." : "How Matgate reaches the farm - in the same compose stack http://browser-farm:8090.")}}</small></label>
                        <label class="bf-field"><span>{{(de ? "Host für VNC (optional)" : "Host for VNC (optional)")}}</span><input type="text" name="vncHost" value="{{A(browser.VncHost)}}" placeholder="{{A(de ? "leer = Host der Adresse" : "empty = the address's host")}}" autocomplete="off" spellcheck="false"><small class="muted">{{(de ? "Nur nötig, wenn die Bildschirme unter einem anderen Namen erreichbar sind." : "Only needed when the screens are reached under another name.")}}</small></label>
                        <label class="bf-field"><span>Token{{(de ? " (optional)" : " (optional)")}}</span><input type="password" name="token" value="" placeholder="{{A(browser.HasToken ? secretNote : "")}}" autocomplete="new-password" spellcheck="false"><small class="muted">{{(de ? "Nur wenn die Farm mit FARM_TOKEN gestartet ist. Wird verschlüsselt gespeichert." : "Only when the farm was started with FARM_TOKEN. Stored encrypted.")}}</small>{{clearToken}}</label>
                    </div>
                    <div class="bf-check"><button type="button" class="button" data-bf-test>{{Icon("refresh")}}<span>{{(de ? "Verbindung prüfen" : "Check the connection")}}</span></button><span class="muted" data-bf-test-result></span></div>
                </div>
                <div class="bf-card-foot"><button type="submit" class="primary" data-bf-save>{{Icon("save")}}{{T(context, "Save")}}</button></div>
            </form>
            """;

        if (!browser.Configured)
        {
            return BrowserAdminFrame(context, de, connection + $$"""
                <section class="bf-section bf-off" data-bf-off>
                    <h3>{{Icon("info")}}{{(de ? "Noch keine Farm" : "No farm yet")}}</h3>
                    <p class="muted">{{(de
                        ? "Ohne Farm öffnen sich Websites nur im eingebauten Browser. Die Farm ist das Image ghcr.io/real-ttx/matgate-browser-farm; docker-compose.full.yml im Repository startet sie gleich mit. Danach hier einschalten und die Adresse eintragen."
                        : "Without a farm, websites open in the built-in browser only. The farm is the image ghcr.io/real-ttx/matgate-browser-farm; docker-compose.full.yml in the repository starts it along. Then switch it on here and enter the address.")}}</p>
                </section>
                """);
        }

        // ---- the pool
        string pool;
        if (status is null)
        {
            pool = $$"""
                <section class="bf-section" data-bf-unreachable>
                    <h3>{{Icon("info")}}{{(de ? "Farm nicht erreichbar" : "Farm unreachable")}}</h3>
                    <p class="bf-warning">{{E(de
                        ? $"Unter {browser.BaseUrl} antwortet keine Farm. Läuft der Container? „Verbindung prüfen“ oben sagt mehr. Websites über die Farm gehen so lange nicht."
                        : $"No farm answers at {browser.BaseUrl}. Is the container running? \"Check the connection\" above tells more. Websites through the farm do not open meanwhile.")}}</p>
                </section>
                """;
        }
        else
        {
            var bySlot = browser.Active.GroupBy(session => session.Slot).ToDictionary(group => group.Key, group => group.First());
            var tiles = string.Join("", status.Slots.OrderBy(slot => slot.Slot).Select(slot =>
            {
                bySlot.TryGetValue(slot.Slot, out var session);
                var who = session is null ? "" : session.UserName;
                var what = session?.ServerName is { Length: > 0 } name ? name : slot.Url;
                var title = slot.Busy
                    ? $"{(de ? "Platz" : "Place")} {slot.Slot}: {slot.Browser} · {what}{(who.Length > 0 ? " · " + who : "")} · {Duration(DateTimeOffset.UtcNow.AddSeconds(-slot.AgeSeconds), DateTimeOffset.UtcNow)}"
                    : $"{(de ? "Platz" : "Place")} {slot.Slot}: {(de ? "frei" : "free")}";
                return $$"""
                    <li class="bf-slot{{(slot.Busy ? " is-busy" : "")}}" title="{{A(title)}}" data-bf-slot="{{slot.Slot}}">
                        <span class="bf-slot-no">{{slot.Slot}}</span>
                        <span class="bf-slot-what">{{(slot.Busy ? E(who.Length > 0 ? who : slot.Browser) : E(de ? "frei" : "free"))}}</span>
                    </li>
                    """;
            }));
            var poolValue = browser.Settings.PoolSize > 0 ? browser.Settings.PoolSize : status.PoolSize;
            var geometryValue = string.IsNullOrWhiteSpace(browser.Settings.Geometry) ? status.Geometry : browser.Settings.Geometry;
            pool = $$"""
                <section class="bf-section" data-bf-pool>
                    <h3>{{Icon("server")}}{{(de ? "Plätze" : "Places")}}</h3>
                    <div class="admdetail-stats bf-stats" data-bf-stats>
                        <div><strong>{{status.PoolSize}}</strong><span>{{(de ? "Plätze" : "places")}}</span></div>
                        <div><strong>{{status.Busy}}</strong><span>{{(de ? "belegt" : "in use")}}</span></div>
                        <div><strong>{{status.Free}}</strong><span>{{(de ? "frei" : "free")}}</span></div>
                        <div><strong>{{E(status.Geometry)}}</strong><span>{{(de ? "Auflösung" : "resolution")}}</span></div>
                    </div>
                    {{(tiles.Length == 0 ? "" : $"""<ul class="bf-slots" aria-label="{A(de ? "Plätze" : "Places")}">{tiles}</ul>""")}}
                    <form method="post" action="/admin/browser/settings" class="bf-pool-form" data-bf-pool-form>
                        {{Csrf(context)}}
                        <label class="bf-field"><span>{{(de ? "Plätze (gleichzeitige Sitzungen)" : "Places (sessions at once)")}}</span><input name="poolSize" type="number" min="1" max="50" value="{{A(poolValue.ToString())}}"></label>
                        <label class="bf-field"><span>{{(de ? "Auflösung, wenn das Fenster keine vorgibt" : "Resolution when the window gives none")}}</span><input name="geometry" value="{{A(geometryValue)}}" placeholder="1280x800x24" spellcheck="false"></label>
                        <button type="submit" class="button" data-bf-pool-save>{{Icon("save")}}<span>{{T(context, "Save")}}</span></button>
                    </form>
                    <p class="muted bf-hint">{{(de
                        ? "Wirkt sofort in der Farm. Weniger Plätze betreffen nur neue Sitzungen; meist nimmt eine Sitzung ohnehin die Größe ihres Fensters."
                        : "Takes effect in the farm at once. Fewer places only affect new sessions; a session mostly takes the size of its window anyway.")}}</p>
                </section>
                """;
        }

        // ---- the sessions running, and those before
        var active = browser.Active.Count == 0
            ? $"""<li class="acc-empty muted">{E(de ? "Gerade läuft keine Sitzung." : "No session running.")}</li>"""
            : string.Join("", browser.Active.Select(session => $$"""
                <li class="bf-row" data-bf-session="{{session.Id}}">
                    <span class="bf-row-icon">{{Icon("globe")}}</span>
                    <span class="bf-row-text"><strong>{{E(session.ServerName)}}</strong><small>{{E(session.UserName)}} · {{E(session.Browser)}} · {{(de ? "Platz" : "place")}} {{session.Slot}} · {{(de ? "seit" : "for")}} {{E(Duration(session.StartedAt, DateTimeOffset.UtcNow))}}</small><small class="bf-url">{{E(session.Url)}}</small></span>
                    <form method="post" action="/admin/browser/{{session.Id}}/release" data-confirm="{{A(de ? $"Sitzung von {session.UserName} auf „{session.ServerName}“ beenden? Das Fenster dort schließt sich." : $"End {session.UserName}'s session on \"{session.ServerName}\"? The window there closes.")}}">{{Csrf(context)}}<button type="submit" class="button" data-bf-release>{{Icon("x")}}<span>{{(de ? "Beenden" : "End")}}</span></button></form>
                </li>
                """));
        var history = browser.History.Count == 0
            ? $"""<p class="muted">{E(de ? "Noch keine." : "None yet.")}</p>"""
            : $$"""
                <div class="table-wrap">
                    <table class="bf-history">
                        <thead><tr><th>{{(de ? "Website" : "Website")}}</th><th>{{T(context, "User")}}</th><th>{{(de ? "Beginn" : "Started")}}</th><th>{{(de ? "Dauer" : "Duration")}}</th><th>{{(de ? "Ende" : "Ended")}}</th></tr></thead>
                        <tbody>{{string.Join("", browser.History.Take(50).Select(entry => $$"""
                            <tr>
                                <td><strong>{{E(entry.ServerName)}}</strong><small class="muted bf-url">{{E(entry.Browser)}} · {{E(entry.Url)}}</small></td>
                                <td>{{E(entry.UserName)}}</td>
                                <td>{{E(When(entry.StartedAt))}}</td>
                                <td>{{E(Duration(entry.StartedAt, entry.EndedAt))}}</td>
                                <td class="muted">{{E(entry.Reason)}}</td>
                            </tr>
                            """))}}</tbody>
                    </table>
                </div>
                """;
        var sessions = $$"""
            <section class="bf-section" data-bf-active>
                <h3>{{Icon("monitor")}}{{(de ? $"Laufende Sitzungen · {browser.Active.Count}" : $"Running sessions · {browser.Active.Count}")}}</h3>
                <ul class="acc-list bf-rows">{{active}}</ul>
                <p class="muted bf-hint">{{(de ? "Eine Sitzung gibt ihren Platz frei, wenn ihr Fenster zugeht - spätestens zwei Minuten danach." : "A session gives its place back when its window closes - two minutes later at most.")}}</p>
            </section>
            <section class="bf-section" data-bf-history>
                <h3>{{Icon("clock")}}{{(de ? "Zuletzt" : "Recently")}}</h3>
                {{history}}
            </section>
            """;
        return BrowserAdminFrame(context, de, connection + pool + sessions);
    }

    private static string BrowserAdminFrame(HttpContext context, bool de, string inner) => $$"""
        <div class="bf-panel" data-bf-panel>
            {{inner}}
        </div>
        {{AccessStyles}}
        <style>
            .bf-panel { display: grid; gap: 14px; }
            .bf-card, .bf-section { background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius, 14px); }
            .bf-card { display: grid; overflow: hidden; }
            .bf-card.is-on { border-color: color-mix(in srgb, var(--accent) 55%, var(--line)); }
            .bf-card-head { align-items: flex-start; display: flex; gap: 14px; padding: 14px 16px; }
            .bf-card-head > div { flex: 1 1 auto; min-width: 0; }
            .bf-card-head h3 { font-size: 16px; margin: 0 0 2px; }
            .bf-card-head p { font-size: 13px; margin: 0; }
            .bf-card-body { border-top: 1px solid var(--line); display: grid; gap: 12px; padding: 14px 16px; }
            .bf-card:not(.is-on) .bf-card-body { display: none; }
            .bf-card-foot { border-top: 1px solid var(--line); padding: 10px 16px; }
            .bf-state { align-self: flex-start; background: var(--surface-2, var(--line)); border-radius: 999px; flex: 0 0 auto; font-size: 12px; font-weight: 700; padding: 3px 10px; white-space: nowrap; }
            .bf-state.is-ok { background: color-mix(in srgb, var(--accent) 18%, var(--surface)); color: var(--accent); }
            .bf-state.is-bad { background: color-mix(in srgb, var(--danger, #b3261e) 14%, var(--surface)); color: var(--danger, #b3261e); }
            .bf-fields { display: grid; gap: 12px; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); }
            .bf-field { display: grid; gap: 4px; min-width: 0; }
            .bf-field > span { font-size: 13px; font-weight: 600; }
            .bf-field small { font-size: 12px; }
            .bf-clear { font-size: 12px; margin-top: 2px; }
            .bf-check { align-items: center; display: flex; flex-wrap: wrap; gap: 10px; }
            .bf-check .ok { color: var(--accent); font-weight: 600; }
            .bf-check .bad { color: var(--danger, #b3261e); }
            .bf-note { background: color-mix(in srgb, var(--accent) 10%, var(--surface)); border-radius: 10px; margin: 0; padding: 10px 14px; }
            .bf-warning { background: color-mix(in srgb, #d97706 14%, var(--surface)); border-radius: 10px; margin: 0; padding: 10px 14px; }
            .bf-switch { cursor: pointer; flex: 0 0 auto; margin-top: 2px; position: relative; }
            .bf-switch input { height: 1px; opacity: 0; position: absolute; width: 1px; }
            .bf-switch-track { background: var(--line); border-radius: 999px; display: block; height: 24px; position: relative; transition: background .15s; width: 42px; }
            .bf-switch-track::after { background: #fff; border-radius: 50%; box-shadow: 0 1px 3px rgba(0,0,0,.25); content: ""; height: 18px; left: 3px; position: absolute; top: 3px; transition: transform .15s; width: 18px; }
            .bf-switch input:checked + .bf-switch-track { background: var(--accent); }
            .bf-switch input:checked + .bf-switch-track::after { transform: translateX(18px); }
            .bf-switch input:focus-visible + .bf-switch-track { box-shadow: var(--focus-ring); }
            .bf-section { display: grid; gap: 10px; padding: 14px 16px; }
            .bf-section h3 { align-items: center; display: flex; font-size: 15px; gap: 8px; margin: 0; }
            .bf-section h3 .icon { color: var(--accent); height: 18px; width: 18px; }
            .bf-section > p { margin: 0; }
            .bf-hint { font-size: 12px; }
            .bf-stats { display: grid; gap: 10px; grid-template-columns: repeat(4, minmax(0, 1fr)); }
            .bf-slots { display: grid; gap: 6px; grid-template-columns: repeat(auto-fill, minmax(92px, 1fr)); list-style: none; margin: 0; padding: 0; }
            .bf-slot { border: 1px dashed var(--line); border-radius: 10px; display: grid; gap: 1px; min-width: 0; padding: 6px 8px; }
            .bf-slot.is-busy { background: color-mix(in srgb, var(--accent) 12%, var(--surface)); border: 1px solid color-mix(in srgb, var(--accent) 45%, var(--line)); }
            .bf-slot-no { color: var(--muted); font-size: 11px; font-weight: 700; }
            .bf-slot-what { font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .bf-slot:not(.is-busy) .bf-slot-what { color: var(--muted); }
            .bf-pool-form { align-items: end; display: grid; gap: 10px 14px; grid-template-columns: minmax(160px, 1fr) minmax(200px, 1fr) auto; }
            .bf-rows .bf-row { align-items: center; display: flex; gap: 12px; padding: 8px 4px; }
            .bf-rows .bf-row + .bf-row { border-top: 1px solid var(--line); }
            .bf-row-icon { color: var(--accent); flex: 0 0 auto; }
            .bf-row-text { display: grid; flex: 1 1 auto; gap: 1px; min-width: 0; }
            .bf-row-text small { color: var(--muted); font-size: 12px; }
            .bf-url { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
            .bf-history td small { display: block; max-width: 420px; }
            @media (max-width: 720px) {
                .bf-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                .bf-pool-form { grid-template-columns: 1fr; }
                .bf-pool-form > button { justify-self: start; }
                .bf-card-head { flex-wrap: wrap; }
            }
        </style>
        <script>
            (() => {
                const panel = document.querySelector('[data-bf-panel]');
                if (!panel || panel.dataset.wired) { return; }
                panel.dataset.wired = '1';
                const card = panel.querySelector('[data-bf-connection]');
                const box = card.querySelector('[data-bf-enabled]');
                box.addEventListener('change', () => card.classList.toggle('is-on', box.checked));
                const test = card.querySelector('[data-bf-test]');
                test.addEventListener('click', async () => {
                    const result = card.querySelector('[data-bf-test-result]');
                    result.className = 'muted';
                    result.textContent = '...';
                    try {
                        const answer = await (await fetch('/admin/browser/test', { method: 'POST', body: new FormData(card), credentials: 'same-origin' })).json();
                        result.className = answer.ok ? 'ok' : 'bad';
                        result.textContent = answer.message;
                    }
                    catch (error) {
                        result.className = 'bad';
                        result.textContent = error.message;
                    }
                });
            })();
        </script>
        """;
}
