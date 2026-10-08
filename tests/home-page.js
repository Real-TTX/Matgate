// The home page can be configured: sections off, a different order, a default for new users.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
// Which sections are on the home page, and in what order?
const sections = page => page.evaluate(() => {
  const home = document.querySelector("[data-home2]");
  if (!home) return ["no home page"];
  return Array.from(home.children).map(el => {
    if (el.classList.contains("home2-head")) return "head";
    if (el.classList.contains("home2-search")) return "search";
    if (el.classList.contains("home2-quick-section")) return "quick";
    if (el.classList.contains("home2-folders-section")) return "folders";
    if (el.hasAttribute("data-home2-recent")) return "recent";
    if (el.hasAttribute("data-home2-all")) return "connections";
    if (el.hasAttribute("data-home2-places")) return "places";
    if (el.hasAttribute("data-home2-workspaces")) return "workspaces";
    if (el.classList.contains("home2-farm-section")) return "farm";
    if (el.hasAttribute("data-home2-noresults")) return "noresults";
    return null;
  }).filter(Boolean);
});
const signIn = async (page, name, pw) => {
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", name);
  await page.fill("input[name=\"password\"]", pw);
  await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), page.click(".login-submit")]);
  await sleep(1600);
};
const toHomePage = async (page) => {
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(600);
  const add = page.locator("[data-tab-kind=\"add\"]").first();
  if (await add.count()) { await add.evaluate(el => (el.querySelector(".session-tab-main") || el).click()); await sleep(1200); }
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1280, height: 950 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await signIn(page, "admin", "test-only-pw");

  // --- A workspace, so the new section has something to show
  await page.goto(BASE + "/workspaces/new", { waitUntil: "networkidle" });
  const nameField = page.locator("input[name=\"name\"]").first();
  if (await nameField.count()) {
    await nameField.fill("Testablage");
    const description = page.locator("[name=\"description\"]").first();
    if (await description.count()) { await description.fill("test only"); }
    await page.evaluate(() => document.querySelector("input[name='name']").form.requestSubmit());
    await sleep(1800);
  }

  // --- Is the tab there?
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(700);
  const entries = await page.evaluate(() =>
    Array.from(document.querySelectorAll("#home-order-list .action-order-item")).map(i => i.dataset.orderKey));
  // The order is a setting - what matters is that all eight are there.
  check("eight sections to choose from", [...entries].sort(),
    ["connections", "farm", "folders", "places", "quick", "recent", "search", "workspaces"]);
  await page.screenshot({ path: "home-1-tab.png" });

  // --- Starting point on the home page
  await toHomePage(page);
  const before = await sections(page);
  console.log("     home page before: " + JSON.stringify(before));
  check("Workspaces erscheinen", before.includes("workspaces"), true);
  await page.screenshot({ path: "home-2-before.png", fullPage: true });

  // --- Switch two sections off and turn the order around
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(700);
  await page.evaluate(() => {
    const list = document.getElementById("home-order-list");
    const find = k => Array.from(list.children).find(i => i.dataset.orderKey === k);
    // "folders" and "places" off
    ["folders", "places"].forEach(k => {
      const box = find(k).querySelector("[data-order-visible]");
      box.checked = false;
      box.dispatchEvent(new Event("change", { bubbles: true }));
    });
    // "workspaces" right to the front
    list.insertBefore(find("workspaces"), list.firstElementChild);
    list.dispatchEvent(new Event("change", { bubbles: true }));
  });
  await sleep(300);
  const sent = await page.evaluate(() => ({
    order: document.querySelector("[name='homeSections']").value,
    off: document.querySelector("[name='hiddenHomeSections']").value,
  }));
  console.log("     being sent: " + JSON.stringify(sent));
  await page.evaluate(() => document.querySelector("[name='homeSections']").form.requestSubmit());
  await sleep(1800);

  await toHomePage(page);
  const afterwards = await sections(page);
  console.log("     home page afterwards: " + JSON.stringify(afterwards));
  check("folders and places are gone", afterwards.includes("folders") || afterwards.includes("places"), false);
  check("workspaces are at the front now", afterwards[1], "workspaces");
  await page.screenshot({ path: "home-3-afterwards.png", fullPage: true });

  // --- Take it as the default and create a new user
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(700);
  const asDefault = page.locator("[name='asDefault']");
  check("an administrator sees the default switch", await asDefault.count(), 1);
  await page.evaluate(() => {
    document.querySelector("[name='asDefault']").checked = true;
    document.querySelector("[name='homeSections']").form.requestSubmit();
  });
  await sleep(1800);

  const NEU = "VorgabeTester";
  const PW = "test-only-pw-1234";
  await page.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", NEU);
  await page.fill("input[name=\"password\"]", PW);
  await page.evaluate(() => document.querySelector("input[name='username']").form.requestSubmit());
  await sleep(1800);

  const ctx2 = await b.newContext({ viewport: { width: 1280, height: 950 } });
  const p2 = await ctx2.newPage();
  await signIn(p2, NEU, PW);
  await p2.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(700);
  const inherited = await p2.evaluate(() => ({
    order: Array.from(document.querySelectorAll("#home-order-list .action-order-item")).map(i => i.dataset.orderKey),
    off: Array.from(document.querySelectorAll("#home-order-list .action-order-item"))
      .filter(i => !i.querySelector("[data-order-visible]").checked).map(i => i.dataset.orderKey),
  }));
  console.log("     new user inherits: " + JSON.stringify(inherited));
  check("the new user inherits the order", inherited.order[0], "workspaces");
  check("the new user inherits what is hidden", inherited.off.sort(), ["folders", "places"]);
  check("the new user sees no default switch", await p2.locator("[name='asDefault']").count(), 0);
  await ctx2.close();

  // --- Clean up: user gone, own arrangement back
  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  await page.evaluate(n => {
    const row = Array.from(document.querySelectorAll("tr")).find(r => r.innerText.includes(n));
    const form = row && row.querySelector("form[action*='/delete']");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  }, NEU);
  await sleep(1200);

  // The share made at the start, so the section had something to show. It was never removed, so
  // every run left one behind.
  await page.evaluate(async name => {
    const parse = html => new DOMParser().parseFromString(html, "text/html");
    const fetchText = async url => (await fetch(url, { credentials: "same-origin" })).text();
    const list = parse(await fetchText("/workspaces"));
    const ids = new Set();
    for (const link of list.querySelectorAll("tr td:first-child a[href^='/workspaces/']")) {
      if ((link.textContent || "").trim() === name) { ids.add(link.getAttribute("href").split("/").pop()); }
    }
    for (const id of ids) {
      const form = parse(await fetchText("/workspaces/" + id)).querySelector("form[action$='/delete']");
      if (form) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  }, "Testablage");

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
