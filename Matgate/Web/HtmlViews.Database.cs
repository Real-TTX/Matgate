using System.Security.Claims;
using System.Text.Json;
using Matgate.Models;

namespace Matgate.Web;

// The database browser: one page for SQL Server, MySQL/MariaDB and PostgreSQL. On the left the databases with
// their tables and views (a filter over them), on the right a table's rows a page at a time - ordered by a
// click on a column - and a field for statements of one's own. It reads through /api/db/<id>/..., which is the
// same for all three servers.
public sealed partial class HtmlViews
{
    public string DatabaseBrowser(HttpContext context, MatgateUser user, ServerEndpoint server)
    {
        var de = Language(context) == "de";
        var texts = JsonSerializer.Serialize(new
        {
            databases = de ? "Datenbanken" : "Databases",
            filter = de ? "Tabellen filtern ..." : "Filter tables ...",
            data = de ? "Daten" : "Data",
            sql = "SQL",
            run = de ? "Ausführen" : "Run",
            runHint = de ? "Strg+Enter führt aus" : "Ctrl+Enter runs it",
            pick = de ? "Links eine Tabelle wählen - oder oben „SQL“ für eine eigene Abfrage." : "Pick a table on the left - or \"SQL\" above for a statement of your own.",
            rows = de ? "Zeilen" : "rows",
            of = de ? "von" : "of",
            affected = de ? "{n} Zeilen geändert" : "{n} rows changed",
            truncated = de ? "Nur die ersten {n} Zeilen." : "Only the first {n} rows.",
            empty = de ? "Keine Zeilen." : "No rows.",
            loading = de ? "Lädt ..." : "Loading ...",
            noTables = de ? "Keine Tabellen." : "No tables.",
            signIn = de ? "Anmelden an der Datenbank" : "Sign in to the database",
            user = de ? "Benutzer" : "User",
            password = de ? "Passwort" : "Password",
            connect = de ? "Verbinden" : "Connect",
            refresh = de ? "Neu laden" : "Reload",
            previous = de ? "Zurück" : "Previous",
            next = de ? "Weiter" : "Next",
            database = de ? "Datenbank" : "Database",
            view = de ? "Sicht" : "View",
        });
        var csrf = JsonSerializer.Serialize(context.User.FindFirstValue("csrf") ?? "");
        var setup = JsonSerializer.Serialize(new
        {
            id = server.Id.ToString(),
            protocol = server.Protocol.ToString().ToLowerInvariant(),
            database = server.DatabaseName,
            user = server.UserName,
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
                    <div class="dbx-tabs" role="tablist">
                        <button type="button" class="dbx-tab active" data-db-tab="data" role="tab">{{Icon("database")}}<span data-db-text="data"></span></button>
                        <button type="button" class="dbx-tab" data-db-tab="sql" role="tab">{{Icon("terminal")}}<span>SQL</span></button>
                        <span class="dbx-title" data-db-title></span>
                    </div>
                    <div class="dbx-pane" data-db-pane="data">
                        <div class="dbx-bar" data-db-pager hidden>
                            <button type="button" class="dbx-icon-button" data-db-prev>‹</button>
                            <span data-db-range></span>
                            <button type="button" class="dbx-icon-button" data-db-next>›</button>
                            <span class="dbx-grow"></span>
                            <button type="button" class="dbx-icon-button" data-db-reload>{{Icon("refresh")}}</button>
                        </div>
                        <div class="dbx-grid" data-db-data></div>
                    </div>
                    <div class="dbx-pane" data-db-pane="sql" hidden>
                        <div class="dbx-sql-head">
                            <select data-db-sql-db aria-label="{{A(de ? "Datenbank" : "Database")}}"></select>
                            <span class="dbx-muted" data-db-text="runHint"></span>
                            <span class="dbx-grow"></span>
                            <button type="button" class="primary" data-db-run>{{Icon("play")}}<span data-db-text="run"></span></button>
                        </div>
                        <textarea class="dbx-sql" data-db-sql spellcheck="false" autocapitalize="off" autocomplete="off"></textarea>
                        <p class="dbx-message" data-db-message hidden></p>
                        <div class="dbx-grid" data-db-result></div>
                    </div>
                </section>
                <div hidden><span data-db-icon="caret">{{Icon("chevron-down")}}</span><span data-db-icon="database">{{Icon("database")}}</span><span data-db-icon="table">{{Icon("grid")}}</span><span data-db-icon="view">{{Icon("eye")}}</span></div>
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
                .dbx-node { align-items: center; background: transparent; border: 0; border-radius: 9px; box-shadow: none; color: var(--text); display: flex; font-size: 13.5px; gap: 7px; justify-content: flex-start; min-height: 32px; padding: 3px 8px; padding-left: calc(8px + var(--depth, 0) * 16px); text-align: left; width: 100%; }
                .dbx-node:hover { background: var(--hover-bg); }
                .dbx-node.active { background: color-mix(in srgb, var(--accent) 13%, var(--surface)); color: var(--accent); font-weight: 600; }
                .dbx-node .icon { flex: 0 0 auto; height: 15px; width: 15px; }
                .dbx-node-db { font-weight: 700; }
                .dbx-node-schema { color: var(--muted); font-size: 12px; font-weight: 700; letter-spacing: .03em; min-height: 26px; text-transform: uppercase; }
                .dbx-node span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-node small { color: var(--muted); margin-left: auto; }
                .dbx-caret { display: inline-flex; transition: transform .15s ease; }
                .dbx-node.closed .dbx-caret { transform: rotate(-90deg); }
                .dbx-main { display: flex; flex-direction: column; min-height: 0; min-width: 0; }
                .dbx-tabs { align-items: center; border-bottom: 1px solid var(--line); display: flex; gap: 4px; padding: 6px 10px 0; }
                .dbx-tab { align-items: center; background: transparent; border: 0; border-bottom: 2px solid transparent; border-radius: 8px 8px 0 0; box-shadow: none; color: var(--muted); display: inline-flex; font-weight: 600; gap: 6px; min-height: 36px; padding: 4px 12px; }
                .dbx-tab.active { border-bottom-color: var(--accent); color: var(--text); }
                .dbx-tab .icon { height: 15px; width: 15px; }
                .dbx-title { color: var(--muted); font-size: 13px; margin-left: auto; overflow: hidden; padding-right: 4px; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-pane { display: flex; flex: 1 1 auto; flex-direction: column; min-height: 0; }
                .dbx-pane[hidden] { display: none; }
                .dbx-bar, .dbx-sql-head { align-items: center; display: flex; gap: 8px; padding: 8px 12px; }
                .dbx-bar[hidden] { display: none; }
                .dbx-grow { flex: 1 1 auto; }
                .dbx-muted { color: var(--muted); font-size: 12.5px; }
                .dbx-icon-button { align-items: center; display: inline-flex; justify-content: center; min-height: 32px; min-width: 32px; padding: 0 8px; }
                .dbx-icon-button .icon { height: 15px; width: 15px; }
                .dbx-sql { border-radius: 10px; font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 13.5px; margin: 0 12px; min-height: 120px; resize: vertical; }
                .dbx-message { margin: 8px 12px 0; }
                .dbx-message.bad, .dbx-error { color: var(--danger, #b3261e); }
                .dbx-grid { flex: 1 1 auto; margin: 8px 0 0; min-height: 0; overflow: auto; }
                .dbx-grid table { border-collapse: separate; border-spacing: 0; font-size: 13px; min-width: 100%; }
                .dbx-grid th { background: var(--surface); border-bottom: 1px solid var(--line); cursor: pointer; font-weight: 700; position: sticky; text-align: left; top: 0; user-select: none; white-space: nowrap; z-index: 1; }
                .dbx-grid th, .dbx-grid td { border-right: 1px solid color-mix(in srgb, var(--line) 60%, transparent); max-width: 420px; overflow: hidden; padding: 6px 10px; text-overflow: ellipsis; white-space: nowrap; }
                .dbx-grid td { border-bottom: 1px solid color-mix(in srgb, var(--line) 60%, transparent); font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
                .dbx-grid tr:hover td { background: var(--hover-bg); }
                .dbx-grid td.null { color: var(--muted); font-style: italic; }
                .dbx-grid th .dbx-sort { color: var(--accent); margin-left: 4px; }
                .dbx-grid .dbx-hint { color: var(--muted); padding: 24px; }
                .dbx-login { align-items: center; background: rgb(0 0 0 / 45%); display: flex; inset: 0; justify-content: center; position: fixed; z-index: 40; }
                .dbx-login[hidden] { display: none; }
                .dbx-login-box { background: var(--panel, var(--surface)); border: 1px solid var(--line); border-radius: 16px; display: grid; gap: 12px; padding: 20px; width: min(360px, calc(100vw - 32px)); }
                .dbx-login-box h2 { margin: 0; }
                .dbx-login-box label { display: grid; gap: 4px; }
                @media (max-width: 760px) {
                    .dbx { grid-template-columns: 1fr; grid-template-rows: minmax(0, 38%) minmax(0, 1fr); }
                    .dbx-side { border-bottom: 1px solid var(--line); border-right: 0; }
                    .dbx-node { min-height: 40px; }
                }
            </style>
            <script>
                window.MatgateDatabase = { texts: {{texts}}, csrf: {{csrf}}, setup: {{setup}} };
            </script>
            <script>{{DatabaseBrowserScript}}</script>
            """;

        return Layout(context, user, server.Name, body, "db-main");
    }

    private const string DatabaseBrowserScript = """
        (() => {
            const { texts, csrf, setup } = window.MatgateDatabase;
            const root = document.querySelector('[data-db-root]');
            const $ = selector => root.querySelector(selector);
            const tree = $('[data-db-tree]');
            const filter = $('[data-db-filter]');
            const dataGrid = $('[data-db-data]');
            const resultGrid = $('[data-db-result]');
            const pager = $('[data-db-pager]');
            const range = $('[data-db-range]');
            const title = $('[data-db-title]');
            const sql = $('[data-db-sql]');
            const sqlDb = $('[data-db-sql-db]');
            const message = $('[data-db-message]');
            const login = $('[data-db-login]');
            const icon = name => (document.querySelector('[data-db-icon="' + name + '"]') || {}).innerHTML || '';
            root.querySelectorAll('[data-db-text]').forEach(node => { node.textContent = texts[node.dataset.dbText] || ''; });
            filter.placeholder = texts.filter;
            const credentials = { user: setup.user || '', password: '' };
            const state = { db: '', schema: '', table: '', offset: 0, limit: 100, order: '', desc: false, total: null };
            const opened = new Map();

            let waitingLogin = null;
            const askLogin = error => {
                if (!waitingLogin) {
                    waitingLogin = new Promise(resolve => {
                        const box = login;
                        box.hidden = false;
                        const fail = box.querySelector('[data-db-login-error]');
                        fail.hidden = !error;
                        fail.textContent = error || '';
                        box.elements.user.value = credentials.user;
                        box.elements.password.value = '';
                        window.setTimeout(() => (credentials.user ? box.elements.password : box.elements.user).focus(), 0);
                        box.onsubmit = event => {
                            event.preventDefault();
                            credentials.user = box.elements.user.value.trim();
                            credentials.password = box.elements.password.value;
                            box.hidden = true;
                            waitingLogin = null;
                            resolve();
                        };
                    });
                }
                return waitingLogin;
            };

            const api = async (path, options = {}) => {
                for (let attempt = 0; attempt < 4; attempt++) {
                    const headers = { 'X-Matgate-Csrf': csrf };
                    if (credentials.user) { headers['X-Matgate-Db-User'] = credentials.user; }
                    if (credentials.password) { headers['X-Matgate-Db-Password'] = credentials.password; }
                    const response = await fetch('/api/db/' + setup.id + path, { credentials: 'same-origin', headers, ...options });
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

            const node = (className, depth, iconHtml, label, extra) => {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'dbx-node ' + className;
                button.style.setProperty('--depth', depth);
                button.setAttribute('role', 'treeitem');
                button.innerHTML = iconHtml + '<span class="dbx-label"></span>' + (extra ? '<small></small>' : '');
                button.querySelector('.dbx-label').textContent = label;
                if (extra) { button.querySelector('small').textContent = extra; }
                return button;
            };

            // The tree: databases, opened on a click; their tables and views under their schemas.
            const renderTree = () => {
                const wanted = filter.value.trim().toLowerCase();
                const nodes = [];
                for (const [db, entry] of opened) {
                    const head = node('dbx-node-db' + (entry.open || wanted ? '' : ' closed'), 0, '<span class="dbx-caret">' + icon('caret') + '</span>' + icon('database'), db);
                    head.dataset.db = db;
                    nodes.push(head);
                    if (!(entry.open || wanted) || !entry.objects) {
                        if (entry.open && entry.error) {
                            const fail = document.createElement('p');
                            fail.className = 'dbx-muted dbx-error';
                            fail.style.margin = '4px 12px';
                            fail.textContent = entry.error;
                            nodes.push(fail);
                        }
                        continue;
                    }
                    const objects = entry.objects.filter(item => !wanted || item.name.toLowerCase().includes(wanted));
                    const schemas = Array.from(new Set(objects.map(item => item.schema)));
                    const showSchemas = schemas.length > 1 || (schemas.length === 1 && setup.protocol !== 'mysql' && schemas[0] !== 'dbo' && schemas[0] !== 'public');
                    if (!objects.length && entry.open) {
                        const none = document.createElement('p');
                        none.className = 'dbx-muted';
                        none.style.margin = '4px 28px';
                        none.textContent = texts.noTables;
                        nodes.push(none);
                    }
                    for (const schema of schemas) {
                        if (showSchemas) {
                            nodes.push(node('dbx-node-schema', 1, '', schema));
                        }
                        for (const item of objects.filter(candidate => candidate.schema === schema)) {
                            const row = node('dbx-node-table' + (state.db === db && state.schema === item.schema && state.table === item.name ? ' active' : ''),
                                showSchemas ? 2 : 1, icon(item.kind === 'view' ? 'view' : 'table'), item.name, item.kind === 'view' ? texts.view : '');
                            row.dataset.db = db;
                            row.dataset.schema = item.schema;
                            row.dataset.table = item.name;
                            nodes.push(row);
                        }
                    }
                }
                tree.replaceChildren(...nodes);
            };

            const loadObjects = async db => {
                const entry = opened.get(db);
                entry.open = true;
                if (!entry.objects) {
                    entry.error = '';
                    renderTree();
                    try {
                        entry.objects = (await api('/objects?db=' + encodeURIComponent(db))).objects;
                    }
                    catch (error) {
                        entry.error = error.message;
                    }
                }
                renderTree();
            };

            const loadDatabases = async () => {
                tree.innerHTML = '<p class="dbx-muted" style="margin:8px 12px">' + texts.loading + '</p>';
                try {
                    const data = await api('/databases');
                    const keep = new Map(opened);
                    opened.clear();
                    data.databases.forEach(db => opened.set(db, { open: (keep.get(db) || {}).open || false, objects: null }));
                    sqlDb.replaceChildren(...data.databases.map(db => { const option = document.createElement('option'); option.value = db; option.textContent = db; return option; }));
                    // The database to open: the connection's own, or the only one besides the server's system databases.
                    const system = ['information_schema', 'performance_schema', 'mysql', 'sys', 'master', 'tempdb', 'model', 'msdb', 'postgres'];
                    const own = data.databases.filter(db => system.indexOf(db.toLowerCase()) < 0);
                    const start = data.current && opened.has(data.current) ? data.current : (own.length === 1 ? own[0] : (data.databases.length === 1 ? data.databases[0] : ''));
                    if (start) {
                        sqlDb.value = start;
                        await loadObjects(start);
                    }
                    for (const [db, entry] of opened) {
                        if (entry.open && !entry.objects) { await loadObjects(db); }
                    }
                    renderTree();
                }
                catch (error) {
                    tree.innerHTML = '';
                    const fail = document.createElement('p');
                    fail.className = 'dbx-error';
                    fail.style.margin = '8px 12px';
                    fail.textContent = error.message;
                    tree.appendChild(fail);
                }
            };

            // A result as a table. Clicking a column of a table's rows orders by it.
            const renderTable = (grid, result, sortable) => {
                if (!result.columns.length) {
                    grid.innerHTML = '';
                    return;
                }
                const table = document.createElement('table');
                const head = table.createTHead().insertRow();
                result.columns.forEach(column => {
                    const cell = document.createElement('th');
                    cell.textContent = column;
                    cell.title = column;
                    if (sortable && state.order === column) {
                        const mark = document.createElement('span');
                        mark.className = 'dbx-sort';
                        mark.textContent = state.desc ? '▼' : '▲';
                        cell.appendChild(mark);
                    }
                    if (sortable) {
                        cell.addEventListener('click', () => {
                            state.desc = state.order === column ? !state.desc : false;
                            state.order = column;
                            state.offset = 0;
                            loadRows();
                        });
                    }
                    head.appendChild(cell);
                });
                const body = table.createTBody();
                result.rows.forEach(values => {
                    const row = body.insertRow();
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
                    const row = body.insertRow();
                    const cell = row.insertCell();
                    cell.colSpan = result.columns.length;
                    cell.className = 'null';
                    cell.textContent = texts.empty;
                }
                grid.replaceChildren(table);
            };

            const hint = (grid, text, bad) => {
                const line = document.createElement('p');
                line.className = 'dbx-hint' + (bad ? ' dbx-error' : '');
                line.textContent = text;
                grid.replaceChildren(line);
            };

            const loadRows = async () => {
                if (!state.table) {
                    return;
                }
                title.textContent = state.db + ' · ' + (state.schema && state.schema !== state.db ? state.schema + '.' : '') + state.table;
                pager.hidden = false;
                range.textContent = texts.loading;
                try {
                    const result = await api('/rows?' + new URLSearchParams({ db: state.db, schema: state.schema, table: state.table, offset: state.offset, limit: state.limit, order: state.order, desc: state.desc ? '1' : '0' }));
                    state.total = result.total;
                    const first = result.rows.length ? state.offset + 1 : 0;
                    range.textContent = first + '–' + (state.offset + result.rows.length) + (result.total !== null && result.total !== undefined ? ' ' + texts.of + ' ' + result.total : '') + ' ' + texts.rows;
                    $('[data-db-prev]').disabled = state.offset === 0;
                    $('[data-db-next]').disabled = result.total !== null && result.total !== undefined ? state.offset + result.rows.length >= result.total : result.rows.length < state.limit;
                    renderTable(dataGrid, result, true);
                }
                catch (error) {
                    range.textContent = '';
                    hint(dataGrid, error.message, true);
                }
            };

            const showPane = name => {
                root.querySelectorAll('[data-db-tab]').forEach(tab => tab.classList.toggle('active', tab.dataset.dbTab === name));
                root.querySelectorAll('[data-db-pane]').forEach(pane => { pane.hidden = pane.dataset.dbPane !== name; });
                if (name === 'sql') { sql.focus(); }
            };

            const runSql = async () => {
                const text = sql.value.trim();
                if (!text) {
                    sql.focus();
                    return;
                }
                message.hidden = false;
                message.className = 'dbx-message dbx-muted';
                message.textContent = texts.loading;
                const body = new FormData();
                body.append('sql', text);
                body.append('db', sqlDb.value || '');
                try {
                    const result = await api('/query', { method: 'POST', body });
                    const time = ' · ' + result.elapsedMs + ' ms';
                    if (!result.columns.length) {
                        message.textContent = texts.affected.replace('{n}', result.affected ?? 0) + time;
                        resultGrid.innerHTML = '';
                    }
                    else {
                        message.textContent = result.rows.length + ' ' + texts.rows + time + (result.truncated ? ' · ' + texts.truncated.replace('{n}', result.rows.length) : '');
                        renderTable(resultGrid, result, false);
                    }
                }
                catch (error) {
                    message.className = 'dbx-message bad';
                    message.textContent = error.message;
                    resultGrid.innerHTML = '';
                }
            };

            tree.addEventListener('click', event => {
                const target = event.target instanceof Element ? event.target.closest('.dbx-node') : null;
                if (!target) {
                    return;
                }
                if (target.dataset.table) {
                    Object.assign(state, { db: target.dataset.db, schema: target.dataset.schema, table: target.dataset.table, offset: 0, order: '', desc: false });
                    sqlDb.value = state.db;
                    showPane('data');
                    renderTree();
                    loadRows();
                }
                else if (target.dataset.db) {
                    const entry = opened.get(target.dataset.db);
                    if (entry.open) {
                        entry.open = false;
                        renderTree();
                    }
                    else {
                        sqlDb.value = target.dataset.db;
                        loadObjects(target.dataset.db);
                    }
                }
            });
            filter.addEventListener('input', renderTree);
            root.querySelectorAll('[data-db-tab]').forEach(tab => tab.addEventListener('click', () => showPane(tab.dataset.dbTab)));
            $('[data-db-run]').addEventListener('click', runSql);
            sql.addEventListener('keydown', event => {
                if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                    event.preventDefault();
                    runSql();
                }
            });
            $('[data-db-prev]').addEventListener('click', () => { state.offset = Math.max(0, state.offset - state.limit); loadRows(); });
            $('[data-db-next]').addEventListener('click', () => { state.offset += state.limit; loadRows(); });
            $('[data-db-reload]').addEventListener('click', loadRows);
            $('[data-db-refresh]').addEventListener('click', () => { opened.forEach(entry => { entry.objects = null; }); loadDatabases(); });
            hint(dataGrid, texts.pick, false);
            window.MatgateDatabase.state = state;
            loadDatabases();
        })();
        """;
}
