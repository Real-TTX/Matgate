// Marking a favourite while a session is running. Does the session survive it?
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
  const b = await chromium.launch({ channel: "msedge", headless: true });
  const page = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=username]", "admin");
  await page.fill("input[name=password]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1700);

  const c = page.locator("a,button").filter({ hasText: /^\s*(Connect|Verbinden)\s*$/ }).first();
  await c.click();
  await sleep(6500);
  const state = () => page.evaluate(() => {
    const el = document.querySelector(".session-statusbar");
    return {
      status: el ? el.innerText.replace(/\s+/g, " ").trim().slice(0, 60) : "",
      tabs: document.querySelectorAll("#session-tabs .session-tab").length,
      aborted: Array.from(document.querySelectorAll(".connection-overlay"))
        .filter(o => !o.classList.contains("hidden")).length,
    };
  });
  console.log("     before: " + JSON.stringify(await state()));

  // To the home page, and press a star there
  const add = page.locator('[data-tab-kind="add"]').first();
  await add.evaluate(el => (el.querySelector(".session-tab-main") || el).click());
  await sleep(1500);
  const star = page.locator(".favorite-toggle-form button").first();
  check("star is there", await star.count() > 0, true);
  const starOn = () => page.evaluate(() => {
    const k = document.querySelector(".favorite-toggle-form button");
    return k ? k.classList.contains("active") : null;
  });
  const starBefore = await starOn();
  const beforeUrl = page.url();
  await star.click();
  await sleep(3500);
  const after = await state();
  console.log("     after: " + JSON.stringify(after) + "  url " + (page.url() === beforeUrl ? "same" : "CHANGED"));
  check("the session is still alive", after.tabs > 1 && after.aborted === 0, true);
  const starAfter = await starOn();
  console.log("     star before/after: " + starBefore + " / " + starAfter);
  check("the star toggled", starAfter, !starBefore);
  // And does it survive a fresh load?
  await page.reload({ waitUntil: "networkidle" });
  await sleep(1500);
  check("the favourite is saved", await starOn(), !starBefore);
  // Put it back
  await page.locator(".favorite-toggle-form button").first().click();
  await sleep(1500);
  await page.screenshot({ path: "favorite-keeps-session.png" });
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
