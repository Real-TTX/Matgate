// The file manager on a phone, as an app installed to the home screen. On an iPhone the toolbar's
// two menus did nothing (they were <details>), the place and the path got 20 pixels each, and a file
// row was 180 pixels tall because four icon buttons wrapped inside a 68 pixel cell. Chromium cannot
// be an iPhone, but it shows what the LAYOUT does at 390 pixels and whether a tap reaches the thing.
//
// "Installed" is faked the way the application itself detects it: display-mode: standalone and
// navigator.standalone. Touch is real (hasTouch/isMobile) and so are the taps.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const PHONE = { width: 390, height: 844 };
const FOLDER = "Telefon";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({
    viewport: PHONE, deviceScaleFactor: 2, hasTouch: true, isMobile: true,
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
  });
  await ctx.addInitScript(() => {
    // A real MediaQueryList refuses to have "matches" assigned, so the stand-in is a plain object.
    const original = window.matchMedia.bind(window);
    window.matchMedia = query => {
      if (!/display-mode/.test(query)) { return original(query); }
      return {
        matches: /standalone/.test(query), media: query, onchange: null,
        addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {},
        dispatchEvent() { return false; },
      };
    };
    Object.defineProperty(navigator, "standalone", { value: true });
  });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const prompts = [];
  page.on("dialog", async d => { prompts.push(d.type()); await d.dismiss(); });

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);

  const where = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const token = (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
    const hit = list.find(s => (s.areaKind || "") === "user");
    return { id: hit ? hit.id : null, token };
  });
  const api = (path, body) => page.evaluate(async ([id, token, route, payload]) => {
    const response = await fetch("/api/files/" + id + route, {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token }, body: JSON.stringify(payload),
    });
    return response.status;
  }, [where.id, where.token, path, body]);
  const seed = async () => {
    await api("/delete", { paths: ["/" + FOLDER] });
    await api("/mkdir", { path: "/", name: FOLDER });
    await api("/mkdir", { path: "/" + FOLDER, name: "Unterordner" });
    await page.evaluate(async ([id, token, folder]) => {
      for (const name of ["eins.txt", "zwei.txt", "drei.txt"]) {
        const data = new FormData();
        data.append("path", "/" + folder);
        data.append("file", new Blob(["content of " + name], { type: "text/plain" }), name);
        await fetch("/api/files/" + id + "/upload", { method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
      }
    }, [where.id, where.token, FOLDER]);
  };
  await seed();

  await page.goto(BASE + "/connect/" + where.id, { waitUntil: "networkidle" });
  await sleep(3000);
  // Into the folder with a real tap on its name.
  await page.locator(".file-table tr").filter({ hasText: FOLDER }).first().locator(".file-name-button").tap();
  await page.waitForFunction(() => /eins.txt/.test((document.querySelector(".file-table tbody") || {}).innerText || ""), null, { timeout: 15000 });
  await sleep(500);

  // --- The toolbar: everything on the screen, in two rows, each thing big enough to hit.
  const bar = await page.evaluate(() => {
    const toolbar = document.querySelector(".file-toolbar");
    const box = toolbar.getBoundingClientRect();
    const find = selector => {
      const el = toolbar.querySelector(selector);
      if (!el) { return null; }
      const r = el.getBoundingClientRect();
      return { left: Math.round(r.left), right: Math.round(r.right), top: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height) };
    };
    return {
      height: Math.round(box.height),
      place: find("[data-file-place-select]"),
      path: find(".file-path-input"),
      up: find("[data-file-action='up']"),
      refresh: find("[data-file-action='refresh']"),
      create: find(".file-create-menu > .toolbar-menu-trigger"),
      actions: find(".file-actions-menu > .toolbar-menu-trigger"),
      queue: find("[data-file-action='toggle-upload-queue']"),
      upload: find(".file-upload-button"),
      share: find("[data-share-place]"),
    };
  });
  console.log("     toolbar: " + JSON.stringify(bar));
  check("the toolbar is two rows, not one cramped and not six", bar.height >= 80 && bar.height <= 120, true);
  check("the place field shows a name, not 20 pixels", bar.place !== null && bar.place.width >= 110, true);
  check("the path field can be read and typed in", bar.path !== null && bar.path.width >= 150, true);
  const small = ["up", "refresh", "create", "actions", "queue", "upload"]
    .filter(key => !bar[key] || bar[key].width < 40 || bar[key].height < 40);
  check("every button is at least 40 pixels", small, []);
  const outside = Object.keys(bar).filter(key => key !== "height" && bar[key] && (bar[key].left < 0 || bar[key].right > PHONE.width));
  check("nothing sticks out of the screen", outside, []);
  check("the place and what it offers are above where you are", bar.place.top < bar.path.top && bar.create.top < bar.up.top, true);
  check("the path and the way up share a row", Math.abs(bar.path.top - bar.up.top) <= 4, true);

  // No button without a face: an icon or a word. The rename button had neither.
  const faceless = await page.evaluate(() => Array.from(document.querySelectorAll(".file-toolbar button, .file-table tbody button, .file-upload-button"))
    .filter(el => !el.querySelector("svg") && !(el.textContent || "").trim())
    .map(el => el.className));
  check("no button is blank", faceless, []);
  await page.screenshot({ path: "phone-files-manager.png" });

  // --- The menus are sheets, and they open with a tap.
  const sheet = () => page.evaluate(() => {
    const panel = document.querySelector(".menu-sheet");
    if (!panel) { return null; }
    const r = panel.getBoundingClientRect();
    const head = panel.querySelector(".menu-sheet-head");
    const items = Array.from(panel.querySelectorAll(".toolbar-menu-item"));
    const first = items[0] ? items[0].getBoundingClientRect() : null;
    const hit = first ? document.elementFromPoint(Math.round(first.left + first.width / 2), Math.round(first.top + first.height / 2)) : null;
    const header = document.querySelector("body > header");
    return {
      box: Math.round(r.left) + "," + Math.round(r.top) + " " + Math.round(r.width) + "x" + Math.round(r.height),
      title: head ? (head.querySelector("strong") || {}).textContent : null,
      closeButton: !!(head && head.querySelector(".menu-sheet-close")),
      items: items.map(el => ({ text: (el.textContent || "").trim(), icon: !!el.querySelector("svg"), disabled: el.disabled })),
      firstItemHittable: !!(hit && panel.contains(hit)),
      appBarHidden: header ? getComputedStyle(header).display === "none" : true,
    };
  });
  await page.locator(".file-create-menu > .toolbar-menu-trigger").tap();
  await sleep(500);
  const create = await sheet();
  console.log("     create sheet: " + JSON.stringify(create));
  check("the create menu opens on a tap", create !== null, true);
  check("and covers the whole screen", create && create.box, "0,0 " + PHONE.width + "x" + PHONE.height);
  check("it says what it is and brings its own way back", create && create.title && create.closeButton, true);
  check("it offers a folder and a file", create && create.items.length, 2);
  check("every entry has an icon", create && create.items.every(item => item.icon), true);
  check("the first entry can be hit", create && create.firstItemHittable, true);
  check("the app bar steps aside", create && create.appBarHidden, true);
  await page.screenshot({ path: "phone-files-menu-create.png" });
  await page.locator(".menu-sheet .menu-sheet-close").tap();
  await sleep(400);
  check("the cross closes it", await sheet(), null);
  const homeAgain = await page.evaluate(() => {
    const panel = document.querySelector(".file-create-menu > .toolbar-menu-panel");
    return { backHome: !!panel, hidden: panel ? panel.hidden : null };
  });
  check("the panel goes back where it came from, closed", homeAgain, { backHome: true, hidden: true });

  // A choice closes the sheet and opens the next question - on top, not underneath.
  await page.locator(".file-create-menu > .toolbar-menu-trigger").tap();
  await sleep(400);
  await page.locator(".menu-sheet .toolbar-menu-item").first().tap();
  await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 8000 });
  check("choosing 'folder' asks for its name", await sheet(), null);
  const asking = await page.evaluate(() => {
    const dialog = document.getElementById("name-dialog");
    const r = dialog.getBoundingClientRect();
    return Math.round(r.width) + "x" + Math.round(r.height);
  });
  check("and the question fills the screen", asking, PHONE.width + "x" + PHONE.height);
  await page.locator("#name-dialog [data-name-close]").tap();
  await sleep(500);
  check("closing the question leaves the list as it was", await page.locator(".file-table tbody tr").count() >= 4, true);

  // --- The actions menu: nothing to act on, then two files selected with real taps.
  await page.locator(".file-actions-menu > .toolbar-menu-trigger").tap();
  await sleep(400);
  const idle = await sheet();
  console.log("     actions sheet, nothing selected: " + JSON.stringify(idle && idle.items));
  check("the actions menu opens too", idle !== null, true);
  check("with nothing selected every entry is off", idle && idle.items.length > 0 && idle.items.every(item => item.disabled), true);
  await page.keyboard.press("Escape");
  await sleep(300);
  check("Escape closes it", await sheet(), null);

  const boxes = page.locator(".file-table tbody .file-select-entry");
  const boxSize = await boxes.nth(1).evaluate(el => { const r = el.getBoundingClientRect(); return Math.round(r.width) + "x" + Math.round(r.height); });
  check("a checkbox is big enough for a finger", boxSize, "22x22");
  await boxes.nth(1).tap();
  await boxes.nth(2).tap();
  await sleep(300);
  const count = await page.locator(".file-actions-menu > .toolbar-menu-trigger").getAttribute("data-count");
  check("the actions button says how many are selected", count, "2");
  await page.locator(".file-actions-menu > .toolbar-menu-trigger").tap();
  await sleep(400);
  const busy = await sheet();
  check("with a selection the entries are on", busy && busy.items.every(item => !item.disabled), true);
  check("each of them has an icon", busy && busy.items.every(item => item.icon), true);
  await page.screenshot({ path: "phone-files-menu-actions.png" });
  // The delete entry asks first, in a sheet of its own - and "no" leaves everything where it is.
  await page.locator(".menu-sheet .toolbar-menu-item.danger").tap();
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 8000 });
  check("deleting a selection asks first", await sheet(), null);
  await page.locator("#confirm-dialog [data-confirm-cancel]").tap();
  await sleep(500);
  const stillThere = await page.evaluate(() => Array.from(document.querySelectorAll(".file-table tbody tr")).map(r => r.innerText).join(" "));
  check("saying no deletes nothing", ["eins.txt", "zwei.txt", "drei.txt"].every(name => stillThere.includes(name)), true);
  // Selection off again for the rows below.
  await page.locator(".file-select-all").tap();
  await sleep(200);
  await page.locator(".file-select-all").tap();
  await sleep(300);
  const cleared = await page.locator(".file-actions-menu > .toolbar-menu-trigger").getAttribute("data-count");
  check("clearing the selection takes the number away", cleared, null);

  // --- A row: the name, its size under it, one button at the end.
  const rowFacts = await page.evaluate(() => {
    const row = Array.from(document.querySelectorAll(".file-table tbody tr")).find(r => r.innerText.includes("eins.txt"));
    const shown = Array.from(row.querySelectorAll("button")).filter(el => el.getBoundingClientRect().width > 0);
    const meta = row.querySelector(".file-row-meta");
    return {
      height: Math.round(row.getBoundingClientRect().height),
      visibleButtons: shown.map(el => el.className.split(" ")[0]),
      meta: meta ? meta.innerText.trim() : null,
      metaShown: meta ? getComputedStyle(meta).display !== "none" : false,
      hiddenActions: row.querySelectorAll(".file-action-button").length,
    };
  });
  console.log("     row: " + JSON.stringify(rowFacts));
  check("a file row is a row, not a column of icons", rowFacts.height <= 70, true);
  check("the only button besides the name is the one for more", rowFacts.visibleButtons, ["file-name-button", "file-row-more"]);
  check("the size stands under the name", rowFacts.metaShown && /B/.test(rowFacts.meta || ""), true);
  check("the actions are still in the row for the sheet to use", rowFacts.hiddenActions >= 3, true);

  const rowMenu = async fileName => {
    const row = page.locator(".file-table tbody tr").filter({ hasText: fileName }).first();
    await row.locator(".file-row-more").tap();
    await sleep(400);
    return sheet();
  };
  const more = await rowMenu("eins.txt");
  console.log("     row sheet: " + JSON.stringify(more));
  check("the row's sheet carries the file's name", more && more.title, "eins.txt");
  check("it covers the screen", more && more.box, "0,0 " + PHONE.width + "x" + PHONE.height);
  check("it offers view, rename, download and delete", more && more.items.length, 4);
  check("each with an icon", more && more.items.every(item => item.icon), true);
  await page.screenshot({ path: "phone-files-menu-row.png" });

  // Rename through the sheet: the name dialog arrives with the current name, and the list follows.
  const renameItem = page.locator(".menu-sheet .toolbar-menu-item").nth(1);
  await renameItem.tap();
  await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 8000 });
  const offered = await page.evaluate(() => document.getElementById("name-dialog-input").value);
  check("renaming starts from the current name", offered, "eins.txt");
  await page.fill("#name-dialog-input", "uno.txt");
  await page.locator("#name-dialog [data-name-confirm]").tap();
  await page.waitForFunction(() => /uno.txt/.test((document.querySelector(".file-table tbody") || {}).innerText || ""), null, { timeout: 15000 });
  const afterRename = await page.evaluate(() => (document.querySelector(".file-table tbody") || {}).innerText || "");
  check("the list shows the new name and not the old one", afterRename.includes("uno.txt") && !afterRename.includes("eins.txt"), true);

  // Delete through the sheet: asks, and on yes the file is gone.
  const second = await rowMenu("zwei.txt");
  check("every row has its own sheet", second && second.title, "zwei.txt");
  await page.locator(".menu-sheet .toolbar-menu-item.danger").tap();
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 8000 });
  await page.locator("#confirm-dialog [data-confirm-ok]").tap();
  await page.waitForFunction(() => !/zwei.txt/.test((document.querySelector(".file-table tbody") || {}).innerText || ""), null, { timeout: 15000 });
  check("confirmed, the file is gone", true, true);
  check("nothing fell back to a browser dialog", prompts, []);

  // --- Clean up
  await api("/delete", { paths: ["/" + FOLDER] });
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
