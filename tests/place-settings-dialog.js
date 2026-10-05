// A place's settings as a dialog: without leaving the tab, and without wiping something on save
// that the dialog does not even offer.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "Dialogablage";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1800);

  // --- Create a place with a root path set (which must not get lost)
  await page.goto(BASE + "/workspaces/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"name\"]", NAME);
  await page.evaluate(() => document.querySelector("input[name='name']").form.requestSubmit());
  await sleep(1800);
  const shareId = (page.url().match(/\/workspaces\/([0-9a-f-]{36})/i) || [])[1];
  check("place created", typeof shareId === "string", true);
  // Set a real root path first - otherwise "not emptied" proves nothing.
  await page.goto(BASE + "/workspaces/" + shareId + "?tab=settings", { waitUntil: "networkidle" });
  await sleep(800);
  await page.evaluate(() => {
    const f = document.querySelector("[name='rootPath']");
    f.value = "/workspaces/dialog-pruefpfad";
    f.form.requestSubmit();
  });
  await sleep(1800);
  await page.goto(BASE + "/workspaces/" + shareId + "?tab=settings", { waitUntil: "networkidle" });
  await sleep(700);
  const rootBefore = await page.evaluate(() => {
    const f = document.querySelector("[name='rootPath']");
    return f ? f.value : null;
  });
  console.log("     root path before: " + JSON.stringify(rootBefore));

  // --- Open it in the file manager
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1300);
  // .first(): an earlier aborted run may have left a share of the same name behind - the test
  // should not fail on that but on the thing it tests.
  const card = page.locator(".home2-workspaces-section .connection-choice").filter({ hasText: NAME }).first();
  await card.locator(".connection-choice-open").click();
  await sleep(3000);
  const tabsBefore = await page.evaluate(() => ({
    count: document.querySelectorAll("#session-tabs .session-tab").length,
    active: (document.querySelector("#session-tabs .session-tab.active .session-tab-title") || {}).textContent || "",
  }));
  console.log("     tabs: " + JSON.stringify(tabsBefore));

  // --- Open the settings: a dialog, no tab change
  // Since the dissolution the gear on the home-page card carries this attribute too - but that
  // card sits hidden behind the file manager. The one meant is the visible one in the bar.
  const button = page.locator("[data-workspace-settings]:visible");
  check("the settings button is there", await button.count() > 0, true);
  await button.first().click();
  await sleep(1800);
  const inDialog = await page.evaluate(() => {
    const d = document.getElementById("workspace-settings-dialog");
    return {
      isOpen: !!d && d.open,
      form: !!(d && d.querySelector("[data-workspace-settings-form]")),
      link: (d && d.querySelector("[data-workspace-link]") || {}).textContent || "",
      copy: !!(d && d.querySelector("[data-workspace-copy]")),
      note: !!(d && d.querySelector("[name='sharedNoteFileName']")),
      root: !!(d && d.querySelector("[name='rootPath']")),
      activeTab: (document.querySelector("#session-tabs .session-tab.active .session-tab-title") || {}).textContent || "",
      tabs: document.querySelectorAll("#session-tabs .session-tab").length,
      // The sheet brings its own surface and fills the dialog: as a small frameless box the
      // fields stood naked over the file table behind them.
      sheet: !!(d && d.querySelector(".share-settings-page")),
      back: !!(d && d.querySelector(".share-settings-back")),
      heading: ((d && d.querySelector(".share-settings-title strong")) || {}).textContent || "",
      fillsHost: (() => {
        const sheet = d && d.querySelector(".share-settings-page");
        if (!sheet) { return null; }
        const a = sheet.getBoundingClientRect();
        const b = d.getBoundingClientRect();
        return Math.abs(a.height - b.height) <= 2 && Math.abs(a.width - b.width) <= 2;
      })(),
    };
  });
  console.log("     Dialog: " + JSON.stringify(inDialog));
  check("the dialog is open", inDialog.isOpen, true);
  check("with a form", inDialog.form, true);
  check("with the link to pass on", /\/workspace\//.test(inDialog.link), true);
  check("with a copy button", inDialog.copy, true);
  check("without the shared note", inDialog.note, false);
  check("without the root path", inDialog.root, false);
  check("the same tab is still active", inDialog.activeTab, tabsBefore.active);
  check("no tab was added", inDialog.tabs, tabsBefore.count);
  check("it is a sheet with its own surface", inDialog.sheet, true);
  check("with a way back at the top", inDialog.back, true);
  check("named after the place", /Dialogablage/.test(inDialog.heading), true);
  check("and it fills its host", inDialog.fillsHost, true);
  await page.screenshot({ path: "place-settings-dialog.png" });

  // --- Rename and save
  await page.evaluate(() => {
    const d = document.getElementById("workspace-settings-dialog");
    d.querySelector("[name='name']").value = "Dialogablage neu";
    d.querySelector("[data-workspace-settings-form]").requestSubmit();
  });
  await sleep(3000);
  const after = await page.evaluate(() => ({
    isOpen: document.getElementById("workspace-settings-dialog").open,
    active: (document.querySelector("#session-tabs .session-tab.active .session-tab-title") || {}).textContent || "",
    tabs: document.querySelectorAll("#session-tabs .session-tab").length,
  }));
  console.log("     after saving: " + JSON.stringify(after));
  check("the dialog is closed", after.isOpen, false);
  check("the tab carries the new name", /Dialogablage neu/.test(after.active), true);
  check("still no tab added", after.tabs, tabsBefore.count);

  // --- And the root path is still there
  await page.goto(BASE + "/workspaces/" + shareId + "?tab=settings", { waitUntil: "networkidle" });
  await sleep(900);
  const rootAfter = await page.evaluate(() => {
    const f = document.querySelector("[name='rootPath']");
    return f ? f.value : null;
  });
  console.log("     root path afterwards: " + JSON.stringify(rootAfter));
  check("the root path was not emptied", rootAfter, rootBefore);

  // --- Clean up
  await page.evaluate(() => {
    const form = document.querySelector("form[action$='/delete']");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  });
  await sleep(1400);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
