// Who may do what (0.41.0): a group gets a folder on its access page, and its member then has what lies in the
// folder and below - the access page of a subfolder says so, with the reason. A subfolder that stops inheriting
// keeps it out; renaming the folder keeps what was given on it. A database the user may only read runs a SELECT
// and refuses a DELETE - also one hidden behind a second statement - and the table is unchanged afterwards.
const { chromium } = require("playwright-core");
const { folderInTree, dissolveFolders } = require("./admin-helpers");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

(async () => {
  const b = await chromium.launch({ headless: true });
  const signIn = async (page, user, password) => {
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name=\"username\"]", user);
    await page.fill("input[name=\"password\"]", password);
    await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
    await sleep(1000);
  };
  const admin = await (await b.newContext({ viewport: { width: 1300, height: 900 }, locale: "de-DE" })).newPage();
  admin.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await signIn(admin, "admin", "test-only-pw");

  const USER = "RechteTester";
  const PW = "test-only-pw-8642";
  const GROUP = "Rechte-Team";
  const FOLDER = "Rechte";
  const DB = "Rechte-DB";
  const servers = page => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]).filter(s => !s.areaKind && (s.protocol || "").toUpperCase() !== "LOCAL");
  });
  const csrf = page => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const csrfToken = ("[^"]*")/)[1]);
  });
  const fileInto = async (id, folder) => {
    const token = await csrf(admin);
    await admin.evaluate(async ([i, f, t]) => { const body = new FormData(); body.append("folder", f); await fetch("/api/servers/" + i + "/folder", { method: "POST", body, credentials: "same-origin", headers: { "X-Matgate-Csrf": t } }); }, [id, folder, token]);
  };
  const submit = async (page, selector) => {
    await Promise.all([page.waitForLoadState("networkidle"), page.click(selector)]);
    await sleep(600);
  };
  const removeByForm = async (page, url, match) => {
    await page.goto(BASE + url, { waitUntil: "networkidle" });
    await page.evaluate(m => {
      const row = Array.from(document.querySelectorAll("tr")).find(r => r.innerText.includes(m));
      const form = row && row.querySelector("form[action*='/delete']");
      if (form) { form.removeAttribute("data-confirm"); form.submit(); }
    }, match);
    await sleep(900);
  };
  const query = (page, id, sql) => page.evaluate(async ([i, s]) => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const token = JSON.parse(text.match(/const csrfToken = ("[^"]*")/)[1]);
    const body = new FormData(); body.append("sql", s); body.append("db", "shop");
    const response = await fetch("/api/db/" + i + "/query", { method: "POST", body, credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
    return { status: response.status, body: await response.json().catch(() => ({})) };
  }, [id, sql]);

  const host = (await servers(admin)).find(s => s.name === "Linux-Testhost");
  const member = await (await b.newContext({ viewport: { width: 1300, height: 900 }, locale: "de-DE" })).newPage();
  let dbId = null;
  try {
    // ---- a user, a group (empty), a folder with a subfolder holding the test host
    await admin.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
    await admin.fill("input[name=\"username\"]", USER);
    await admin.fill("input[name=\"password\"]", PW);
    await admin.evaluate(() => document.querySelector("input[name='username']").form.requestSubmit());
    await sleep(1500);
    await admin.goto(BASE + "/admin/groups/new", { waitUntil: "networkidle" });
    await admin.fill("input[name='name']", GROUP);
    await submit(admin, "button[form='group-form']");
    await fileInto(host.id, FOLDER + "/Linux");

    const userId = await admin.evaluate(async u => {
      const doc = new DOMParser().parseFromString(await (await fetch("/admin/users", { credentials: "same-origin" })).text(), "text/html");
      const link = [...doc.querySelectorAll("a[href^='/admin/users/']")].find(a => a.textContent.trim() === u);
      return link ? link.getAttribute("href").split("/").pop() : null;
    }, USER);
    check("the user exists", !!userId, true);

    // ---- membership from the user's own page
    await admin.goto(BASE + "/admin/users/" + userId, { waitUntil: "networkidle" });
    await admin.evaluate(g => { const box = [...document.querySelectorAll("input[name='groups']")].find(i => i.closest("label").textContent.includes(g)); box.checked = true; }, GROUP);
    await submit(admin, "[data-user-access-save]");
    await admin.goto(BASE + "/admin/users/" + userId, { waitUntil: "networkidle" });
    check("the user page ticks the group they were put in", await admin.evaluate(g => [...document.querySelectorAll("input[name='groups']")].some(i => i.checked && i.closest("label").textContent.includes(g)), GROUP), true);

    await signIn(member, USER, PW);
    check("without anything given the member has no connection", (await servers(member)).length, 0);

    // ---- the folder to the group, on its access page
    check("the folder is in the administration's tree", await folderInTree(admin, FOLDER + "/Linux"), true);
    await admin.goto(BASE + "/admin/access?folder=" + FOLDER, { waitUntil: "networkidle" });
    const groupValue = await admin.evaluate(g => [...document.querySelectorAll("[data-access-new-principal] option")].find(o => o.textContent === g).value, GROUP);
    await admin.selectOption("[data-access-new-principal]", groupValue);
    await admin.selectOption("[data-access-new-level]", "Connect");
    await submit(admin, "[data-access-add]");
    check("the entry is listed on the folder", await admin.evaluate(() => document.querySelectorAll("[data-access-given] [data-grant-row]").length), 1);

    const names = async () => (await servers(member)).map(s => s.name).sort();
    check("the member has what lies in a subfolder of the folder", await names(), ["Linux-Testhost"]);

    await admin.goto(BASE + "/admin/access?folder=" + encodeURIComponent(FOLDER + "/Linux"), { waitUntil: "networkidle" });
    const row = await admin.evaluate(u => {
      const tr = document.querySelector("[data-access-effective] [data-effective-user='" + u + "']");
      return tr ? { level: tr.dataset.effectiveLevel, why: tr.querySelector(".access-reasons").innerText.replace(/\s+/g, " ").trim() } : null;
    }, USER);
    check("the subfolder's page says who may", row && row.level, "Connect");
    check("and why: the group, the folder above, inherited", !!row && row.why.includes(GROUP) && row.why.includes(FOLDER) && /geerbt|inherited/.test(row.why), true);

    // ---- the subfolder stops inheriting
    await Promise.all([admin.waitForNavigation({ waitUntil: "networkidle" }), admin.evaluate(() => { const box = document.querySelector("[data-access-inherit]"); box.checked = false; box.dispatchEvent(new Event("change", { bubbles: true })); })]);
    await sleep(500);
    check("a subfolder that does not inherit keeps the folder's entry out", await names(), []);
    await Promise.all([admin.waitForNavigation({ waitUntil: "networkidle" }), admin.evaluate(() => { const box = document.querySelector("[data-access-inherit]"); box.checked = true; box.dispatchEvent(new Event("change", { bubbles: true })); })]);
    await sleep(500);
    check("inheriting again, it is back", await names(), ["Linux-Testhost"]);

    // ---- renaming the folder keeps the entry, and the subfolder moves with it
    await admin.goto(BASE + "/admin/folders/edit?name=" + FOLDER, { waitUntil: "networkidle" });
    await admin.fill("input[name='name']", FOLDER + "2");
    await submit(admin, "button[form='folder-form']");
    const moved = (await servers(admin)).find(s => s.id === host.id);
    check("the subfolder was renamed along", moved && moved.folder, FOLDER + "2/Linux");
    check("the member still has it after the rename", await names(), ["Linux-Testhost"]);

    // ---- a database the user may only read
    await admin.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(400);
    await admin.evaluate(n => {
      document.querySelector("[data-protocol-option='Postgres']").click();
      const set = (field, value) => {
        const el = [...document.querySelectorAll("[name='" + field + "']")].find(e => !e.disabled);
        if (!el) { return; }
        el.value = value;
        el.dispatchEvent(new Event("input", { bubbles: true }));
        el.dispatchEvent(new Event("change", { bubbles: true }));
      };
      set("name", n); set("host", "postgres"); set("port", "5432"); set("targetUserName", "demo"); set("targetPassword", "demo-only-pw"); set("databaseName", "shop");
      document.querySelector("[name='name']").form.requestSubmit();
    }, DB).catch(() => { });
    await sleep(1500);
    dbId = ((await servers(admin)).find(s => s.name === DB) || {}).id;
    check("the database connection exists", !!dbId, true);

    await admin.goto(BASE + "/admin/users/" + userId, { waitUntil: "networkidle" });
    await admin.selectOption("select[name='level_" + dbId.replace(/-/g, "") + "']", "ReadOnly");
    await submit(admin, "[data-user-access-save]");
    await admin.goto(BASE + "/admin/users/" + userId, { waitUntil: "networkidle" });
    check("the user page shows it as read only", await admin.evaluate(n => document.querySelector("tr[data-user-access-row='" + n + "']").dataset.effectiveLevel, DB), "ReadOnly");

    const before = await query(admin, dbId, "SELECT count(*) AS n FROM customers");
    const read = await query(member, dbId, "SELECT name FROM customers ORDER BY id");
    check("reading works", read.status === 200 && (read.body.rows || []).length > 0, true);
    const del = await query(member, dbId, "DELETE FROM customers");
    check("a DELETE is refused", del.status !== 200 && /Nur lesen|Read-only/.test(del.body.error || ""), true);
    const hidden = await query(member, dbId, "SELECT 1; DELETE FROM customers");
    check("also one behind a second statement", hidden.status !== 200, true);
    const cte = await query(member, dbId, "WITH gone AS (DELETE FROM customers RETURNING 1) SELECT count(*) FROM gone");
    check("and one inside a WITH", cte.status !== 200, true);
    const after = await query(admin, dbId, "SELECT count(*) AS n FROM customers");
    check("the table is unchanged", after.body.rows, before.body.rows);
    const write = await query(admin, dbId, "SELECT 1");
    check("an administrator still runs anything", write.status, 200);
  }
  finally {
    await fileInto(host.id, "");
    if (dbId) { await removeByForm(admin, "/admin?tab=servers", DB); }
    // The group (its delete button is on its own page).
    await admin.goto(BASE + "/admin?tab=groups", { waitUntil: "networkidle" });
    const groupLink = await admin.evaluate(g => { const a = [...document.querySelectorAll("a[href^='/admin/groups/']")].find(x => x.textContent.trim() === g); return a ? a.getAttribute("href") : null; }, GROUP);
    if (groupLink) {
      await admin.goto(BASE + groupLink, { waitUntil: "networkidle" });
      await admin.evaluate(() => { const form = document.querySelector("form[action$='/delete']"); form.removeAttribute("data-confirm"); form.submit(); });
      await sleep(900);
    }
    await dissolveFolders(admin, [FOLDER, FOLDER + "2", FOLDER + "/Linux", FOLDER + "2/Linux"]);
    await removeByForm(admin, "/admin/users", USER);
  }

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
