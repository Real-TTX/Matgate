// The browser of quick connect where Matgate has no host names for websites - the way it runs where nobody
// set them up (the second Matgate of the test stack, port 18092). It is Matgate's own browser there as well:
// the page is fetched by Matgate and shown in the tab, under a path of Matgate's address - never the browser
// farm's picture of one. Under a path a tab shows one website, so the browser asks for the address to start
// with; an empty one is refused with words that say so.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18092";
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
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await context.newPage();
  const dialogs = [];
  page.on("dialog", async d => { dialogs.push(d.type()); await d.dismiss(); });
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const chips = await page.evaluate(() => [...document.querySelectorAll(".home2-quick-row [data-home2-qc]")].map(chip => chip.dataset.home2Qc + ":" + chip.textContent.trim()));
  check("the browser is one of the chips - where the website was, not in front", [chips.indexOf("website:Browser"), chips.some(chip => /^browser:/.test(chip)), chips[0]], [5, false, "rdp:RDP"]);

  await page.click(".home2-quick-row [data-home2-qc='website']");
  await page.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  const asked = () => page.evaluate(() => ({
    label: document.querySelector("[data-home2-qc-hostlabel]").textContent,
    required: document.querySelector("[data-home2-qc-host]").required,
    focused: document.activeElement === document.querySelector("[data-home2-qc-host]"),
    button: document.querySelector("[data-home2-qc-submit]").textContent,
    fields: [...document.querySelectorAll("[data-home2-qc-form] input:not([type='hidden'])")].filter(field => field.offsetParent !== null).map(field => field.name),
  }));
  check("the chip asks for the address to start with", await asked(), { label: "Start URL", required: true, focused: true, button: "Open browser", fields: ["host", "username", "password"] });
  check("an example instead of a promise of an empty browser", await page.evaluate(() => /empty/i.test(document.querySelector("[data-home2-qc-host]").placeholder)), false);
  await page.click("[data-home2-qc-proto='rdp']");
  check("another protocol has its own words again", [(await asked()).label, (await asked()).button], ["Host", "Connect"]);
  await page.click("[data-home2-qc-proto='website']");

  check("without an address the browser is refused, and it says why", await page.evaluate(async () => {
    const body = new FormData();
    body.append("protocol", "website");
    const token = (document.documentElement.innerHTML.match(/csrfToken = ["']([^"']+)["']/) || [])[1];
    const answer = await fetch("/api/quick-connect", { method: "POST", headers: { "X-Matgate-Csrf": token }, body });
    return [answer.status, (await answer.json()).error];
  }), [400, "Please enter the address the browser should start with."]);

  await page.fill("[data-home2-qc-host]", "http://site:8080/");
  await page.click("[data-home2-qc-form] button[type='submit']");
  await page.waitForSelector(".website-frame", { timeout: 20000 });
  const frame = async () => (await page.$(".website-frame")).contentFrame();
  let shown = "";
  for (let i = 0; i < 60 && shown !== "Fixture Home"; i++) {
    try { shown = await (await frame()).evaluate(() => (document.querySelector("#what") || {}).textContent || ""); } catch { /* loading */ }
    if (shown !== "Fixture Home") { await sleep(250); }
  }
  check("the address opens in Matgate's own browser: the page itself, under a path of Matgate's", [
    shown,
    /^http:\/\/127\.0\.0\.1:18092\/website\/[0-9a-f-]+\/[^/]+\/proxy\//.test((await frame()).url()),
    await page.evaluate(() => !!document.querySelector(".guac-display canvas")),
  ], ["Fixture Home", true, false]);
  check("the tab is named after the address", await page.evaluate(() => [...document.querySelectorAll(".session-tab .session-tab-title")].some(title => /site:8080/.test(title.textContent))), true);
  check("no dialog of the browser was involved", dialogs, []);

  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
