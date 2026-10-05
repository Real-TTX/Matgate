// Checks the neighbours of the rebuilt part: the action overflow menu ("...") and the normal
// (non-compact) view on a phone.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const signIn = async (page) => {
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), page.click(".login-submit")]);
  await sleep(1600);
};
const connecting = async (page) => {
  // Sessions from before may still be open - go to the home page first.
  const add = page.locator("[data-tab-kind=\"add\"]").first();
  if (await add.count()) { await add.evaluate(el => (el.querySelector(".session-tab-main") || el).click()); await sleep(1200); }
  const c = page.locator("a,button").filter({ hasText: /^\s*(Connect|Verbinden)\s*$/ }).first();
  await c.tap(); await sleep(4000);
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }

  // ---------- compact: the "..." menu of the actions
  const ctx1 = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  await ctx1.addInitScript(() => { try { localStorage.setItem("matgate.view.mode.v1", "minimal"); } catch (e) {} });
  const p1 = await ctx1.newPage();
  p1.on("pageerror", e => { console.log("JS ERROR compact: " + e.message); failures.push("JS error compact"); });
  await signIn(p1); await connecting(p1);
  const more = p1.locator(".tab-action-more-trigger").first();
  check("action overflow is there", await more.count() > 0, true);
  if (await more.count()) {
    await more.tap(); await sleep(500);
    const isOpen = await p1.evaluate(() => Array.from(document.querySelectorAll("body > .tab-action-overflow-panel"))
      .filter(x => x.style.display === "flex").map(x => x.classList.contains("mobile-tab-menu-panel") ? "connections" : "actions"));
    check("only the action menu is open", isOpen, ["actions"]);
    // Tap the connections button: the action menu has to give way, the sheet opens
    await p1.locator(".mobile-tab-menu-trigger").tap(); await sleep(600);
    const open2 = await p1.evaluate(() => Array.from(document.querySelectorAll("body > .tab-action-overflow-panel"))
      .filter(x => x.style.display === "flex").map(x => x.classList.contains("mobile-tab-menu-panel") ? "connections" : "actions"));
    check("only the connection sheet is open", open2, ["connections"]);
    await p1.keyboard.press("Escape"); await sleep(400);
  }
  await ctx1.close();

  // ---------- normal view on a phone: the button stays gone, the tabs are there
  const ctx2 = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  const p2 = await ctx2.newPage();
  p2.on("pageerror", e => { console.log("JS ERROR normal: " + e.message); failures.push("JS error normal"); });
  await signIn(p2); await connecting(p2);
  const view = await p2.evaluate(() => {
    const m = document.getElementById("mobile-tab-menu");
    const strip = document.getElementById("session-tabs");
    return { mode: document.documentElement.dataset.viewMode || "normal",
      button: m ? getComputedStyle(m).display : "missing",
      bar: strip ? (strip.getBoundingClientRect().height > 5) : false };
  });
  check("normal view: button off, tab strip there", view, { mode: "normal", button: "none", bar: true });
  await ctx2.close();

  // ---------- Desktop: unchanged
  const ctx3 = await b.newContext({ viewport: { width: 1280, height: 800 } });
  const p3 = await ctx3.newPage();
  p3.on("pageerror", e => { console.log("JS ERROR desktop: " + e.message); failures.push("JS error desktop"); });
  await signIn(p3);
  const d = await p3.evaluate(() => {
    const m = document.getElementById("mobile-tab-menu");
    return m ? getComputedStyle(m).display : "missing";
  });
  check("desktop: button invisible", d, "none");
  await ctx3.close();

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
