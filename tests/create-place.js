// Making a place from the file manager: the last entry under the workspaces asks for a name and
// the manager then stands in the new place. Since the workspace page left the menu this is the
// only way to get a new one - sharing only ever turns an EXISTING place into a share.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "Frisch angelegt";
const MARKER = "__create-place__";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  // The app asks in a dialog of its own now, so there is nothing to accept - it gets typed.
  const nameIt = async () => {
    await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 10000 });
    await page.fill("#name-dialog-input", NAME);
    await page.click("#name-dialog button[type='submit']");
  };

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1800);

  // The file manager through the menu, so the select is the one in the toolbar.
  await page.evaluate(() => {
    const entry = document.querySelector('.shell-tabs a[data-shell-open-server]');
    if (entry) { entry.click(); }
  });
  await sleep(2500);

  const before = await page.evaluate(marker => {
    const select = document.querySelector("[data-file-place-select]");
    if (!select) { return null; }
    const groups = Array.from(select.querySelectorAll("optgroup"));
    const workspaces = groups.find(g => /Workspace/i.test(g.label));
    const options = workspaces ? Array.from(workspaces.querySelectorAll("option")) : [];
    return {
      hasWorkspaceGroup: !!workspaces,
      createIsLast: options.length > 0 && options[options.length - 1].value === marker,
      createCount: Array.from(select.querySelectorAll("option")).filter(o => o.value === marker).length,
      tabs: document.querySelectorAll("#session-tabs .session-tab").length,
    };
  }, MARKER);
  console.log("     select: " + JSON.stringify(before));
  check("the workspaces group is there", (before || {}).hasWorkspaceGroup, true);
  check("the create entry is its last one", (before || {}).createIsLast, true);
  check("and exists exactly once", (before || {}).createCount, 1);

  await page.selectOption("[data-file-place-select]", MARKER);
  const askedInTab = await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 10000 }).then(() => true).catch(() => false);
  await page.fill("#name-dialog-input", NAME);
  await page.click("#name-dialog button[type='submit']");
  await sleep(3000);

  check("a dialog asks for the name", askedInTab, true);

  const after = await page.evaluate(() => {
    const select = document.querySelector("[data-file-place-select]");
    const tab = document.querySelector("#session-tabs .session-tab.active .session-tab-title");
    return {
      selected: select ? (select.options[select.selectedIndex] || {}).textContent.trim() : null,
      tabTitle: tab ? tab.textContent.trim() : null,
      tabs: document.querySelectorAll("#session-tabs .session-tab").length,
    };
  });
  console.log("     after: " + JSON.stringify(after));
  check("the manager stands in the new place", /Frisch angelegt/.test(after.selected || ""), true);
  check("the tab carries its name", /Frisch angelegt/.test(after.tabTitle || ""), true);
  check("without opening a second tab", after.tabs, (before || {}).tabs);
  await page.screenshot({ path: "create-place.png", clip: { x: 0, y: 0, width: 1400, height: 300 } });

  // --- The places dialog of a session has a select of its own - the entry has to work there too,
  //     and that is exactly where it did nothing at first.
  // The dialog only exists when the connection has a file area of its own, and that is a
  // permission - without it the action button is not there and nothing can be opened.
  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  const adminHref = await page.evaluate(() => {
    const link = Array.from(document.querySelectorAll("a[href^='/admin/users/']"))
      .find(a => /admin/i.test(a.textContent || ""));
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
  await sleep(1200);
  const connect = page.locator("a,button").filter({ hasText: /^ *(Connect|Verbinden) *$/ }).first();
  if (await connect.count()) { await connect.click(); await sleep(5000); }
  // The action is icon-only - its label lives in the title, not in the text.
  const filesButton = page.locator('#connection-tab-actions [title*="Files for this" i], #connection-tab-actions [title*="Dateien dieser" i]').first();
  if (await filesButton.count()) { await filesButton.click(); await sleep(2500); }

  const dialogSelect = page.locator("#file-area-dialog-select");
  if (await dialogSelect.count()) {
    const hasEntry = await page.evaluate(marker =>
      !!document.querySelector('#file-area-dialog-select option[value="' + marker + '"]'), MARKER);
    check("the places dialog offers it too", hasEntry, true);
    await page.selectOption("#file-area-dialog-select", MARKER);
    const askedInDialog = await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 10000 }).then(() => true).catch(() => false);
    await page.fill("#name-dialog-input", NAME);
    await page.click("#name-dialog button[type='submit']");
    await sleep(3000);
    const inDialog = await page.evaluate(() => {
      const select = document.getElementById("file-area-dialog-select");
      const option = select ? select.options[select.selectedIndex] : null;
      const dialog = document.getElementById("file-area-dialog");
      return {
        shown: option ? option.textContent.trim() : null,
        host: !!document.querySelector(".file-area-host"),
        open: !!dialog && !dialog.classList.contains("hidden"),
      };
    });
    console.log("     in the dialog: " + JSON.stringify(inDialog));
    check("it asks for a name there as well", askedInDialog, true);
    check("and the dialog shows the new place", /Frisch angelegt/.test(inDialog.shown || ""), true);
    // The question sits on top of the dialog. A press on it used to count as a press BESIDE the
    // dialog and closed it - so every rename, every delete and every new folder in there ended with
    // the dialog gone.
    check("answering the question leaves the dialog open", inDialog.open, true);
  }

  // --- Clean up: the new place is a share like any other. This test makes the place TWICE - once
  // from the tab, once from the places dialog - so every share of that name goes, not the first
  // one found. Taking only the first left one behind on every run.
  const removed = await page.evaluate(async name => {
    const parse = html => new DOMParser().parseFromString(html, "text/html");
    const fetchText = async url => (await fetch(url, { credentials: "same-origin" })).text();
    const list = parse(await fetchText("/workspaces"));
    const ids = new Set();
    for (const link of list.querySelectorAll("tr td:first-child a[href^='/workspaces/']")) {
      if ((link.textContent || "").trim() === name) { ids.add(link.getAttribute("href").split("/").pop()); }
    }
    for (const id of ids) {
      const form = parse(await fetchText("/workspaces/" + id)).querySelector("form[action$='/delete']");
      if (form) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
    return ids.size;
  }, NAME);
  console.log("     cleaned up: " + removed + " share(s)");

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
