// The server manager: on a wide screen, a panel on the left with the connections by folder - one click
// connects, or goes to the session that is already open; the middle button and Ctrl open another one; the
// right button brings the connection's menu. Whether it is shown is remembered. A phone has no room for it.
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
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const shown = () => page.evaluate(() => { const panel = document.getElementById("server-manager"); return !panel.hidden && panel.offsetWidth > 200; });
  const rows = () => page.evaluate(() => [...document.querySelectorAll("#server-manager-tree [data-manager-server]")].map(row => row.querySelector("strong").textContent));
  const sessions = () => page.evaluate(() => document.querySelectorAll(".session-tab[data-tab-kind='connection'], .session-tab[data-tab-kind='page']").length);
  const row = name => page.locator("#server-manager-tree [data-manager-server]").filter({ has: page.locator("strong", { hasText: new RegExp("^" + name + "$") }) });
  const mark = name => page.evaluate(n => {
    const found = [...document.querySelectorAll("#server-manager-tree [data-manager-server]")].find(r => r.querySelector("strong").textContent === n);
    return { open: found.classList.contains("open"), active: found.classList.contains("active"), dot: !!found.querySelector(".server-manager-dot"), count: (found.querySelector(".server-manager-dot") || {}).textContent || "" };
  }, name);

  check("it is not in the way until asked for", await shown(), false);
  const widthBefore = await page.evaluate(() => document.getElementById("shell-page-panels").offsetWidth);
  await page.click("#server-manager-toggle");
  await sleep(300);
  check("the button in the tab row shows it", [await shown(), await page.evaluate(() => document.getElementById("server-manager-toggle").getAttribute("aria-pressed"))], [true, "true"]);
  check("beside the sessions, not over them", await page.evaluate(() => document.getElementById("shell-page-panels").offsetWidth) < widthBefore - 200, true);
  const all = await rows();
  check("it lists the saved connections, by name", [all.includes("Linux-Testhost"), all.includes("Linux-Testhost 2"), JSON.stringify(all) === JSON.stringify([...all].sort((a, c) => a.localeCompare(c, undefined, { sensitivity: "base" })))], [true, true, true]);
  check("and no places of the file manager", all.some(name => /^(Files|Dateien|My place|Shared)/i.test(name)), false);

  // ---- one click connects
  await row("Linux-Testhost").click();
  await sleep(3500);
  check("one click opens the connection", await sessions(), 1);
  check("its row says that a session is open, and that it is the one in front", await mark("Linux-Testhost"), { open: true, active: true, dot: true, count: "" });
  check("the manager stays where it is beside the session", await shown(), true);
  // back to the start page, and to the session by its row
  await page.click("#new-connection-tab .session-tab-main");
  await sleep(400);
  check("with the start page in front the row is no longer the active one", (await mark("Linux-Testhost")).active, false);
  await row("Linux-Testhost").click();
  await sleep(600);
  check("a click on a connection with a session goes to that session - no second one", [await sessions(), (await mark("Linux-Testhost")).active], [1, true]);
  await row("Linux-Testhost").click({ modifiers: ["Control"] });
  await sleep(3500);
  check("with Ctrl it opens another session", [await sessions(), (await mark("Linux-Testhost")).count], [2, "2"]);

  // ---- searching
  await page.fill("#server-manager-filter", "testhost 2");
  await sleep(300);
  check("the field narrows the list", await rows(), ["Linux-Testhost 2"]);
  await page.fill("#server-manager-filter", "nothing-like-this");
  await sleep(300);
  check("and says when nothing is left", [await rows(), await page.evaluate(() => !!document.querySelector("#server-manager-tree .server-manager-empty"))], [[], true]);
  await page.fill("#server-manager-filter", "");
  await sleep(300);

  // ---- the right button
  await row("Linux-Testhost 2").click({ button: "right" });
  await sleep(400);
  const menu = await page.evaluate(() => [...document.querySelectorAll(".toolbar-menu-panel:not([hidden]) button, .toolbar-menu-panel:not([hidden]) a")].filter(item => item.offsetParent).map(item => item.textContent.trim()));
  check("the right button brings the connection's menu", [menu.length >= 2, menu.some(item => /Connect|Verbinden/.test(item))], [true, true]);
  await page.keyboard.press("Escape");
  await sleep(300);

  // ---- remembered
  await page.reload({ waitUntil: "networkidle" });
  await sleep(1500);
  check("it is still shown after loading the page anew", await shown(), true);
  await page.click("#server-manager-toggle");
  await sleep(300);
  await page.reload({ waitUntil: "networkidle" });
  await sleep(1500);
  check("and still hidden once it was closed", await shown(), false);

  for (let i = 0; i < 4; i++) {
    const more = await page.evaluate(() => { const button = document.querySelector("#connection-tab-actions [data-action='disconnect']"); if (button) { button.click(); return true; } return false; });
    if (!more) { break; }
    await sleep(900);
  }

  // ---- folders: a connection filed in one stands under it, and a folder can be shut
  const second = (await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  })).find(s => s.name === "Linux-Testhost 2");
  const file = async folder => {
    await page.goto(BASE + "/admin/servers/" + second.id, { waitUntil: "networkidle" });
    await sleep(400);
    if (folder) {
      await page.selectOption("[data-folder-select]", "__new__");
      await page.fill("[data-folder-new-input]", folder);
    }
    else {
      await page.selectOption("[data-folder-select]", "");
    }
    await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='server-edit-form']")]);
    await sleep(600);
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(1000);
  };
  const tree = () => page.evaluate(() => [...document.querySelectorAll("#server-manager-tree > *")].map(node =>
    node.matches(".server-manager-folder") ? "[" + node.querySelector(".server-manager-folder-name").textContent + " " + node.querySelector(".server-manager-count").textContent + "]"
      : (node.querySelector("strong") || node).textContent));
  try {
    await file("Managerordner");
    await page.click("#server-manager-toggle");
    await sleep(400);
    const filed = await tree();
    check("a folder is a heading with its connections under it, and those without a folder come last",
      [filed[0], filed[1], filed.indexOf("[No folder 4]") === 2 || filed.indexOf("[Ohne Ordner 4]") === 2, filed.length], ["[Managerordner 1]", "Linux-Testhost 2", true, 7]);
    await page.click("#server-manager-tree .server-manager-folder[data-folder='Managerordner']");
    await sleep(300);
    check("a click on the folder shuts it", [(await tree()).includes("Linux-Testhost 2"), await page.evaluate(() => document.querySelector(".server-manager-folder[data-folder='Managerordner']").getAttribute("aria-expanded"))], [false, "false"]);
    await page.fill("#server-manager-filter", "testhost 2");
    await sleep(300);
    check("a search looks into shut folders as well", (await tree()).includes("Linux-Testhost 2"), true);
    await page.fill("#server-manager-filter", "");
    await page.click("#server-manager-tree .server-manager-folder[data-folder='Managerordner']");
    await sleep(300);
    check("and one more click opens it again", (await tree()).includes("Linux-Testhost 2"), true);
    await page.click("#server-manager-toggle");
  }
  finally {
    await file("");
  }
  await page.click("#server-manager-toggle");
  await sleep(300);
  check("without folders there are no headings", (await tree()).some(entry => entry.startsWith("[")), false);
  await page.click("#server-manager-toggle");

  // ---- a phone has no room for it
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  await phone.addCookies(await context.cookies());
  const small = await phone.newPage();
  small.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await small.goto(BASE + "/", { waitUntil: "networkidle" });
  await small.evaluate(() => window.localStorage.setItem("matgate.serverManager.open", "true"));
  await small.reload({ waitUntil: "networkidle" });
  await sleep(1000);
  check("on a phone neither the panel nor its button is there", await small.evaluate(() => [document.getElementById("server-manager").offsetWidth, document.getElementById("server-manager-toggle").offsetWidth]), [0, 0]);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
