// The dialog with the file manager in it - opened from a running session - on a phone, as an app
// installed to the home screen. It was a box 78% of the screen high and 32 pixels short of the right
// edge, and everything you opened from inside it (a question, a menu) counted as a press BESIDE it
// and closed it.
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
const FOLDER = "Dialogordner";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({
    viewport: PHONE, deviceScaleFactor: 2, hasTouch: true, isMobile: true,
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
  });
  await ctx.addInitScript(() => {
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

  // The action only exists with the permission to share a connection's files.
  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  const adminHref = await page.evaluate(() => {
    const link = Array.from(document.querySelectorAll("a[href^='/admin/users/']")).find(a => /admin/i.test(a.textContent || ""));
    return link ? link.getAttribute("href") : null;
  });
  if (adminHref) {
    await page.goto(BASE + adminHref, { waitUntil: "networkidle" });
    await page.evaluate(() => {
      const box = document.querySelector("[name='fileShareConnection']");
      if (box && !box.checked) { box.checked = true; }
      box.form.requestSubmit();
    });
    await sleep(1800);
  }

  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1500);
  const connect = page.locator("a,button").filter({ hasText: /^ *(Connect|Verbinden) *$/ }).first();
  check("there is a connection to open", await connect.count() > 0, true);
  await connect.tap();
  await sleep(6000);

  const openDialog = async () => {
    await page.locator("#connection-tab-actions .tab-action-more-trigger").tap();
    await sleep(600);
    await page.locator(".tab-action-overflow-panel button").filter({ hasText: /Files|Dateien/ }).first().tap();
    await sleep(2500);
  };
  // Deletes the test folder in whichever area the dialog shows - a run that died halfway leaves it behind.
  const removeFolder = async () => {
    const area = await page.evaluate(() => document.getElementById("file-area-dialog-select").value);
    const token = await page.evaluate(() => (document.documentElement.innerHTML.match(/const csrfToken = "([^"]+)"/) || [])[1] || "");
    await page.evaluate(async ([id, csrf, folder]) => {
      await fetch("/api/files/" + id + "/delete", {
        method: "POST", credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-Matgate-Csrf": csrf },
        body: JSON.stringify({ paths: ["/" + folder] }),
      });
    }, [area, token, FOLDER]);
  };
  const dialogBox = () => page.evaluate(() => {
    const d = document.getElementById("file-area-dialog");
    const r = d.getBoundingClientRect();
    return { hidden: d.classList.contains("hidden"), box: Math.round(r.left) + "," + Math.round(r.top) + " " + Math.round(r.width) + "x" + Math.round(r.height) };
  });

  await openDialog();
  await removeFolder();
  await page.locator("#file-area-dialog [data-file-action='refresh']").tap();
  await sleep(1000);
  const first = await dialogBox();
  console.log("     dialog: " + JSON.stringify(first));
  check("the dialog opens", first.hidden, false);
  check("and fills the whole screen", first.box, "0,0 " + PHONE.width + "x" + PHONE.height);

  const layout = await page.evaluate(() => {
    const d = document.getElementById("file-area-dialog");
    const rect = el => { const r = el.getBoundingClientRect(); return { left: Math.round(r.left), right: Math.round(r.right), top: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height) }; };
    const send = document.getElementById("file-area-dialog-send");
    const bar = d.querySelector(".file-toolbar");
    const controls = Array.from(bar.querySelectorAll("button, .file-upload-button, input.file-path-input"))
      .filter(el => el.getBoundingClientRect().width > 0);
    return {
      select: rect(document.getElementById("file-area-dialog-select")),
      close: rect(document.getElementById("file-area-dialog-close")),
      send: send && getComputedStyle(send).display !== "none" ? rect(send) : null,
      toolbarHeight: Math.round(bar.getBoundingClientRect().height),
      outside: controls.filter(el => { const r = el.getBoundingClientRect(); return r.left < 0 || r.right > window.innerWidth; }).length,
      tooSmall: controls.filter(el => !el.classList.contains("file-path-input") && (el.getBoundingClientRect().width < 40 || el.getBoundingClientRect().height < 40)).length,
      blank: controls.filter(el => !el.querySelector("svg") && !(el.value || el.textContent || "").trim()).length,
      border: getComputedStyle(d).borderTopWidth,
    };
  });
  console.log("     layout: " + JSON.stringify(layout));
  check("the place field has room for a name", layout.select.width >= 150, true);
  check("the way out is a finger-sized button", layout.close.width >= 40 && layout.close.height >= 40, true);
  check("sending into the session has a whole row", layout.send !== null && layout.send.width >= 300, true);
  check("the bar is two rows", layout.toolbarHeight >= 80 && layout.toolbarHeight <= 120, true);
  check("nothing in it sticks out of the screen", layout.outside, 0);
  check("every button in it is big enough", layout.tooSmall, 0);
  check("no button in it is blank", layout.blank, 0);
  check("a sheet that fills the screen has no border at its edge", layout.border, "0px");
  await page.screenshot({ path: "phone-filearea-dialog.png" });

  // --- A menu from inside the dialog opens ABOVE it, and the question after it as well.
  await page.locator("#file-area-dialog .file-create-menu > .toolbar-menu-trigger").tap();
  await sleep(500);
  const above = await page.evaluate(() => {
    const sheet = document.querySelector(".menu-sheet");
    if (!sheet) { return null; }
    const item = sheet.querySelector(".toolbar-menu-item").getBoundingClientRect();
    const hit = document.elementFromPoint(Math.round(item.left + item.width / 2), Math.round(item.top + item.height / 2));
    return { onTop: !!(hit && sheet.contains(hit)), dialogOpen: !document.getElementById("file-area-dialog").classList.contains("hidden") };
  });
  check("the menu sheet is above the dialog", above, { onTop: true, dialogOpen: true });
  // Escape (a keyboard on the iPad, say) closes what is on top and nothing else: the dialog's own
  // handler used to take the same key and close the dialog under the menu as well.
  await page.keyboard.press("Escape");
  await sleep(400);
  const afterEscape = await page.evaluate(() => ({
    sheets: document.querySelectorAll(".menu-sheet").length,
    dialogOpen: !document.getElementById("file-area-dialog").classList.contains("hidden"),
  }));
  check("Escape closes the menu and leaves the dialog under it open", afterEscape, { sheets: 0, dialogOpen: true });
  await page.locator("#file-area-dialog .file-create-menu > .toolbar-menu-trigger").tap();
  await sleep(500);
  await page.locator(".menu-sheet .toolbar-menu-item").first().tap();
  await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 8000 });
  await page.fill("#name-dialog-input", FOLDER);
  await page.locator("#name-dialog [data-name-confirm]").tap();
  await page.waitForFunction(name => (document.querySelector("#file-area-dialog .file-table tbody") || {}).innerText.includes(name), FOLDER, { timeout: 15000 }).catch(() => {});
  const afterCreate = await dialogBox();
  check("answering the question leaves the dialog open", afterCreate.hidden, false);
  const listed = await page.evaluate(name => (document.querySelector("#file-area-dialog .file-table tbody") || {}).innerText.includes(name), FOLDER);
  check("and the new folder is in the list", listed, true);

  // --- The row's sheet works in here too: a folder offers open, rename and delete.
  const row = page.locator("#file-area-dialog .file-table tbody tr").filter({ hasText: FOLDER }).first();
  await row.locator(".file-row-more").tap();
  await sleep(500);
  const rowSheet = await page.evaluate(() => {
    const sheet = document.querySelector(".menu-sheet");
    if (!sheet) { return null; }
    return {
      title: (sheet.querySelector(".menu-sheet-head strong") || {}).textContent,
      items: Array.from(sheet.querySelectorAll(".toolbar-menu-item")).map(el => (el.textContent || "").trim()),
    };
  });
  console.log("     row sheet: " + JSON.stringify(rowSheet));
  check("the folder's sheet carries its name", rowSheet && rowSheet.title, FOLDER);
  check("it offers open, rename and delete", rowSheet && rowSheet.items.length, 3);
  await page.locator(".menu-sheet .toolbar-menu-item.danger").tap();
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 8000 });
  await page.locator("#confirm-dialog [data-confirm-ok]").tap();
  await page.waitForFunction(name => !(document.querySelector("#file-area-dialog .file-table tbody") || {}).innerText.includes(name), FOLDER, { timeout: 15000 });
  const afterDelete = await dialogBox();
  check("confirming the deletion leaves the dialog open", afterDelete.hidden, false);

  // --- Ways out: the cross, and Escape.
  await page.locator("#file-area-dialog-close").tap();
  await sleep(500);
  check("the cross closes the dialog", (await dialogBox()).hidden, true);
  await openDialog();
  check("it opens again", (await dialogBox()).hidden, false);
  await page.keyboard.press("Escape");
  await sleep(500);
  check("Escape closes it", (await dialogBox()).hidden, true);
  check("nothing fell back to a browser dialog", prompts, []);

  // --- Clean up what a failed run may have left behind in the connection's area.
  await openDialog();
  await removeFolder();

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
