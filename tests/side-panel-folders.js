// Folders in the side panel: a connection moved into a folder by its menu (a subfolder with "/"), dragged out
// of it again onto "No folder", a folder's own menu (a new connection in it, with the folder already chosen)
// and the menu of the panel's free room.
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
  await sleep(1200);
  await page.evaluate(() => { localStorage.removeItem("matgate.serverManager.closed"); localStorage.setItem("matgate.view.mode.v1", "normal"); localStorage.setItem("matgate.serverManager.open", "true"); });
  await page.reload({ waitUntil: "networkidle" });
  await sleep(1200);

  const NAME = "Linux-Testhost 2";
  const row = () => page.locator("#server-manager-tree [data-manager-in='folder']").filter({ has: page.locator("strong", { hasText: new RegExp("^" + NAME + "$") }) });
  const head = folder => page.locator("#server-manager-tree .server-manager-folder[data-kind='folder'][data-folder='" + folder + "']");
  const menuItems = () => page.evaluate(() => [...document.querySelectorAll(".toolbar-menu-panel:not([hidden]) .toolbar-menu-item")].filter(item => item.offsetParent).map(item => item.textContent.trim()));
  const pick = async label => { await page.locator(".toolbar-menu-panel:not([hidden]) .toolbar-menu-item").filter({ hasText: label }).first().click(); await sleep(400); };
  const tree = () => page.evaluate(() => [...document.querySelectorAll("#server-manager-tree > .server-manager-folder--folder, #server-manager-tree > [data-manager-in='folder']")].map(node =>
    node.matches(".server-manager-folder") ? "[" + node.style.getPropertyValue("--depth") + " " + node.dataset.folder + " " + node.querySelector(".server-manager-count").textContent + "]" : node.querySelector("strong").textContent));
  const folderOf = () => page.evaluate(async n => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return (JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]).find(s => s.name === n) || {}).folder;
  }, NAME);

  // ---- the scroll bar: only while the mouse is over the list, and the list does not move when it comes
  await page.mouse.move(700, 400);
  await sleep(200);
  const bar = () => page.evaluate(() => { const tree = document.getElementById("server-manager-tree"); return [getComputedStyle(tree).scrollbarColor, tree.clientWidth]; });
  const away = await bar();
  await page.hover("#server-manager-tree");
  await sleep(200);
  const over = await bar();
  check("the side panel's scroll bar is invisible until the mouse is over it, and nothing moves", [/transparent transparent/.test(away[0]) || away[0] === "rgba(0, 0, 0, 0) rgba(0, 0, 0, 0)", over[0] !== away[0], over[1] === away[1]], [true, true, true]);

  try {
    // ---- into a subfolder, by the connection's menu
    await row().click({ button: "right" });
    await sleep(400);
    check("a connection's menu can move it into a folder", (await menuItems()).some(item => /Move to folder|In Ordner verschieben/.test(item)), true);
    await pick(/Move to folder|In Ordner verschieben/);
    await page.fill("#name-dialog-input", " Testlabor / Linux ");
    await page.click("[data-name-confirm]");
    await sleep(1500);
    check("it is stored with its subfolder, the name cleaned", await folderOf(), "Testlabor/Linux");
    const filed = await tree();
    const at = filed.indexOf("[0 Testlabor 1]");
    check("the folder stands with its subfolder indented under it, the connection in that, all counted", [at >= 0, filed[at + 1], filed[at + 2]], [true, "[1 Testlabor/Linux 1]", NAME]);
    await head("Testlabor").click();
    await sleep(300);
    check("shutting the folder hides its subfolder as well", (await tree()).includes("[1 Testlabor/Linux 1]"), false);
    await head("Testlabor").click();
    await sleep(300);

    // ---- a folder's menu: a new connection in it, with the folder already chosen
    await head("Testlabor/Linux").click({ button: "right" });
    await sleep(400);
    const folderMenu = await menuItems();
    check("a folder's menu has a new connection in it, a subfolder and the folder's page", [folderMenu.some(i => /New connection here|Neue Verbindung hier/.test(i)), folderMenu.some(i => /subfolder|Unterordner/.test(i)), folderMenu.some(i => /Edit folder|Ordner bearbeiten/.test(i))], [true, true, true]);
    await pick(/New connection here|Neue Verbindung hier/);
    await sleep(2500);
    const chosen = await page.evaluate(() => {
      const frame = [...document.querySelectorAll("iframe")].find(f => /\/admin\/servers\/new/.test(f.getAttribute("src") || ""));
      const field = frame && frame.contentDocument && frame.contentDocument.querySelector("[data-folder-value]");
      return field ? field.value : null;
    });
    check("the form of the new connection opens in a tab with that folder chosen", chosen, "Testlabor/Linux");
    await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab:not(.session-tab--add) .session-tab-close").forEach(x => x.click()));
    await sleep(600);

    // ---- out again, by dragging it onto "No folder"
    await row().dragTo(head(""));
    await sleep(1500);
    check("dragged onto \"No folder\" it is out of every folder", await folderOf(), "");
    check("and the folder is gone with its last connection", (await tree()).some(entry => /Testlabor/.test(entry)), false);

    // ---- dragged onto a folder it lands there
    const other = page.locator("#server-manager-tree [data-manager-in='folder']").filter({ has: page.locator("strong", { hasText: /^Linux-Testhost$/ }) });
    await row().click({ button: "right" });
    await sleep(300);
    await pick(/Move to folder|In Ordner verschieben/);
    await page.fill("#name-dialog-input", "Ziehordner");
    await page.click("[data-name-confirm]");
    await sleep(1500);
    await other.dragTo(head("Ziehordner"));
    await sleep(1500);
    check("a connection dragged onto a folder lands in it", (await tree()).slice(0, 3), ["[0 Ziehordner 2]", "Linux-Testhost", NAME]);

    // ---- the free room of the panel
    // On the free room itself - with many rows a point on the list may well be a row.
    await page.evaluate(() => { const tree = document.getElementById("server-manager-tree"); const box = tree.getBoundingClientRect(); tree.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: box.left + 100, clientY: box.bottom - 10 })); });
    await sleep(400);
    const roomMenu = await menuItems();
    check("the panel's free room offers quick connect, a new connection and a new folder", [roomMenu.some(i => /Quick/.test(i)), roomMenu.some(i => /^(New connection|Neue Verbindung)$/.test(i)), roomMenu.some(i => /New folder|Neuer Ordner/.test(i))], [true, true, true]);
    await page.keyboard.press("Escape");
  }
  finally {
    // Both back where the other scripts expect them: without a folder.
    await page.evaluate(async () => {
      const text = await (await fetch("/", { credentials: "same-origin" })).text();
      const csrf = text.match(/const csrfToken = ("[^"]*")/);
      const token = csrf ? JSON.parse(csrf[1]) : "";
      const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
      for (const s of list.filter(s => /^Linux-Testhost/.test(s.name))) {
        const body = new FormData(); body.append("folder", "");
        await fetch("/api/servers/" + s.id + "/folder", { method: "POST", body, credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
      }
    });
  }

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
