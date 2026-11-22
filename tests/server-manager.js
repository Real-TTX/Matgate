// The side panel: what there is to open - the favourites, what was used last, the connections by folder, the
// places, the shares. On a wide screen it stands on the left from the start, and the start page beside it is
// quick connect instead of the same list a second time; put away, the page is the list again. One click
// connects, or goes to the session that is already open; the middle button and Ctrl open another one; the
// right button brings the connection's menu. Whether it is shown is remembered, and it is shown and hidden
// from the menu as well - in the compact view that is the only way. On a phone it is a sheet over the page.
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
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const shown = () => page.evaluate(() => { const panel = document.getElementById("server-manager"); return !panel.hidden && panel.offsetWidth > 200; });
  // The rows of a group: "folder" are the connections under their folders, the others are named after what they are.
  const rows = (group = "folder") => page.evaluate(g => [...document.querySelectorAll("#server-manager-tree [data-manager-in='" + g + "']")].map(row => row.querySelector("strong").textContent), group);
  const sessions = () => page.evaluate(() => document.querySelectorAll(".session-tab[data-tab-kind='connection'], .session-tab[data-tab-kind='page']").length);
  const row = name => page.locator("#server-manager-tree [data-manager-in='folder']").filter({ has: page.locator("strong", { hasText: new RegExp("^" + name + "$") }) });
  const mark = name => page.evaluate(n => {
    const found = [...document.querySelectorAll("#server-manager-tree [data-manager-in='folder']")].find(r => r.querySelector("strong").textContent === n);
    return { open: found.classList.contains("open"), active: found.classList.contains("active"), dot: !!found.querySelector(".server-manager-dot"), count: (found.querySelector(".server-manager-dot") || {}).textContent || "" };
  }, name);
  const startPage = () => page.evaluate(() => ({
    quick: !!(document.querySelector(".home2-quick-section") || {}).offsetParent,
    list: [...document.querySelectorAll("[data-home2-card]")].some(card => card.offsetParent),
    title: [...document.querySelectorAll(".home2-head h1 span")].filter(span => span.offsetParent).map(span => span.textContent)[0],
  }));
  const headings = () => page.evaluate(() => [...document.querySelectorAll("#server-manager-tree .server-manager-folder")].map(head => head.dataset.folder));

  // The panel has no button of its own any more: it is chosen in the view menu. Between the steps below it is put
  // away and back the way that menu does it.
  const togglePanel = async () => {
    await page.evaluate(() => window.MatgateChooseView(window.MatgateSidePanel.isOpen() ? "normal" : "sidebar"));
    await sleep(100);
  };

  // ---- there from the start, in place of the list on the start page
  check("on a wide screen it is there from the start - and the tab row has no button for it", [await shown(), await page.evaluate(() => !!document.getElementById("server-manager-toggle"))], [true, false]);
  const beside = await startPage();
  check("the start page beside it is quick connect, not the same list a second time", [beside.quick, beside.list, /New connection|Neue Verbindung/.test(beside.title)], [true, false, true]);
  const widthBeside = await page.evaluate(() => document.getElementById("shell-page-panels").offsetWidth);
  const tabRow = () => page.evaluate(() => {
    const row = document.querySelector(".shell-page-row").getBoundingClientRect();
    const tab = document.querySelector("#session-tabs .session-tab").getBoundingClientRect();
    return [Math.round(row.height), Math.round(tab.top - row.top)];
  });
  const rowBeside = await tabRow();
  await togglePanel();
  await sleep(300);
  check("the view \"Normal\" puts it away", await shown(), false);
  check("the tab row is as high with the panel as without, the tabs in the same place", rowBeside, await tabRow());
  const alone = await startPage();
  check("then the page has the room and is the list again", [await page.evaluate(() => document.getElementById("shell-page-panels").offsetWidth) > widthBeside + 200, alone.list, /Connections|Verbindungen/.test(alone.title)], [true, true, true]);
  await togglePanel();
  await sleep(300);
  check("and \"Normal with side panel\" brings it back", await shown(), true);

  const all = await rows();
  check("it lists the saved connections, by name", [all.includes("Linux-Testhost"), all.includes("Linux-Testhost 2"), JSON.stringify(all) === JSON.stringify([...all].sort((a, c) => a.localeCompare(c, undefined, { sensitivity: "base" })))], [true, true, true]);
  const places = await rows("places");
  check("the places of the file manager have a group of their own, after the connections", [places.length > 0, all.some(name => places.includes(name)), (await headings()).indexOf("places:") > (await headings()).indexOf("")], [true, false, true]);

  // ---- one click connects
  await row("Linux-Testhost").click();
  await sleep(3500);
  check("one click opens the connection", await sessions(), 1);
  check("its row says that a session is open, and that it is the one in front", await mark("Linux-Testhost"), { open: true, active: true, dot: true, count: "" });
  check("the panel stays where it is beside the session", await shown(), true);
  // back to the start page, and to the session by its row
  await page.click("#new-connection-tab .session-tab-main");
  await sleep(400);
  check("with the start page in front the row is no longer the active one", (await mark("Linux-Testhost")).active, false);
  await row("Linux-Testhost").click();
  await sleep(600);
  check("a click on a connection with a session goes to that session - no second one", [await sessions(), (await mark("Linux-Testhost")).active], [1, true]);
  await row("Linux-Testhost").click({ modifiers: ["Control"] });
  await sleep(3500);
  check("with Ctrl it opens another session", [await sessions(), (await mark("Linux-Testhost")).count], [2, "2"]);

  // ---- searching
  await page.fill("#server-manager-filter", "testhost 2");
  await sleep(300);
  check("the field narrows the list - every connection once, no favourites or last used beside it", [await rows(), await rows("favorites"), await rows("recent")], [["Linux-Testhost 2"], [], []]);
  await page.fill("#server-manager-filter", "nothing-like-this");
  await sleep(300);
  check("and says when nothing is left", [await rows(), await page.evaluate(() => !!document.querySelector("#server-manager-tree .server-manager-empty"))], [[], true]);
  await page.fill("#server-manager-filter", "");
  await sleep(300);

  // ---- the right button
  await row("Linux-Testhost 2").click({ button: "right" });
  await sleep(400);
  const menu = await page.evaluate(() => [...document.querySelectorAll(".toolbar-menu-panel:not([hidden]) button, .toolbar-menu-panel:not([hidden]) a")].filter(item => item.offsetParent).map(item => item.textContent.trim()));
  check("the right button brings the connection's menu", [menu.length >= 2, menu.some(item => /Connect|Verbinden/.test(item))], [true, true]);
  await page.keyboard.press("Escape");
  await sleep(300);

  // ---- remembered; and what was used last
  await page.reload({ waitUntil: "networkidle" });
  await sleep(1500);
  check("it is still shown after loading the page anew", await shown(), true);
  check("what was used last stands at the top, the newest first", [(await rows("recent"))[0], (await headings()).indexOf("recent:") < (await headings()).indexOf("")], ["Linux-Testhost", true]);
  await togglePanel();
  await sleep(300);
  await page.reload({ waitUntil: "networkidle" });
  await sleep(1500);
  check("and still hidden once it was closed", await shown(), false);

  for (let i = 0; i < 4; i++) {
    const more = await page.evaluate(() => { const button = document.querySelector("#connection-tab-actions [data-action='disconnect']"); if (button) { button.click(); return true; } return false; });
    if (!more) { break; }
    await sleep(900);
  }

  // ---- favourites: the star on a connection's card puts it into the group at the top
  const star = () => page.evaluate(() => {
    const card = [...document.querySelectorAll("[data-home2-all] [data-home2-card]")].find(c => c.querySelector("h3").textContent.trim() === "Linux-Testhost 2");
    card.querySelector(".favorite-toggle-form button").click();
  });
  const wasFavourite = await page.evaluate(() => [...document.querySelectorAll("[data-home2-all] [data-home2-card]")].find(c => c.querySelector("h3").textContent.trim() === "Linux-Testhost 2").dataset.fav === "1");
  if (!wasFavourite) { await star(); await sleep(900); }
  await togglePanel();
  await sleep(400);
  check("a favourite stands in the group of the favourites, which comes first", [(await rows("favorites")).includes("Linux-Testhost 2"), (await headings())[0]], [true, "favorites:"]);
  check("and under its folder as before", (await rows()).includes("Linux-Testhost 2"), true);
  await star();
  await sleep(900);
  check("without the star it is gone from there, without loading the page anew", (await rows("favorites")).includes("Linux-Testhost 2"), false);
  if (wasFavourite) { await star(); await sleep(900); }
  await togglePanel();
  await sleep(300);

  // ---- folders: a connection filed in one stands under it, and a folder can be shut
  const second = (await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  })).find(s => s.name === "Linux-Testhost 2");
  const file = async folder => {
    await page.goto(BASE + "/admin/servers/" + second.id, { waitUntil: "networkidle" });
    await sleep(400);
    if (folder) {
      await page.selectOption("[data-folder-select]", "__new__");
      await page.fill("[data-folder-new-input]", folder);
    }
    else {
      await page.selectOption("[data-folder-select]", "");
    }
    await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='server-edit-form']")]);
    await sleep(600);
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(1000);
  };
  // The folders and what stands under them - not the other groups.
  const tree = () => page.evaluate(() => [...document.querySelectorAll("#server-manager-tree > .server-manager-folder--folder, #server-manager-tree > [data-manager-in='folder']")].map(node =>
    node.matches(".server-manager-folder") ? "[" + node.querySelector(".server-manager-folder-name").textContent + " " + node.querySelector(".server-manager-count").textContent + "]"
      : node.querySelector("strong").textContent));
  const unfiled = all.length - 1;
  try {
    await file("Managerordner");
    await togglePanel();
    await sleep(400);
    const filed = await tree();
    check("a folder is a heading with its connections under it, and those without a folder come last",
      [filed[0], filed[1], filed.indexOf("[No folder " + unfiled + "]") === 2 || filed.indexOf("[Ohne Ordner " + unfiled + "]") === 2, filed.length], ["[Managerordner 1]", "Linux-Testhost 2", true, all.length + 2]);
    await page.click("#server-manager-tree .server-manager-folder[data-folder='Managerordner']");
    await sleep(300);
    check("a click on the folder shuts it", [(await tree()).includes("Linux-Testhost 2"), await page.evaluate(() => document.querySelector(".server-manager-folder[data-folder='Managerordner']").getAttribute("aria-expanded"))], [false, "false"]);
    await page.fill("#server-manager-filter", "testhost 2");
    await sleep(300);
    check("a search looks into shut folders as well", (await tree()).includes("Linux-Testhost 2"), true);
    await page.fill("#server-manager-filter", "");
    await page.click("#server-manager-tree .server-manager-folder[data-folder='Managerordner']");
    await sleep(300);
    check("and one more click opens it again", (await tree()).includes("Linux-Testhost 2"), true);
    await page.click("#server-manager-tree .server-manager-folder[data-folder='places:']");
    await sleep(300);
    check("the other groups can be shut the same way", [(await rows("places")).length, (await headings()).includes("places:")], [0, true]);
    await page.click("#server-manager-tree .server-manager-folder[data-folder='places:']");
    await togglePanel();
  }
  finally {
    await file("");
  }
  await togglePanel();
  await sleep(300);
  const plain = (await tree()).filter(entry => entry.startsWith("["));
  check("where nobody made a folder the connections stand under what they are", [plain.length, /^\[(Connections|Verbindungen) /.test(plain[0] || "")], [1, true]);

  // ---- no head of its own: switched in the view menu, things made with a right click on it
  check("beside the page the panel has no head - no title, no plus, no close button", await page.evaluate(() => {
    const head = document.querySelector("#server-manager .server-manager-head");
    return [!head || head.offsetParent === null, /Connections|Verbindungen/.test(document.getElementById("server-manager").innerText.split("\n")[0])];
  }), [true, false]);
  const panelMenu = () => page.evaluate(() => [...document.querySelectorAll(".toolbar-menu-panel[data-menu-dynamic]:not([hidden]) .toolbar-menu-item")].map(item => item.textContent.trim()));
  // An empty spot on the panel: below its last entry. A stack with many connections has none in sight, so
  // the list is narrowed to a few first.
  await page.fill("#server-manager-filter", "Linux-Testhost");
  await sleep(300);
  const emptySpot = await page.evaluate(() => {
    const tree = document.getElementById("server-manager-tree");
    const r = tree.getBoundingClientRect();
    const last = [...tree.children].filter(entry => entry.offsetParent).pop();
    const below = last ? last.getBoundingClientRect().bottom : r.top;
    return { x: r.left + r.width / 2, y: Math.round((below + r.bottom) / 2) };
  });
  await page.mouse.click(emptySpot.x, emptySpot.y, { button: "right" });
  await sleep(300);
  const offered = await panelMenu();
  check("a right click on the panel makes a new connection or a folder", [offered.includes("New connection"), offered.some(label => /^New folder/.test(label))], [true, true]);
  await page.keyboard.press("Escape");
  await sleep(200);
  await page.fill("#server-manager-filter", "");
  await sleep(300);

  // ---- the view: normal or compact, and apart from that the side panel, ticked or not
  const choices = async () => {
    await page.click("#view-mode-toggle");
    await sleep(400);
    return page.evaluate(() => [...document.querySelectorAll(".view-choice-panel:not([hidden]) .toolbar-menu-item, .view-choice-panel:not([hidden]) .toolbar-menu-separator")]
      .map(item => item.classList.contains("toolbar-menu-separator") ? "-" : (item.getAttribute("aria-checked") === "true" ? "*" : "") + item.textContent.trim()));
  };
  const choose = async label => {
    await page.locator(".view-choice-panel:not([hidden]) .toolbar-menu-item").filter({ hasText: new RegExp("^\\s*" + label + "\\s*$") }).click();
    await sleep(600);
  };
  const viewMode = () => page.evaluate(() => document.documentElement.dataset.viewMode);
  check("the view button offers two views and, apart, the side panel - what is on marked", await choices(), ["*Normal", "Compact", "-", "*Side panel"]);
  // It opens at the button, not in the page's top left corner.
  const placed = await page.evaluate(() => {
    const button = document.getElementById("view-mode-toggle").getBoundingClientRect();
    const panel = document.querySelector(".view-choice-panel:not([hidden])").getBoundingClientRect();
    return Math.abs(panel.top - button.bottom) < 16 && panel.right > button.left - 4 && panel.left < button.right + 4;
  });
  check("the choice opens just below the view button", placed, true);
  await page.click("#view-mode-toggle");
  await sleep(300);
  check("a second click on the button closes it", await page.locator(".view-choice-panel:not([hidden])").count(), 0);
  await choices();
  await choose("Compact");
  check("compact: one bar - and the side panel stays, it is switched on its own", [await viewMode(), await shown()], ["minimal", true]);
  await choices();
  await choose("Side panel");
  check("unticked, it goes", [await viewMode(), await shown()], ["minimal", false]);
  await page.click(".shell-burger-trigger");
  await sleep(300);
  check("the menu has no side panel entry on a wide screen - the view is where it is chosen", await page.locator("[data-server-manager-menu]:visible").count(), 0);
  await page.click(".shell-burger-trigger");
  await page.reload({ waitUntil: "networkidle" });
  await sleep(1200);
  check("compact is remembered - still without the panel", [await viewMode(), await shown()], ["minimal", false]);
  check("and marked as the view shown", await choices(), ["Normal", "*Compact", "-", "Side panel"]);
  await choose("Normal");
  check("normal: the bars as before, the panel still off", [await viewMode(), await shown()], ["normal", false]);
  await choices();
  await choose("Side panel");
  check("ticked: the panel is back", [await viewMode(), await shown()], ["normal", true]);
  await page.locator(".account-menu [data-menu-toggle]").click();
  await sleep(300);
  check("the account's menu has no entry for it either", await page.locator("[data-server-manager-menu]:visible").count(), 0);
  await page.keyboard.press("Escape");
  await sleep(200);
  check("Escape closes the account's menu", await page.locator(".account-menu [data-menu-toggle]").getAttribute("aria-expanded"), "false");

  // ---- a phone: a sheet over the page, brought by the menu
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, storageState: await context.storageState() });
  const small = await phone.newPage();
  small.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await small.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1000);
  const sheet = () => small.evaluate(() => [document.getElementById("server-manager").offsetWidth]);
  check("on a phone it is not there until asked for, though the wide screen has it shown", await sheet(), [0]);
  check("the start page there is the list, as ever", await small.evaluate(() => [...document.querySelectorAll("[data-home2-card]")].some(card => card.offsetParent)), true);
  await small.click(".shell-burger-trigger");
  await sleep(300);
  await small.locator("[data-server-manager-menu]:visible").click();
  await sleep(400);
  check("the menu's entry brings it as a sheet over the whole page", [(await sheet())[0], await small.evaluate(() => !!document.querySelector(".shell-burger-panel:not([hidden])"))], [390, false]);
  check("with rows a thumb can hit", await small.evaluate(() => [...document.querySelectorAll("#server-manager-tree .server-manager-item")].every(item => item.offsetHeight >= 44)), true);
  await small.locator("#server-manager [data-server-manager-close]").click();
  await sleep(300);
  check("its own button closes it", (await sheet())[0], 0);
  await small.click(".shell-burger-trigger");
  await small.locator("[data-server-manager-menu]:visible").click();
  await sleep(400);
  check("on the sheet only the way back is left of the head", await small.evaluate(() => [...document.querySelectorAll("#server-manager .server-manager-head > *")].map(e => e.innerText.trim()).join("|") + "|" + document.querySelectorAll("#server-manager .server-manager-head a").length), "|0");
  // A finger held still on it is its right click (iOS sends no contextmenu for that).
  await small.evaluate(() => {
    const tree = document.getElementById("server-manager-tree").getBoundingClientRect();
    const spot = document.elementFromPoint(tree.left + tree.width / 2, tree.bottom - 20);
    spot.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, pointerType: "touch", clientX: tree.left + tree.width / 2, clientY: tree.bottom - 20 }));
  });
  await sleep(800);
  const held = await small.evaluate(() => [...document.querySelectorAll(".toolbar-menu-panel[data-menu-dynamic]:not([hidden]) .toolbar-menu-item")].map(item => item.textContent.trim()));
  // (The sheet is full of rows - one held brings that row's menu, as a right click on it does.)
  check("a finger held on the panel brings its menu", held.length > 0, true);
  await small.evaluate(() => document.dispatchEvent(new PointerEvent("pointerup", { bubbles: true, pointerType: "touch" })));
  await small.evaluate(() => window.MatgateCloseMenus());
  await sleep(200);
  if (!(await sheet())[0]) {
    await small.click(".shell-burger-trigger");
    await small.locator("[data-server-manager-menu]:visible").click();
    await sleep(400);
  }
  await small.locator("#server-manager-tree [data-manager-in='folder']").filter({ has: small.locator("strong", { hasText: /^Linux-Testhost$/ }) }).click();
  await sleep(3500);
  check("a tap on a connection opens it, and the sheet goes", [await small.evaluate(() => document.querySelectorAll(".session-tab[data-tab-kind='connection'], .session-tab[data-tab-kind='page']").length), (await sheet())[0]], [1, 0]);
  await small.reload({ waitUntil: "networkidle" });
  await sleep(800);
  check("nothing of that is remembered: the wide screen still has its panel", await small.evaluate(() => window.localStorage.getItem("matgate.serverManager.open")), "true");

  // ---- the view on a phone: compact unless chosen otherwise, and only two to choose from
  await small.evaluate(() => window.localStorage.removeItem("matgate.view.mode.v1"));
  await small.reload({ waitUntil: "networkidle" });
  await sleep(1000);
  check("a phone starts in the compact view", await small.evaluate(() => document.documentElement.dataset.viewMode), "minimal");
  await small.click(".shell-burger-trigger");
  await sleep(300);
  await small.locator("[data-view-mode-toggle]:visible").click();
  await sleep(500);
  const phoneChoices = await small.evaluate(() => [...document.querySelectorAll(".view-choice-panel:not([hidden]) .toolbar-menu-item")].filter(item => item.offsetParent).map(item => (item.classList.contains("active") ? "*" : "") + item.textContent.trim()));
  check("the menu's entry offers two views - the side panel does not fit beside the page", phoneChoices, ["Normal", "*Compact"]);
  await small.locator(".view-choice-panel:not([hidden]) .toolbar-menu-item").filter({ hasText: /^\s*Normal\s*$/ }).click();
  await sleep(600);
  check("choosing normal gives the normal bars", [await small.evaluate(() => document.documentElement.dataset.viewMode), await small.evaluate(() => window.localStorage.getItem("matgate.view.mode.v1"))], ["normal", "normal"]);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
