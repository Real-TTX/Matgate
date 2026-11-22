const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const sheet = page => page.evaluate(() => {
  const p = document.querySelector(".mobile-tab-menu-panel");
  return p ? (p.getBoundingClientRect().height > 10 && getComputedStyle(p).display !== "none" ? "open" : "closed") : "missing";
});
const button = page => page.evaluate(() => {
  const m = document.getElementById("mobile-tab-menu");
  const t = m && m.querySelector(".mobile-tab-menu-trigger");
  if (!t) return { missing: true };
  const number = m.querySelector("[data-mobile-tab-count]");
  const plus = m.querySelector("[data-mobile-tab-plus]");
  return {
    empty: m.getAttribute("data-empty"),
    countVisible: number ? getComputedStyle(number).display !== "none" : null,
    plusVisible: plus ? getComputedStyle(plus).display !== "none" : null,
    number: number ? number.textContent.trim() : null,
    label: t.getAttribute("aria-label"),
    expanded: t.getAttribute("aria-expanded"),
  };
});

(async () => {
  let b = await chromium.launch({ headless: true });
  const ctx = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  await ctx.addInitScript(() => { try { localStorage.setItem("matgate.view.mode.v1", "minimal"); } catch (e) {} });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), page.click(".login-submit")]);
  await sleep(1600);

  // --- With no connection: a plus
  const k0 = await button(page);
  check("no connection: plus instead of a number", { empty: k0.empty, number: k0.countVisible, plus: k0.plusVisible }, { empty: "true", number: false, plus: true });
  console.log("     label with no connection: " + JSON.stringify(k0.label));
  await page.screenshot({ path: "pruef-0-plus.png", clip: { x: 0, y: 0, width: 390, height: 56 } });

  // Press the plus -> opens a tab, does NOT open the sheet
  const before = await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab").length);
  await page.locator(".mobile-tab-menu-trigger").tap();
  await sleep(900);
  check("the plus opens no sheet", await sheet(page), "closed");
  const afterwards = await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab").length);
  console.log("     tabs before/after: " + before + "/" + afterwards);

  // --- Open two sessions
  const newSession = async () => {
    const add = page.locator("[data-tab-kind=\"add\"]").first();
    if (await add.count()) { await add.evaluate(el => (el.querySelector(".session-tab-main") || el).click()); await sleep(1000); }
    const c = page.locator("a,button").filter({ hasText: /^\s*(Connect|Verbinden)\s*$/ }).first();
    if (await c.count()) { await c.tap(); await sleep(3800); }
  };
  await newSession(); await newSession();
  const k2 = await button(page);
  check("two sessions: a number instead of the plus", { empty: k2.empty, number: k2.countVisible, plus: k2.plusVisible, n: k2.number }, { empty: "false", number: true, plus: false, n: "2" });
  console.log("     label with connections: " + JSON.stringify(k2.label));

  const open = async () => { await page.locator(".mobile-tab-menu-trigger").tap(); await sleep(500); };

  // --- Switching closes it
  await open();
  check("sheet open after tapping", await sheet(page), "open");
  check("aria-expanded open", (await button(page)).expanded, "true");
  // The sheet is built like the main menu's: a head with the count, a row per connection with its
  // icon in a tile and what it is below the name, the new connection apart at the end.
  const look = await page.evaluate(() => {
    const panel = document.querySelector(".mobile-tab-menu-panel");
    const rows = Array.from(panel.querySelectorAll(".mobile-tab-item"));
    const sessions = rows.filter(row => !row.classList.contains("mobile-tab-item--add"));
    const add = rows[rows.length - 1];
    return {
      count: (panel.querySelector(".mobile-tab-sheet-count") || {}).textContent,
      icons: sessions.every(row => !!row.querySelector(".mobile-tab-item-icon svg")),
      names: sessions.every(row => !!(row.querySelector(".mobile-tab-item-name") || {}).textContent),
      states: sessions.every(row => !!(row.querySelector(".mobile-tab-item-state") || {}).textContent),
      addLast: add.classList.contains("mobile-tab-item--add") && !add.querySelector(".mobile-tab-item-close"),
      addHasNoPlusInText: !/^\+/.test(add.textContent.trim()),
      closeInsideRow: sessions.every(row => { const r = row.getBoundingClientRect(); const c = row.querySelector(".mobile-tab-item-close").getBoundingClientRect(); return c.right <= r.right && c.width >= 40 && c.height >= 40; }),
    };
  });
  check("the sheet looks like the main menu's", look, { count: "2", icons: true, names: true, states: true, addLast: true, addHasNoPlusInText: true, closeInsideRow: true });
  await page.screenshot({ path: "mobile-1-sheet.png" });
  await page.locator(".mobile-tab-item-main").first().tap();
  await sleep(1000);
  check("switching closes the sheet", await sheet(page), "closed");
  check("aria-expanded zu", (await button(page)).expanded, "false");

  // --- The old race: close, then switch straight away
  await open();
  await page.evaluate(() => {
    const x = document.querySelector(".mobile-tab-item-close");
    if (x) x.click();
    const target = document.querySelectorAll(".mobile-tab-item-main")[0];
    if (target) target.click();
  });
  await sleep(1400);
  check("close + switch at once closes the sheet", await sheet(page), "closed");

  // --- Closing alone leaves the sheet open (that was the intent)
  await newSession();
  await open();
  const beforeN = await page.evaluate(() => document.querySelectorAll(".mobile-tab-item-close").length);
  if (beforeN > 0) {
    await page.locator(".mobile-tab-item-close").first().tap();
    await sleep(500);
    check("closing a tab leaves the sheet open", await sheet(page), "open");
    const afterN = await page.evaluate(() => document.querySelectorAll(".mobile-tab-item-close").length);
    check("the list was rebuilt", afterN, beforeN - 1);
  }

  // --- Escape and tapping beside it
  check("sheet still open", await sheet(page), "open");
  await page.keyboard.press("Escape");
  await sleep(400);
  check("escape closes it", await sheet(page), "closed");

  await open();
  const closeX = page.locator(".mobile-tab-sheet-close");
  if (await closeX.count()) { await closeX.tap(); await sleep(400); }
  check("the X in the sheet closes it", await sheet(page), "closed");

  // --- Open and switch a second time
  await open();
  await page.locator(".mobile-tab-item-main").last().tap();
  await sleep(1000);
  check("the second round closes it too", await sheet(page), "closed");

  await page.screenshot({ path: "pruef-2-ende.png" });
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
