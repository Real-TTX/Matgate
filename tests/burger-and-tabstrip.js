// 1) Does the burger menu close after a choice?  2) Is the tab strip the same height with no
// tabs as with them?
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
  await sleep(1800);
};
const barHeight = page => page.evaluate(() => {
  const s = document.getElementById("session-tabs");
  const add = document.getElementById("new-connection-tab");
  return { bar: s ? +s.getBoundingClientRect().height.toFixed(1) : null,
    add: add ? +add.getBoundingClientRect().height.toFixed(1) : null,
    tabs: document.querySelectorAll("#session-tabs .session-tab:not(.session-tab--add)").length };
});

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }

  // ---------- Desktop: strip height and burger
  const ctx = await b.newContext({ viewport: { width: 1280, height: 860 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await signIn(page);

  // Close every tab first, so only the plus is left
  await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab:not(.session-tab--add) .session-tab-close").forEach(x => x.click()));
  await sleep(1200);
  const empty = await barHeight(page);
  console.log("     without tabs: " + JSON.stringify(empty));

  // Open a connection
  const c = page.locator("a,button").filter({ hasText: /^\s*(Connect|Verbinden)\s*$/ }).first();
  if (await c.count()) { await c.click(); await sleep(4500); }
  const full = await barHeight(page);
  console.log("     with a tab:   " + JSON.stringify(full));
  check("strip equally high with and without tabs", empty.bar, full.bar);
  check("at least one tab open", full.tabs > 0, true);

  // ---------- The account menu on the desktop: closed after a choice
  const accountOpen = () => page.evaluate(() => {
    const d = document.querySelector("details.account-menu");
    return d ? d.hasAttribute("open") : "missing";
  });
  const accountHandle = page.locator("details.account-menu > summary");
  if (await accountHandle.count()) {
    await accountHandle.click(); await sleep(400);
    check("account menu open", await accountOpen(), true);
    await page.locator("details.account-menu a[href], details.account-menu button").first().click();
    await sleep(900);
    check("account menu closed after the choice", await accountOpen(), false);
  }
  await page.screenshot({ path: "burger-bar-desktop.png", clip: { x: 0, y: 0, width: 1280, height: 130 } });
  await ctx.close();

  // ---------- Phone, compact: the strip is hidden there, the burger is a sheet
  const ctx2 = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  await ctx2.addInitScript(() => { try { localStorage.setItem("matgate.view.mode.v1", "minimal"); } catch (e) {} });
  const p2 = await ctx2.newPage();
  p2.on("pageerror", e => { console.log("JS ERROR phone: " + e.message); failures.push("JS error phone"); });
  await signIn(p2);
  await p2.locator(".shell-burger-trigger").tap(); await sleep(500);
  const open1 = await p2.evaluate(() => document.querySelector("details.shell-burger").hasAttribute("open"));
  check("phone: burger open", open1, true);
  await p2.locator(".shell-burger .shell-menu-item").filter({ hasText: /Tools/i }).first().tap();
  await sleep(1000);
  const open2 = await p2.evaluate(() => document.querySelector("details.shell-burger").hasAttribute("open"));
  check("phone: burger closed after the choice", open2, false);
  await p2.screenshot({ path: "burger-bar-phone.png" });
  await ctx2.close();

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
