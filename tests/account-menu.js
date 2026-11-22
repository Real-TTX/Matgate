// The header's menus: the account menu (a button and its panel, no <details>), the main menu behind the burger -
// a dropdown in the compact view, a sheet over the whole screen on a phone - and light/dark/system chosen there:
// applied at once in the shell and its page tabs, saved in the background, and taken back when the save fails.
// Two sign-ins: the administrator, and a plain user who must not be offered the tools.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const signIn = async (page, user, password) => {
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", user);
  await page.fill("input[name=\"password\"]", password);
  await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);
};
// Saved in the background, as the menu does it: the theme alone, answered with 204.
const saveTheme = (page, mode) => page.evaluate(async mode => {
  const body = new FormData();
  body.append("_csrf", document.querySelector("input[name='_csrf']").value);
  body.append("preferredTheme", mode);
  const response = await fetch("/account", { method: "POST", body, credentials: "same-origin", redirect: "manual", headers: { "X-Matgate-Inline": "1" } });
  return response.status;
}, mode);

(async () => {
  const b = await chromium.launch({ headless: true });

  // ================= Desktop: the account menu
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await signIn(page, "admin", "test-only-pw");
  await saveTheme(page, "system");
  await page.reload({ waitUntil: "networkidle" });
  await sleep(800);

  const account = () => page.evaluate(() => {
    const trigger = document.querySelector(".account-menu [data-menu-toggle]");
    const panel = document.querySelector(".account-menu-panel");
    return trigger && panel ? [trigger.getAttribute("aria-expanded"), !panel.hidden] : "missing";
  });
  check("a button and its panel, closed - no <details> left in the header", [await account(), await page.evaluate(() => document.querySelectorAll("header details").length)], [["false", false], 0]);
  await page.click(".account-menu [data-menu-toggle]");
  await sleep(300);
  check("a click opens it", await account(), ["true", true]);
  const entries = await page.evaluate(() => [...document.querySelectorAll(".account-menu-panel [role='menuitem'], .account-menu-panel [role='menuitemradio'], .account-menu-panel .hm-foot a")]
    .map(entry => entry.hasAttribute("data-theme-choice") ? "theme:" + entry.getAttribute("data-theme-choice")
      : entry.closest(".hm-foot") ? "foot:" + entry.getAttribute("href")
      : (entry.getAttribute("href") || "") + ":" + entry.textContent.trim()));
  check("Settings, Credentials, light/dark/system, Sign out, About in the foot - in that order", entries.map(e => e.replace(/^(\/account|\/credentials):.*$/, "$1").replace(/^:(Sign out|Abmelden)$/, "logout")),
    ["/account", "/credentials", "theme:light", "theme:dark", "theme:system", "logout", "foot:/about"]);
  check("the user card is no entry: the first link is the settings, under the name and the role", await page.evaluate(() => {
    const card = document.querySelector(".account-menu-panel .ui-user-card");
    return [document.querySelector(".account-menu-panel a[href]").getAttribute("href"), !!card && !card.querySelector("a, button"), !!card && /·/.test(card.querySelector("small").textContent)];
  }), ["/account", true, true]);
  check("the button shows the initials, never a second .brand-mark", await page.evaluate(() => [!!document.querySelector(".account-menu [data-menu-toggle] .ui-avatar"), !!document.querySelector(".account-menu .brand-mark")]), [true, false]);

  // ---- keys: Escape closes and gives the focus back; Enter opens onto the first entry; the arrows move; Tab leaves
  await page.keyboard.press("Escape");
  await sleep(200);
  check("Escape closes it, the focus back on its button", [await account(), await page.evaluate(() => document.activeElement === document.querySelector(".account-menu [data-menu-toggle]"))], [["false", false], true]);
  await page.keyboard.press("Enter");
  await sleep(300);
  const focused = () => page.evaluate(() => (document.activeElement.getAttribute("data-theme-choice") ? "theme:" + document.activeElement.getAttribute("data-theme-choice") : document.activeElement.getAttribute("href") || document.activeElement.textContent.trim()));
  check("opened with Enter: the focus on its first entry", [await account(), await focused()], [["true", true], "/account"]);
  await page.keyboard.press("ArrowDown");
  const second = await focused();
  await page.keyboard.press("End");
  const last = await focused();
  await page.keyboard.press("Home");
  check("the arrows, End and Home go over the entries", [second, last, await focused()], ["/credentials", "/about", "/account"]);
  await page.keyboard.press("End");
  await page.keyboard.press("Tab");
  await sleep(300);
  check("Tab out of it closes it", await account(), ["false", false]);

  // ---- a click into a page tab closes it: the page never hears that press, the window loses its focus
  await page.evaluate(() => window.MatgateOpenShellTab("/about", "About"));
  await sleep(1800);
  await page.click(".account-menu [data-menu-toggle]");
  await sleep(300);
  check("open above a page tab", await account(), ["true", true]);
  await page.frameLocator(".shell-page-panel:not(.hidden) iframe").locator("body").click({ position: { x: 40, y: 260 } });
  await sleep(500);
  check("a click into the page tab closes it", await account(), ["false", false]);

  // ---- dark: at once, the menu stays open, nothing navigates; the page tab too; still dark after a reload
  const origin = await page.evaluate(() => performance.timeOrigin);
  await page.click(".account-menu [data-menu-toggle]");
  await sleep(300);
  await page.click(".account-menu-panel [data-theme-choice='dark']");
  await sleep(1500);
  check("Dark: applied at once, the menu still open, no page change", await page.evaluate(() => [
    document.documentElement.dataset.theme, !document.querySelector(".account-menu-panel").hidden,
    !!document.getElementById("matgate-shell"), location.pathname, performance.timeOrigin,
    [...document.querySelectorAll(".account-menu-panel [data-theme-choice]")].map(choice => choice.getAttribute("aria-checked"))]),
  ["dark", true, true, "/", origin, ["false", "true", "false"]]);
  const pageTab = page.frames().find(frame => /\/about/.test(frame.url()));
  check("the page tab follows without a reload", pageTab ? await pageTab.evaluate(() => document.documentElement.dataset.theme) : "no page tab", "dark");
  const bars = () => page.evaluate(() => [...document.querySelectorAll("meta[name='theme-color']")].map(bar => (/dark/.test(bar.dataset.media || "") ? "dark:" : "light:") + bar.getAttribute("media")));
  check("the system bar takes the dark colour, whatever the device says", await bars(), ["light:not all", "dark:all"]);
  await page.reload({ waitUntil: "networkidle" });
  await sleep(1000);
  check("still dark after a reload: it was saved", await page.evaluate(() => document.documentElement.dataset.theme), "dark");

  // ---- a save that is refused (here: the form token is wrong) goes back and says so
  await page.click(".account-menu [data-menu-toggle]");
  await sleep(300);
  await page.evaluate(() => { document.querySelector("header input[name='_csrf']").value = "x"; });
  await page.click(".account-menu-panel [data-theme-choice='light']");
  await sleep(1500);
  check("refused: back to dark, with the note under the switch", await page.evaluate(() => [
    document.documentElement.dataset.theme, !document.querySelector(".account-menu-panel .hm-theme-error").hidden, location.pathname]), ["dark", true, "/"]);
  await page.reload({ waitUntil: "networkidle" });
  await sleep(800);
  await page.click(".account-menu [data-menu-toggle]");
  await sleep(300);
  await page.click(".account-menu-panel [data-theme-choice='system']");
  await sleep(1500);
  check("back to the system's choice", await page.evaluate(() => document.documentElement.dataset.theme), "system");
  check("and the system bar to the device's", await bars(), ["light:(prefers-color-scheme: light)", "dark:(prefers-color-scheme: dark)"]);
  await page.keyboard.press("Escape");
  await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab[data-tab-kind='page'] .session-tab-close").forEach(x => x.click()));
  await sleep(500);
  check("in the shell the rule in front of the open tabs' button is there", await page.evaluate(() => document.querySelectorAll("header .shell-header-sep").length), 1);

  // ================= Desktop, compact view: the main menu drops down from the burger
  const view = await page.evaluate(() => document.documentElement.dataset.viewMode === "minimal" ? "minimal" : (window.MatgateSidePanel && window.MatgateSidePanel.isOpen() ? "sidebar" : "normal"));
  await page.evaluate(() => window.MatgateChooseView("minimal"));
  await sleep(700);
  await page.click(".shell-burger-trigger");
  await sleep(300);
  check("compact: a dropdown under the burger, right-aligned, not a sheet", await page.evaluate(() => {
    const panel = document.querySelector(".shell-burger-panel");
    const box = panel.getBoundingClientRect();
    const trigger = document.querySelector(".shell-burger-trigger").getBoundingClientRect();
    return [!panel.hidden, panel.classList.contains("menu-sheet"), box.width < 400, Math.abs(box.right - trigger.right) < 2, box.top >= trigger.bottom, panel.getAttribute("role")];
  }), [true, false, true, true, true, null]);
  check("with the user card and the theme, but no head of the phone's", await page.evaluate(() => {
    const panel = document.querySelector(".shell-burger-panel");
    const visible = selector => { const element = panel.querySelector(selector); return !!element && element.getClientRects().length > 0; };
    return [visible(".ui-user-card"), visible("[data-theme-switch]"), visible(".shell-burger-sheet-head"), visible(".hm-foot a[href='/about']")];
  }), [true, true, false, true]);
  await page.click(".status-primary");
  await sleep(300);
  check("a click beside it closes it", await page.evaluate(() => [document.querySelector(".shell-burger-trigger").getAttribute("aria-expanded"), document.querySelector(".shell-burger-panel").hidden]), ["false", true]);
  await page.evaluate(choice => window.MatgateChooseView(choice), view);
  await sleep(500);

  // ================= Phone: the main menu is a sheet over everything, a dialog while it is open
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, storageState: await context.storageState() });
  const small = await phone.newPage();
  small.on("pageerror", e => { console.log("JS ERROR phone: " + e.message); failures.push("JS error phone"); });
  await small.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1000);
  check("the open tabs' button is called that - not \"Connections\" like the side panel's entry", await small.evaluate(() => /^(Open tabs|Offene Tabs)$/.test(document.getElementById("mobile-tab-menu").getAttribute("data-label-tabs"))), true);
  const inertBefore = await small.evaluate(() => document.querySelectorAll("[inert]").length);
  await small.locator(".shell-burger-trigger").tap();
  await sleep(500);
  check("a tap opens it as a sheet over the whole screen", await small.evaluate(() => {
    const box = document.querySelector(".shell-burger-panel").getBoundingClientRect();
    return [box.left, box.top, box.width, box.height].map(Math.round);
  }), [0, 0, 390, 844]);
  check("a dialog under <body>, the header stepped aside, the focus on its way back", await small.evaluate(() => {
    const panel = document.querySelector(".shell-burger-panel");
    return [panel.getAttribute("role"), panel.getAttribute("aria-modal"), panel.parentElement === document.body,
      getComputedStyle(document.querySelector("body > header")).display, document.activeElement === panel.querySelector("[data-burger-close]")];
  }), ["dialog", "true", true, "none", true]);
  check("everything behind it is inert", await small.evaluate(() => [...document.body.children].filter(child => !child.classList.contains("shell-burger-panel")).every(child => child.hasAttribute("inert"))), true);
  check("the initials in the circle", await small.evaluate(() => {
    const name = document.querySelector(".shell-burger-panel .ui-user-card-text strong").textContent.trim();
    const parts = name.split(/[\s._-]+/).map(part => (part.match(/[\p{L}\p{N}]/u) || [""])[0]).filter(Boolean);
    const initials = (parts.length > 1 ? parts[0] + parts[parts.length - 1] : parts[0] || "").toUpperCase();
    return [document.querySelector(".shell-burger-panel .ui-avatar").textContent.trim() === initials, initials.length > 0];
  }), [true, true]);
  check("light, dark, system: a thumb's size each", await small.evaluate(() => [...document.querySelectorAll(".shell-burger-panel [data-theme-switch] .ui-seg > button")].map(button => {
    const box = button.getBoundingClientRect();
    return box.height >= 44 && box.width >= 44;
  })), [true, true, true]);
  check("Sign out is the last row, About in the foot below it", await small.evaluate(() => {
    const rows = [...document.querySelectorAll(".shell-burger-panel .shell-menu-item")].filter(row => row.getClientRects().length > 0);
    const about = document.querySelector(".shell-burger-panel .hm-foot a[href='/about']");
    return [rows[rows.length - 1].classList.contains("shell-menu-logout"), !!about && about.getBoundingClientRect().top >= rows[rows.length - 1].getBoundingClientRect().bottom];
  }), [true, true]);
  check("nothing cut off: every row inside the screen", await small.evaluate(() => [...document.querySelectorAll(".shell-burger-panel .shell-menu-item, .shell-burger-panel .hm-foot")]
    .filter(row => row.getClientRects().length > 0).every(row => { const box = row.getBoundingClientRect(); return box.left >= 0 && box.right <= innerWidth && box.bottom <= innerHeight; })), true);
  await small.locator(".shell-burger-panel [data-burger-close]").tap();
  await sleep(400);
  check("its way back closes it, and nothing stays inert", await small.evaluate(() => [
    document.querySelector(".shell-burger-trigger").getAttribute("aria-expanded"), document.querySelector(".shell-burger-panel").hidden,
    document.querySelector(".shell-burger-panel").parentElement.classList.contains("shell-burger"), getComputedStyle(document.querySelector("body > header")).display !== "none",
    document.querySelectorAll("[inert]").length]), ["false", true, true, true, inertBefore]);
  // An entry opens its page as a tab of the shell - the shell itself stays.
  const phoneOrigin = await small.evaluate(() => performance.timeOrigin);
  await small.locator(".shell-burger-trigger").tap();
  await sleep(400);
  await small.locator(".shell-burger-panel .shell-menu-item").filter({ hasText: /^\s*(Settings|Einstellungen)\s*$/ }).tap();
  await sleep(1500);
  check("Settings from the sheet: a page tab, the sheet gone, the shell still the same page", await small.evaluate(() => [
    [...document.querySelectorAll(".shell-page-panel iframe")].some(frame => { try { return frame.contentWindow.location.pathname === "/account"; } catch (e) { return false; } }),
    document.querySelector(".shell-burger-panel").hidden, location.pathname, performance.timeOrigin]), [true, true, "/", phoneOrigin]);
  await small.evaluate(() => document.querySelectorAll("#session-tabs .session-tab[data-tab-kind='page'] .session-tab-close").forEach(x => x.click()));
  await phone.close();

  // ================= A page of its own: the open page's link a tinted pill, no underline; no rule without the open tabs
  await page.goto(BASE + "/admin", { waitUntil: "networkidle" });
  await sleep(500);
  check("on /admin: Administration is a tinted pill, not underlined, and no rule dangles in the bar", await page.evaluate(() => {
    const link = document.querySelector(".shell-nav-row > a.shell-tab[href='/admin']");
    const style = link ? getComputedStyle(link) : null;
    return [!!link && link.classList.contains("active"), !!style && style.boxShadow === "none", !!style && style.backgroundColor !== "rgba(0, 0, 0, 0)", !!style && parseFloat(style.borderTopLeftRadius) > 0,
      document.querySelectorAll("header .shell-header-sep").length];
  }), [true, true, true, true, 0]);

  // ================= Somebody who is not an administrator: no tools offered, Administration only if let in
  const NAME = "MenueTester";
  const PASSWORD = "test-only-pw-1234";
  await page.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", NAME);
  await page.fill("input[name=\"password\"]", PASSWORD);
  await page.evaluate(() => document.querySelector("input[name='username']").form.requestSubmit());
  await sleep(1800);
  const other = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const plain = await other.newPage();
  plain.on("pageerror", e => { console.log("JS ERROR user: " + e.message); failures.push("JS error user"); });
  await signIn(plain, NAME, PASSWORD);
  check("no Tools - not in the bar, not in the menu", await plain.evaluate(() => [
    !!document.querySelector(".shell-tabs a[href='/tools']"), !!document.querySelector(".shell-burger-panel a[href='/tools']")]), [false, false]);
  check("Administration exactly when /admin lets the user in", await plain.evaluate(async () => {
    const response = await fetch("/admin", { credentials: "same-origin", redirect: "manual" });
    return [!!document.querySelector("header a[href='/admin']"), response.status === 200];
  }).then(([offered, allowed]) => offered === allowed), true);
  check("the role under the name says so", await plain.evaluate(() => /User|Benutzer/.test(document.querySelector(".account-menu-panel .ui-user-card small").textContent)), true);
  await other.close();
  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  await page.evaluate(name => {
    const row = [...document.querySelectorAll("tr")].find(r => r.innerText.includes(name));
    const form = row && row.querySelector("form[action*='/delete']");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  }, NAME);
  await sleep(1200);

  await saveTheme(page, "system").catch(() => {});
  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
