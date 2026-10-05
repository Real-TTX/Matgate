// The file manager as a tab: changing folders has to work (the select must not close at
// once), and the tab should say that it is a place.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const activeTitle = page => page.evaluate(() => {
  const t = document.querySelector("#session-tabs .session-tab.active .session-tab-title");
  return t ? t.textContent.trim() : "";
});

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1800);

  // --- Open the file manager through the menu
  await page.locator('.shell-tabs a[data-server-id]').first().click();
  await sleep(3000);
  const titleNew = await activeTitle(page);
  console.log("     Titel beim Oeffnen: " + JSON.stringify(titleNew));
  check("the tab says that it is a place", /^(Files|Dateien):\s/.test(titleNew), true);

  // --- The select must not lose focus straight away
  await page.evaluate(() => {
    const f = document.querySelector("[data-file-place-select]");
    f.dataset.token = "1";
  });
  await page.locator("[data-file-place-select]").click();
  await sleep(700);
  const afterClick = await page.evaluate(() => {
    const f = document.querySelector("[data-file-place-select]");
    return { da: !!f, same: f ? f.dataset.token === "1" : null, focus: document.activeElement === f };
  });
  console.log("     after clicking: " + JSON.stringify(afterClick));
  check("the select stays open", afterClick.same, true);
  check("and keeps the focus", afterClick.focus, true);

  // --- Change folder: same tab, new title
  const before = await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab").length);
  const target = await page.evaluate(() => {
    const f = document.querySelector("[data-file-place-select]");
    // The last entry of the Workspaces group makes a NEW place - not a folder to switch to.
    const others = Array.from(f.options).find(o => o.value !== f.value && o.value !== "__create-place__");
    return others ? { value: others.value, text: others.textContent.trim() } : null;
  });
  check("there is another folder", target !== null, true);
  await page.selectOption("[data-file-place-select]", target.value);
  await sleep(2800);
  const after = await page.evaluate(() => ({
    tabs: document.querySelectorAll("#session-tabs .session-tab").length,
    value: document.querySelector("[data-file-place-select]").value,
  }));
  const titleAfter = await activeTitle(page);
  console.log("     after the switch: " + JSON.stringify(after) + " title " + JSON.stringify(titleAfter));
  check("the folder changed", after.value, target.value);
  check("without opening a second tab", after.tabs, before);
  check("the title names the new folder", titleAfter.includes(target.text), true);
  check("and still says that it is a place", /^(Files|Dateien):\s/.test(titleAfter), true);

  // --- A remote session does NOT carry the prefix
  const add = page.locator('[data-tab-kind="add"]').first();
  await add.evaluate(el => (el.querySelector(".session-tab-main") || el).click());
  await sleep(1400);
  const c = page.locator("a,button").filter({ hasText: /^\s*(Connect|Verbinden)\s*$/ }).first();
  if (await c.count()) { await c.click(); await sleep(5500); }
  const titleSession = await activeTitle(page);
  console.log("     title of the session: " + JSON.stringify(titleSession));
  check("a session is still named after its machine", /^(Files|Dateien):/.test(titleSession), false);

  await page.screenshot({ path: "file-manager-tab.png", clip: { x: 0, y: 0, width: 1400, height: 140 } });
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
