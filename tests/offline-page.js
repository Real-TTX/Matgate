// The page the installed app shows when the gateway cannot be reached. Two things are checked:
// that the page itself works - a retry button and a countdown that moves - and that the service
// worker actually serves it for a failed page load. A pretty page nobody ever sees would be no
// feature at all.
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

  // --- The page on its own, without signing in: it has to stand for someone who is locked out.
  const plain = await b.newContext({ viewport: { width: 760, height: 620 } });
  const page = await plain.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const response = await page.goto(BASE + "/offline", { waitUntil: "networkidle" });
  check("reachable without signing in", response.status(), 200);
  check("and cacheable, so the worker can keep it",
    /max-age/.test(response.headers()["cache-control"] || ""), true);
  await sleep(1500);
  const shown = await page.evaluate(() => ({
    title: (document.getElementById("title") || {}).textContent || "",
    button: (document.getElementById("retry") || {}).textContent || "",
    status: (document.getElementById("status") || {}).textContent || "",
    external: Array.from(document.querySelectorAll("link[href], script[src], img[src], iframe[src]"))
      .map(el => el.getAttribute("href") || el.getAttribute("src"))
      .filter(value => value && !value.startsWith("data:")),
  }));
  console.log("     page: " + JSON.stringify(shown));
  check("it names the problem", shown.title.length > 0, true);
  check("with a button to try again", shown.button.length > 0, true);
  check("and a countdown that says when", /\d/.test(shown.status), true);
  // Nothing may be fetched - this is the one page that has to work without the network.
  check("nothing is loaded from outside", shown.external, []);
  await page.screenshot({ path: "offline-page.png" });

  const moved = await page.evaluate(async () => {
    const before = document.getElementById("status").textContent;
    await new Promise(r => setTimeout(r, 2200));
    return { before, after: document.getElementById("status").textContent };
  });
  console.log("     countdown: " + JSON.stringify(moved));
  check("the countdown runs", moved.before !== moved.after, true);
  await plain.close();

  // --- And the real thing: signed in, worker installed, then the gateway disappears.
  const ctx = await b.newContext({ viewport: { width: 760, height: 620 } });
  const live = await ctx.newPage();
  await live.goto(BASE + "/login", { waitUntil: "networkidle" });
  await live.fill("input[name=\"username\"]", "admin");
  await live.fill("input[name=\"password\"]", "test-only-pw");
  await live.click(".login-submit");
  await sleep(2500);
  const worker = await live.evaluate(async () => {
    const registration = await navigator.serviceWorker.ready;
    const cache = await caches.open("matgate-offline-v1");
    return { active: !!registration.active, cached: !!(await cache.match("/offline")) };
  });
  console.log("     worker: " + JSON.stringify(worker));
  check("the service worker is active", worker.active, true);
  check("and keeps the offline page", worker.cached, true);

  await ctx.route("**/*", route => route.abort());
  await live.goto(BASE + "/", { waitUntil: "domcontentloaded" }).catch(() => {});
  await sleep(1200);
  const offline = await live.evaluate(() => ({
    isOfflinePage: !!document.getElementById("retry"),
    url: location.pathname,
  }));
  console.log("     while offline: " + JSON.stringify(offline));
  check("a failed page load shows the offline page", offline.isOfflinePage, true);
  // The address stays the one that was wanted, so a reload goes back exactly there.
  check("and the address is kept", offline.url, "/");
  await live.screenshot({ path: "offline-served.png" });
  await ctx.close();

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
