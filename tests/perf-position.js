// The performance numbers in the picture (0.43.1) stand in a corner of one's choosing: the display dialog offers
// the four corners once the numbers are on, and the choice is kept on this device.
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
  const page = await (await b.newContext({ viewport: { width: 1300, height: 860 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name='username']", "admin");
  await page.fill("input[name='password']", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString())), page.click(".login-submit")]);
  await sleep(800);
  const servers = async () => JSON.parse((await page.evaluate(async () => (await (await fetch("/", { credentials: "same-origin" })).text()))).match(/const availableServers = (\[.*?\]);/s)[1]);
  let rdp = (await servers()).find(s => s.name === "Perf-RDP");
  if (!rdp) {
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(300);
    await page.evaluate(() => {
      document.querySelector("[data-protocol-option='Rdp']").click();
      const set = (field, value) => { const el = [...document.querySelectorAll("[name='" + field + "']")].find(e => !e.disabled); if (el) { el.value = value; el.dispatchEvent(new Event("input", { bubbles: true })); el.dispatchEvent(new Event("change", { bubbles: true })); } };
      set("name", "Perf-RDP"); set("host", "desktop"); set("port", "3389"); set("targetUserName", "demo"); set("targetPassword", "demo-only-pw"); set("keyboardLayout", "en-us-qwerty"); set("engine", "");
      document.querySelector("[name='name']").form.requestSubmit();
    }).catch(() => { });
    await sleep(1500);
    rdp = (await servers()).find(s => s.name === "Perf-RDP");
  }
  try {
    await page.goto(BASE + "/?open=" + rdp.id, { waitUntil: "networkidle" });
    let screen = null;
    for (let i = 0; i < 60; i++) {
      screen = page.frames().find(f => /\/display\/.+\/screen/.test(f.url()));
      if (screen && (await screen.evaluate(() => window.__screen ? window.__screen.frames : 0).catch(() => 0)) >= 2) { break; }
      await sleep(500);
    }
    await screen.evaluate(() => localStorage.removeItem("matgate.screen.perfAt"));
    const openDialog = async () => {
      await page.evaluate(() => document.querySelector("#connection-tab-actions [data-action='resolution']").click());
      await sleep(500);
    };
    const perfToggle = () => page.locator("[data-perf]").first();
    await openDialog();
    if ((await perfToggle().getAttribute("data-perf")) !== "1") { await perfToggle().click(); await sleep(400); }
    const choices = await page.evaluate(() => [...document.querySelectorAll(".resolution-seg button, .resolution-choices button")].map(x => x.textContent.trim()).filter(t => /links|rechts|left|right/i.test(t)));
    check("with the numbers on, the dialog offers the four corners", choices.length, 4);
    const corner = () => screen.evaluate(() => {
      const box = document.getElementById("perf").getBoundingClientRect();
      return [box.left < innerWidth / 2 ? "left" : "right", box.top < innerHeight / 2 ? "top" : "bottom"].join("-");
    });
    check("they start top left", await corner(), "left-top");
    await page.locator(".resolution-seg button, .resolution-choices button").filter({ hasText: /Unten rechts|Bottom right/ }).first().click();
    await sleep(500);
    check("chosen bottom right, they stand there", await corner(), "right-bottom");
    check("and the choice is kept on this device", await screen.evaluate(() => localStorage.getItem("matgate.screen.perfAt")), "bottom-right");
    await screen.evaluate(() => { window.MatgateScreen.setPerfPosition("top-left"); window.MatgateScreen.setPerf(false); });
  }
  finally {
    await page.goto(BASE + "/admin?tab=servers", { waitUntil: "networkidle" });
    await page.evaluate(() => { const row = [...document.querySelectorAll("tr")].find(r => r.innerText.includes("Perf-RDP")); const form = row && row.querySelector("form[action*='/delete']"); if (form) { form.removeAttribute("data-confirm"); form.submit(); } });
    await sleep(800);
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
