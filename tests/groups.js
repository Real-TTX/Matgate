// For a company: an administrator makes a folder for everyone (there while still empty), files connections in
// it, and gives it to a group - whose members then have every connection in that folder and below, also the
// ones added later, and nothing else. Without the group they have nothing again.
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
  let b = await chromium.launch({ headless: true });
  const signIn = async (page, user, password) => {
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name=\"username\"]", user);
    await page.fill("input[name=\"password\"]", password);
    await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
    await sleep(1000);
  };
  const admin = await (await b.newContext({ viewport: { width: 1300, height: 900 } })).newPage();
  admin.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await signIn(admin, "admin", "test-only-pw");

  const USER = "GruppenTester";
  const PW = "test-only-pw-5678";
  const FOLDER = "Firma";
  const servers = page => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]).filter(s => !s.areaKind && (s.protocol || "").toUpperCase() !== "LOCAL");
  });
  const sideFolders = page => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/let sidebarData = (\{.*?\});\r?\n/s)[1]).folders;
  });
  const csrf = () => admin.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const csrfToken = ("[^"]*")/)[1]);
  });
  const fileInto = async (id, folder) => {
    const token = await csrf();
    await admin.evaluate(async ([i, f, t]) => { const body = new FormData(); body.append("folder", f); await fetch("/api/servers/" + i + "/folder", { method: "POST", body, credentials: "same-origin", headers: { "X-Matgate-Csrf": t } }); }, [id, folder, token]);
  };
  const second = (await servers(admin)).find(s => s.name === "Linux-Testhost 2");
  const first = (await servers(admin)).find(s => s.name === "Linux-Testhost");

  let groupId = null;
  const member = await (await b.newContext({ viewport: { width: 1300, height: 900 } })).newPage();
  try {
    // ---- a user without anything
    await admin.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
    await admin.fill("input[name=\"username\"]", USER);
    await admin.fill("input[name=\"password\"]", PW);
    await admin.evaluate(() => document.querySelector("input[name='username']").form.requestSubmit());
    await sleep(1500);

    // ---- an empty folder for everyone
    await admin.goto(BASE + "/admin/folders/new", { waitUntil: "networkidle" });
    await admin.fill("input[name='name']", FOLDER);
    await Promise.all([admin.waitForLoadState("networkidle"), admin.evaluate(() => document.querySelector("input[name='name']").form.requestSubmit())]);
    await sleep(800);
    check("an administrator makes a folder without any connection in it", await folderInTree(admin, FOLDER), true);
    check("it is in the side panel while still empty", (await sideFolders(admin)).includes(FOLDER), true);

    await signIn(member, USER, PW);
    check("the new user has no connection", (await servers(member)).length, 0);
    check("but sees the folder made for everyone", (await sideFolders(member)).includes(FOLDER), true);

    // ---- a group with the folder
    await fileInto(second.id, FOLDER + "/Technik");
    await admin.goto(BASE + "/admin/groups/new", { waitUntil: "networkidle" });
    await admin.fill("input[name='name']", "Technik-Team");
    await admin.check("input[name='member'][value='" + (await admin.evaluate(async u => {
      const html = await (await fetch("/admin/groups/new", { credentials: "same-origin" })).text();
      const doc = new DOMParser().parseFromString(html, "text/html");
      const box = [...doc.querySelectorAll("input[name='member']")].find(i => i.closest("label").textContent.includes(u));
      return box ? box.value : "";
    }, USER)) + "']");
    await admin.selectOption("[data-group-folder='" + FOLDER + "'] select", "Connect");
    await Promise.all([admin.waitForLoadState("networkidle"), admin.click("button[form='group-form']")]);
    await sleep(800);
    await admin.goto(BASE + "/admin?tab=groups", { waitUntil: "networkidle" });
    groupId = await admin.evaluate(() => { const link = [...document.querySelectorAll("a[href^='/admin/groups/']")].find(a => /Technik-Team/.test(a.textContent)); return link ? link.getAttribute("href").split("/").pop() : null; });
    check("the group is in the administration's list", !!groupId, true);

    const names = async () => (await servers(member)).map(s => s.name).sort();
    check("a member has what lies in the group's folder - also in a subfolder - and nothing else", await names(), ["Linux-Testhost 2"]);
    check("and can open it", await member.evaluate(async id => (await fetch("/connect/" + id, { credentials: "same-origin", redirect: "manual" })).status !== 404, second.id), true);
    await fileInto(first.id, FOLDER);
    check("a connection filed there later is the member's at once", await names(), ["Linux-Testhost", "Linux-Testhost 2"]);

    // ---- without the group: nothing again
    await admin.goto(BASE + "/admin/groups/" + groupId, { waitUntil: "networkidle" });
    await admin.evaluate(() => { const form = document.querySelector("form[action$='/delete']"); form.removeAttribute("data-confirm"); form.submit(); });
    await sleep(1200);
    groupId = null;
    check("with the group deleted the member has nothing again", await names(), []);
  }
  finally {
    await fileInto(second.id, "");
    await fileInto(first.id, "");
    if (groupId) {
      await admin.goto(BASE + "/admin/groups/" + groupId, { waitUntil: "networkidle" });
      await admin.evaluate(() => { const form = document.querySelector("form[action$='/delete']"); form.removeAttribute("data-confirm"); form.submit(); });
      await sleep(800);
    }
    // The folder and the user.
    await dissolveFolders(admin, [FOLDER]);
    await sleep(800);
    await admin.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
    await admin.evaluate(n => {
      const row = Array.from(document.querySelectorAll("tr")).find(r => r.innerText.includes(n));
      const form = row && row.querySelector("form[action*='/delete']");
      if (form) { form.removeAttribute("data-confirm"); form.submit(); }
    }, USER);
    await sleep(800);
  }

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
