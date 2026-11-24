// Administration -> Browser farm (0.51.0), against a small stand-in for the farm that this script runs itself
// (the test stack reaches it as host.docker.internal):
//   - the tab is there without a farm too, "Off", with what the farm is
//   - "Check the connection" says why not (wrong token) and then that it works, before saving
//   - saved, the farm is on at once: places as tiles, the counts, the pool saved goes to the farm
//   - the token is stored, not shown again
//   - a farm that stops answering is shown as unreachable
//   - switched off again: "Off"
const http = require("http");
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const FARM_PORT = 18099;
const FARM_URL = "http://host.docker.internal:" + FARM_PORT;
const TOKEN = "farm-test-only-token";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

// The stand-in: four places, one in use; /configure is remembered; without the token: 401.
const configured = [];
const farm = http.createServer((req, res) => {
  if (req.headers["x-farm-token"] !== TOKEN) { res.writeHead(401); res.end("{}"); return; }
  let body = "";
  req.on("data", chunk => body += chunk);
  req.on("end", () => {
    if (req.url === "/configure") { configured.push(JSON.parse(body || "{}")); }
    const slots = [1, 2, 3, 4].map(n => ({ slot: n, port: 5900 + n, busy: n === 2, browser: n === 2 ? "chromium" : "", url: n === 2 ? "https://example.com" : "", geometry: "1280x800x24", ageSeconds: n === 2 ? 300 : 0 }));
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ poolSize: 4, vncBasePort: 5901, geometry: "1280x800x24", busy: 1, free: 3, slots }));
  });
});

(async () => {
  await new Promise(resolve => farm.listen(FARM_PORT, "0.0.0.0", resolve));
  const b = await chromium.launch({ headless: true });
  const p = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  p.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await p.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p.fill("input[name='username']", "admin");
  await p.fill("input[name='password']", "test-only-pw");
  await Promise.all([p.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), p.click(".login-submit")]);
  const card = "[data-bf-connection]";
  const switchOff = async () => {
    await p.goto(BASE + "/admin?tab=browser", { waitUntil: "networkidle" });
    await p.evaluate(async () => {
      const form = document.querySelector("[data-bf-connection]");
      const body = new FormData(form);
      body.delete("enabled");
      body.set("clearToken", "on");
      await fetch(form.action, { method: "POST", body, credentials: "same-origin" });
    });
  };
  const state = () => p.locator("[data-bf-state]").innerText();
  try {
    await switchOff();
    await p.goto(BASE + "/admin?tab=browser", { waitUntil: "networkidle" });
    check("the tab is there without a farm", await p.locator(".tab-link, a, button").filter({ hasText: /^Browser farm$/ }).count() >= 1, true);
    check("it says \"Off\" and what the farm is", [await state(), await p.locator("[data-bf-off]").count()], ["Off", 1]);

    await p.click(card + " .bf-switch");
    await p.fill(card + " input[name='baseUrl']", FARM_URL);
    await p.click("[data-bf-test]");
    await p.waitForFunction(() => document.querySelector("[data-bf-test-result]").textContent.length > 3, null, { timeout: 15000 });
    check("checked without the token: refused, and why", await p.locator("[data-bf-test-result]").innerText(), "The farm refuses: the token is wrong.");
    await p.fill(card + " input[name='token']", TOKEN);
    await p.click("[data-bf-test]");
    await p.waitForFunction(() => /Connected/.test(document.querySelector("[data-bf-test-result]").textContent), null, { timeout: 15000 }).catch(() => {});
    check("with the token: connected, with its places", await p.locator("[data-bf-test-result]").innerText(), "Connected - 4 slots, 1 in use, resolution 1280x800x24.");

    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click("[data-bf-save]")]);
    check("saved: connected at once", await state(), "Connected");
    check("the counts", await p.locator("[data-bf-stats] strong").allInnerTexts(), ["4", "1", "3", "1280x800x24"]);
    check("a tile per place, one in use", [await p.locator(".bf-slot").count(), await p.locator(".bf-slot.is-busy").count()], [4, 1]);
    check("the token is not shown again", [await p.locator(card + " input[name='token']").inputValue(), await p.locator(card + " input[name='token']").getAttribute("placeholder"), await p.locator(card + " input[name='clearToken']").count()], ["", "Stored - leave empty to keep it", 1]);
    check("no session running", await p.locator("[data-bf-active] .acc-empty").count(), 1);

    configured.length = 0;
    await p.fill("[data-bf-pool-form] input[name='poolSize']", "6");
    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click("[data-bf-pool-save]")]);
    check("the pool saved goes to the farm", configured.map(c => c.poolSize), [6]);

    // ---- a phone: nothing wider than the screen
    const phone = await (await b.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, storageState: await p.context().storageState() })).newPage();
    await phone.goto(BASE + "/admin?tab=browser", { waitUntil: "networkidle" });
    check("phone: nothing wider than the screen", await phone.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    await phone.context().close();

    // ---- the farm stops answering
    await new Promise(resolve => farm.close(resolve));
    await p.goto(BASE + "/admin?tab=browser", { waitUntil: "networkidle" });
    check("a farm that does not answer is unreachable", [await state(), await p.locator("[data-bf-unreachable]").count()], ["Unreachable", 1]);
  }
  finally {
    await switchOff();
    await p.goto(BASE + "/admin?tab=browser", { waitUntil: "networkidle" });
    check("switched off again", await state(), "Off");
    await b.close();
    farm.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
