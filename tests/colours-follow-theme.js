// Do the surfaces that used to be hard-coded follow the theme now? Checked with a palette of
// our own AND colours of our own, so the answer is not right by accident.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
// Browsers give colours back in two spellings: rgb(r, g, b) and - since color-mix -
// color(srgb a b c / alpha) with values from 0 to 1. Both have to arrive here.
const asNumbers = value => {
  const srgb = /color\(srgb\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)/.exec(value || "");
  if (srgb) return [1, 2, 3].map(i => Math.round(+srgb[i] * 255));
  const rgb = /(\d+),\s*(\d+),\s*(\d+)/.exec(value || "");
  return rgb ? [1, 2, 3].map(i => +rgb[i]) : null;
};
const contrast = (a, b) => {
  const lum = value => {
    const z = asNumbers(value);
    if (!z) return null;
    const v = z.map(c => c / 255).map(c => c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
    return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2];
  };
  const la = lum(a), lb = lum(b);
  if (la === null || lb === null) return null;
  return +(((Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05))).toFixed(2);
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1280, height: 900 }, colorScheme: "light" })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1600);

  const token = () => page.evaluate(() => {
    const el = document.querySelector(".brand-mark");
    if (!el) return null;
    const s = getComputedStyle(el);
    return { background: s.backgroundImage, textColour: s.color };
  });

  // --- Starting point
  await page.goto(BASE + "/account?tab=profile", { waitUntil: "networkidle" });
  await sleep(800);
  await page.evaluate(() => {
    ["accentOwn", "accent2Own", "backgroundOwn"].forEach(n => { document.querySelector("[name='" + n + "']").checked = false; });
    document.querySelector("[name='backgroundOwn']").form.requestSubmit();
  });
  await sleep(1800);
  const before = await token();
  console.log("     logo before: " + JSON.stringify(before));
  check("the logo has a gradient", /linear-gradient/.test(before.background), true);

  // --- Set colours of our own
  await page.goto(BASE + "/account?tab=profile", { waitUntil: "networkidle" });
  await sleep(800);
  await page.evaluate(() => {
    const set = (field, toggle, value) => {
      const f = document.querySelector("[name='" + field + "']");
      f.value = value;
      document.querySelector("[name='" + toggle + "']").checked = true;
    };
    set("accentColor", "accentOwn", "#b3261e");
    set("accentColor2", "accent2Own", "#1b5e20");
    set("backgroundColor", "backgroundOwn", "#101317");
    document.querySelector("[name='backgroundOwn']").form.requestSubmit();
  });
  await sleep(2000);

  const afterwards = await token();
  console.log("     logo afterwards: " + JSON.stringify(afterwards));
  check("the logo changed colour", afterwards.background !== before.background, true);
  // The chosen shades are corrected for readability against the ground - so what matters is not
  // the raw value but that both spots changed and are different from each other.
  const spots = (afterwards.background.match(/rgb([^)]+)/g) || []);
  check("the logo carries two different accents", spots.length === 2 && spots[0] !== spots[1], true);

  // --- The overlay while a connection is being established: that only exists in the shell
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1200);
  // The overlay only appears with a connection tab - so open one.
  const connect = page.locator("a,button").filter({ hasText: /^\s*(Connect|Verbinden)\s*$/ }).first();
  if (await connect.count()) { await connect.click(); await sleep(3000); }
  const overlay = await page.evaluate(() => {
    const el = document.querySelector(".connection-overlay");
    if (!el) return null;
    const s = getComputedStyle(el);
    return { background: s.backgroundColor, textColour: s.color };
  });
  console.log("     overlay: " + JSON.stringify(overlay));
  check("overlay found", overlay !== null, true);
  if (overlay) {
    const panel = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--panel").trim());
    const a = asNumbers(overlay.background);
    const p = asNumbers(panel.startsWith("#")
      ? "rgb(" + parseInt(panel.slice(1, 3), 16) + ", " + parseInt(panel.slice(3, 5), 16) + ", " + parseInt(panel.slice(5, 7), 16) + ")"
      : panel);
    console.log("     panel per theme: " + panel + " -> " + JSON.stringify(p) + ", overlay " + JSON.stringify(a));
    check("the overlay takes the theme's surface colour", a && p && a.every((v, i) => Math.abs(v - p[i]) <= 1), true);
    const k = contrast(overlay.textColour, overlay.background);
    console.log("     contrast in the overlay: " + k);
    check("text in it stays readable", (k || 0) >= 4.5, true);
  }

  await page.screenshot({ path: "farbfest-1.png", clip: { x: 0, y: 0, width: 400, height: 60 } });

  // --- Reset
  await page.goto(BASE + "/account?tab=profile", { waitUntil: "networkidle" });
  await sleep(800);
  await page.evaluate(() => {
    ["accentOwn", "accent2Own", "backgroundOwn"].forEach(n => { document.querySelector("[name='" + n + "']").checked = false; });
    document.querySelector("[name='backgroundOwn']").form.requestSubmit();
  });
  await sleep(1600);
  const back = await token();
  check("switched off, back to before", back.background, before.background);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
