// The user page after the rebuild: does it still save, and is the button inside the form?
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NEU = "UmbauTester";
const PW = "test-only-pw-1234";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1360, height: 1000 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1600);

  // An earlier run that was aborted may have left this user behind - then creating it again is
  // refused as a duplicate, and the test would fail on that instead of on what it tests.
  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  await page.evaluate(async name => {
    const row = Array.from(document.querySelectorAll("tr")).find(r => ((r.querySelector("td") || {}).innerText || "").trim() === name);
    const form = row && row.querySelector("form[action*='/delete']");
    if (form) {
      await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
    }
  }, NEU);

  // Create a user that can be fiddled with safely. Waiting for the page that follows the form, not
  // for the clock: under load the redirect arrived after a fixed pause, the next read ran into the
  // navigation, and the page it was reading from was gone ("Execution context was destroyed").
  await page.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", NEU);
  await page.fill("input[name=\"password\"]", PW);
  await Promise.all([
    page.waitForNavigation({ waitUntil: "load", timeout: 20000 }),
    page.evaluate(() => { window.setTimeout(() => document.querySelector("input[name='username']").form.requestSubmit(), 0); }),
  ]);
  await sleep(500);

  const id = await page.evaluate((n) => {
    const row = Array.from(document.querySelectorAll("tr")).find(r => r.innerText.includes(n));
    const a = row && row.querySelector("a[href*='/admin/users/']");
    return a ? a.getAttribute("href").split("/").pop() : null;
  }, NEU);
  check("user created and found", typeof id === "string", true);

  await page.goto(BASE + "/admin/users/" + id, { waitUntil: "networkidle" });
  await sleep(700);
  const layout = await page.evaluate(() => {
    const form = document.querySelector("form[action$='/update']");
    const button = form ? form.querySelector("button[type='submit']") : null;
    return {
      groups: form ? Array.from(form.querySelectorAll("legend")).map(l => l.textContent.trim()) : [],
      buttonInForm: !!button,
      orphanButtons: Array.from(document.querySelectorAll("body > .actions, .stack > .actions")).length,
    };
  });
  console.log("     Aufbau: " + JSON.stringify(layout));
  check("three labelled groups", layout.groups.length, 3);
  check("save sits inside the form", layout.buttonInForm, true);
  await page.screenshot({ path: "user-1-neu.png", fullPage: true });

  // --- Does it still save? Flip two checkboxes, change a name
  await page.evaluate(() => {
    const form = document.querySelector("form[action$='/update']");
    form.querySelector("[name='displayName']").value = "Umgebaut";
    form.querySelector("[name='canQuickConnect']").checked = true;
    form.querySelector("[name='fileSharePersonal']").checked = true;
    form.requestSubmit();
  });
  await sleep(1800);

  await page.goto(BASE + "/admin/users/" + id, { waitUntil: "networkidle" });
  await sleep(700);
  const after = await page.evaluate(() => {
    const form = document.querySelector("form[action$='/update']");
    return {
      name: form.querySelector("[name='displayName']").value,
      quick: form.querySelector("[name='canQuickConnect']").checked,
      own: form.querySelector("[name='fileSharePersonal']").checked,
      global: form.querySelector("[name='fileShareGlobal']").checked,
      active: form.querySelector("[name='isEnabled']").checked,
    };
  });
  console.log("     saved: " + JSON.stringify(after));
  check("Name saved", after.name, "Umgebaut");
  check("Quick-Connect saved", after.quick, true);
  check("own folder saved", after.own, true);
  check("what was not ticked stays off", after.global, false);
  check("active stays active", after.active, true);

  // --- And off again, so nothing is left behind
  await page.evaluate(() => {
    const form = document.querySelector("form[action$='/update']");
    form.querySelector("[name='canQuickConnect']").checked = false;
    form.requestSubmit();
  });
  await sleep(1500);
  await page.goto(BASE + "/admin/users/" + id, { waitUntil: "networkidle" });
  await sleep(600);
  const off = await page.evaluate(() =>
    document.querySelector("form[action$='/update'] [name='canQuickConnect']").checked);
  check("unticking works too", off, false);

  // --- Clean up
  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  await page.evaluate(n => {
    const row = Array.from(document.querySelectorAll("tr")).find(r => r.innerText.includes(n));
    const form = row && row.querySelector("form[action*='/delete']");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  }, "Umgebaut");
  await sleep(1300);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
