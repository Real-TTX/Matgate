// Stored credentials: made once, chosen in a connection, changed in one place for all - and deleted, after
// which the connection has no login. Checked through an SFTP connection to the stack's ssh host: its file
// list opens only with the right login.
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
  const page = await (await b.newContext({ viewport: { width: 1300, height: 900 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const NAME = "Testlogin-" + Date.now().toString(36);
  const CONN = "SFTP-" + NAME;
  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  const canList = id => page.evaluate(async i => (await fetch("/api/files/" + i + "/list?path=/", { credentials: "same-origin" })).ok, id);
  const credentialId = async () => {
    await page.goto(BASE + "/credentials", { waitUntil: "networkidle" });
    return page.evaluate(n => {
      const row = [...document.querySelectorAll("[data-credential-row]")].find(r => r.querySelector("strong").textContent === n);
      const link = row && row.querySelector("a[href^='/credentials/']");
      return link ? link.getAttribute("href").split("/").pop() : null;
    }, NAME);
  };
  const saveCredential = async (fields, id) => {
    await page.goto(BASE + "/credentials/" + (id || "new"), { waitUntil: "networkidle" });
    for (const [field, value] of Object.entries(fields)) { await page.fill("#credential-form [name='" + field + "']", value); }
    await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='credential-form']")]);
    await sleep(500);
  };

  let server = null;
  try {
    // ---- made once
    await saveCredential({ name: NAME, userName: "demo", password: "demo-only-pw" });
    const id = await credentialId();
    check("credentials are made on their own page and listed, without their password", [!!id, await page.evaluate(() => !document.body.innerHTML.includes("demo-only-pw"))], [true, true]);

    // ---- chosen in a connection, which then keeps no login of its own
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(([n, c]) => {
      document.querySelector("[data-protocol-option='Sftp']").click();
      const set = (field, value) => { const all = [...document.querySelectorAll("[name='" + field + "']")]; const el = all.find(e => !e.disabled) || all[0]; el.value = value; el.dispatchEvent(new Event("change", { bubbles: true })); };
      set("name", n); set("host", "ssh"); set("port", "2222"); set("credentialId", c);
    }, [CONN, id]);
    check("with credentials chosen the form does not ask for a login of its own", await page.evaluate(() => [...document.querySelectorAll("[data-credential-own]")].every(field => !field.offsetParent)), true);
    await page.evaluate(() => document.querySelector("[name='name']").form.requestSubmit()).catch(() => {});
    await sleep(1500);
    server = (await servers()).find(s => s.name === CONN);
    check("the connection signs in with them", !!server && await canList(server.id), true);

    // ---- changed in one place
    await saveCredential({ password: "wrong-on-purpose" }, id);
    check("a new password in the credentials is the connection's new password", await canList(server.id), false);
    await saveCredential({ password: "demo-only-pw" }, id);
    check("and back again", await canList(server.id), true);

    // ---- the connection's page shows which ones it uses
    await page.goto(BASE + "/admin/servers/" + server.id, { waitUntil: "networkidle" });
    check("the connection's page has them chosen", await page.evaluate(() => document.querySelector("[data-credential-select]").value), id);

    // ---- an id that is not the user's to use is not taken
    const status = await page.evaluate(async sid => {
      const form = document.querySelector("#server-edit-form");
      const data = new FormData(form);
      data.set("credentialId", "00000000-0000-0000-0000-00000000beef");
      return (await fetch(form.action, { method: "POST", body: data, credentials: "same-origin", redirect: "manual" })).type;
    }, server.id);
    await page.goto(BASE + "/admin/servers/" + server.id, { waitUntil: "networkidle" });
    check("credentials that do not exist (or are someone else's) are not taken", [status !== "error", await page.evaluate(() => document.querySelector("[data-credential-select]").value)], [true, ""]);
    // Put them back for the last step.
    await page.selectOption("[data-credential-select]", id);
    await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='server-edit-form']")]);
    await sleep(500);
    check("chosen again they sign in again", await canList(server.id), true);

    // ---- deleted: the connection has no login any more
    await page.goto(BASE + "/credentials/" + id, { waitUntil: "networkidle" });
    page.once("dialog", dialog => dialog.accept());
    await page.evaluate(() => { const form = document.querySelector("form[action$='/delete']"); form.removeAttribute("data-confirm"); form.submit(); });
    await sleep(1500);
    check("deleted, they are gone from the list", await credentialId(), null);
    check("and the connection that used them has no login", await canList(server.id), false);
  }
  finally {
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    if (server) {
      await page.evaluate(async sid => {
        const html = await (await fetch("/admin/servers/" + sid, { credentials: "same-origin" })).text();
        const doc = new DOMParser().parseFromString(html, "text/html");
        const form = doc.querySelector("form[action$='/delete']");
        if (form) { await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin" }); }
      }, server.id);
    }
  }

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
