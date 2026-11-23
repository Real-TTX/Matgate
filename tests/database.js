// The database browser, against one server of each kind in the test stack: PostgreSQL and MariaDB with a small
// shop database (tests/db/shop.sql), SQL Server empty - its table is made through the SQL field. For each: the
// connection opens as a tab, the databases and tables are listed, a table's rows come a page at a time and
// can be ordered by a column, and a statement typed in runs. One connection has no stored password: the page
// asks for it, and asks again when it was wrong.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

(async () => {
  let b = await chromium.launch({ headless: true });
  const context = await b.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  const connection = async (name, protocol, fields) => {
    const existing = (await servers()).find(s => s.name === name);
    if (existing) { return existing; }
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(([n, p, f]) => {
      document.querySelector("[data-protocol-option='" + p + "']").click();
      const set = (field, value) => {
        const all = [...document.querySelectorAll("[name='" + field + "']")];
        const el = all.find(e => !e.disabled) || all[0];
        if (el.type === "checkbox") { el.checked = !!value; } else { el.value = value; }
        el.dispatchEvent(new Event("change", { bubbles: true }));
      };
      set("name", n);
      Object.entries(f).forEach(([field, value]) => set(field, value));
      document.querySelector("[name='name']").form.requestSubmit();
    }, [name, protocol, fields]).catch(() => {});
    await sleep(1500);
    return (await servers()).find(s => s.name === name);
  };

  const kinds = [
    { name: "Test-Postgres", protocol: "Postgres", fields: { host: "postgres", port: "5432", targetUserName: "demo", targetPassword: "demo-only-pw", databaseName: "shop" }, db: "shop" },
    { name: "Test-MariaDB", protocol: "MySql", fields: { host: "mariadb", port: "3306", targetUserName: "demo", targetPassword: "demo-only-pw" }, db: "shop" },
    // No password stored: the page asks.
    { name: "Test-MSSQL", protocol: "MsSql", fields: { host: "mssql", port: "1433", targetUserName: "sa", dbTrustCertificate: true }, db: "master", ask: "Demo-only-pw1" },
  ];
  // SQL Server takes a while to come up the first time.
  for (const kind of kinds) { kind.server = await connection(kind.name, kind.protocol, kind.fields); }
  check("the three connections are there, as databases", kinds.map(k => k.server && k.server.iconKey), ["database", "database", "database"]);
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1000);

  const frameOf = () => page.frames().find(f => /\/db\/[0-9a-f-]{36}\/browser/.test(f.url()));
  const inFrame = async (fn, arg) => frameOf().evaluate(fn, arg);
  const waitFor = async (fn, arg, ms = 30000) => {
    for (let i = 0; i < ms / 300; i++) {
      const value = await inFrame(fn, arg).catch(() => null);
      if (value) { return value; }
      await sleep(300);
    }
    return null;
  };
  const tableNames = () => inFrame(() => [...document.querySelectorAll("[data-db-tree] .dbx-node-table .dbx-label")].map(s => s.textContent));
  // The document in front (0.44.0: queries, rows and structures are tabs of the page; the first column is "#").
  const V = "[data-db-pane]:not([hidden]) ";
  const runQuery = async sql => {
    await inFrame(text => window.MatgateDatabase.active().setSql(text), sql);
    await frameOf().click(V + "[data-db-run]");
  };
  const newQuery = async sql => {
    await inFrame(text => window.MatgateDatabase.openQuery({ sql: text }), sql);
    await sleep(300);
  };
  const grid = selector => inFrame(s => {
    const table = document.querySelector(s + " table");
    return table ? { head: [...table.tHead.rows[0].cells].map(c => c.firstChild.textContent), rows: [...table.tBodies[0].rows].map(r => [...r.cells].map(c => c.textContent)) } : null;
  }, selector);

  for (const kind of kinds) {
    const label = kind.protocol + ": ";
    // Whatever an earlier round left open goes first: the frame looked at is the one of this connection.
    await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab:not(.session-tab--add) .session-tab-close").forEach(x => x.click()));
    await sleep(600);
    await page.evaluate(id => window.MatgateOpenServerTab ? window.MatgateOpenServerTab(id) : null, kind.server.id);
    await sleep(2500);
    check(label + "the connection opens the database browser in a tab", !!frameOf(), true);
    if (!frameOf()) { continue; }

    if (kind.ask) {
      const asked = await waitFor(() => !document.querySelector("[data-db-login]").hidden, null, 60000);
      check(label + "without a stored password the page asks for it", !!asked, true);
      const frame = frameOf();
      await frame.fill("[data-db-login] [name='password']", "wrong-on-purpose");
      await frame.click("[data-db-login] button[type='submit']");
      const again = await waitFor(() => !document.querySelector("[data-db-login]").hidden && !document.querySelector("[data-db-login-error]").hidden, null, 30000);
      check(label + "a wrong password asks again, with the server's words", !!again, true);
      await frame.fill("[data-db-login] [name='password']", kind.ask);
      await frame.click("[data-db-login] button[type='submit']");
      // The table to read, made through the SQL field.
      await waitFor(() => document.querySelectorAll("[data-db-tree] .dbx-node-db").length > 0, null, 30000);
      // Typed into the editor and run with Ctrl+Enter, as a person does.
      await newQuery("");
      await frame.click(V + ".cm-content");
      await page.keyboard.insertText("IF OBJECT_ID('dbo.customers') IS NULL BEGIN CREATE TABLE dbo.customers (id INT PRIMARY KEY, name NVARCHAR(80) NOT NULL, city NVARCHAR(80) NULL, since DATE NULL); INSERT INTO dbo.customers VALUES (1, N'Anna Berg', N'Hamburg', '2021-03-01'), (2, N'Ben Clausen', N'Kiel', '2022-07-15'), (3, N'Cora Dahl', NULL, '2023-01-09'); END");
      await page.keyboard.press("Control+Enter");
      const made = await waitFor(v => { const m = document.querySelector(v + "[data-db-message]"); return m && !m.hidden && !/Läuft|Running/.test(m.textContent) ? m.textContent : null; }, V);
      check(label + "a statement without rows says how many it changed", /changed|geändert|Done|Fertig/.test(made || ""), true);
      await frame.click("[data-db-refresh]");
      // Only system databases there: none opens by itself - a click on one opens it.
      await waitFor(() => [...document.querySelectorAll("[data-db-tree] .dbx-node-db")].some(n => n.textContent === "master") || null);
      await frame.locator("[data-db-tree] .dbx-node-db").filter({ hasText: /^master$/ }).click();
    }

    const listed = await waitFor(() => { const names = [...document.querySelectorAll("[data-db-tree] .dbx-node-table .dbx-label")].map(s => s.textContent); return names.includes("customers") ? names : null; }, null, 60000);
    check(label + "its database is open with the tables" + (kind.ask ? "" : " and the view"), [!!listed && listed.includes("customers"), kind.ask ? true : !!listed && listed.includes("customers_in_kiel")], [true, true]);
    if (!listed) { continue; }
    // A database's name stands beside its arrow, not in it (the arrow of a closed one is turned, and so was the
    // name: "postgres" stood on end); the filter's text starts after its magnifier.
    check(label + "each database: arrow, then its name lying flat", await inFrame(() => [...document.querySelectorAll("[data-db-tree] .dbx-node-db")].every(node =>
      !!node.querySelector(".dbx-caret svg") && !node.querySelector(".dbx-caret").textContent.trim() && !!node.querySelector(".dbx-label").textContent.trim())), true);
    check(label + "the status bar names the connection, not \"New connection\"", await page.evaluate(() => document.getElementById("status-title").textContent), kind.name);
    check(label + "the filter's text starts after its magnifier", await inFrame(() => parseFloat(getComputedStyle(document.querySelector("[data-db-filter]")).paddingLeft) >= 28), true);

    await frameOf().locator("[data-db-tree] .dbx-node-table").filter({ hasText: /^customers$/ }).first().click();
    const rows = await waitFor(v => { const t = document.querySelector(v + "[data-db-data] table"); return t && t.tBodies[0].rows.length ? true : null; }, V);
    const first = await grid(V + "[data-db-data]");
    check(label + "a click on the table shows its rows in a tab of their own, NULL as NULL", [!!rows, first && first.head.slice(1, 4), first && first.rows.length, first && first.rows[2][3]], [true, ["id", "name", "city"], 3, "NULL"]);
    check(label + "with how many there are", await inFrame(v => document.querySelector(v + "[data-db-range]").textContent, V), "1–3 of 3 rows");
    await frameOf().click(V + "[data-db-data] th:nth-child(3)");
    await sleep(400);
    await frameOf().click(V + "[data-db-data] th:nth-child(3)");
    const ordered = await waitFor(v => { const t = document.querySelector(v + "[data-db-data] table"); return t && /▼/.test(t.tHead.textContent) ? [...t.tBodies[0].rows].map(r => r.cells[2].textContent) : null; }, V);
    check(label + "a second click on a column orders by it backwards", ordered, ["Cora Dahl", "Ben Clausen", "Anna Berg"]);

    if (!kind.ask) {
      await frameOf().locator("[data-db-tree] .dbx-node-table").filter({ hasText: /^numbers$/ }).first().click();
      await waitFor(v => /of 150/.test(document.querySelector(v + "[data-db-range]").textContent) || null, V);
      check(label + "a big table comes a hundred rows at a time", await inFrame(v => document.querySelector(v + "[data-db-range]").textContent, V), "1–100 of 150 rows");
      await frameOf().click(V + "[data-db-next]");
      await waitFor(v => /^101/.test(document.querySelector(v + "[data-db-range]").textContent) || null, V);
      check(label + "and the next page has the rest", await inFrame(v => document.querySelector(v + "[data-db-range]").textContent, V), "101–150 of 150 rows");
    }

    // ---- the SQL editor
    await newQuery("");
    check(label + "a query has a real editor (CodeMirror)", await inFrame(v => !!document.querySelector(v + ".cm-editor") && window.MatgateDatabase.hasEditor, V), true);
    await runQuery("SELECT name FROM customers WHERE city = 'Kiel'");
    const answer = await waitFor(v => { const t = document.querySelector(v + "[data-db-result] table"); return t ? [...t.tBodies[0].rows].map(r => r.cells[1].textContent) : null; }, V);
    check(label + "a statement typed in runs and shows its rows", answer, ["Ben Clausen"]);
    check(label + "its words are in colour", await inFrame(v => {
      const spans = [...document.querySelectorAll(v + ".cm-line span")];
      const keyword = spans.find(s => s.textContent === "SELECT");
      const text = spans.find(s => /^'/.test(s.textContent));
      return !!keyword && getComputedStyle(keyword).color !== getComputedStyle(text).color;
    }, V), true);
    await runQuery("SELECT * FROM no_such_table");
    const fault = await waitFor(v => { const m = document.querySelector(v + "[data-db-message]"); return m && m.classList.contains("bad") ? m.textContent : null; }, V);
    check(label + "a wrong one says what the server said", !!fault && /no_such_table/i.test(fault), true);

    // Two statements, two results.
    await runQuery("SELECT 1 AS one;\nSELECT 2 AS two;");
    const tabs = await waitFor(v => { const t = [...document.querySelectorAll(v + "[data-db-result-tabs] [data-db-result-index]")]; return t.length >= 3 ? t.map(x => x.textContent) : null; }, V);
    check(label + "two statements give two results (and the messages)", tabs && tabs.length, 3);
    // Only the selection runs when something is selected.
    await inFrame(v => {
      const doc = window.MatgateDatabase.active();
      doc.setSql("SELECT 'not this' AS x;\nSELECT 'this' AS x;");
    }, V);
    await frameOf().click(V + ".cm-content");
    await page.keyboard.press("Control+End");
    await page.keyboard.press("Shift+Home");
    await frameOf().click(V + "[data-db-run]");
    const selected = await waitFor(v => { const t = document.querySelector(v + "[data-db-result] table"); const value = t ? t.tBodies[0].rows[0].cells[1].textContent : ""; return /this/.test(value) ? value : null; }, V);
    check(label + "with a selection only it runs", selected, "this");
    // Completion of tables (and, once known, their columns).
    await inFrame(() => window.MatgateDatabase.active().setSql(""));
    await frameOf().click(V + ".cm-content");
    await page.keyboard.type("SELECT * FROM custo", { delay: 30 });
    const offered = await waitFor(() => { const box = document.querySelector(".cm-tooltip-autocomplete"); return box ? box.textContent : null; }, null, 6000);
    check(label + "the editor offers the tables as one types", !!offered && /customers/.test(offered), true);
    await page.keyboard.press("Escape");

    // ---- the object explorer: a table's columns and keys, its structure, the statement that makes it
    await frameOf().locator("[data-db-tree] .dbx-node-table").filter({ hasText: /^customers$/ }).first().locator(".dbx-caret").click();
    const columns = await waitFor(() => { const names = [...document.querySelectorAll("[data-db-tree] .dbx-node[data-kind='column'] .dbx-label")].map(n => n.textContent); return names.length >= 4 ? names : null; });
    check(label + "opened in the tree, a table shows its columns", columns && columns.slice(0, 4), ["id", "name", "city", "since"]);
    check(label + "the primary key's column is marked", await inFrame(() => !!document.querySelector("[data-db-tree] .dbx-node.pk[data-kind='column']")), true);
    const struct = await inFrame(name => {
      const db = [...document.querySelectorAll("[data-db-tree] .dbx-node-table")].find(n => n.dataset.name === name);
      return window.MatgateDatabase.openStructure(db.dataset.db, db.dataset.schema, name).then(() => true);
    }, "customers");
    const structure = await waitFor(v => { const box = document.querySelector(v + "[data-db-structure]"); return box && box.querySelector("table") ? box.innerText : null; }, V);
    check(label + "the structure lists columns, types and the primary key", !!struct && !!structure && /name/.test(structure) && /Primary key|Primärschlüssel/.test(structure), true);
    await frameOf().click(V + "[data-db-structure] .dbx-bar button");
    const script = await waitFor(() => { const text = window.MatgateDatabase.active() && window.MatgateDatabase.active().getSql ? window.MatgateDatabase.active().getSql() : ""; return /CREATE TABLE/i.test(text) ? text : null; });
    check(label + "\"Script as CREATE\" gives the statement that makes the table", !!script && /customers/i.test(script) && /PRIMARY KEY/i.test(script), true);

    // ---- export: every row, as a file
    const csv = await inFrame(async ([id, password]) => {
      const body = new FormData(); body.append("sql", "SELECT id, name FROM customers ORDER BY id"); body.append("format", "csv"); body.append("separator", ",");
      const db = document.querySelector("[data-db-tree] .dbx-node-table").dataset.db; body.append("db", db);
      const headers = { "X-Matgate-Csrf": window.MatgateDatabase.csrf };
      if (password) { headers["X-Matgate-Db-Password"] = password; }
      const response = await fetch("/api/db/" + id + "/export", { method: "POST", body, credentials: "same-origin", headers });
      return response.ok ? (await response.text()).replace(/^﻿/, "").trim().split(/\r?\n/) : ["HTTP " + response.status];
    }, [kind.server.id, kind.ask || ""]);
    check(label + "the export has every row with a header", csv, ["id,name", "1,Anna Berg", "2,Ben Clausen", "3,Cora Dahl"]);

    if (kind.protocol === "Postgres") {
      // A long query can be stopped.
      await newQuery("");
      await runQuery("SELECT pg_sleep(30)");
      await sleep(1200);
      await frameOf().click(V + "[data-db-stop]");
      const stopped = await waitFor(v => { const m = document.querySelector(v + "[data-db-message]"); return m && m.classList.contains("bad") ? m.textContent : null; }, V, 15000);
      check(label + "\"Cancel\" stops a running query", !!stopped && /Abgebrochen|Cancelled|cancel/i.test(stopped), true);
      // The history has what ran; the queries are there again after a reload.
      await frameOf().click(V + "[data-db-history]");
      check(label + "the history lists what ran", await inFrame(() => [...document.querySelectorAll("[data-db-menu] .dbx-history-item span")].some(s => /pg_sleep/.test(s.textContent))), true);
      await frameOf().press("body", "Escape");
      const before = await inFrame(() => document.querySelectorAll("[data-db-doc='query']").length);
      await frameOf().evaluate(() => location.reload());
      await sleep(2500);
      const after = await waitFor(() => document.querySelectorAll("[data-db-doc='query']").length || null, null, 15000);
      check(label + "the query tabs are there again after a reload", after, before);
    }
    // Leave no query tabs behind for the next connection's round.
    await inFrame(() => { try { Object.keys(localStorage).filter(k => k.startsWith("matgate.db.")).forEach(k => localStorage.removeItem(k)); } catch { } });

    // Close the tab again.
    await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab:not(.session-tab--add) .session-tab-close").forEach(x => x.click()));
    await sleep(600);
  }

  // Someone without the connection gets nothing of it.
  const status = await page.evaluate(async id => (await fetch("/api/db/" + id + "/databases", { headers: { "X-Matgate-Csrf": "wrong" } })).status, kinds[0].server.id);
  check("without the page's token the database is not read", status, 400);

  // ================================================================== quick connect, with nothing saved
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(800);
  check("the home page offers the three databases for quick connect", await page.evaluate(() => [...document.querySelectorAll(".home2-quick-row [data-home2-qc]")].map(chip => chip.dataset.home2Qc).filter(key => /sql|postgres/.test(key))), ["mssql", "mysql", "postgres"]);
  await page.click(".home2-quick-row [data-home2-more]");
  await page.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  check("the kind is one field, not a row of chips", await page.evaluate(() => [
    document.querySelectorAll("[data-home2-proto-dialog] [data-kind-button]").length,
    [...document.querySelectorAll("[data-home2-proto-dialog] [data-home2-qc-proto]")].filter(chip => chip.offsetParent !== null).length]), [1, 0]);
  await page.click("[data-home2-proto-dialog] [data-kind-button]");
  check("it opens a list grouped by what a connection is for", await page.evaluate(() => [...document.querySelectorAll(".kind-menu .toolbar-menu-heading")].map(h => h.textContent)), ["Remote access", "Files", "Web", "Databases"]);
  check("the list hangs from the field, inside the dialog", await page.evaluate(() => {
    const field = document.querySelector("[data-home2-proto-dialog] [data-kind-button]").getBoundingClientRect();
    const list = document.querySelector(".kind-menu").getBoundingClientRect();
    return [!!document.querySelector(".kind-menu").closest("dialog[open]"), Math.abs(list.left - field.left) < 2, list.top >= field.bottom, list.width >= field.width - 1];
  }), [true, true, true, true]);
  await page.click(".kind-menu .toolbar-menu-item[data-value='postgres']");
  const quickFields = () => page.evaluate(() => ({
    kind: document.querySelector("[data-home2-qc-proto-input]").value,
    shown: document.querySelector("[data-home2-proto-dialog] [data-kind-current] .kind-name").textContent,
    port: document.querySelector("[data-home2-qc-port]").value,
    fields: [...document.querySelectorAll("[data-home2-qc-form] input:not([type='hidden'])")].filter(field => field.offsetParent !== null).map(field => field.name),
  }));
  check("PostgreSQL asks for host, port, sign-in and, if wanted, the database", await quickFields(), { kind: "postgres", shown: "Postgres", port: "5432", fields: ["host", "port", "username", "password", "database"] });
  check("and the list is closed again", await page.evaluate(() => !document.querySelector(".kind-menu")), true);
  await page.click("[data-home2-proto-dialog] [data-kind-button]");
  await page.click("[data-home2-proto-dialog] [data-kind-button]");
  check("a second click on the field closes the list", await page.evaluate(() => !document.querySelector(".kind-menu")), true);
  await page.fill("[data-home2-qc-host]", "postgres");
  await page.fill("[data-home2-qc-form] [name='username']", "demo");
  await page.fill("[data-home2-qc-form] [name='password']", "demo-only-pw");
  await page.fill("[data-home2-qc-form] [name='database']", "shop");
  await page.click("[data-home2-qc-form] button[type='submit']");
  let quickFrame = null;
  for (let i = 0; i < 60 && !quickFrame; i++) { await sleep(300); quickFrame = frameOf(); }
  check("connecting opens the database browser in a tab", !!quickFrame, true);
  if (quickFrame) {
    const quickTables = await waitFor(() => { const names = [...document.querySelectorAll("[data-db-tree] .dbx-node-table .dbx-label")].map(s => s.textContent); return names.includes("customers") ? names : null; }, null, 30000);
    check("with the tables of the database asked for", !!quickTables && quickTables.includes("customers"), true);
  }
  check("and nothing was saved", (await servers()).filter(s => s.name === "postgres").length, 0);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
