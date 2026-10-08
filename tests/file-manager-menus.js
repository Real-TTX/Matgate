// The two menus of the file manager on a wide screen. They used to be <details> elements; they are
// a button and a panel now, and the same code that makes them a sheet on a phone has to leave them
// a dropdown here: open under the button, one at a time, closed by a second tap, by a press beside
// them, by Escape and by a choice - and never off the edge of the screen.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const FOLDER = "Schreibtisch";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1400, height: 900 } });
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
  const api = (route, body) => page.evaluate(async ([id, token, path, payload]) => {
    const response = await fetch("/api/files/" + id + path, {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token }, body: JSON.stringify(payload),
    });
    return response.status;
  }, [where.id, where.token, route, body]);
  await api("/delete", { paths: ["/" + FOLDER] });
  await api("/mkdir", { path: "/", name: FOLDER });
  await page.evaluate(async ([id, token, folder]) => {
    for (const name of ["a.txt", "b.txt"]) {
      const data = new FormData();
      data.append("path", "/" + folder);
      data.append("file", new Blob(["content of " + name], { type: "text/plain" }), name);
      await fetch("/api/files/" + id + "/upload", { method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
    }
  }, [where.id, where.token, FOLDER]);

  await page.goto(BASE + "/connect/" + where.id, { waitUntil: "networkidle" });
  await sleep(3000);
  await page.locator(".file-table tr").filter({ hasText: FOLDER }).first().locator(".file-name-button").click();
  await page.waitForFunction(() => /a.txt/.test((document.querySelector(".file-table tbody") || {}).innerText || ""), null, { timeout: 15000 });
  await sleep(400);

  const create = ".file-create-menu > .toolbar-menu-trigger";
  const actions = ".file-actions-menu > .toolbar-menu-trigger";
  const state = selector => page.evaluate(sel => {
    const trigger = document.querySelector(sel);
    const wrapper = trigger.parentElement;
    const panel = wrapper.querySelector(":scope > .toolbar-menu-panel") || document.querySelector("body > .toolbar-menu-panel");
    const t = trigger.getBoundingClientRect();
    const p = panel.getBoundingClientRect();
    return {
      open: !panel.hidden,
      expanded: trigger.getAttribute("aria-expanded"),
      sheet: panel.classList.contains("menu-sheet"),
      inPlace: panel.parentElement === wrapper,
      position: getComputedStyle(panel).position,
      under: p.top >= t.bottom - 1,
      onScreen: p.left >= 0 && p.right <= window.innerWidth && p.bottom <= window.innerHeight,
    };
  }, selector);

  // --- Closed to begin with
  const closed = await state(create);
  check("both menus start closed", closed.open || (await state(actions)).open, false);
  check("the trigger says so", closed.expanded, "false");

  // --- Opens under its button, as a dropdown
  await page.click(create);
  await sleep(300);
  const opened = await state(create);
  console.log("     create, open: " + JSON.stringify(opened));
  check("a click opens the create menu", opened.open, true);
  check("as a dropdown and not as a sheet", [opened.sheet, opened.inPlace, opened.position], [false, true, "absolute"]);
  check("under its button", opened.under, true);
  check("on the screen", opened.onScreen, true);
  check("the trigger says it is open", opened.expanded, "true");

  // --- A second click closes it
  await page.click(create);
  await sleep(300);
  check("a second click closes it", (await state(create)).open, false);

  // --- A press beside it closes it
  await page.click(create);
  await sleep(300);
  await page.mouse.click(700, 700);
  await sleep(300);
  check("a press beside it closes it", (await state(create)).open, false);

  // --- Escape closes it
  await page.click(create);
  await sleep(300);
  await page.keyboard.press("Escape");
  await sleep(300);
  check("Escape closes it", (await state(create)).open, false);

  // --- One at a time
  await page.click(create);
  await sleep(300);
  await page.click(actions);
  await sleep(300);
  const both = [(await state(create)).open, (await state(actions)).open];
  check("opening the second closes the first", both, [false, true]);
  const idle = await page.evaluate(() => Array.from(document.querySelectorAll(".file-actions-menu .toolbar-menu-item")).map(el => el.disabled));
  check("with nothing selected the actions are off", idle.length > 0 && idle.every(Boolean), true);
  await page.keyboard.press("Escape");
  await sleep(200);

  // --- A choice closes the menu and asks the next question - the menu is not left open beside it
  await page.click(create);
  await sleep(300);
  await page.click(".file-create-menu .toolbar-menu-item");
  await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 8000 });
  check("a choice closes the menu", (await state(create)).open, false);
  await page.click("#name-dialog [data-name-close]");
  await sleep(400);

  // --- With something selected the actions are on, and the button says how many
  await page.locator(".file-table tbody .file-select-entry").first().check();
  await sleep(300);
  check("the actions button carries the number", await page.locator(actions).getAttribute("data-count"), "1");
  await page.click(actions);
  await sleep(300);
  const on = await page.evaluate(() => Array.from(document.querySelectorAll(".file-actions-menu .toolbar-menu-item")).map(el => el.disabled));
  check("then the actions are on", on.every(disabled => !disabled), true);
  await page.keyboard.press("Escape");
  await sleep(200);

  // --- The rows are what they always were on a wide screen
  const rows = await page.evaluate(() => {
    const row = Array.from(document.querySelectorAll(".file-table tbody tr")).find(r => r.innerText.includes("a.txt"));
    const shown = el => el.getBoundingClientRect().width > 0 && getComputedStyle(el).display !== "none";
    return {
      actions: Array.from(row.querySelectorAll(".file-action-button")).filter(shown).length,
      moreButton: shown(row.querySelector(".file-row-more")),
      metaLine: shown(row.querySelector(".file-row-meta")),
      sizeColumn: shown(row.children[2]),
      renameHasIcon: !!row.querySelector(".file-action-rename svg"),
    };
  });
  console.log("     row: " + JSON.stringify(rows));
  check("the row keeps its labelled buttons", rows.actions >= 4, true);
  check("the phone's one button stays away", rows.moreButton, false);
  check("so does the line under the name", rows.metaLine, false);
  check("the size column is there", rows.sizeColumn, true);
  check("and rename has an icon like the others", rows.renameHasIcon, true);

  // --- A narrower window: the panel must not leave the screen
  await page.setViewportSize({ width: 760, height: 900 });
  await sleep(600);
  // A tablet held upright: the row's buttons are icons, so the actions do not hide behind a sideways scroll.
  const tablet = await page.evaluate(() => {
    const wrap = document.querySelector(".file-table-wrap");
    const row = Array.from(document.querySelectorAll(".file-table tbody tr")).find(r => r.innerText.includes("a.txt"));
    return {
      sideways: wrap.scrollWidth > wrap.clientWidth + 2,
      labels: Array.from(row.querySelectorAll(".file-action-button > span")).filter(el => getComputedStyle(el).display !== "none").length,
    };
  });
  console.log("     list at 760px: " + JSON.stringify(tablet));
  check("at 760 pixels the list needs no sideways scrolling", tablet.sideways, false);
  check("and the row buttons are icons", tablet.labels, 0);
  await page.click(actions);
  await sleep(300);
  const narrow = await state(actions);
  console.log("     actions at 760px: " + JSON.stringify(narrow));
  check("at 760 pixels it is still a dropdown", narrow.sheet, false);
  check("and still on the screen", narrow.onScreen, true);
  await page.keyboard.press("Escape");
  await sleep(200);

  // --- Crossing the line to a phone closes whatever is open
  await page.click(actions);
  await sleep(300);
  await page.setViewportSize({ width: 600, height: 900 });
  await sleep(600);
  const crossed = await page.evaluate(() => document.querySelectorAll(".menu-sheet").length + Array.from(document.querySelectorAll(".toolbar-menu-panel")).filter(p => !p.hidden).length);
  check("turning it into a phone closes the open menu", crossed, 0);
  check("nothing fell back to a browser dialog", prompts, []);

  await api("/delete", { paths: ["/" + FOLDER] });
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
