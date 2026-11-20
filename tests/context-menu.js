// Right click. The things one works with have a menu at the pointer: a connection's card, a tab, a
// quick-connect chip, a row in the file list. A card's and a row's menu are read off the buttons they have,
// so they say and do the same. The menu is Matgate's own (the one the file manager has, opened at the
// pointer); the browser's stays where text is typed, and with Shift held. On a phone it is a sheet.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const FOLDER = "Rechtsklick";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const context = await b.newContext({ viewport: { width: 1300, height: 900 } });
  await require("./list-on-start-page")(context);
  await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: BASE });
  const page = await context.newPage();
  const dialogs = [];
  page.on("dialog", async d => { dialogs.push(d.type()); await d.dismiss(); });
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  // The menu that is open: its entries ("-" is a line between them), or null.
  const menu = target => target.evaluate(() => {
    const panel = document.querySelector(".context-menu-home .toolbar-menu-panel, .toolbar-menu-panel.menu-sheet[data-menu-dynamic]");
    return panel && !panel.hidden
      ? [...panel.children].filter(entry => !entry.classList.contains("menu-sheet-head")).map(entry => entry.classList.contains("toolbar-menu-separator") ? "-" : entry.textContent.trim() + (entry.disabled ? " (off)" : ""))
      : null;
  });
  const choose = (target, label) => target.evaluate(wanted => {
    [...document.querySelectorAll(".toolbar-menu-panel[data-menu-dynamic] .toolbar-menu-item")].find(entry => entry.textContent.trim() === wanted).click();
  }, label);
  const card = name => page.locator("[data-home2-card]", { has: page.locator("h3", { hasText: new RegExp("^" + name + "$") }) }).first();
  const favourite = name => page.evaluate(wanted => {
    const found = [...document.querySelectorAll("[data-home2-all] [data-home2-card]")].find(entry => entry.querySelector("h3").textContent === wanted);
    return found ? found.dataset.fav : null;
  }, name);
  const rightClick = async (locator, position = { x: 60, y: 30 }, options = {}) => {
    await locator.click({ button: "right", position, ...options });
    await sleep(250);
  };

  // ================================================================================== a connection's card
  // An earlier run that died may have left the star on.
  if (await favourite("Linux-Testhost") === "1") {
    await rightClick(card("Linux-Testhost"));
    await choose(page, "Remove from favorites");
    await sleep(1500);
  }

  const box = await card("Linux-Testhost").boundingBox();
  await rightClick(card("Linux-Testhost"));
  check("a right click on a card opens its menu: what the card's buttons do, and two things more", await menu(page),
    ["Connect", "Open in a new browser tab", "-", "Add to favorites", "Settings", "-", "Copy address"]);
  check("at the pointer, and on one line each", await page.evaluate(([x, y]) => {
    const panel = document.querySelector(".context-menu-home .toolbar-menu-panel");
    const rect = panel.getBoundingClientRect();
    const heights = [...panel.querySelectorAll(".toolbar-menu-item")].map(entry => Math.round(entry.getBoundingClientRect().height));
    return [Math.abs(rect.left - x) <= 4, Math.abs(rect.top - y) <= 4, new Set(heights).size];
  }, [box.x + 60, box.y + 30]), [true, true, 1]);
  await page.keyboard.press("Escape");
  await sleep(200);
  check("Escape closes it, and nothing of it stays behind", [await menu(page), await page.evaluate(() => document.querySelectorAll(".context-menu-home").length)], [null, 0]);

  await rightClick(card("Linux-Testhost"));
  // Beside it: on the heading of the page, where nothing reacts to a click. (A fixed point further down lay
  // on a card's Connect button as soon as the stack had a few more connections.)
  const beside = await page.evaluate(() => {
    const rect = [...document.querySelectorAll("h1")].find(heading => heading.offsetParent).getBoundingClientRect();
    return { x: rect.left + 20, y: rect.top + rect.height / 2 };
  });
  await page.mouse.click(beside.x, beside.y);
  await sleep(200);
  check("a click beside it closes it", await menu(page), null);
  await rightClick(card("Linux-Testhost"));
  await page.mouse.click(box.x + 20, box.y + 12);
  await sleep(200);
  check("so does a click on the card itself - but not one in the menu", await menu(page), null);

  await rightClick(card("Linux-Testhost"));
  await choose(page, "Add to favorites");
  for (let i = 0; i < 30 && await favourite("Linux-Testhost") !== "1"; i++) { await sleep(200); }
  check("chosen: the star is set, as by its button", await favourite("Linux-Testhost"), "1");
  await rightClick(card("Linux-Testhost"));
  check("and the menu says what the star does now", (await menu(page))[3], "Remove from favorites");
  await choose(page, "Remove from favorites");
  for (let i = 0; i < 30 && await favourite("Linux-Testhost") !== "0"; i++) { await sleep(200); }
  check("taken back again", await favourite("Linux-Testhost"), "0");

  await rightClick(card("Linux-Testhost"));
  await choose(page, "Copy address");
  await sleep(300);
  check("the address goes onto the clipboard", await page.evaluate(() => navigator.clipboard.readText()), "ssh:2222");

  await rightClick(card("Linux-Testhost"));
  const [own] = await Promise.all([context.waitForEvent("page", { timeout: 15000 }), choose(page, "Open in a new browser tab")]);
  check("a tab of the browser's for this connection alone", /\/\?open=[0-9a-f-]{36}$/.test(own.url()), true);
  await own.close();

  // ================================================================================== where it is not
  await rightClick(page.locator("[data-home2-search]"), { x: 80, y: 12 });
  check("in a field the browser's own menu stays", await menu(page), null);
  await rightClick(card("Linux-Testhost"), { x: 60, y: 30 }, { modifiers: ["Shift"] });
  check("and with Shift held, everywhere", await menu(page), null);
  await page.keyboard.press("Escape");

  // ================================================================================== a quick-connect chip
  await rightClick(page.locator(".home2-quick-row [data-home2-qc='ssh']"), { x: 20, y: 20 });
  check("a chip: connect with it, or arrange the chips", await menu(page), ["SSH", "-", "Change order and selection"]);
  await choose(page, "SSH");
  await page.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  check("the first opens quick connect on that protocol", await page.evaluate(() => document.querySelector("[data-home2-qc-proto].active").dataset.home2QcProto), "ssh");
  await page.click("[data-home2-proto-close]");
  await sleep(200);
  await rightClick(page.locator(".home2-quick-row [data-home2-qc='ssh']"), { x: 20, y: 20 });
  await choose(page, "Change order and selection");
  await sleep(1200);
  check("the second leads to where the chips are arranged", await page.evaluate(() => [...document.querySelectorAll(".shell-page-panel iframe")].map(frame => new URL(frame.src).pathname + new URL(frame.src).hash)), ["/account#quick-order-list"]);
  await page.click(".session-tab[data-tab-kind='page'] .session-tab-close");
  await sleep(300);

  // ================================================================================== tabs
  // Two tabs that are no sessions: the settings of two connections, opened from the cards' menus.
  for (const name of ["Linux-Testhost", "Linux-Testhost 2"]) {
    await page.click("#new-connection-tab");
    await sleep(300);
    await rightClick(card(name));
    await choose(page, "Settings");
    await sleep(1200);
  }
  const pageTabs = () => page.evaluate(() => [...document.querySelectorAll(".session-tab[data-tab-kind='page'] .session-tab-title")].map(title => title.textContent.trim()));
  check("\"Settings\" from the menu opens the connection's page as a tab", await pageTabs(), ["Edit connection", "Edit connection"]);
  const tab = index => page.locator(".session-tab[data-tab-kind='page']").nth(index);
  const second = await tab(1).getAttribute("data-tab-id");
  await rightClick(tab(1), { x: 30, y: 12 });
  check("a tab's menu", await menu(page), ["Reload", "-", "Close tab", "Close other tabs"]);
  await choose(page, "Close other tabs");
  await sleep(300);
  check("closing the others is asked about - by Matgate's own question", await page.evaluate(() => {
    const dialog = document.getElementById("confirm-dialog");
    return [!dialog.classList.contains("hidden"), dialog.querySelector("[data-confirm-title]").textContent, dialog.querySelector("[data-confirm-ok]").textContent.trim()];
  }), [true, "Close the other tabs?", "Close other tabs"]);
  await page.click("#confirm-dialog [data-confirm-cancel]");
  await sleep(200);
  check("\"no\" leaves them", (await pageTabs()).length, 2);
  await rightClick(tab(1), { x: 30, y: 12 });
  await choose(page, "Close other tabs");
  await sleep(300);
  await page.click("#confirm-dialog [data-confirm-ok]");
  await sleep(500);
  check("\"yes\" closes them - the one that was right-clicked stays", [(await pageTabs()).length, await tab(0).getAttribute("data-tab-id") === second], [1, true]);
  await rightClick(tab(0), { x: 30, y: 12 });
  check("alone, a tab has no others to close", (await menu(page))[3], "Close other tabs (off)");
  await choose(page, "Close tab");
  await sleep(500);
  check("and \"Close tab\" closes it", await pageTabs(), []);

  // ================================================================================== a row in the file list
  const where = await page.evaluate(async folder => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const token = (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
    const hit = list.find(s => (s.areaKind || "") === "user");
    await fetch("/api/files/" + hit.id + "/mkdir", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ path: "/", name: folder }),
    });
    const data = new FormData();
    data.append("path", "/" + folder);
    data.append("file", new Blob(["right click"], { type: "text/plain" }), "eins.txt");
    await fetch("/api/files/" + hit.id + "/upload", { method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
    return { id: hit.id, token };
  }, FOLDER);
  await page.goto(BASE + "/connect/" + where.id + "?path=" + encodeURIComponent("/" + FOLDER), { waitUntil: "networkidle" });
  await sleep(2500);
  let row = page.locator(".file-table tbody tr", { has: page.locator(".file-name-button", { hasText: "eins.txt" }) }).first();
  if (await row.count() === 0) {
    // The folder is not opened by the address: go into it.
    await page.locator(".file-table tbody tr .file-name-button", { hasText: FOLDER }).first().dblclick();
    await sleep(1500);
    row = page.locator(".file-table tbody tr", { has: page.locator(".file-name-button", { hasText: "eins.txt" }) }).first();
  }
  const offered = await row.evaluate(element => [...element.querySelectorAll(".file-row-actions .file-action-button")].map(button => button.title));
  await rightClick(row, { x: 160, y: 12 });
  check("a file's row: the actions of the row, as a menu", [await menu(page), offered.length > 2], [offered, true]);
  await page.keyboard.press("Escape");
  await page.evaluate(async ([id, token, folder]) => {
    await fetch("/api/files/" + id + "/delete", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ paths: ["/" + folder] }),
    });
  }, [where.id, where.token, FOLDER]);

  // ================================================================================== a phone: a sheet
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, hasTouch: true, isMobile: true, storageState: await context.storageState() });
  const small = await phone.newPage();
  small.on("pageerror", e => { console.log("JS ERROR (phone): " + e.message); failures.push("JS error"); });
  await small.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(800);
  // What a held finger sends where the browser makes a context menu of it (Android does; an iPhone does not).
  await small.evaluate(() => {
    const target = [...document.querySelectorAll("[data-home2-card]")].find(entry => entry.querySelector("h3").textContent === "Linux-Testhost");
    const rect = target.getBoundingClientRect();
    target.querySelector("h3").dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: rect.left + 40, clientY: rect.top + 30 }));
  });
  await sleep(300);
  check("on a phone the same menu is a sheet with the card's name on it", [
    await menu(small),
    await small.evaluate(() => { const head = document.querySelector(".toolbar-menu-panel.menu-sheet .menu-sheet-head strong"); return head ? head.textContent : null; }),
  ], [["Connect", "Open in a new browser tab", "-", "Add to favorites", "Settings", "-", "Copy address"], "Linux-Testhost"]);
  await small.click(".menu-sheet-close");
  await sleep(200);
  check("closed by its cross", await menu(small), null);
  await phone.close();

  check("no dialog of the browser was involved", dialogs, []);
  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
