// The settings page (/account): six sections in two groups. On a wide screen a rail lists them beside the chosen
// one; on a phone the list is a page of its own and a section opens as a view of its own, with a way back at its top
// and its end. Each form ends in a save bar that says when something is not saved yet and can throw it away;
// saving says so in the section, and saving one section while another has unsaved changes asks first. Old
// addresses still land where they belong: ?tab=favorites on the start page's favorites, a #place in another section
// on that section.
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
  const desk = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await desk.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);
  const open = async address => { await page.goto(BASE + address, { waitUntil: "networkidle" }); await sleep(300); };
  const shown = () => page.evaluate(() => [...document.querySelectorAll(".set-panel")].filter(p => p.offsetParent).map(p => p.dataset.tabPanel));

  // ---- the rail
  await open("/account");
  check("six sections in two groups, the profile chosen, under the title \"Settings\"", await page.evaluate(() => ({
    groups: [...document.querySelectorAll(".set-nav-group")].map(g => [...g.querySelectorAll(".set-nav-item[data-tab-target]")].map(a => a.dataset.tabTarget)),
    active: document.querySelector(".set-nav-item.active").dataset.tabTarget,
    current: document.querySelector(".set-nav-item[aria-current='page']").dataset.tabTarget,
    title: document.querySelector("h1").textContent.trim(),
    avatar: !!document.querySelector(".set-aside .ui-avatar") && !document.querySelector(".set-aside .brand-mark"),
  })), { groups: [["profile", "appearance", "security"], ["home", "session", "keys"]], active: "profile", current: "profile", title: "Settings", avatar: true });
  check("only the profile shows, as a section with its title", [await shown(), await page.evaluate(() => document.querySelector("[data-tab-panel='profile'] .set-section-title").textContent.trim())], [["profile"], "Profile"]);
  check("every section is in the page, the others hidden", await page.evaluate(() => [...document.querySelectorAll("[data-tab-panel]")].map(p => p.dataset.tabPanel + (p.classList.contains("hidden") ? "-" : ""))),
    ["profile", "appearance-", "security-", "home-", "session-", "keys-"]);
  await page.click(".set-nav-item[data-tab-target='security']");
  await sleep(300);
  check("a click on Security shows it, with its address", [await shown(), new URL(page.url()).search, await page.evaluate(() => document.querySelector(".set-nav-item[aria-current='page']").dataset.tabTarget)], [["security"], "?tab=security", "security"]);

  // ---- old and other addresses
  await open("/account?tab=favorites");
  check("?tab=favorites opens the start page with the favorites in view", await page.evaluate(() => {
    const card = document.getElementById("favorites").getBoundingClientRect();
    return [[...document.querySelectorAll(".set-panel")].filter(p => p.offsetParent).map(p => p.dataset.tabPanel)[0], card.top >= 0 && card.top < innerHeight];
  }), ["home", true]);
  await open("/account?tab=session#quick-order-list");
  check("a place in another section opens that section, the place kept in the address", await page.evaluate(() => {
    const list = document.getElementById("quick-order-list").getBoundingClientRect();
    return [[...document.querySelectorAll(".set-panel")].filter(p => p.offsetParent).map(p => p.dataset.tabPanel)[0], location.search + location.hash, list.top >= 0 && list.top < innerHeight];
  }), ["home", "?tab=home#quick-order-list", true]);

  // ---- unsaved, discarded (inside a frame-like address: embed stays)
  await open("/account?tab=profile&embed=1");
  const name = await page.inputValue("[name='displayName']");
  await page.fill("[name='displayName']", name + " (changed)");
  await sleep(400);
  check("a change marks the form, says so in the save bar and dots the section", await page.evaluate(() => {
    const form = document.getElementById("set-form-profile");
    return [form.hasAttribute("data-dirty"), form.querySelector(".set-savebar-status").textContent.trim().length > 0, !form.querySelector("[data-settings-discard]").disabled,
      document.querySelector(".set-nav-item[data-tab-target='profile']").classList.contains("is-dirty")];
  }), [true, true, true, true]);
  await page.fill("[name='displayName']", name);
  await sleep(400);
  check("changed back, it is clean again", await page.evaluate(() => document.getElementById("set-form-profile").hasAttribute("data-dirty")), false);
  await page.fill("[name='displayName']", name + " (changed)");
  await sleep(400);
  await Promise.all([page.waitForNavigation({ waitUntil: "networkidle" }), page.click("#set-form-profile [data-settings-discard]")]);
  await sleep(300);
  check("Discard brings back what is saved, in the same section, embed kept", [await page.inputValue("[name='displayName']"), new URL(page.url()).searchParams.get("tab"), new URL(page.url()).searchParams.get("embed")], [name, "profile", "1"]);

  // ---- saved
  await open("/account?tab=profile");
  await Promise.all([page.waitForNavigation({ waitUntil: "networkidle" }), page.evaluate(() => document.getElementById("set-form-profile").requestSubmit())]);
  await sleep(800);
  check("saved: the section says so, and the address forgets it", await page.evaluate(() => [
    !!document.querySelector("[data-tab-panel='profile'] .set-notice--success")?.offsetParent, location.search,
    [...document.querySelectorAll(".set-panel")].filter(p => p.offsetParent).map(p => p.dataset.tabPanel)[0],
  ]), [true, "?tab=profile", "profile"]);

  // ---- saving one section while another has unsaved changes asks first
  await open("/account?tab=profile");
  await page.evaluate(() => {
    const box = document.querySelector("#set-form-session [name='stretchToWindow']");
    box.checked = !box.checked;
    box.dispatchEvent(new Event("change", { bubbles: true }));
  });
  await sleep(400);
  await page.evaluate(() => document.getElementById("set-form-profile").requestSubmit());
  await sleep(500);
  check("saving the profile while the sessions have changes asks first", await page.evaluate(() => [
    !!document.querySelector("#confirm-dialog:not(.hidden)"), document.querySelector("#confirm-dialog [data-confirm-text]").textContent.includes("Sessions"),
  ]), [true, true]);
  await page.click("#confirm-dialog [data-confirm-cancel]");
  await sleep(500);
  check("no: nothing is sent, the page and the change stay", await page.evaluate(() => [
    !!document.querySelector("#confirm-dialog:not(.hidden)"), location.search, document.getElementById("set-form-session").hasAttribute("data-dirty"),
  ]), [false, "?tab=profile", true]);

  // ---- a phone
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, storageState: await desk.storageState() });
  const small = await phone.newPage();
  small.on("pageerror", e => { console.log("JS ERROR (phone): " + e.message); failures.push("JS error"); });
  await small.goto(BASE + "/account", { waitUntil: "networkidle" });
  await sleep(400);
  check("on a phone /account is the list of sections: six cards a finger can hit, no section, nothing sideways", await small.evaluate(() => {
    const items = [...document.querySelectorAll(".set-nav-item")];
    return [items.length, items.every(i => i.getBoundingClientRect().height >= 56), [...document.querySelectorAll(".set-panel")].some(p => p.offsetParent),
      document.documentElement.scrollWidth <= innerWidth + 1, items.every(i => i.querySelector(".set-nav-summary").offsetParent !== null)];
  }), [6, true, false, true, true]);
  await small.tap(".set-nav-item[data-tab-target='session']");
  await sleep(500);
  check("a tap on Sessions opens it: its title has the focus, the way back is at the top", await small.evaluate(() => [
    document.querySelector(".set-layout").dataset.settingsView, document.activeElement.classList.contains("set-section-title"),
    document.activeElement.closest("[data-tab-panel]")?.dataset.tabPanel, !!document.querySelector("[data-tab-panel='session'] .set-back:not(.set-back--end)").offsetParent,
    !!document.querySelector(".set-aside").offsetParent,
  ]), ["section", true, "session", true, false]);
  await small.tap("[data-tab-panel='session'] .set-back:not(.set-back--end)");
  await sleep(500);
  check("back: the list again, the focus on the section just left", await small.evaluate(() => [
    document.querySelector(".set-layout").dataset.settingsView, document.activeElement.dataset.tabTarget || "", location.search,
  ]), ["index", "session", ""]);
  await small.goto(BASE + "/account?tab=keys", { waitUntil: "networkidle" });
  await sleep(300);
  check("?tab=keys opens the key bar directly, with a way back at its end as well", await small.evaluate(() => [
    document.querySelector(".set-layout").dataset.settingsView, [...document.querySelectorAll(".set-panel")].filter(p => p.offsetParent).map(p => p.dataset.tabPanel),
    !!document.querySelector("[data-tab-panel='keys'] .set-back--end")?.offsetParent, document.documentElement.scrollWidth <= innerWidth + 1,
  ]), ["section", ["keys"], true, true]);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
