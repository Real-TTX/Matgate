// Does a new user keep their capitals - and can they still sign in with any spelling? Two
// names that differ only in case are the same user.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "TestGrossKlein";
const PW = "test-only-pw-1234";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1280, height: 900 } });
  const page = await ctx.newPage();
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), page.click(".login-submit")]);
  await sleep(1500);

  await page.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", NAME);
  await page.fill("input[name=\"password\"]", PW);
  await page.locator("button[type=\"submit\"]:visible").last().click();
  await sleep(1500);

  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  const list = await page.evaluate(() => document.body.innerText);
  check("the name keeps its capitals", list.includes("TestGrossKlein"), true);
  if (!list.includes("TestGrossKlein")) {
    console.log("     list: " + list.replace(/\s+/g, " ").slice(0, 300));
  }

  // Signing in with a different spelling
  const ctx2 = await b.newContext({ viewport: { width: 1280, height: 900 } });
  const p2 = await ctx2.newPage();
  await p2.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p2.fill("input[name=\"username\"]", NAME.toLowerCase());
  await p2.fill("input[name=\"password\"]", PW);
  await Promise.all([p2.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), p2.click(".login-submit")]);
  await sleep(1500);
  check("signing in all lower case works", !/\/login/.test(p2.url()), true);
  await ctx2.close();

  // And in CAPITALS
  const ctx3 = await b.newContext({ viewport: { width: 1280, height: 900 } });
  const p3 = await ctx3.newPage();
  await p3.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p3.fill("input[name=\"username\"]", NAME.toUpperCase());
  await p3.fill("input[name=\"password\"]", PW);
  await Promise.all([p3.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), p3.click(".login-submit")]);
  await sleep(1500);
  check("signing in all upper case works", !/\/login/.test(p3.url()), true);
  await ctx3.close();

  // Creating the same name again in another spelling has to be refused
  await page.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", NAME.toLowerCase());
  await page.fill("input[name=\"password\"]", PW);
  await page.locator("button[type=\"submit\"]:visible").last().click();
  await sleep(1200);
  const count = await (async () => {
    await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
    return page.evaluate(n => Array.from(document.querySelectorAll("tbody tr"))
      .filter(r => ((r.querySelector("td") || {}).innerText || "").trim().toLowerCase() === n.toLowerCase()).length, NAME);
  })();
  check("no second user with the same name", count, 1);

  // Clean up
  await page.evaluate(async (n) => {
    const rows = Array.from(document.querySelectorAll("tr"));
    const row = rows.find(r => r.innerText.toLowerCase().includes(n.toLowerCase()));
    const form = row && row.querySelector("form[action*=\"/delete\"]");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  }, NAME);
  await sleep(1200);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
