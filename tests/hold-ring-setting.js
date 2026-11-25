// The right click with a finger (0.54.0): how long the finger waits - short, medium, long - and whether the circle
// that fills under it is shown at all (on unless switched off). Both in the settings, both reach the session page.
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
  const b = await chromium.launch({ headless: true });
  try {
    const page = await (await b.newContext({ viewport: { width: 1280, height: 860 } })).newPage();
    page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name='username']", "admin");
    await page.fill("input[name='password']", "test-only-pw");
    await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);

    // A session page to look at: the agent screen agent-screen.js sets up (the same page as RDP and VNC).
    const servers = await page.evaluate(async () => {
      const text = await (await fetch("/", { credentials: "same-origin" })).text();
      return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    });
    const target = servers.find(s => s.name === "Test-Bildschirm") || servers.find(s => s.name === "Test-Agent");
    const sessionPage = async () => page.evaluate(async id => {
      const html = await (await fetch("/agent/" + id + "/screen", { credentials: "same-origin" })).text();
      const body = new DOMParser().parseFromString(html, "text/html").body;
      return [body.dataset.holdMs, body.dataset.holdRing];
    }, target.id);

    const settings = async () => {
      await page.goto(BASE + "/account?tab=session", { waitUntil: "networkidle" });
      await sleep(300);
    };
    const save = () => Promise.all([page.waitForNavigation({ waitUntil: "networkidle" }), page.evaluate(() => document.getElementById("set-form-session").requestSubmit())]);
    await settings();
    check("the wait is short, medium or long", await page.evaluate(() => [...document.querySelectorAll("#set-long-press option")].map(o => [o.value, o.textContent.split(" ")[0]])),
      await page.evaluate(() => document.documentElement.lang === "de") ? [["400", "Kurz"], ["550", "Mittel"], ["850", "Lang"]] : [["400", "Short"], ["550", "Medium"], ["850", "Long"]]);
    check("the circle is on by default", await page.evaluate(() => document.querySelector("#set-form-session [name='holdRing']").checked), true);
    check("a session page shows it, with the medium wait", target ? await sessionPage() : null, ["550", "on"]);

    await page.selectOption("#set-long-press", "850");
    await page.evaluate(() => { const box = document.querySelector("#set-form-session [name='holdRing']"); box.checked = false; box.dispatchEvent(new Event("change", { bubbles: true })); });
    await save();
    await settings();
    check("saved: long, circle off", await page.evaluate(() => [document.getElementById("set-long-press").value, document.querySelector("#set-form-session [name='holdRing']").checked]), ["850", false]);
    check("the session page waits long and hides the circle", await sessionPage(), ["850", "off"]);
    // The page hides the ring by its own rule.
    if (target) {
      const view = await page.context().newPage();
      await view.route("**/agent/*/screen*", async route => {
        const response = await route.fetch();
        await route.fulfill({ response, body: (await response.text()).replace(/<script[\s\S]*?<\/script>/g, "") });
      });
      await view.goto(BASE + "/agent/" + target.id + "/screen", { waitUntil: "domcontentloaded" });
      check("its circle is not drawn", await view.evaluate(() => {
        const ring = document.createElement("div");
        ring.className = "hold-ring show";
        document.body.appendChild(ring);
        return getComputedStyle(ring).display;
      }), "none");
      await view.close();
    }

    // Back as it was.
    await page.selectOption("#set-long-press", "550");
    await page.evaluate(() => { const box = document.querySelector("#set-form-session [name='holdRing']"); box.checked = true; box.dispatchEvent(new Event("change", { bubbles: true })); });
    await save();
    check("switched on again", await sessionPage(), ["550", "on"]);
  }
  finally {
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
