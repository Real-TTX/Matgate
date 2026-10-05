// Does the session survive a BIG download - and does it stay usable while it runs?
// With a WebSocket tunnel it should; if the tunnel falls back to HTTP, the download competes
// for the same six connections, and that would be the next instance of the same problem.
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
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1400, height: 950 }, acceptDownloads: true });
  const page = await ctx.newPage();
  page.on("pageerror", e => console.log("JS ERROR: " + e.message));

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1700);

  // An earlier test hides sections of the home page - all of them are needed here.
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(800);
  await page.evaluate(() => {
    document.querySelectorAll("#home-order-list [data-order-visible]").forEach(k => {
      k.checked = true;
      k.dispatchEvent(new Event("change", { bubbles: true }));
    });
    document.querySelector("[name='homeSections']").form.requestSubmit();
  });
  await sleep(1800);
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1200);

  const c = page.locator("a,button").filter({ hasText: /^\s*(Connect|Verbinden)\s*$/ }).first();
  await c.click();
  await sleep(6000);

  const state = () => page.evaluate(() => {
    const el = document.querySelector(".session-statusbar");
    return {
      status: el ? el.innerText.replace(/\s+/g, " ").trim() : "",
      aborted: Array.from(document.querySelectorAll(".connection-overlay"))
        .filter(o => !o.classList.contains("hidden")).length,
    };
  });
  const before = await state();
  console.log("     before: " + JSON.stringify(before));
  check("the session stands", before.aborted, 0);
  check("the tunnel is open", /Tunnel: Open/.test(before.status), true);
  // Which tunnel? WebSocket does not count against the six connections, HTTP does.
  const tunnelKind = await page.evaluate(() => {
    const entries = performance.getEntriesByType("resource").map(e => e.name);
    return {
      websocket: entries.some(n => n.includes("websocket-tunnel")),
      http: entries.some(n => /\/guacamole\/tunnel/.test(n)),
    };
  });
  console.log("     tunnel kind per network: " + JSON.stringify(tunnelKind));

  // --- Open the place as a second tab
  const placeId = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const hits = list.find(s => (s.areaKind || "") === "global");
    return hits ? hits.id : null;
  });
  const add = page.locator('[data-tab-kind="add"]').first();
  if (await add.count()) { await add.evaluate(el => (el.querySelector(".session-tab-main") || el).click()); await sleep(1500); }
  await page.locator('[data-server-id="' + placeId + '"]').first().click();
  await sleep(3000);

  // --- Download 180 MB and watch while it runs
  const row = page.locator(".file-table tr").filter({ hasText: "large.bin" }).first();
  check("the big file is there", await row.count() > 0, true);
  const download = page.waitForEvent("download", { timeout: 120000 }).catch(() => null);
  await row.locator("button").filter({ hasText: /Download|Herunterladen/ }).first().click();

  // Look several times during the download
  for (let i = 0; i < 6; i++) {
    await sleep(1500);
    const z = await state();
    if (z.aborted > 0) { console.log("     ABORTED while downloading: " + JSON.stringify(z)); break; }
  }

  const file = await download;
  console.log("     Download: " + (file ? await file.suggestedFilename() : "none"));
  await sleep(3000);

  // --- Back to the session and look
  await page.evaluate(() => {
    const tabs = Array.from(document.querySelectorAll("#session-tabs .session-tab"))
      .find(t => (t.textContent || "").includes("Linux-Testhost"));
    if (tabs) { (tabs.querySelector(".session-tab-main") || tabs).click(); }
  });
  await sleep(2500);
  const afterwards = await state();
  console.log("     afterwards: " + JSON.stringify(afterwards));
  check("no abort from the big download", afterwards.aborted, 0);
  check("the tunnel is still open", /Tunnel: Open/.test(afterwards.status), true);

  // --- And still usable? Send a key and see whether the screen moves.
  // The display is made of several canvases; the first is a background layer and never
  // changes. So the whole set is measured.
  const canvases = () => page.evaluate(() => Array.from(document.querySelectorAll("canvas"))
    .filter(k => k.width > 100 && k.height > 100)
    .map(k => k.toDataURL().length));
  const beforeInput = await canvases();
  // Click the display first: without focus the keys land on the page, not in the session -
  // and then you measure your own test error instead of the session.
  const display = page.locator(".guac-display").first();
  if (await display.count()) { await display.click({ position: { x: 40, y: 40 } }); await sleep(800); }
  await page.keyboard.type("echo matgate-lebt");
  await page.keyboard.press("Enter");
  await sleep(2500);
  const afterInput = await canvases();
  console.log("     canvases before: " + JSON.stringify(beforeInput) + "  after: " + JSON.stringify(afterInput));
  check("the session still reacts to input", beforeInput.length > 0 && JSON.stringify(beforeInput) !== JSON.stringify(afterInput), true);

  await page.screenshot({ path: "download-large.png" });
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
