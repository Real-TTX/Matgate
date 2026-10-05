// The file manager in the menu - and the compact-view toggle only where the icon next to it
// is missing.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const visible = (page, sel) => page.evaluate(s => {
  const el = document.querySelector(s);
  if (!el) return "missing";
  return el.getBoundingClientRect().height > 2 && getComputedStyle(el).display !== "none";
}, sel);

const signIn = async (page) => {
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1700);
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }

  // ================= Desktop, normal view
  const ctx = await b.newContext({ viewport: { width: 1400, height: 950 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await signIn(page);

  const entry = await page.evaluate(() => {
    const a = document.querySelector('.shell-tabs a[data-server-id]');
    return a ? { text: a.textContent.trim(), target: a.getAttribute("href"), place: a.getAttribute("data-server-id") } : null;
  });
  console.log("     entry in the bar: " + JSON.stringify(entry));
  check("Files sits in the navigation bar", entry !== null, true);
  check("the entry points at a place", /^\/connect\//.test((entry || {}).target || ""), true);

  check("desktop normal: icon is there", await visible(page, "#view-mode-toggle"), true);
  check("desktop normal: burger off", await visible(page, ".shell-burger"), false);

  // --- One click opens the file manager as a tab
  const before = await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab").length);
  await page.locator('.shell-tabs a[data-server-id]').first().click();
  await sleep(3000);
  const afterwards = await page.evaluate(() => ({
    tabs: document.querySelectorAll("#session-tabs .session-tab").length,
    tabTitle: Array.from(document.querySelectorAll("#session-tabs .session-tab-title")).map(t => t.textContent.trim()),
    fileManager: !!document.querySelector(".file-manager"),
    url: location.pathname,
  }));
  console.log("     after the click: " + JSON.stringify(afterwards));
  check("a tab was added", afterwards.tabs > before, true);
  check("and it is the file manager", afterwards.fileManager, true);
  check("without a page change", afterwards.url, "/");
  await page.screenshot({ path: "menu-1-desktop.png", clip: { x: 0, y: 0, width: 1400, height: 130 } });

  // ================= Desktop, compact view: icon AND entry would be the same thing twice
  await page.evaluate(() => { document.getElementById("view-mode-toggle").click(); });
  await sleep(1200);
  const mode = await page.evaluate(() => document.documentElement.dataset.viewMode);
  check("kompakte Ansicht an", mode, "minimal");
  check("desktop compact: icon still there", await visible(page, "#view-mode-toggle"), true);
  check("desktop compact: menu entry NOT duplicated", await visible(page, "[data-view-mode-toggle]"), false);
  await page.screenshot({ path: "menu-2-desktop-compact.png", clip: { x: 0, y: 0, width: 1400, height: 130 } });
  await page.evaluate(() => { document.getElementById("view-mode-toggle").click(); });
  await sleep(800);
  await ctx.close();

  // ================= Phone, compact view: the icon is missing there, so the entry exists
  const ctx2 = await b.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  await ctx2.addInitScript(() => { try { localStorage.setItem("matgate.view.mode.v1", "minimal"); } catch (e) {} });
  const p2 = await ctx2.newPage();
  p2.on("pageerror", e => { console.log("JS ERROR phone: " + e.message); failures.push("JS error phone"); });
  await signIn(p2);
  check("phone compact: icon gone", await visible(p2, "#view-mode-toggle"), false);
  await p2.locator(".shell-burger-trigger").tap();
  await sleep(600);
  check("phone compact: menu entry present", await visible(p2, "[data-view-mode-toggle]"), true);
  check("phone: Files in the burger", await visible(p2, ".shell-burger-panel a[data-server-id]"), true);
  await p2.screenshot({ path: "menu-3-phone.png" });
  await ctx2.close();

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
