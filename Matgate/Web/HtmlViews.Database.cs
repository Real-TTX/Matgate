using System.Security.Claims;
using System.Text.Json;
using Matgate.Models;

namespace Matgate.Web;

// The database browser - a small SQL Server Management Studio, the same for SQL Server, MySQL/MariaDB and
// PostgreSQL. On the left the object explorer: databases, their tables and views (with columns, keys, indexes and
// foreign keys), procedures and functions, each with a menu (rows, a SELECT, the statement that makes it). On the
// right documents in tabs: queries in an editor (CodeMirror: colours per server, completion of tables and columns,
// several results, run the selection, cancel, history, export), a table's rows a page at a time, a table's
// structure. It reads through /api/db/<id>/..., which is the same for all three servers.
public sealed partial class HtmlViews
{
    public string DatabaseBrowser(HttpContext context, MatgateUser user, ServerEndpoint server)
    {
        var de = Language(context) == "de";
        var texts = JsonSerializer.Serialize(new
        {
            databases = de ? "Datenbanken" : "Databases",
            filter = de ? "Objekte filtern ..." : "Filter objects ...",
            data = de ? "Daten" : "Data",
            run = de ? "Ausführen" : "Run",
            runHint = de ? "Strg+Enter oder F5 - nur die Markierung, wenn etwas markiert ist" : "Ctrl+Enter or F5 - only the selection when something is selected",
            stop = de ? "Abbrechen" : "Cancel",
            pick = de ? "Links eine Tabelle wählen - oder „Neue Abfrage“." : "Pick a table on the left - or \"New query\".",
            rows = de ? "Zeilen" : "rows",
            of = de ? "von" : "of",
            affected = de ? "{n} Zeilen geändert" : "{n} rows changed",
            done = de ? "Fertig" : "Done",
            truncated = de ? "Nur die ersten {n} Zeilen - „Exportieren“ holt alle." : "Only the first {n} rows - \"Export\" gets all of them.",
            empty = de ? "Keine Zeilen." : "No rows.",
            loading = de ? "Lädt ..." : "Loading ...",
            running = de ? "Läuft ..." : "Running ...",
            noTables = de ? "Nichts darin." : "Nothing in it.",
            signIn = de ? "Anmelden an der Datenbank" : "Sign in to the database",
            user = de ? "Benutzer" : "User",
            password = de ? "Passwort" : "Password",
            connect = de ? "Verbinden" : "Connect",
            refresh = de ? "Neu laden" : "Reload",
            previous = de ? "Zurück" : "Previous",
            next = de ? "Weiter" : "Next",
            database = de ? "Datenbank" : "Database",
            view = de ? "Sicht" : "View",
            tables = de ? "Tabellen" : "Tables",
            views = de ? "Sichten" : "Views",
            procedures = de ? "Prozeduren" : "Procedures",
            functions = de ? "Funktionen" : "Functions",
            columns = de ? "Spalten" : "Columns",
            keys = de ? "Schlüssel" : "Keys",
            indexes = de ? "Indizes" : "Indexes",
            foreignKeys = de ? "Fremdschlüssel" : "Foreign keys",
            newQuery = de ? "Neue Abfrage" : "New query",
            query = de ? "Abfrage" : "Query",
            structure = de ? "Struktur" : "Structure",
            script = de ? "Skript" : "Script",
            history = de ? "Verlauf" : "History",
            historyEmpty = de ? "Noch nichts ausgeführt." : "Nothing run yet.",
            export = de ? "Exportieren" : "Export",
            exportCsv = de ? "Alle Zeilen als CSV" : "All rows as CSV",
            exportJson = de ? "Alle Zeilen als JSON" : "All rows as JSON",
            copyResult = de ? "Ergebnis kopieren (Tabelle)" : "Copy the result (as a table)",
            copied = de ? "Kopiert." : "Copied.",
            messages = de ? "Meldungen" : "Messages",
            result = de ? "Ergebnis" : "Result",
            showRows = de ? "Daten anzeigen" : "Show rows",
            scriptSelect = de ? "Skript: SELECT" : "Script: SELECT",
            scriptCreate = de ? "Skript als CREATE" : "Script as CREATE",
            showStructure = de ? "Struktur anzeigen" : "Show structure",
            copyName = de ? "Namen kopieren" : "Copy the name",
            close = de ? "Schließen" : "Close",
            type = de ? "Typ" : "Type",
            nullable = de ? "NULL erlaubt" : "Nullable",
            defaultValue = de ? "Vorgabe" : "Default",
            name = "Name",
            unique = de ? "Eindeutig" : "Unique",
            primary = de ? "Primärschlüssel" : "Primary key",
            references = de ? "Verweist auf" : "References",
            onDelete = de ? "Beim Löschen" : "On delete",
            onUpdate = de ? "Beim Ändern" : "On update",
            identity = de ? "Automatisch" : "Identity",
            yes = de ? "ja" : "yes",
            readOnly = de ? "Nur lesen" : "Read only",
            readOnlyHint = de ? "Für dich ist diese Verbindung nur zum Lesen: eine lesende Anweisung auf einmal." : "This connection is read-only for you: one reading statement at a time.",
            asQuery = de ? "Als Abfrage öffnen" : "Open as a query",
            more = de ? "Mehr" : "More",
        });
        var csrf = JsonSerializer.Serialize(context.User.FindFirstValue("csrf") ?? "");
        var setup = JsonSerializer.Serialize(new
        {
            id = server.Id.ToString(),
            protocol = server.Protocol.ToString().ToLowerInvariant(),
            database = server.DatabaseName,
            user = server.UserName,
            readOnly = Services.AccessResolver.IsReadOnly(user, server),
        });

        var body = $$"""
            <div class="dbx" data-db-root>
                <aside class="dbx-side">
                    <div class="dbx-side-head">
                        <span class="dbx-side-icon" aria-hidden="true">{{Icon("database")}}</span>
                        <strong title="{{A(server.Name)}}">{{E(server.Name)}}</strong>
                        <button type="button" class="dbx-icon-button" data-db-refresh title="{{A(de ? "Neu laden" : "Reload")}}" aria-label="{{A(de ? "Neu laden" : "Reload")}}">{{Icon("refresh")}}</button>
                    </div>
                    <label class="dbx-filter">{{Icon("search")}}<input type="search" data-db-filter autocomplete="off" spellcheck="false"></label>
                    <div class="dbx-tree" data-db-tree role="tree"></div>
                </aside>
                <section class="dbx-main">
                    <div class="dbx-docs" role="tablist" data-db-docs>
                        <div class="dbx-doc-strip" data-db-doc-strip></div>
                        <button type="button" class="dbx-doc-new" data-db-new-query title="{{A(de ? "Neue Abfrage" : "New query")}}" aria-label="{{A(de ? "Neue Abfrage" : "New query")}}">{{Icon("plus")}}</button>
                        <span class="dbx-badge" data-db-readonly hidden></span>
                    </div>
                    <div class="dbx-doc-panes" data-db-panes></div>
                </section>
                <div hidden>
                    <span data-db-icon="caret">{{Icon("chevron-down")}}</span><span data-db-icon="database">{{Icon("database")}}</span>
                    <span data-db-icon="table">{{Icon("grid")}}</span><span data-db-icon="view">{{Icon("eye")}}</span>
                    <span data-db-icon="folder">{{Icon("folder")}}</span><span data-db-icon="key">{{Icon("key")}}</span>
                    <span data-db-icon="column">{{Icon("list")}}</span><span data-db-icon="code">{{Icon("terminal")}}</span>
                    <span data-db-icon="play">{{Icon("play")}}</span><span data-db-icon="stop">{{Icon("x")}}</span>
                    <span data-db-icon="clock">{{Icon("clock")}}</span><span data-db-icon="download">{{Icon("download")}}</span>
                    <span data-db-icon="copy">{{Icon("copy")}}</span>
                    <span data-db-icon="close">{{Icon("x")}}</span><span data-db-icon="link">{{Icon("arrow-right")}}</span>
                </div>
                <div class="dbx-menu" data-db-menu role="menu" hidden></div>
                <form class="dbx-login" data-db-login hidden>
                    <div class="dbx-login-box">
                        <h2 data-db-text="signIn"></h2>
                        <p class="dbx-error" data-db-login-error hidden></p>
                        <label><span data-db-text="user"></span><input name="user" autocomplete="username" autocapitalize="off" spellcheck="false"></label>
                        <label><span data-db-text="password"></span><input name="password" type="password" autocomplete="current-password"></label>
                        <button type="submit" class="primary"><span data-db-text="connect"></span></button>
                    </div>
                </form>
            </div>
            <style>
                .dbx { background: var(--bg); color: var(--text); display: grid; grid-template-columns: minmax(220px, 300px) minmax(0, 1fr); height: calc(100dvh - 120px); min-height: 360px; }
                html[data-embedded="1"] .dbx { inset: 0; height: auto; position: fixed; }
                .dbx-side { background: var(--surface); border-right: 1px solid var(--line); display: flex; flex-direction: column; min-height: 0; }
                .dbx-side-head { align-items: center; display: flex; gap: 8px; padding: 10px 10px 8px 14px; }
                .dbx-side-head strong { flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-side-icon { color: var(--accent); display: inline-flex; }
                .dbx-filter { align-items: center; display: flex; margin: 0 10px 8px; position: relative; }
                .dbx-filter > .icon { color: var(--muted); height: 15px; left: 10px; pointer-events: none; position: absolute; width: 15px; }
                .dbx-filter input[data-db-filter][type="search"] { border-radius: 10px; font-size: 13.5px; min-height: 34px; padding: 5px 10px 5px 32px; width: 100%; }
                .dbx-tree { flex: 1 1 auto; min-height: 0; overflow-y: auto; padding: 0 6px 12px; scrollbar-color: transparent transparent; scrollbar-gutter: stable; scrollbar-width: thin; }
                .dbx-tree:hover { scrollbar-color: var(--line) transparent; }
                .dbx-node { align-items: center; background: transparent; border: 0; border-radius: 9px; box-shadow: none; color: var(--text); display: flex; font-size: 13.5px; gap: 7px; justify-content: flex-start; min-height: 30px; padding: 3px 8px; padding-left: calc(8px + var(--depth, 0) * 16px); position: relative; text-align: left; width: 100%; }
                .dbx-node:hover { background: var(--hover-bg); }
                .dbx-node.active { background: color-mix(in srgb, var(--accent) 13%, var(--surface)); color: var(--accent); font-weight: 600; }
                .dbx-node .icon { flex: 0 0 auto; height: 15px; width: 15px; }
                .dbx-node-db { font-weight: 700; }
                .dbx-node-group { color: var(--muted); font-size: 12.5px; font-weight: 600; }
                .dbx-node-schema { color: var(--muted); font-size: 12px; font-weight: 700; letter-spacing: .03em; min-height: 26px; text-transform: uppercase; }
                .dbx-node-leaf { color: var(--text); font-size: 12.5px; min-height: 26px; }
                .dbx-node-leaf .icon { color: var(--muted); height: 13px; width: 13px; }
                .dbx-node-leaf.pk .icon { color: #c99a1a; }
                .dbx-node span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-node small { color: var(--muted); flex: 0 1 auto; margin-left: auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-caret { display: inline-flex; transition: transform .15s ease; }
                .dbx-node.closed .dbx-caret { transform: rotate(-90deg); }
                .dbx-caret-space { display: inline-block; flex: 0 0 auto; width: 15px; }
                .dbx-main { display: flex; flex-direction: column; min-height: 0; min-width: 0; }
                .dbx-docs { align-items: flex-end; border-bottom: 1px solid var(--line); display: flex; gap: 4px; padding: 6px 10px 0; }
                .dbx-doc-strip { display: flex; flex: 0 1 auto; gap: 2px; min-width: 0; overflow-x: auto; scrollbar-width: none; }
                .dbx-doc-strip::-webkit-scrollbar { display: none; }
                .dbx-doc { align-items: center; background: transparent; border: 1px solid transparent; border-bottom: 0; border-radius: 8px 8px 0 0; box-shadow: none; color: var(--muted); display: inline-flex; flex: 0 0 auto; font-size: 13px; font-weight: 600; gap: 6px; max-width: 220px; min-height: 34px; padding: 3px 6px 3px 10px; }
                .dbx-doc.active { background: var(--surface); border-color: var(--line); color: var(--text); margin-bottom: -1px; }
                .dbx-doc .icon { flex: 0 0 auto; height: 14px; width: 14px; }
                .dbx-doc-label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-doc-close { align-items: center; border-radius: 6px; display: inline-flex; height: 20px; justify-content: center; opacity: .55; width: 20px; }
                .dbx-doc-close:hover { background: var(--hover-bg); opacity: 1; }
                .dbx-doc-close .icon { height: 12px; width: 12px; }
                .dbx-doc-new { align-items: center; background: transparent; border: 0; box-shadow: none; display: inline-flex; flex: 0 0 auto; justify-content: center; margin-bottom: 3px; min-height: 30px; min-width: 30px; padding: 0; }
                .dbx-doc-new .icon { height: 16px; width: 16px; }
                .dbx-badge { background: color-mix(in srgb, #c99a1a 18%, var(--surface)); border-radius: 999px; font-size: 12px; font-weight: 700; margin: 0 0 6px auto; padding: 3px 10px; }
                .dbx-badge[hidden] { display: none; }
                .dbx-doc-panes { display: flex; flex: 1 1 auto; flex-direction: column; min-height: 0; }
                .dbx-pane { display: flex; flex: 1 1 auto; flex-direction: column; min-height: 0; }
                .dbx-pane[hidden] { display: none; }
                .dbx-bar { align-items: center; display: flex; flex-wrap: wrap; gap: 8px; padding: 8px 12px; }
                .dbx-bar[hidden] { display: none; }
                .dbx-bar select { max-width: 220px; }
                .dbx-grow { flex: 1 1 auto; }
                .dbx-muted { color: var(--muted); font-size: 12.5px; }
                .dbx-icon-button { align-items: center; display: inline-flex; gap: 6px; justify-content: center; min-height: 32px; min-width: 32px; padding: 0 8px; }
                .dbx-icon-button .icon, .dbx-bar button .icon { height: 15px; width: 15px; }
                .dbx-editor { border-bottom: 1px solid var(--line); border-top: 1px solid var(--line); flex: 0 0 auto; height: var(--editor-height, 38%); min-height: 90px; overflow: hidden; }
                .dbx-editor .cm-editor { background: var(--surface); color: var(--text); height: 100%; }
                .dbx-editor .cm-editor.cm-focused { outline: none; }
                .dbx-editor .cm-scroller { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 13.5px; line-height: 1.55; }
                .dbx-editor .cm-gutters { background: var(--surface); border-right: 1px solid var(--line); color: var(--muted); }
                .dbx-editor .cm-activeLine, .dbx-editor .cm-activeLineGutter { background: color-mix(in srgb, var(--accent) 6%, transparent); }
                .dbx-editor .cm-cursor { border-left-color: var(--text); }
                .dbx-editor .cm-selectionBackground, .dbx-editor .cm-editor.cm-focused .cm-selectionBackground { background: color-mix(in srgb, var(--accent) 24%, transparent) !important; }
                .dbx-editor .cm-tooltip { background: var(--panel, var(--surface)); border: 1px solid var(--line); border-radius: 8px; color: var(--text); }
                .dbx-editor .cm-tooltip-autocomplete ul li[aria-selected] { background: var(--accent); color: #fff; }
                .dbx-editor textarea { border: 0; border-radius: 0; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; height: 100%; width: 100%; }
                .dbx-split { cursor: row-resize; flex: 0 0 auto; height: 6px; margin-top: -3px; position: relative; z-index: 1; }
                .dbx-results { display: flex; flex: 1 1 auto; flex-direction: column; min-height: 0; }
                .dbx-result-tabs { align-items: center; display: flex; gap: 2px; padding: 6px 12px 0; }
                .dbx-result-tab { background: transparent; border: 0; border-bottom: 2px solid transparent; border-radius: 0; box-shadow: none; color: var(--muted); font-size: 12.5px; font-weight: 600; min-height: 28px; padding: 2px 10px; }
                .dbx-result-tab.active { border-bottom-color: var(--accent); color: var(--text); }
                .dbx-message { font-size: 13px; margin: 6px 12px 0; }
                .dbx-message.bad, .dbx-error { color: var(--danger, #b3261e); }
                .dbx-messages { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12.5px; margin: 0; padding: 10px 14px; white-space: pre-wrap; }
                .dbx-grid { flex: 1 1 auto; margin: 6px 0 0; min-height: 0; overflow: auto; }
                .dbx-grid table { border-collapse: separate; border-spacing: 0; font-size: 13px; min-width: 100%; }
                .dbx-grid th { background: var(--surface); border-bottom: 1px solid var(--line); font-weight: 700; position: sticky; text-align: left; top: 0; user-select: none; white-space: nowrap; z-index: 1; }
                .dbx-grid th.sortable { cursor: pointer; }
                .dbx-grid th, .dbx-grid td { border-right: 1px solid color-mix(in srgb, var(--line) 60%, transparent); max-width: 420px; overflow: hidden; padding: 5px 10px; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-grid td { border-bottom: 1px solid color-mix(in srgb, var(--line) 60%, transparent); font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
                .dbx-grid td.num { color: var(--muted); text-align: right; user-select: none; }
                .dbx-grid th:first-child, .dbx-grid td.num { width: 1%; }
                .dbx-node small { max-width: 55%; }
                .dbx-bar select { min-width: 140px; width: auto; }
                .dbx-grid tr:hover td { background: var(--hover-bg); }
                .dbx-grid td.null { color: var(--muted); font-style: italic; }
                .dbx-grid th .dbx-sort { color: var(--accent); margin-left: 4px; }
                .dbx-grid .dbx-hint { color: var(--muted); padding: 24px; }
                .dbx-structure { flex: 1 1 auto; min-height: 0; overflow: auto; padding: 4px 14px 18px; }
                .dbx-structure h3 { font-size: 14px; margin: 16px 0 6px; }
                .dbx-structure table { border-collapse: collapse; font-size: 13px; width: 100%; }
                .dbx-structure th, .dbx-structure td { border-bottom: 1px solid var(--line); padding: 5px 8px; text-align: left; }
                .dbx-structure td code { font-size: 12.5px; }
                .dbx-pk { color: #c99a1a; font-weight: 700; }
                .dbx-menu { background: var(--panel, var(--surface)); border: 1px solid var(--line); border-radius: 10px; box-shadow: var(--shadow-strong, 0 10px 30px rgba(0,0,0,.2)); display: grid; min-width: 210px; padding: 4px; position: fixed; z-index: 50; }
                .dbx-menu[hidden] { display: none; }
                .dbx-menu button { align-items: center; background: transparent; border: 0; border-radius: 7px; box-shadow: none; display: flex; font-size: 13.5px; gap: 8px; justify-content: flex-start; min-height: 32px; padding: 4px 10px; text-align: left; }
                .dbx-menu button:hover, .dbx-menu button:focus-visible { background: var(--hover-bg); }
                .dbx-menu button .icon { height: 15px; width: 15px; }
                .dbx-menu hr { border: 0; border-top: 1px solid var(--line); margin: 4px 0; }
                .dbx-menu .dbx-history-item { display: grid; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 12px; gap: 2px; max-width: 520px; }
                .dbx-menu .dbx-history-item small { color: var(--muted); font-family: system-ui, sans-serif; }
                .dbx-menu .dbx-history-item span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-login { align-items: center; background: rgb(0 0 0 / 45%); display: flex; inset: 0; justify-content: center; position: fixed; z-index: 40; }
                .dbx-login[hidden] { display: none; }
                .dbx-login-box { background: var(--panel, var(--surface)); border: 1px solid var(--line); border-radius: 16px; display: grid; gap: 12px; padding: 20px; width: min(360px, calc(100vw - 32px)); }
                .dbx-login-box h2 { margin: 0; }
                .dbx-login-box label { display: grid; gap: 4px; }
                /* SQL colours - readable on light and dark alike. */
                .dbx { --sql-keyword: #8250df; --sql-string: #1a7f37; --sql-number: #b35900; --sql-comment: #6e7781; --sql-type: #0a6cc2; --sql-operator: var(--muted); --sql-name: var(--text); }
                html[data-theme="dark"] .dbx { --sql-keyword: #c49bff; --sql-string: #7ee2a1; --sql-number: #ffb070; --sql-comment: #8b949e; --sql-type: #79c0ff; }
                @media (prefers-color-scheme: dark) { html[data-theme="system"] .dbx { --sql-keyword: #c49bff; --sql-string: #7ee2a1; --sql-number: #ffb070; --sql-comment: #8b949e; --sql-type: #79c0ff; } }
                @media (max-width: 760px) {
                    .dbx { grid-template-columns: 1fr; grid-template-rows: minmax(0, 34%) minmax(0, 1fr); }
                    .dbx-side { border-bottom: 1px solid var(--line); border-right: 0; }
                    .dbx-node { min-height: 38px; }
                }
            </style>
            <script>
                window.MatgateDatabase = { texts: {{texts}}, csrf: {{csrf}}, setup: {{setup}} };
            </script>
            <script src="/assets/codemirror/codemirror.js"></script>
            <script>{{DatabaseBrowserScript}}</script>
            """;

        return Layout(context, user, server.Name, body, "db-main");
    }

    private const string DatabaseBrowserScript = """
        (() => {
            const { texts, csrf, setup } = window.MatgateDatabase;
            const CM = window.MatgateCodeMirror || null;
            const root = document.querySelector('[data-db-root]');
            const $ = selector => root.querySelector(selector);
            const tree = $('[data-db-tree]');
            const filter = $('[data-db-filter]');
            const strip = $('[data-db-doc-strip]');
            const panes = $('[data-db-panes]');
            const menu = $('[data-db-menu]');
            const login = $('[data-db-login]');
            const icon = name => (document.querySelector('[data-db-icon="' + name + '"]') || {}).innerHTML || '';
            root.querySelectorAll('[data-db-text]').forEach(node => { node.textContent = texts[node.dataset.dbText] || ''; });
            filter.placeholder = texts.filter;
            if (setup.readOnly) {
                const badge = $('[data-db-readonly]');
                badge.hidden = false;
                badge.textContent = texts.readOnly;
                badge.title = texts.readOnlyHint;
            }
            const credentials = { user: setup.user || '', password: '' };
            const storeKey = 'matgate.db.' + setup.id + '.';
            const read = (key, fallback) => { try { return JSON.parse(localStorage.getItem(storeKey + key)) ?? fallback; } catch { return fallback; } };
            const write = (key, value) => { try { localStorage.setItem(storeKey + key, JSON.stringify(value)); } catch { } };
            const el = (tag, className, text) => { const node = document.createElement(tag); if (className) { node.className = className; } if (text !== undefined) { node.textContent = text; } return node; };

            // ---- talking to the gateway ---------------------------------------------------------------------
            let waitingLogin = null;
            const askLogin = error => {
                if (!waitingLogin) {
                    waitingLogin = new Promise(resolve => {
                        login.hidden = false;
                        const fail = login.querySelector('[data-db-login-error]');
                        fail.hidden = !error;
                        fail.textContent = error || '';
                        login.elements.user.value = credentials.user;
                        login.elements.password.value = '';
                        window.setTimeout(() => (credentials.user ? login.elements.password : login.elements.user).focus(), 0);
                        login.onsubmit = event => {
                            event.preventDefault();
                            credentials.user = login.elements.user.value.trim();
                            credentials.password = login.elements.password.value;
                            login.hidden = true;
                            waitingLogin = null;
                            resolve();
                        };
                    });
                }
                return waitingLogin;
            };
            const headers = () => {
                const all = { 'X-Matgate-Csrf': csrf };
                if (credentials.user) { all['X-Matgate-Db-User'] = credentials.user; }
                if (credentials.password) { all['X-Matgate-Db-Password'] = credentials.password; }
                return all;
            };
            const api = async (path, options = {}) => {
                for (let attempt = 0; attempt < 4; attempt++) {
                    const response = await fetch('/api/db/' + setup.id + path, { credentials: 'same-origin', headers: headers(), ...options });
                    let data = null;
                    try { data = await response.json(); } catch { data = null; }
                    if (response.status === 401 && data && data.needsLogin) {
                        await askLogin(attempt > 0 || credentials.password ? data.error : '');
                        continue;
                    }
                    if (!response.ok) {
                        throw new Error((data && data.error) || response.statusText);
                    }
                    return data;
                }
                throw new Error(texts.signIn);
            };
            const quote = name => setup.protocol === 'mssql' ? '[' + name.replace(/]/g, ']]') + ']' : setup.protocol === 'mysql' ? '`' + name.replace(/`/g, '``') + '`' : '"' + name.replace(/"/g, '""') + '"';
            const qualified = (schema, name) => (schema ? quote(schema) + '.' : '') + quote(name);

            // ---- the object explorer ------------------------------------------------------------------------
            // State of the tree: per database its objects, routines and which nodes are open; per table its
            // columns, keys, indexes and foreign keys once opened.
            const databases = new Map();
            const tableInfo = new Map();
            const openNodes = new Set(read('open', []));
            const isOpen = key => openNodes.has(key);
            const toggle = key => { if (openNodes.has(key)) { openNodes.delete(key); } else { openNodes.add(key); } write('open', Array.from(openNodes).slice(-300)); };
            let activeKey = '';
            const node = (className, depth, iconHtml, label, extra, opts = {}) => {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'dbx-node ' + className + (opts.open === false ? ' closed' : '');
                button.style.setProperty('--depth', depth);
                button.setAttribute('role', 'treeitem');
                const caret = opts.open === undefined ? '<span class="dbx-caret-space"></span>' : '<span class="dbx-caret">' + icon('caret') + '</span>';
                button.innerHTML = (opts.noCaret ? '' : caret) + iconHtml + '<span class="dbx-label"></span>' + (extra ? '<small></small>' : '');
                button.querySelector('.dbx-label').textContent = label;
                if (extra) { button.querySelector('small').textContent = extra; }
                if (opts.open !== undefined) { button.setAttribute('aria-expanded', opts.open ? 'true' : 'false'); }
                return button;
            };
            const info = (depth, text, bad) => {
                const line = el('p', 'dbx-muted' + (bad ? ' dbx-error' : ''), text);
                line.style.margin = '2px 0 2px ' + (22 + depth * 16) + 'px';
                return line;
            };
            const renderTree = () => {
                const wanted = filter.value.trim().toLowerCase();
                const nodes = [];
                for (const [db, entry] of databases) {
                    const dbOpen = isOpen('db:' + db) || !!wanted;
                    const head = node('dbx-node-db', 0, icon('database'), db, '', { open: dbOpen });
                    Object.assign(head.dataset, { db, key: 'db:' + db, kind: 'database' });
                    nodes.push(head);
                    if (!dbOpen) {
                        continue;
                    }
                    if (entry.error) {
                        nodes.push(info(1, entry.error, true));
                        continue;
                    }
                    if (!entry.objects) {
                        nodes.push(info(1, texts.loading));
                        continue;
                    }
                    const match = name => !wanted || name.toLowerCase().includes(wanted);
                    const groups = [
                        ['tables', texts.tables, entry.objects.filter(item => item.kind !== 'view'), 'table'],
                        ['views', texts.views, entry.objects.filter(item => item.kind === 'view'), 'view'],
                        ['procedures', texts.procedures, (entry.routines || []).filter(item => item.kind === 'procedure'), 'procedure'],
                        ['functions', texts.functions, (entry.routines || []).filter(item => item.kind !== 'procedure'), 'function'],
                    ];
                    const schemas = new Set(entry.objects.map(item => item.schema).concat((entry.routines || []).map(item => item.schema)));
                    const showSchema = schemas.size > 1 || (schemas.size === 1 && setup.protocol !== 'mysql' && !['dbo', 'public'].includes(Array.from(schemas)[0]));
                    for (const [groupKey, label, all, kind] of groups) {
                        const items = all.filter(item => match(item.name));
                        if (!items.length && (groupKey === 'procedures' || groupKey === 'functions' || wanted)) {
                            continue;
                        }
                        const key = 'group:' + db + ':' + groupKey;
                        const open = ((groupKey === 'tables' || groupKey === 'views') ? !isOpen('shut:' + key) : isOpen(key)) || !!wanted;
                        const group = node('dbx-node-group', 1, icon('folder'), label, String(items.length), { open });
                        Object.assign(group.dataset, { key: (groupKey === 'tables' || groupKey === 'views') ? 'shut:' + key : key, kind: 'group' });
                        nodes.push(group);
                        if (!open) {
                            continue;
                        }
                        if (!items.length) {
                            nodes.push(info(2, texts.noTables));
                        }
                        for (const item of items) {
                            const itemKey = kind + ':' + db + ':' + item.schema + '.' + item.name;
                            const expandable = kind === 'table' || kind === 'view';
                            const itemOpen = expandable && isOpen(itemKey) && !wanted;
                            const row = node('dbx-node-table' + (activeKey === itemKey ? ' active' : ''), 2, icon(kind === 'table' ? 'table' : kind === 'view' ? 'view' : 'code'),
                                (showSchema ? item.schema + '.' : '') + item.name, '', { open: expandable ? itemOpen : undefined });
                            Object.assign(row.dataset, { db, schema: item.schema, name: item.name, kind, key: itemKey });
                            if (kind === 'table' || kind === 'view') {
                                row.dataset.table = item.name;
                            }
                            nodes.push(row);
                            if (itemOpen) {
                                nodes.push(...tableNodes(db, item, kind));
                            }
                        }
                    }
                }
                tree.replaceChildren(...nodes);
            };
            const tableNodes = (db, item, kind) => {
                const id = db + ':' + item.schema + '.' + item.name;
                const detail = tableInfo.get(id);
                if (!detail) {
                    loadTable(db, item.schema, item.name);
                    return [info(3, texts.loading)];
                }
                if (detail.error) {
                    return [info(3, detail.error, true)];
                }
                const out = [];
                const section = (key, label, list, make) => {
                    if (!list.length) {
                        return;
                    }
                    const sectionKey = 'sec:' + id + ':' + key;
                    const open = key === 'columns' ? !isOpen('shut:' + sectionKey) : isOpen(sectionKey);
                    const head = node('dbx-node-group', 3, icon('folder'), label, String(list.length), { open });
                    Object.assign(head.dataset, { key: key === 'columns' ? 'shut:' + sectionKey : sectionKey, kind: 'group' });
                    out.push(head);
                    if (open) {
                        list.forEach(entry => out.push(make(entry)));
                    }
                };
                section('columns', texts.columns, detail.columns, column => {
                    const leaf = node('dbx-node-leaf' + (column.primaryKey ? ' pk' : ''), 4, icon(column.primaryKey ? 'key' : 'column'), column.name,
                        column.type + (column.nullable ? '' : ', NOT NULL'), { noCaret: false });
                    leaf.title = column.name + ' ' + column.type + (column.nullable ? ' NULL' : ' NOT NULL') + (column.default ? ' DEFAULT ' + column.default : '');
                    Object.assign(leaf.dataset, { kind: 'column', column: column.name });
                    return leaf;
                });
                if (kind === 'table') {
                    section('keys', texts.keys, detail.indexes.filter(index => index.primary || index.unique), index => {
                        const leaf = node('dbx-node-leaf pk', 4, icon('key'), index.name, index.columns);
                        leaf.dataset.kind = 'index';
                        return leaf;
                    });
                    section('indexes', texts.indexes, detail.indexes.filter(index => !index.primary), index => {
                        const leaf = node('dbx-node-leaf', 4, icon('list'), index.name, (index.unique ? 'UNIQUE · ' : '') + index.columns);
                        leaf.dataset.kind = 'index';
                        return leaf;
                    });
                    section('fks', texts.foreignKeys, detail.foreignKeys, key => {
                        const leaf = node('dbx-node-leaf', 4, icon('link'), key.name, key.columns + ' → ' + key.referencedTable);
                        leaf.dataset.kind = 'fk';
                        return leaf;
                    });
                }
                return out;
            };
            const loadTable = async (db, schema, name) => {
                const id = db + ':' + schema + '.' + name;
                if (tableInfo.has(id) && !tableInfo.get(id).loading) {
                    return tableInfo.get(id);
                }
                if (tableInfo.get(id)?.loading) {
                    return tableInfo.get(id).loading;
                }
                const loading = api('/table?' + new URLSearchParams({ db, schema, table: name }))
                    .then(detail => { tableInfo.set(id, detail); completeSchema(); return detail; })
                    .catch(error => { const failed = { error: error.message, columns: [], indexes: [], foreignKeys: [] }; tableInfo.set(id, failed); return failed; })
                    .finally(() => renderTree());
                tableInfo.set(id, { loading });
                return loading;
            };
            const loadObjects = async db => {
                const entry = databases.get(db);
                if (!entry.objects && !entry.loading) {
                    entry.error = '';
                    entry.loading = Promise.all([
                        api('/objects?db=' + encodeURIComponent(db)).then(data => { entry.objects = data.objects; }),
                        api('/routines?db=' + encodeURIComponent(db)).then(data => { entry.routines = data.routines; }).catch(() => { entry.routines = []; }),
                    ]).catch(error => { entry.error = error.message; }).finally(() => { entry.loading = null; renderTree(); completeSchema(); });
                }
                renderTree();
                return entry.loading;
            };
            const loadDatabases = async () => {
                tree.replaceChildren(info(0, texts.loading));
                try {
                    const data = await api('/databases');
                    databases.clear();
                    data.databases.forEach(db => databases.set(db, { objects: null, routines: null }));
                    const system = ['information_schema', 'performance_schema', 'mysql', 'sys', 'master', 'tempdb', 'model', 'msdb', 'postgres'];
                    const own = data.databases.filter(db => system.indexOf(db.toLowerCase()) < 0);
                    defaultDb = data.current && databases.has(data.current) ? data.current : (own.length === 1 ? own[0] : (data.databases.length === 1 ? data.databases[0] : (own[0] || data.databases[0] || '')));
                    // Opened by itself: the connection's own database, or the only one besides the server's system
                    // databases - not one of those (a click opens it).
                    const opensItself = data.current && databases.has(data.current) ? data.current : (own.length === 1 ? own[0] : (data.databases.length === 1 ? data.databases[0] : ''));
                    if (opensItself && !Array.from(openNodes).some(key => key.startsWith('db:'))) {
                        openNodes.add('db:' + opensItself);
                    }
                    documents.forEach(doc => doc.syncDatabases && doc.syncDatabases());
                    for (const db of databases.keys()) {
                        if (isOpen('db:' + db)) {
                            loadObjects(db);
                        }
                    }
                    renderTree();
                }
                catch (error) {
                    tree.replaceChildren(info(0, error.message, true));
                }
            };
            let defaultDb = setup.database || '';

            // ---- a result as a table ----------------------------------------------------------------------------
            const renderTable = (grid, result, sort) => {
                if (!result.columns.length) {
                    grid.replaceChildren();
                    return;
                }
                const table = document.createElement('table');
                const head = table.createTHead().insertRow();
                head.appendChild(el('th', '', '#'));
                result.columns.forEach(column => {
                    const cell = el('th', sort ? 'sortable' : '', column);
                    cell.title = column;
                    if (sort && sort.order === column) {
                        cell.appendChild(el('span', 'dbx-sort', sort.desc ? '▼' : '▲'));
                    }
                    if (sort) {
                        cell.addEventListener('click', () => sort.by(column));
                    }
                    head.appendChild(cell);
                });
                const body = table.createTBody();
                const first = result.first || 1;
                result.rows.forEach((values, index) => {
                    const row = body.insertRow();
                    const number = row.insertCell();
                    number.className = 'num';
                    number.textContent = String(first + index);
                    values.forEach(value => {
                        const cell = row.insertCell();
                        if (value === null) {
                            cell.className = 'null';
                            cell.textContent = 'NULL';
                        }
                        else {
                            cell.textContent = value;
                            cell.title = value.length > 40 ? value : '';
                        }
                    });
                });
                if (!result.rows.length) {
                    const cell = body.insertRow().insertCell();
                    cell.colSpan = result.columns.length + 1;
                    cell.className = 'null';
                    cell.textContent = texts.empty;
                }
                grid.replaceChildren(table);
            };
            const hint = (grid, text, bad) => grid.replaceChildren(el('p', 'dbx-hint' + (bad ? ' dbx-error' : ''), text));
            const asTsv = result => [result.columns.join('\t')].concat(result.rows.map(row => row.map(value => value === null ? 'NULL' : String(value).replace(/[\t\n\r]/g, ' ')).join('\t'))).join('\n');
            const download = async (sql, db, format, name) => {
                const body = new FormData();
                body.append('sql', sql);
                body.append('db', db || '');
                body.append('format', format);
                body.append('name', name || 'export');
                const response = await fetch('/api/db/' + setup.id + '/export', { method: 'POST', body, credentials: 'same-origin', headers: headers() });
                if (!response.ok) {
                    let error = response.statusText;
                    try { error = (await response.json()).error || error; } catch { }
                    throw new Error(error);
                }
                const blob = await response.blob();
                const link = document.createElement('a');
                link.href = URL.createObjectURL(blob);
                link.download = (name || 'export') + '.' + (format === 'json' ? 'json' : 'csv');
                document.body.appendChild(link);
                link.click();
                link.remove();
                window.setTimeout(() => URL.revokeObjectURL(link.href), 4000);
            };

            // ---- the menu (own, not the browser's) ---------------------------------------------------------------
            const closeMenu = () => { menu.hidden = true; menu.replaceChildren(); };
            const openMenu = (anchor, items) => {
                menu.replaceChildren(...items.map(item => {
                    if (item === '-') {
                        return document.createElement('hr');
                    }
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.setAttribute('role', 'menuitem');
                    if (item.html) {
                        button.innerHTML = item.html;
                        button.className = item.className || '';
                    }
                    else {
                        button.innerHTML = (item.icon ? icon(item.icon) : '') + '<span></span>';
                        button.querySelector('span').textContent = item.label;
                    }
                    button.dataset.menuAction = item.action || '';
                    button.addEventListener('click', () => { closeMenu(); item.run(); });
                    return button;
                }));
                menu.hidden = false;
                const box = anchor.getBoundingClientRect ? anchor.getBoundingClientRect() : { left: anchor.x, right: anchor.x, top: anchor.y, bottom: anchor.y };
                const width = menu.offsetWidth;
                const height = menu.offsetHeight;
                menu.style.left = Math.max(8, Math.min(window.innerWidth - width - 8, box.left)) + 'px';
                menu.style.top = (box.bottom + height + 8 > window.innerHeight ? Math.max(8, box.top - height - 4) : box.bottom + 4) + 'px';
                (menu.querySelector('button') || menu).focus();
            };
            document.addEventListener('pointerdown', event => { if (!menu.hidden && !menu.contains(event.target)) { closeMenu(); } }, true);
            document.addEventListener('keydown', event => { if (event.key === 'Escape' && !menu.hidden) { closeMenu(); } });

            // ---- documents (tabs) -----------------------------------------------------------------------------
            const documents = [];
            let active = null;
            let queryNumber = read('queryNumber', 0);
            const saveQueries = () => write('queries', documents.filter(doc => doc.kind === 'query').map(doc => ({ title: doc.title, sql: doc.getSql(), db: doc.db() })));
            const showDocument = doc => {
                active = doc;
                documents.forEach(other => {
                    other.tab.classList.toggle('active', other === doc);
                    other.tab.setAttribute('aria-selected', other === doc ? 'true' : 'false');
                    other.pane.hidden = other !== doc;
                });
                if (doc.key) {
                    activeKey = doc.key;
                    renderTree();
                }
                doc.shown && doc.shown();
                doc.tab.scrollIntoView({ inline: 'nearest', block: 'nearest' });
            };
            const closeDocument = doc => {
                const index = documents.indexOf(doc);
                if (index < 0) {
                    return;
                }
                documents.splice(index, 1);
                doc.tab.remove();
                doc.pane.remove();
                doc.closed && doc.closed();
                if (doc.kind === 'query') {
                    saveQueries();
                }
                if (active === doc) {
                    const next = documents[Math.min(index, documents.length - 1)];
                    if (next) {
                        showDocument(next);
                    }
                    else {
                        activeKey = '';
                        renderTree();
                        welcome();
                    }
                }
            };
            const addDocument = (doc, iconName) => {
                const tab = el('button', 'dbx-doc');
                tab.type = 'button';
                tab.setAttribute('role', 'tab');
                tab.dataset.dbDoc = doc.kind;
                tab.innerHTML = icon(iconName) + '<span class="dbx-doc-label"></span><span class="dbx-doc-close" role="button" aria-label="' + texts.close + '">' + icon('close') + '</span>';
                tab.querySelector('.dbx-doc-label').textContent = doc.title;
                tab.title = doc.title;
                tab.addEventListener('click', event => {
                    if (event.target.closest('.dbx-doc-close')) {
                        closeDocument(doc);
                    }
                    else {
                        showDocument(doc);
                    }
                });
                tab.addEventListener('auxclick', event => { if (event.button === 1) { closeDocument(doc); } });
                doc.tab = tab;
                doc.rename = title => { doc.title = title; tab.querySelector('.dbx-doc-label').textContent = title; tab.title = title; };
                strip.appendChild(tab);
                panes.appendChild(doc.pane);
                documents.push(doc);
                const intro = panes.querySelector('[data-db-welcome]');
                if (intro) {
                    intro.remove();
                }
                showDocument(doc);
                return doc;
            };
            const welcome = () => {
                if (panes.querySelector('[data-db-welcome]')) {
                    return;
                }
                const pane = el('div', 'dbx-pane');
                pane.dataset.dbWelcome = '1';
                const grid = el('div', 'dbx-grid');
                grid.dataset.dbData = '1';
                hint(grid, texts.pick, false);
                pane.appendChild(grid);
                panes.appendChild(pane);
            };
            const findDocument = key => documents.find(doc => doc.key === key);

            // A table's (or view's) rows, a page at a time, ordered by a click on a column.
            const openData = (db, schema, name, kind) => {
                const key = (kind || 'table') + ':' + db + ':' + schema + '.' + name;
                const existing = findDocument(key);
                if (existing) {
                    showDocument(existing);
                    return existing;
                }
                const pane = el('div', 'dbx-pane');
                pane.dataset.dbPane = 'data';
                pane.innerHTML = '<div class="dbx-bar" data-db-pager>' +
                    '<button type="button" class="dbx-icon-button" data-db-prev title="' + texts.previous + '">‹</button><span data-db-range></span>' +
                    '<button type="button" class="dbx-icon-button" data-db-next title="' + texts.next + '">›</button><span class="dbx-grow"></span>' +
                    '<button type="button" class="dbx-icon-button" data-db-as-query>' + icon('code') + '<span></span></button>' +
                    '<button type="button" class="dbx-icon-button" data-db-export>' + icon('download') + '<span></span></button>' +
                    '<button type="button" class="dbx-icon-button" data-db-reload title="' + texts.refresh + '">' + icon('caret').replace('chevron', 'chevron') + '</button></div><div class="dbx-grid" data-db-data></div>';
                pane.querySelector('[data-db-reload]').innerHTML = (document.querySelector('[data-db-refresh]') || {}).innerHTML || '↻';
                pane.querySelector('[data-db-as-query] span').textContent = texts.asQuery;
                pane.querySelector('[data-db-export] span').textContent = texts.export;
                const grid = pane.querySelector('[data-db-data]');
                const range = pane.querySelector('[data-db-range]');
                const state = { db, schema, table: name, offset: 0, limit: 100, order: '', desc: false, total: null };
                const doc = { kind: 'data', key, pane, title: name, state };
                const load = async () => {
                    range.textContent = texts.loading;
                    try {
                        const result = await api('/rows?' + new URLSearchParams({ db, schema, table: name, offset: state.offset, limit: state.limit, order: state.order, desc: state.desc ? '1' : '0' }));
                        state.total = result.total;
                        const first = result.rows.length ? state.offset + 1 : 0;
                        range.textContent = first + '–' + (state.offset + result.rows.length) + (result.total !== null && result.total !== undefined ? ' ' + texts.of + ' ' + result.total : '') + ' ' + texts.rows;
                        pane.querySelector('[data-db-prev]').disabled = state.offset === 0;
                        pane.querySelector('[data-db-next]').disabled = result.total !== null && result.total !== undefined ? state.offset + result.rows.length >= result.total : result.rows.length < state.limit;
                        renderTable(grid, { ...result, first: state.offset + 1 }, {
                            order: state.order,
                            desc: state.desc,
                            by: column => { state.desc = state.order === column ? !state.desc : false; state.order = column; state.offset = 0; load(); },
                        });
                    }
                    catch (error) {
                        range.textContent = '';
                        hint(grid, error.message, true);
                    }
                };
                pane.querySelector('[data-db-prev]').addEventListener('click', () => { state.offset = Math.max(0, state.offset - state.limit); load(); });
                pane.querySelector('[data-db-next]').addEventListener('click', () => { state.offset += state.limit; load(); });
                pane.querySelector('[data-db-reload]').addEventListener('click', load);
                pane.querySelector('[data-db-as-query]').addEventListener('click', () => openQuery({ sql: selectOf(db, schema, name), db }));
                pane.querySelector('[data-db-export]').addEventListener('click', event => openMenu(event.currentTarget, [
                    { icon: 'download', label: texts.exportCsv, action: 'csv', run: () => download('SELECT * FROM ' + qualified(schema, name), db, 'csv', name).catch(error => hint(grid, error.message, true)) },
                    { icon: 'download', label: texts.exportJson, action: 'json', run: () => download('SELECT * FROM ' + qualified(schema, name), db, 'json', name).catch(error => hint(grid, error.message, true)) },
                ]));
                doc.reload = load;
                addDocument(doc, kind === 'view' ? 'view' : 'table');
                load();
                return doc;
            };
            const selectOf = (db, schema, name) => {
                const id = db + ':' + schema + '.' + name;
                const detail = tableInfo.get(id);
                const columns = detail && detail.columns && detail.columns.length ? detail.columns.map(column => '    ' + quote(column.name)).join(',\n') : '    *';
                return setup.protocol === 'mssql'
                    ? 'SELECT TOP (100)\n' + columns + '\nFROM ' + qualified(schema, name) + ';'
                    : 'SELECT\n' + columns + '\nFROM ' + qualified(schema, name) + '\nLIMIT 100;';
            };

            // A table's structure: columns, keys and indexes, foreign keys.
            const openStructure = async (db, schema, name) => {
                const key = 'structure:' + db + ':' + schema + '.' + name;
                const existing = findDocument(key);
                if (existing) {
                    showDocument(existing);
                    return existing;
                }
                const pane = el('div', 'dbx-pane');
                pane.dataset.dbPane = 'structure';
                const body = el('div', 'dbx-structure');
                body.dataset.dbStructure = '1';
                body.textContent = texts.loading;
                pane.appendChild(body);
                const doc = addDocument({ kind: 'structure', key: null, pane, title: name + ' · ' + texts.structure }, 'list');
                doc.key = key;
                const detail = await loadTable(db, schema, name);
                if (detail.error) {
                    body.replaceChildren(el('p', 'dbx-error', detail.error));
                    return doc;
                }
                const table = (heads, rows) => {
                    const t = document.createElement('table');
                    const h = t.createTHead().insertRow();
                    heads.forEach(text => h.appendChild(el('th', '', text)));
                    const b = t.createTBody();
                    rows.forEach(values => {
                        const r = b.insertRow();
                        values.forEach(value => {
                            const c = r.insertCell();
                            if (value instanceof Node) { c.appendChild(value); } else { c.textContent = value; }
                        });
                    });
                    return t;
                };
                const code = text => { const node = el('code', '', text); return node; };
                const parts = [el('h3', '', texts.columns), table([texts.name, texts.type, texts.nullable, texts.defaultValue, texts.primary, texts.identity],
                    detail.columns.map(column => [column.primaryKey ? el('span', 'dbx-pk', column.name) : column.name, code(column.type), column.nullable ? texts.yes : '', column.default ? code(column.default) : '', column.primaryKey ? texts.yes : '', column.identity ? texts.yes : '']))];
                if (detail.indexes.length) {
                    parts.push(el('h3', '', texts.indexes), table([texts.name, texts.columns, texts.unique, texts.primary], detail.indexes.map(index => [index.name, code(index.columns), index.unique ? texts.yes : '', index.primary ? texts.yes : ''])));
                }
                if (detail.foreignKeys.length) {
                    parts.push(el('h3', '', texts.foreignKeys), table([texts.name, texts.columns, texts.references, texts.onDelete, texts.onUpdate], detail.foreignKeys.map(fk => [fk.name, code(fk.columns), code(fk.referencedTable + ' (' + fk.referencedColumns + ')'), fk.onDelete, fk.onUpdate])));
                }
                const create = el('button', 'dbx-icon-button');
                create.type = 'button';
                create.innerHTML = icon('code') + '<span></span>';
                create.querySelector('span').textContent = texts.scriptCreate;
                create.addEventListener('click', () => scriptAsCreate(db, 'table', schema, name));
                const bar = el('div', 'dbx-bar');
                bar.appendChild(create);
                body.replaceChildren(bar, ...parts);
                return doc;
            };
            const scriptAsCreate = async (db, kind, schema, name) => {
                const doc = openQuery({ sql: '-- ' + texts.loading, db, title: name + ' · ' + texts.script });
                try {
                    const data = await api('/script?' + new URLSearchParams({ db, kind, schema, name }));
                    doc.setSql(data.script || '');
                }
                catch (error) {
                    doc.setSql('-- ' + error.message);
                }
            };

            // ---- a query: editor, run, results -----------------------------------------------------------------
            const dialectOf = () => CM ? (setup.protocol === 'mssql' ? CM.dialects.mssql : setup.protocol === 'mysql' ? CM.dialects.mysql : setup.protocol === 'postgres' ? CM.dialects.postgres : CM.dialects.standard) : null;
            const languages = new Set();
            // What the editor completes: the tables and views of every database it knows (with their columns, once
            // a table has been opened in the tree or used), keyed as the server writes them.
            const schemaOf = () => {
                const schema = {};
                for (const [db, entry] of databases) {
                    (entry.objects || []).forEach(item => {
                        const detail = tableInfo.get(db + ':' + item.schema + '.' + item.name);
                        const columns = detail && detail.columns ? detail.columns.map(column => column.name) : [];
                        schema[item.name] = columns;
                        if (setup.protocol !== 'mysql' && item.schema) {
                            schema[item.schema + '.' + item.name] = columns;
                        }
                    });
                }
                return schema;
            };
            let schemaTimer = 0;
            const completeSchema = () => {
                window.clearTimeout(schemaTimer);
                schemaTimer = window.setTimeout(() => languages.forEach(update => update()), 150);
            };
            const highlight = CM ? CM.HighlightStyle.define([
                { tag: CM.tags.keyword, color: 'var(--sql-keyword)', fontWeight: '600' },
                { tag: [CM.tags.string, CM.tags.special(CM.tags.string)], color: 'var(--sql-string)' },
                { tag: [CM.tags.number, CM.tags.bool, CM.tags.null], color: 'var(--sql-number)' },
                { tag: [CM.tags.lineComment, CM.tags.blockComment, CM.tags.comment], color: 'var(--sql-comment)', fontStyle: 'italic' },
                { tag: [CM.tags.typeName, CM.tags.standard(CM.tags.name)], color: 'var(--sql-type)' },
                { tag: [CM.tags.operator, CM.tags.punctuation], color: 'var(--sql-operator)' },
                { tag: [CM.tags.special(CM.tags.name), CM.tags.propertyName], color: 'var(--sql-name)' },
            ]) : null;
            const history = read('history', []);
            const remember = (sql, db) => {
                const at = history.findIndex(entry => entry.sql === sql);
                if (at >= 0) {
                    history.splice(at, 1);
                }
                history.unshift({ sql, db, at: Date.now() });
                history.length = Math.min(history.length, 100);
                write('history', history);
            };

            const openQuery = (options = {}) => {
                queryNumber += 1;
                write('queryNumber', queryNumber);
                const pane = el('div', 'dbx-pane');
                pane.dataset.dbPane = 'sql';
                pane.innerHTML = '<div class="dbx-bar">' +
                    '<select data-db-sql-db aria-label="' + texts.database + '"></select>' +
                    '<button type="button" class="primary dbx-icon-button" data-db-run>' + icon('play') + '<span></span></button>' +
                    '<button type="button" class="dbx-icon-button" data-db-stop disabled>' + icon('stop') + '<span></span></button>' +
                    '<span class="dbx-grow"></span>' +
                    '<button type="button" class="dbx-icon-button" data-db-history>' + icon('clock') + '<span></span></button>' +
                    '<button type="button" class="dbx-icon-button" data-db-export>' + icon('download') + '<span></span></button></div>' +
                    '<div class="dbx-editor" data-db-editor></div><div class="dbx-split" data-db-split></div>' +
                    '<div class="dbx-results"><div class="dbx-result-tabs" data-db-result-tabs></div><p class="dbx-message" data-db-message hidden></p><div class="dbx-grid" data-db-result></div></div>';
                pane.querySelector('[data-db-run] span').textContent = texts.run;
                pane.querySelector('[data-db-stop] span').textContent = texts.stop;
                pane.querySelector('[data-db-history] span').textContent = texts.history;
                pane.querySelector('[data-db-export] span').textContent = texts.export;
                pane.querySelector('[data-db-run]').title = texts.runHint;
                const dbSelect = pane.querySelector('[data-db-sql-db]');
                const message = pane.querySelector('[data-db-message]');
                const resultGrid = pane.querySelector('[data-db-result]');
                const resultTabs = pane.querySelector('[data-db-result-tabs]');
                const host = pane.querySelector('[data-db-editor]');
                const runButton = pane.querySelector('[data-db-run]');
                const stopButton = pane.querySelector('[data-db-stop]');
                const doc = { kind: 'query', key: null, pane, title: options.title || (texts.query + ' ' + queryNumber) };
                const syncDatabases = () => {
                    const wanted = dbSelect.value || options.db || defaultDb;
                    dbSelect.replaceChildren(...Array.from(databases.keys()).map(db => { const option = el('option', '', db); option.value = db; return option; }));
                    if (wanted && databases.has(wanted)) {
                        dbSelect.value = wanted;
                    }
                };
                doc.syncDatabases = syncDatabases;
                doc.db = () => dbSelect.value || options.db || defaultDb || '';
                syncDatabases();
                dbSelect.addEventListener('change', saveQueries);

                // The editor - CodeMirror where it loaded, a plain field where not.
                let view = null;
                let fallback = null;
                if (CM) {
                    const language = new CM.Compartment();
                    const languageFor = () => CM.sql({ dialect: dialectOf(), schema: schemaOf(), upperCaseKeywords: true });
                    const update = () => view && view.dispatch({ effects: language.reconfigure(languageFor()) });
                    languages.add(update);
                    doc.closed = () => languages.delete(update);
                    let saveTimer = 0;
                    view = new CM.EditorView({
                        parent: host,
                        state: CM.EditorState.create({
                            doc: options.sql || '',
                            extensions: [
                                CM.lineNumbers(), CM.highlightActiveLineGutter(), CM.highlightActiveLine(), CM.history(), CM.drawSelection(),
                                CM.indentOnInput(), CM.bracketMatching(), CM.closeBrackets(), CM.highlightSelectionMatches(),
                                CM.autocompletion({ activateOnTyping: true }), CM.syntaxHighlighting(highlight),
                                CM.placeholder('SELECT ...'),
                                language.of(languageFor()),
                                CM.Prec.highest(CM.keymap.of([
                                    { key: 'Mod-Enter', run: () => { run(); return true; } },
                                    { key: 'F5', run: () => { run(); return true; } },
                                ])),
                                CM.keymap.of([...CM.closeBracketsKeymap, ...CM.defaultKeymap, ...CM.searchKeymap, ...CM.historyKeymap, ...CM.completionKeymap, CM.indentWithTab]),
                                CM.EditorView.updateListener.of(change => {
                                    if (change.docChanged) {
                                        window.clearTimeout(saveTimer);
                                        saveTimer = window.setTimeout(saveQueries, 400);
                                    }
                                }),
                            ],
                        }),
                    });
                }
                else {
                    fallback = el('textarea');
                    fallback.spellcheck = false;
                    fallback.value = options.sql || '';
                    fallback.addEventListener('keydown', event => {
                        if ((event.key === 'Enter' && (event.ctrlKey || event.metaKey)) || event.key === 'F5') {
                            event.preventDefault();
                            run();
                        }
                    });
                    fallback.addEventListener('input', saveQueries);
                    host.appendChild(fallback);
                }
                doc.getSql = () => view ? view.state.doc.toString() : fallback.value;
                doc.setSql = text => {
                    if (view) {
                        view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text } });
                    }
                    else {
                        fallback.value = text;
                    }
                    saveQueries();
                };
                // What runs: the selection when there is one, else everything.
                const statement = () => {
                    if (view) {
                        const selection = view.state.selection.main;
                        return selection.empty ? view.state.doc.toString() : view.state.sliceDoc(selection.from, selection.to);
                    }
                    return fallback.selectionStart !== fallback.selectionEnd ? fallback.value.slice(fallback.selectionStart, fallback.selectionEnd) : fallback.value;
                };
                doc.focus = () => (view ? view.focus() : fallback.focus());
                doc.shown = () => window.setTimeout(() => doc.focus(), 0);

                // The split between editor and results can be dragged.
                const split = pane.querySelector('[data-db-split]');
                split.addEventListener('pointerdown', event => {
                    event.preventDefault();
                    split.setPointerCapture(event.pointerId);
                    const top = pane.getBoundingClientRect().top + host.offsetTop;
                    const move = moved => {
                        const height = Math.max(90, Math.min(pane.clientHeight - 140, moved.clientY - top));
                        pane.style.setProperty('--editor-height', height + 'px');
                    };
                    const stop = () => { split.removeEventListener('pointermove', move); split.removeEventListener('pointerup', stop); };
                    split.addEventListener('pointermove', move);
                    split.addEventListener('pointerup', stop);
                });

                let results = [];
                let lastSql = '';
                let elapsed = '';
                const showResult = index => {
                    resultTabs.querySelectorAll('[data-db-result-index]').forEach(tab => tab.classList.toggle('active', Number(tab.dataset.dbResultIndex) === index));
                    if (index < 0) {
                        resultGrid.replaceChildren(el('pre', 'dbx-messages', doc.messages || ''));
                        return;
                    }
                    // The line above the rows speaks of the result shown.
                    const result = results[index];
                    message.className = 'dbx-message dbx-muted';
                    message.textContent = result.rows.length + ' ' + texts.rows + ' · ' + elapsed + (result.truncated ? ' · ' + texts.truncated.replace('{n}', result.rows.length) : '');
                    renderTable(resultGrid, result, null);
                };
                let running = null;
                const run = async () => {
                    const text = statement().trim();
                    if (!text || running) {
                        doc.focus();
                        return;
                    }
                    const queryId = (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()).replace(/-/g, '');
                    running = queryId;
                    runButton.disabled = true;
                    stopButton.disabled = false;
                    message.hidden = false;
                    message.className = 'dbx-message dbx-muted';
                    message.textContent = texts.running;
                    const body = new FormData();
                    body.append('sql', text);
                    body.append('db', doc.db());
                    body.append('queryId', queryId);
                    remember(text, doc.db());
                    lastSql = text;
                    const started = performance.now();
                    try {
                        const data = await api('/query', { method: 'POST', body });
                        results = data.results || (data.columns && data.columns.length ? [data] : []);
                        const time = Math.round(data.elapsedMs ?? (performance.now() - started)) + ' ms';
                        elapsed = time;
                        const lines = results.map((result, index) => (results.length > 1 ? texts.result + ' ' + (index + 1) + ': ' : '') + result.rows.length + ' ' + texts.rows + (result.truncated ? ' (' + texts.truncated.replace('{n}', result.rows.length) + ')' : ''));
                        if (data.affected !== null && data.affected !== undefined && data.affected >= 0 && !results.length) {
                            lines.push(texts.affected.replace('{n}', data.affected));
                        }
                        doc.messages = (lines.length ? lines.join('\n') : texts.done) + '\n' + time;
                        message.textContent = (results.length ? results.map(result => result.rows.length).reduce((a, b) => a + b, 0) + ' ' + texts.rows : (data.affected !== null && data.affected !== undefined && data.affected >= 0 ? texts.affected.replace('{n}', data.affected) : texts.done)) + ' · ' + time + (results.some(result => result.truncated) ? ' · ' + texts.truncated.replace('{n}', 1000) : '');
                        const tabs = results.map((result, index) => {
                            const tab = el('button', 'dbx-result-tab', results.length > 1 ? texts.result + ' ' + (index + 1) : texts.result);
                            tab.type = 'button';
                            tab.dataset.dbResultIndex = String(index);
                            tab.addEventListener('click', () => showResult(index));
                            return tab;
                        });
                        const messagesTab = el('button', 'dbx-result-tab', texts.messages);
                        messagesTab.type = 'button';
                        messagesTab.dataset.dbResultIndex = '-1';
                        messagesTab.addEventListener('click', () => showResult(-1));
                        resultTabs.replaceChildren(...tabs, messagesTab);
                        showResult(results.length ? 0 : -1);
                    }
                    catch (error) {
                        message.className = 'dbx-message bad';
                        message.textContent = error.message;
                        results = [];
                        resultTabs.replaceChildren();
                        resultGrid.replaceChildren();
                    }
                    finally {
                        running = null;
                        runButton.disabled = false;
                        stopButton.disabled = true;
                    }
                };
                doc.run = run;
                runButton.addEventListener('click', run);
                stopButton.addEventListener('click', async () => {
                    if (!running) {
                        return;
                    }
                    const body = new FormData();
                    body.append('queryId', running);
                    try { await fetch('/api/db/' + setup.id + '/cancel', { method: 'POST', body, credentials: 'same-origin', headers: headers() }); } catch { }
                });
                pane.querySelector('[data-db-history]').addEventListener('click', event => {
                    const items = history.slice(0, 25).map(entry => ({
                        html: '<span></span><small></small>',
                        className: 'dbx-history-item',
                        action: 'history',
                        run: () => { doc.setSql(entry.sql); if (entry.db && databases.has(entry.db)) { dbSelect.value = entry.db; } doc.focus(); },
                    }));
                    openMenu(event.currentTarget, items.length ? items : [{ label: texts.historyEmpty, run: () => { } }]);
                    menu.querySelectorAll('.dbx-history-item').forEach((button, index) => {
                        const entry = history[index];
                        button.querySelector('span').textContent = entry.sql.replace(/\s+/g, ' ').slice(0, 140);
                        button.querySelector('small').textContent = new Date(entry.at).toLocaleString() + (entry.db ? ' · ' + entry.db : '');
                    });
                });
                pane.querySelector('[data-db-export]').addEventListener('click', event => {
                    const sqlText = lastSql || statement().trim();
                    const fail = error => { message.hidden = false; message.className = 'dbx-message bad'; message.textContent = error.message; };
                    openMenu(event.currentTarget, [
                        { icon: 'download', label: texts.exportCsv, action: 'csv', run: () => download(sqlText, doc.db(), 'csv', 'query').catch(fail) },
                        { icon: 'download', label: texts.exportJson, action: 'json', run: () => download(sqlText, doc.db(), 'json', 'query').catch(fail) },
                        '-',
                        {
                            icon: 'copy', label: texts.copyResult, action: 'copy', run: async () => {
                                const shown = Number((resultTabs.querySelector('.active') || {}).dataset?.dbResultIndex ?? 0);
                                const result = results[Math.max(0, shown)];
                                if (result) {
                                    try { await navigator.clipboard.writeText(asTsv(result)); message.textContent = texts.copied; } catch { }
                                }
                            },
                        },
                    ]);
                });
                addDocument(doc, 'code');
                saveQueries();
                return doc;
            };

            // ---- the tree's clicks and menus -------------------------------------------------------------------
            const menuFor = target => {
                const { db, schema, name, kind } = target.dataset;
                if (kind === 'table' || kind === 'view') {
                    return [
                        { icon: 'table', label: texts.showRows, action: 'rows', run: () => openData(db, schema, name, kind) },
                        { icon: 'code', label: texts.scriptSelect, action: 'select', run: async () => { await loadTable(db, schema, name); openQuery({ sql: selectOf(db, schema, name), db }); } },
                        { icon: 'code', label: texts.scriptCreate, action: 'create', run: () => scriptAsCreate(db, kind, schema, name) },
                        ...(kind === 'table' ? [{ icon: 'list', label: texts.showStructure, action: 'structure', run: () => openStructure(db, schema, name) }] : []),
                        '-',
                        { icon: 'copy', label: texts.copyName, action: 'copy', run: () => navigator.clipboard.writeText(qualified(schema, name)).catch(() => { }) },
                    ];
                }
                if (kind === 'procedure' || kind === 'function') {
                    return [
                        { icon: 'code', label: texts.scriptCreate, action: 'create', run: () => scriptAsCreate(db, kind, schema, name) },
                        { icon: 'copy', label: texts.copyName, action: 'copy', run: () => navigator.clipboard.writeText(qualified(schema, name)).catch(() => { }) },
                    ];
                }
                if (kind === 'database') {
                    return [{ icon: 'code', label: texts.newQuery, action: 'query', run: () => openQuery({ db }) }];
                }
                return null;
            };
            tree.addEventListener('click', event => {
                const target = event.target instanceof Element ? event.target.closest('.dbx-node') : null;
                if (!target) {
                    return;
                }
                const { kind, key, db } = target.dataset;
                if (kind === 'database') {
                    toggle(key);
                    if (isOpen(key)) {
                        loadObjects(db);
                    }
                    renderTree();
                    return;
                }
                if (kind === 'group') {
                    toggle(key);
                    renderTree();
                    return;
                }
                if (kind === 'table' || kind === 'view') {
                    // A click on the arrow opens it in the tree; on the name, its rows.
                    if (event.target.closest('.dbx-caret')) {
                        toggle(key);
                        renderTree();
                        return;
                    }
                    openData(db, target.dataset.schema, target.dataset.name, kind);
                    return;
                }
                if (kind === 'procedure' || kind === 'function') {
                    scriptAsCreate(db, kind, target.dataset.schema, target.dataset.name);
                }
            });
            tree.addEventListener('dblclick', event => {
                const target = event.target instanceof Element ? event.target.closest('.dbx-node[data-kind="table"], .dbx-node[data-kind="view"]') : null;
                if (target) {
                    toggle(target.dataset.key);
                    renderTree();
                }
            });
            tree.addEventListener('contextmenu', event => {
                const target = event.target instanceof Element ? event.target.closest('.dbx-node') : null;
                const items = target ? menuFor(target) : null;
                if (items) {
                    event.preventDefault();
                    openMenu({ x: event.clientX, y: event.clientY }, items);
                }
            });
            filter.addEventListener('input', renderTree);
            $('[data-db-new-query]').addEventListener('click', () => openQuery({}));
            $('[data-db-refresh]').addEventListener('click', () => { tableInfo.clear(); loadDatabases(); });
            document.addEventListener('keydown', event => {
                if (event.key === 'F5' && active && active.kind === 'query') {
                    event.preventDefault();
                    active.run();
                }
            });

            // The queries of last time, as they were left.
            const saved = read('queries', []);
            welcome();
            saved.forEach(entry => openQuery({ sql: entry.sql, db: entry.db, title: entry.title }));
            if (documents.length) {
                showDocument(documents[0]);
            }

            // For the tests and the shell.
            window.MatgateDatabase.openQuery = openQuery;
            window.MatgateDatabase.openData = openData;
            window.MatgateDatabase.openStructure = openStructure;
            window.MatgateDatabase.active = () => active;
            window.MatgateDatabase.hasEditor = !!CM;
            loadDatabases();
        })();
        """;
}
