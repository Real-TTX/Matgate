// Three colours, a preview while setting them, and the same picture after saving.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const tokens = page => page.evaluate(() => {
  const s = getComputedStyle(document.documentElement);
  const read = n => s.getPropertyValue(n).trim();
  return { bg: read("--bg"), text: read("--text"), panel: read("--panel"), line: read("--line"),
    accent: read("--accent"), accent2: read("--accent-2") };
});
// Contrast per WCAG, so that "readable" is a number and not an impression.
const contrast = (a, b) => {
  const lum = hex => {
    const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
    if (!m) return null;
    const v = [0, 2, 4].map(i => parseInt(m[1].substr(i, 2), 16) / 255)
      .map(c => c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
    return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2];
  };
  const la = lum(a), lb = lum(b);
  if (la === null || lb === null) return null;
  return +(((Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05))).toFixed(2);
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1280, height: 950 }, colorScheme: "light" });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), page.click(".login-submit")]);
  await sleep(1500);

  await page.goto(BASE + "/account?tab=profile", { waitUntil: "networkidle" });
  await sleep(800);
  check("three colour groups", await page.locator("[data-colour-field]").count(), 3);

  // Start clean: switch off whatever an earlier run saved - otherwise the value we compare
  // against is already a colour of our own and not the theme's.
  await Promise.all([
    page.waitForNavigation({ waitUntil: "load", timeout: 20000 }),
    page.evaluate(() => {
    ["accentOwn", "accent2Own", "backgroundOwn"].forEach(n => { document.querySelector("[name='" + n + "']").checked = false; });
    window.setTimeout(() => document.querySelector("[name='backgroundOwn']").form.requestSubmit(), 0);
    }),
  ]);
  await sleep(2000);
  await page.goto(BASE + "/account?tab=profile", { waitUntil: "networkidle" });
  await sleep(700);
  const before = await tokens(page);
  console.log("     before: " + JSON.stringify(before));
  await page.screenshot({ path: "colours-1-before.png", fullPage: false });

  // --- Set the background to a dark colour, in light mode
  await page.evaluate(() => {
    const an = document.querySelector("[name='backgroundOwn']");
    const field = document.querySelector("[name='backgroundColor']");
    field.value = "#101317";
    an.checked = true;
    field.dispatchEvent(new Event("input", { bubbles: true }));
  });
  await sleep(900);
  const liveBg = await tokens(page);
  console.log("     live:   " + JSON.stringify(liveBg));
  check("the preview sets the background at once", liveBg.bg.toLowerCase(), "#101317");
  check("text stays readable on a dark ground", (contrast(liveBg.text, liveBg.bg) || 0) >= 4.5, true);
  console.log("     Kontrast Schrift/Grund: " + contrast(liveBg.text, liveBg.bg));
  check("fields stand out from the ground", liveBg.panel.toLowerCase() !== liveBg.bg.toLowerCase(), true);
  await page.screenshot({ path: "farb-2-live.png", fullPage: false });

  // --- The second accent
  await page.evaluate(() => {
    const an = document.querySelector("[name='accent2Own']");
    const field = document.querySelector("[name='accentColor2']");
    field.value = "#c2410c";
    an.checked = true;
    field.dispatchEvent(new Event("input", { bubbles: true }));
  });
  await sleep(900);
  const liveA2 = await tokens(page);
  check("the second accent shows in the preview", liveA2.accent2.toLowerCase() !== before.accent2.toLowerCase(), true);
  console.log("     accent-2 now: " + liveA2.accent2);

  // --- Saving
  await page.evaluate(() => {
    const an = document.querySelector("[name='accentOwn']");
    const field = document.querySelector("[name='accentColor']");
    field.value = "#7a3fb5";
    an.checked = true;
    field.dispatchEvent(new Event("input", { bubbles: true }));
  });
  await sleep(900);
  const beforeSave = await tokens(page);
  await Promise.all([
    page.waitForNavigation({ waitUntil: "load", timeout: 20000 }),
    page.evaluate(() => { window.setTimeout(() => document.querySelector("[name='backgroundOwn']").form.requestSubmit(), 0); }),
  ]);
  await sleep(2000);

  const afterSave = await tokens(page);
  console.log("     before saving: " + JSON.stringify(beforeSave));
  console.log("     after saving:  " + JSON.stringify(afterSave));
  check("preview and result agree", afterSave, beforeSave);

  // --- And after a fresh load it is still there
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(900);
  const onHomePage = await tokens(page);
  check("holds on other pages too", onHomePage, beforeSave);
  await page.screenshot({ path: "farb-3-start.png", fullPage: false });

  // --- The checkboxes stay ticked
  await page.goto(BASE + "/account?tab=profile", { waitUntil: "networkidle" });
  await sleep(700);
  const ticks = await page.evaluate(() => ({
    accent: document.querySelector("[name='accentOwn']").checked,
    accent2: document.querySelector("[name='accent2Own']").checked,
    bg: document.querySelector("[name='backgroundOwn']").checked,
  }));
  check("all three boxes are still ticked", ticks, { accent: true, accent2: true, bg: true });

  // --- Reset: boxes off means the theme's colour again
  await page.evaluate(() => {
    ["accentOwn", "accent2Own", "backgroundOwn"].forEach(n => {
      const an = document.querySelector("[name='" + n + "']");
      an.checked = false;
      an.dispatchEvent(new Event("change", { bubbles: true }));
    });
  });
  await sleep(900);
  const back = await tokens(page);
  check("switched off, the theme applies again", back, before);
  await page.screenshot({ path: "colours-4-reset.png", fullPage: false });

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
