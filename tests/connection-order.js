// Dragging connections (0.43.0), in the side panel and on the start page:
//   - between two others: the user's OWN order - it stays after a reload, and someone else still sees theirs
//   - onto another folder: moved there, for everyone - only by whoever may change the connection; anyone else is
//     told so, and nothing moves
// With the mouse (the whole row or card); a finger takes the grip, which is the same pointer handling.
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
  const b = await chromium.launch({ headless: true });
  const signIn = async (page, user, password) => {
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name='username']", user);
    await page.fill("input[name='password']", password);
    await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
    await sleep(800);
  };
  const admin = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  admin.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await signIn(admin, "admin", "test-only-pw");
  const FOLDER = "Sortierung";
  const TARGET = "Sortierung-Ziel";
  const EMPTY = "Sortierung-Leer";
  const NAMES = ["Sort-A", "Sort-B", "Sort-C"];
  const USER = "SortierTester";
  const PW = "test-only-pw-2468";

  const servers = page => page.evaluate(async () => JSON.parse((await (await fetch("/", { credentials: "same-origin" })).text()).match(/const availableServers = (\[.*?\]);/s)[1]));
  const make = async (name, folder) => {
    if ((await servers(admin)).some(s => s.name === name)) { return; }
    await admin.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(300);
    await admin.evaluate(([n, f]) => {
      document.querySelector("[data-protocol-option='Ssh']").click();
      const set = (field, value) => { const el = [...document.querySelectorAll("[name='" + field + "']")].find(e => !e.disabled); if (el) { el.value = value; el.dispatchEvent(new Event("input", { bubbles: true })); el.dispatchEvent(new Event("change", { bubbles: true })); } };
      set("name", n); set("host", "ssh"); set("port", "2222"); set("folderName", f);
      document.querySelector("[name='name']").form.requestSubmit();
    }, [name, folder]).catch(() => { });
    await sleep(1300);
  };
  const panelOrder = (page, folder) => page.evaluate(f => [...document.querySelectorAll("#server-manager-tree .server-manager-item[data-folder-path='" + f + "'] strong")].map(s => s.textContent.trim()), folder);
  const panelRow = (page, name) => page.locator("#server-manager-tree .server-manager-item[data-sortable='1']").filter({ hasText: name }).first();
  const drag = async (page, from, to, where) => {
    // In the middle of the view: the status bar lies over the bottom of the page.
    await from.evaluate(el => el.scrollIntoView({ block: "center" }));
    await sleep(200);
    const a = await from.boundingBox();
    const t = await to.boundingBox();
    await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2);
    await page.mouse.down();
    await page.mouse.move(a.x + a.width / 2, a.y + a.height / 2 + 10, { steps: 3 });
    const y = where === "above" ? t.y + 4 : where === "below" ? t.y + t.height - 4 : t.y + t.height / 2;
    const x = where === "left" ? t.x + 6 : where === "right" ? t.x + t.width - 6 : t.x + t.width / 2;
    await page.mouse.move(x, y, { steps: 8 });
    await page.mouse.up();
    await sleep(1500);
  };
  const openPanel = async page => {
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await page.evaluate(() => window.MatgateSidePanel && window.MatgateSidePanel.set(true));
    await sleep(600);
  };

  const member = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  member.on("pageerror", e => { console.log("JS ERROR (member): " + e.message); failures.push("JS error"); });
  try {
    for (const name of NAMES) { await make(name, FOLDER); }

    // ---- side panel: C above A, in the user's own order
    await openPanel(admin);
    check("the folder starts in the order of the names", await panelOrder(admin, FOLDER), NAMES);
    await drag(admin, panelRow(admin, "Sort-C"), panelRow(admin, "Sort-A"), "above");
    check("dragged above another, it stands there", await panelOrder(admin, FOLDER), ["Sort-C", "Sort-A", "Sort-B"]);
    check("no connection was opened by the drag", await admin.evaluate(() => document.querySelectorAll(".connection-tab, .shell-tab").length === 0 || !document.querySelector(".connection-panel:not(.hidden)")), true);
    await openPanel(admin);
    check("and stays there after a reload", await panelOrder(admin, FOLDER), ["Sort-C", "Sort-A", "Sort-B"]);

    // ---- start page: the cards in the same order; a card dragged behind another
    await admin.evaluate(() => window.MatgateSidePanel.set(false));
    await sleep(600);
    const cardOrder = () => admin.evaluate(f => [...document.querySelectorAll("[data-home2-all] .home2-card-grid .connection-choice[data-folder-path='" + f + "'] h3")].map(h => h.textContent.trim()), FOLDER);
    check("the start page shows the same order", await cardOrder(), ["Sort-C", "Sort-A", "Sort-B"]);
    const card = name => admin.locator("[data-home2-all] .home2-card-grid .connection-choice").filter({ has: admin.locator("h3", { hasText: new RegExp("^" + name + "$") }) }).first();
    await drag(admin, card("Sort-C"), card("Sort-B"), "right");
    await admin.goto(BASE + "/", { waitUntil: "networkidle" });
    await admin.evaluate(() => window.MatgateSidePanel.set(false));
    await sleep(500);
    check("a card dragged behind another moves there too", await cardOrder(), ["Sort-A", "Sort-B", "Sort-C"]);

    // ---- someone else: their own order, and no moving without the right to change
    await admin.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
    await admin.fill("input[name='username']", USER);
    await admin.fill("input[name='password']", PW);
    await admin.evaluate(() => document.querySelector("input[name='username']").form.requestSubmit());
    await sleep(1200);
    await admin.goto(BASE + "/admin/access?folder=" + FOLDER, { waitUntil: "networkidle" });
    const value = await admin.evaluate(u => [...document.querySelectorAll("[data-access-new-principal] option")].find(o => o.textContent.startsWith(u)).value, USER);
    await admin.selectOption("[data-access-new-principal]", value);
    await admin.selectOption("[data-access-new-level]", "Connect");
    await Promise.all([admin.waitForLoadState("networkidle"), admin.click("[data-access-add]")]);
    await signIn(member, USER, PW);
    await openPanel(member);
    check("someone else still sees the names' order", await panelOrder(member, FOLDER), NAMES);
    await drag(member, panelRow(member, "Sort-B"), panelRow(member, "Sort-A"), "above");
    check("and can order them for themselves", await panelOrder(member, FOLDER), ["Sort-B", "Sort-A", "Sort-C"]);
    // The folder's menu gives the own order up again: by name.
    await member.locator("#server-manager-tree .server-manager-folder[data-folder='" + FOLDER + "']").first().click({ button: "right" });
    await sleep(400);
    const sortItem = member.locator("[role='menuitem'], .context-menu button, .menu-item").filter({ hasText: /Nach Namen sortieren|Sort by name/ }).first();
    check("the folder's menu offers to sort by name again", await sortItem.count(), 1);
    if (await sortItem.count()) {
      await sortItem.click();
      await sleep(1200);
      check("and then they are by name again", await panelOrder(member, FOLDER), NAMES);
    }
    await openPanel(admin);
    check("which changes nothing for the administrator", await panelOrder(admin, FOLDER), ["Sort-A", "Sort-B", "Sort-C"]);

    // An empty folder for everyone, as a place to drag to.
    await admin.evaluate(async name => {
      const token = JSON.parse((await (await fetch("/", { credentials: "same-origin" })).text()).match(/const csrfToken = ("[^"]*")/)[1]);
      const body = new FormData(); body.append("name", name);
      await fetch("/api/folders", { method: "POST", body, credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
    }, EMPTY);
    await openPanel(member);
    const empty = member.locator("#server-manager-tree .server-manager-folder[data-folder='" + EMPTY + "']").first();
    check("the member sees the empty folder", await empty.count(), 1);
    await drag(member, panelRow(member, "Sort-A"), empty, "middle");
    check("without the right to change it, it is not moved into another folder", ((await servers(member)).find(s => s.name === "Sort-A") || {}).folder, FOLDER);
    check("and the panel says why", await member.evaluate(() => { const n = document.querySelector(".server-manager-note"); return !!n && !n.hidden && n.textContent.length > 10; }), true);

    // ---- moving, by the administrator: onto another folder's heading
    await make("Sort-Ziel-Platzhalter", TARGET);
    await openPanel(admin);
    await drag(admin, panelRow(admin, "Sort-B"), admin.locator("#server-manager-tree .server-manager-folder[data-folder='" + TARGET + "']").first(), "middle");
    check("dragged onto another folder, it moves there", ((await servers(admin)).find(s => s.name === "Sort-B") || {}).folder, TARGET);
    await drag(admin, panelRow(admin, "Sort-B"), panelRow(admin, "Sort-A"), "below");
    check("dragged next to a connection of another folder, it moves into that folder and lands there", [((await servers(admin)).find(s => s.name === "Sort-B") || {}).folder, await panelOrder(admin, FOLDER)], [FOLDER, ["Sort-A", "Sort-B", "Sort-C"]]);
  }
  finally {
    for (const name of [...NAMES, "Sort-Ziel-Platzhalter"]) {
      await admin.goto(BASE + "/admin?tab=servers", { waitUntil: "networkidle" });
      await admin.evaluate(n => { const row = [...document.querySelectorAll("tr")].find(r => r.innerText.includes(n)); const form = row && row.querySelector("form[action*='/delete']"); if (form) { form.removeAttribute("data-confirm"); form.submit(); } }, name);
      await sleep(700);
    }
    for (const name of [FOLDER, TARGET, EMPTY]) {
      await admin.goto(BASE + "/admin?tab=folders", { waitUntil: "networkidle" });
      await admin.evaluate(f => { const row = document.querySelector("[data-folder-row='" + f + "']"); const form = row && row.querySelector("form[action$='/delete']"); if (form) { form.removeAttribute("data-confirm"); form.submit(); } }, name);
      await sleep(600);
    }
    await admin.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
    await admin.evaluate(n => { const row = [...document.querySelectorAll("tr")].find(r => r.innerText.includes(n)); const form = row && row.querySelector("form[action*='/delete']"); if (form) { form.removeAttribute("data-confirm"); form.submit(); } }, USER);
    await sleep(700);
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
