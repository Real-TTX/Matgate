// The SQL studio's rows and plans (0.52.0), on PostgreSQL, MariaDB and SQL Server of the test stack:
//   - in a table's data view, "Edit": a cell changed, one set to NULL, a row removed, a row added; the SQL preview
//     shows UPDATE, DELETE and INSERT; saved, the table is so
//   - a row changed meanwhile by someone else: saving says so and changes nothing (one transaction)
//   - a table without a primary key is not edited
//   - "Plan": the steps of a statement (a tree; MySQL's table), nothing run; two statements are refused;
//     PostgreSQL "with run times" runs an UPDATE and rolls it back
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

const kinds = [
  {
    name: "Test-Postgres", db: "shop", schema: "public", label: "PostgreSQL: ",
    setup: [
      "DROP TABLE IF EXISTS edit_test, edit_nokey",
      "CREATE TABLE edit_test (id serial PRIMARY KEY, name varchar(40) NOT NULL, amount numeric(10,2), note text)",
      "CREATE TABLE edit_nokey (name text)",
      "INSERT INTO edit_test (name, amount) VALUES ('Anna', 10.50), ('Bernd', 20), ('Clara', 30)",
    ],
  },
  {
    name: "Test-MariaDB", db: "shop", schema: "shop", label: "MariaDB: ",
    setup: [
      "DROP TABLE IF EXISTS edit_test, edit_nokey",
      "CREATE TABLE edit_test (id int AUTO_INCREMENT PRIMARY KEY, name varchar(40) NOT NULL, amount decimal(10,2), note text)",
      "CREATE TABLE edit_nokey (name text)",
      "INSERT INTO edit_test (name, amount) VALUES ('Anna', 10.50), ('Bernd', 20), ('Clara', 30)",
    ],
  },
  {
    name: "Test-MSSQL", db: "master", schema: "dbo", label: "SQL Server: ", password: "Demo-only-pw1",
    setup: [
      "IF OBJECT_ID('dbo.edit_test') IS NOT NULL DROP TABLE dbo.edit_test",
      "IF OBJECT_ID('dbo.edit_nokey') IS NOT NULL DROP TABLE dbo.edit_nokey",
      "CREATE TABLE dbo.edit_test (id int IDENTITY PRIMARY KEY, name nvarchar(40) NOT NULL, amount decimal(10,2), note nvarchar(max))",
      "CREATE TABLE dbo.edit_nokey (name nvarchar(40))",
      "INSERT INTO dbo.edit_test (name, amount) VALUES ('Anna', 10.50), ('Bernd', 20), ('Clara', 30)",
    ],
  },
];

(async () => {
  const b = await chromium.launch({ headless: true });
  const p = await (await b.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
  p.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await p.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p.fill("input[name='username']", "admin");
  await p.fill("input[name='password']", "test-only-pw");
  await Promise.all([p.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), p.click(".login-submit")]);
  const servers = await p.evaluate(async () => JSON.parse((await (await fetch("/", { credentials: "same-origin" })).text()).match(/const availableServers = (\[.*?\]);/s)[1]));
  const V = "[data-db-pane]:not([hidden]) ";
  const waitFor = async (fn, arg, ms = 30000) => {
    for (let i = 0; i < ms / 250; i++) {
      const value = await p.evaluate(fn, arg).catch(() => null);
      if (value) { return value; }
      await sleep(250);
    }
    return null;
  };
  try {
    for (const kind of kinds) {
      const server = servers.find(s => s.name === kind.name);
      if (!server) { check(kind.label + "the connection is there (run database.js first)", false, true); continue; }
      await p.goto(BASE + "/db/" + server.id + "/browser", { waitUntil: "networkidle" });
      // A statement straight through the gateway, as the page sends it.
      const sql = text => p.evaluate(async ([id, db, statement, password]) => {
        const body = new FormData();
        body.append("sql", statement);
        body.append("db", db);
        const headers = { "X-Matgate-Csrf": window.MatgateDatabase.csrf };
        if (password) { headers["X-Matgate-Db-User"] = "sa"; headers["X-Matgate-Db-Password"] = password; }
        const response = await fetch("/api/db/" + id + "/query", { method: "POST", body, headers, credentials: "same-origin" });
        return response.json();
      }, [server.id, kind.db, text, kind.password || ""]);
      for (const statement of kind.setup) { await sql(statement); }
      const rows = async () => (await sql("SELECT id, name, amount FROM " + (kind.schema === "dbo" ? "dbo." : "") + "edit_test ORDER BY id")).rows;
      check(kind.label + "the test table has its three rows", (await rows()).map(r => r[1]), ["Anna", "Bernd", "Clara"]);

      await p.evaluate(([db, schema]) => window.MatgateDatabase.openData(db, schema, "edit_test", "table"), [kind.db, kind.schema]);
      if (kind.password) {
        if (await waitFor(() => !document.querySelector("[data-db-login]").hidden, null, 20000)) {
          await p.fill("[data-db-login] [name='password']", kind.password);
          await p.click("[data-db-login] button[type='submit']");
        }
      }
      await waitFor(() => document.querySelectorAll("[data-db-pane]:not([hidden]) [data-db-data] tbody tr").length === 3);
      await p.click(V + "[data-db-edit]");
      await waitFor(() => !document.querySelector("[data-db-pane]:not([hidden]) [data-db-edit-bar]").hidden);
      check(kind.label + "edit: the automatic number is not editable", await p.evaluate(v => document.querySelector(v + "tr[data-row='0'] td[data-col='id']").classList.contains("locked"), V), true);

      await p.click(V + "tr[data-row='0'] td[data-col='name']");
      await p.fill(V + "[data-db-cell-input]", "Änne");
      await p.keyboard.press("Enter");
      await p.click(V + "tr[data-row='1'] td[data-col='amount']");
      await p.click(V + "[data-db-cell-null]");
      await p.click(V + "tr[data-row='2'] td.num");
      await p.click(V + "[data-db-edit-add]");
      await p.fill(V + "[data-db-cell-input]", "Dora");
      await p.keyboard.press("Tab");
      await p.fill(V + "[data-db-cell-input]", "7.25");
      await p.keyboard.press("Enter");
      check(kind.label + "the changes are marked", await p.evaluate(v => [
        document.querySelectorAll(v + "td.changed").length,
        document.querySelectorAll(v + "tr.deleted").length,
        document.querySelectorAll(v + "tr.added").length,
        document.querySelector(v + "[data-db-edit-text]").textContent.startsWith("4 change"),
      ], V), [4, 1, 1, true]);
      await p.click(V + "[data-db-edit-preview-button]");
      const preview = await waitFor(v => (document.querySelector(v + "[data-db-edit-sql]") || {}).textContent, V);
      check(kind.label + "the SQL preview: one DELETE, two UPDATEs, one INSERT", preview && [/DELETE FROM/g, /UPDATE /g, /INSERT INTO/g].map(r => (preview.match(r) || []).length), [1, 2, 1]);
      check(kind.label + "paging waits until saved", await p.evaluate(v => { document.querySelector(v + "[data-db-reload]").click(); return document.querySelector(v + "[data-db-edit-preview]").textContent; }, V), "Save or discard first");
      await p.click(V + "[data-db-edit-save]");
      await waitFor(v => /Saved/.test(document.querySelector(v + "[data-db-edit-preview]").textContent), V);
      const after = await rows();
      check(kind.label + "saved: changed, NULL, removed, added", after.map(r => [r[1], r[2] === null ? null : Number(r[2])]), [["Änne", 10.5], ["Bernd", null], ["Dora", 7.25]]);

      // Somebody else removes the row meanwhile: nothing is saved.
      await p.click(V + "tr[data-row='0'] td[data-col='name']");
      await p.fill(V + "[data-db-cell-input]", "Zu spät");
      await p.keyboard.press("Enter");
      await p.click(V + "tr[data-row='1'] td[data-col='name']");
      await p.fill(V + "[data-db-cell-input]", "Auch nicht");
      await p.keyboard.press("Enter");
      await sql("DELETE FROM " + (kind.schema === "dbo" ? "dbo." : "") + "edit_test WHERE id = " + after[0][0]);
      await p.click(V + "[data-db-edit-save]");
      const refused = await waitFor(v => (document.querySelector(v + "[data-db-edit-preview] .dbx-error") || {}).textContent, V);
      check(kind.label + "a row gone meanwhile: saving says so", !!refused && /meanwhile/.test(refused), true);
      check(kind.label + "and nothing at all was saved", (await rows()).map(r => r[1]), ["Bernd", "Dora"]);
      await p.click(V + "[data-db-edit-discard]");

      // A table without a primary key.
      await p.evaluate(([db, schema]) => window.MatgateDatabase.openData(db, schema, "edit_nokey", "table"), [kind.db, kind.schema]);
      await waitFor(() => document.querySelector("[data-db-pane]:not([hidden]) [data-db-data] table"));
      await p.click(V + "[data-db-edit]");
      check(kind.label + "no primary key: not edited, and why", await waitFor(v => { const text = document.querySelector(v + "[data-db-edit-text]").textContent; return /primary key/.test(text) && document.querySelector(v + "[data-db-edit-add]").disabled; }, V), true);

      // The plan.
      await p.evaluate(([db, schema]) => window.MatgateDatabase.openQuery({ sql: "SELECT * FROM " + (schema === "dbo" ? "dbo." : "") + "edit_test WHERE name = 'Dora'", db }), [kind.db, kind.schema]);
      await p.click(V + "[data-db-plan]");
      const shape = await waitFor(v => { const box = document.querySelector(v + "[data-db-plan-shape]"); return box && box.dataset.dbPlanShape; }, V);
      if (kind.name === "Test-MariaDB") {
        check(kind.label + "the plan is the table of steps, the full scan marked", [shape, await p.evaluate(v => document.querySelectorAll(v + "[data-db-plan-shape] tr.full-scan").length > 0, V)], ["table", true]);
      }
      else {
        const steps = await p.evaluate(v => [...document.querySelectorAll(v + ".dbx-plan-node")].map(n => n.dataset.planStep), V);
        check(kind.label + "the plan is a tree of steps on the table", [shape, steps.length > 0, steps.some(s => /Scan|Seek/.test(s))], ["tree", true, true]);
      }
      check(kind.label + "the plan ran nothing", (await rows()).map(r => r[1]), ["Bernd", "Dora"]);
      await p.evaluate(text => window.MatgateDatabase.active().setSql(text), "SELECT 1; SELECT 2");
      await p.click(V + "[data-db-plan]");
      check(kind.label + "two statements: one, please", !!(await waitFor(v => /exactly one statement/.test(document.querySelector(v + "[data-db-message]").textContent), V)), true);

      if (kind.name === "Test-Postgres") {
        await p.evaluate(text => window.MatgateDatabase.active().setSql(text), "UPDATE edit_test SET name = 'Weg'");
        await p.check(V + "[data-db-plan-times]");
        await p.click(V + "[data-db-plan]");
        const notes = await waitFor(v => [...document.querySelectorAll(v + ".dbx-plan-notes li")].map(n => n.textContent).join(" | "), V);
        check(kind.label + "with run times: measured, and rolled back", [!!notes && /rolled back/.test(notes), await p.evaluate(v => /actually/.test(document.querySelector(v + ".dbx-plan-numbers").textContent), V)], [true, true]);
        check(kind.label + "the UPDATE did not stay", (await rows()).map(r => r[1]), ["Bernd", "Dora"]);
      }
      for (const statement of kind.setup.slice(0, kind.name === "Test-MSSQL" ? 2 : 1)) { await sql(statement); }
    }
  }
  finally {
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
