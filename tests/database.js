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
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
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
  const tableNames = () => inFrame(() => [...document.querySelectorAll("[data-db-tree] .dbx-node-table span")].map(s => s.textContent));
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
      await frame.click("[data-db-tab='sql']");
      await frame.fill("[data-db-sql]", "IF OBJECT_ID('dbo.customers') IS NULL BEGIN CREATE TABLE dbo.customers (id INT PRIMARY KEY, name NVARCHAR(80) NOT NULL, city NVARCHAR(80) NULL, since DATE NULL); INSERT INTO dbo.customers VALUES (1, N'Anna Berg', N'Hamburg', '2021-03-01'), (2, N'Ben Clausen', N'Kiel', '2022-07-15'), (3, N'Cora Dahl', NULL, '2023-01-09'); END");
      await frame.press("[data-db-sql]", "Control+Enter");
      const made = await waitFor(() => { const m = document.querySelector("[data-db-message]"); return m && !/Lädt|Loading/.test(m.textContent) ? m.textContent : null; });
      check(label + "a statement without rows says how many it changed", /changed|geändert/.test(made || ""), true);
      await frame.click("[data-db-refresh]");
      // Only system databases there: none opens by itself - a click on one opens it.
      await waitFor(() => [...document.querySelectorAll("[data-db-tree] .dbx-node-db")].some(n => n.textContent === "master") || null);
      await frame.locator("[data-db-tree] .dbx-node-db").filter({ hasText: /^master$/ }).click();
    }

    const listed = await waitFor(() => { const names = [...document.querySelectorAll("[data-db-tree] .dbx-node-table span")].map(s => s.textContent); return names.includes("customers") ? names : null; }, null, 60000);
    check(label + "its database is open with the tables" + (kind.ask ? "" : " and the view"), [!!listed && listed.includes("customers"), kind.ask ? true : !!listed && listed.includes("customers_in_kiel")], [true, true]);
    if (!listed) { continue; }

    await frameOf().locator("[data-db-tree] .dbx-node-table").filter({ hasText: /^customers$/ }).first().click();
    const rows = await waitFor(() => { const t = document.querySelector("[data-db-data] table"); return t && t.tBodies[0].rows.length ? true : null; });
    const first = await grid("[data-db-data]");
    check(label + "a click on the table shows its rows, NULL as NULL", [!!rows, first && first.head.slice(0, 3), first && first.rows.length, first && first.rows[2][2]], [true, ["id", "name", "city"], 3, "NULL"]);
    check(label + "with how many there are", await inFrame(() => document.querySelector("[data-db-range]").textContent), "1–3 of 3 rows");
    await frameOf().click("[data-db-data] th:nth-child(2)");
    await sleep(400);
    await frameOf().click("[data-db-data] th:nth-child(2)");
    const ordered = await waitFor(() => { const t = document.querySelector("[data-db-data] table"); return t && /▼/.test(t.tHead.textContent) ? [...t.tBodies[0].rows].map(r => r.cells[1].textContent) : null; });
    check(label + "a second click on a column orders by it backwards", ordered, ["Cora Dahl", "Ben Clausen", "Anna Berg"]);

    if (!kind.ask) {
      await frameOf().locator("[data-db-tree] .dbx-node-table").filter({ hasText: /^numbers$/ }).first().click();
      await waitFor(() => /of 150/.test(document.querySelector("[data-db-range]").textContent) || null);
      check(label + "a big table comes a hundred rows at a time", await inFrame(() => document.querySelector("[data-db-range]").textContent), "1–100 of 150 rows");
      await frameOf().click("[data-db-next]");
      await waitFor(() => /^101/.test(document.querySelector("[data-db-range]").textContent) || null);
      check(label + "and the next page has the rest", await inFrame(() => document.querySelector("[data-db-range]").textContent), "101–150 of 150 rows");
    }

    await frameOf().click("[data-db-tab='sql']");
    await frameOf().fill("[data-db-sql]", "SELECT name FROM customers WHERE city = 'Kiel'");
    await frameOf().click("[data-db-run]");
    const answer = await waitFor(() => { const t = document.querySelector("[data-db-result] table"); return t ? [...t.tBodies[0].rows].map(r => r.cells[0].textContent) : null; });
    check(label + "a statement typed in runs and shows its rows", answer, ["Ben Clausen"]);
    await frameOf().fill("[data-db-sql]", "SELECT * FROM no_such_table");
    await frameOf().click("[data-db-run]");
    const fault = await waitFor(() => { const m = document.querySelector("[data-db-message]"); return m && m.classList.contains("bad") ? m.textContent : null; });
    check(label + "a wrong one says what the server said", !!fault && /no_such_table/i.test(fault), true);

    // Close the tab again.
    await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab:not(.session-tab--add) .session-tab-close").forEach(x => x.click()));
    await sleep(600);
  }

  // Someone without the connection gets nothing of it.
  const status = await page.evaluate(async id => (await fetch("/api/db/" + id + "/databases", { headers: { "X-Matgate-Csrf": "wrong" } })).status, kinds[0].server.id);
  check("without the page's token the database is not read", status, 400);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
