// Reproducing it: a running session in the background, then download something in the file
// manager. Does the tunnel break?
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
  page.on("console", m => { if (/BEFOREUNLOAD|PAGEHIDE/.test(m.text())) console.log("     >> " + m.text()); });

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1700);

  // An earlier test hid sections of the home page - all of them are needed here.
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

  // --- Open a session and wait until it really stands
  const c = page.locator("a,button").filter({ hasText: /^\s*(Connect|Verbinden)\s*$/ }).first();
  await c.click();
  await sleep(6000);
  const session = () => page.evaluate(() => {
    const el = document.querySelector(".session-statusbar");
    const tabs = Array.from(document.querySelectorAll("#session-tabs .session-tab"));
    return {
      status: el ? el.innerText.replace(/\s+/g, " ").trim().slice(0, 70) : "",
      tabs: tabs.length,
      overlay: Array.from(document.querySelectorAll(".connection-overlay"))
        .filter(o => !o.classList.contains("hidden")).length,
    };
  });
  const before = await session();
  console.log("     session before: " + JSON.stringify(before));
  check("the session stands", before.overlay, 0);

  // --- Set up the listening post
  await page.evaluate(() => {
    window.addEventListener("beforeunload", () => console.log("BEFOREUNLOAD"));
    window.addEventListener("pagehide", () => console.log("PAGEHIDE"));
  });

  // --- Open a place as a second tab and put something into it
  const token = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const t = text.match(/const csrfToken = "([^"]+)"/);
    return t ? t[1] : "";
  });
  const placeId = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const hits = list.find(s => (s.areaKind || "") === "global") || list.find(s => (s.protocol || "") === "LOCAL");
    return hits ? hits.id : null;
  });
  check("a place was found", typeof placeId === "string", true);

  await page.evaluate(async ([id, token]) => {
    const data = new FormData();
    data.append("path", "/");
    data.append("file", new Blob(["content for the download"], { type: "text/plain" }), "tunnel-check.txt");
    await fetch("/api/files/" + id + "/upload", {
      method: "POST", body: data, credentials: "same-origin",
      headers: { "X-Matgate-Csrf": token },
    });
  }, [placeId, token]);

  // Opened through the UI: openServer lives in a closed scope.
  const add = page.locator('[data-tab-kind="add"]').first();
  if (await add.count()) { await add.evaluate(el => (el.querySelector(".session-tab-main") || el).click()); await sleep(1500); }
  await page.locator('[data-server-id="' + placeId + '"]').first().click();
  await sleep(3000);

  // --- And now the download, straight from the row
  const button = page.locator(".file-row-actions button").filter({ hasText: /Download|Herunterladen/ }).first();
  check("download button is there", await button.count() > 0, true);
  const download = page.waitForEvent("download", { timeout: 15000 }).catch(() => null);
  await button.click();
  const file = await download;
  console.log("     Download: " + (file ? await file.suggestedFilename() : "none"));
  await sleep(4000);

  const afterwards = await page.evaluate(() => {
    const tabs = Array.from(document.querySelectorAll("#session-tabs .session-tab"));
    return {
      tabs: tabs.length,
      overlays: Array.from(document.querySelectorAll(".connection-overlay"))
        .filter(o => !o.classList.contains("hidden"))
        .map(o => (o.innerText || "").replace(/\s+/g, " ").trim().slice(0, 80)),
    };
  });
  console.log("     afterwards: " + JSON.stringify(afterwards));
  check("no connection was dropped", afterwards.overlays.length, 0);

  // Back to the session and see whether the tunnel really is still up.
  await page.evaluate(() => {
    const tabs = Array.from(document.querySelectorAll("#session-tabs .session-tab"))
      .find(t => (t.textContent || "").includes("Linux-Testhost"));
    if (tabs) { (tabs.querySelector(".session-tab-main") || tabs).click(); }
  });
  await sleep(2000);
  const after = await session();
  console.log("     session afterwards: " + JSON.stringify(after));
  check("the tunnel is still open", /Tunnel: Open/.test(after.status), true);
  check("the session still reports connected", /Connected|Verbunden/.test(after.status), true);

  await page.screenshot({ path: "download-keeps-tunnel.png", fullPage: false });
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
