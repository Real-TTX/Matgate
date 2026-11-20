// Creating and editing a connection, and the folders they are sorted into.
//
// The form had grown into seven panels in the order they were added: two rows of icons came before the host,
// a folder was a name typed again for every connection (with an icon per connection), and saving led to the
// administration's list wherever one had come from. Now: what a connection needs first comes first, the rest
// sits behind one button; a folder is chosen from those there are; a folder has a page of its own (name,
// icon, which connections); and a connection opened from the home page closes its tab when it is done.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAMES = ["Test-Form-A", "Test-Form-B", "Test-Form-C"];
const FOLDER = "Test-Ordner";
const RENAMED = "Test-Ordner neu";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const context = await b.newContext({ viewport: { width: 1300, height: 900 } });
  await require("./list-on-start-page")(context);
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const browserDialogs = [];
  page.on("dialog", async d => { browserDialogs.push(d.type()); await d.dismiss(); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  // The connections and folders of this script, removed without the question a person gets.
  const cleanUp = () => page.evaluate(async names => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && names.includes(cell)) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  }, NAMES);
  await cleanUp();
  const folders = async () => {
    await page.goto(BASE + "/admin?tab=folders", { waitUntil: "networkidle" });
    return page.evaluate(() => [...document.querySelectorAll("[data-folder-row]")].map(row => ({
      name: row.dataset.folderRow,
      count: Number(row.querySelectorAll("td")[1].textContent.trim()),
      inside: row.querySelectorAll("td")[2].textContent.trim(),
      icon: row.querySelector(".server-icon").innerHTML,
    })));
  };

  // ============================================================================================ the form
  await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
  const form = "#server-create-form";
  check("the page is called what the home page calls it", await page.evaluate(() => document.querySelector("h1").textContent.trim()), "New connection");
  check("what a connection needs comes first: kind, target, sign-in - then filing", await page.evaluate(f =>
    [...document.querySelectorAll(f + " > section")].filter(s => !s.hidden).map(s => (s.querySelector("h2, .conn-more-title") || {}).textContent.trim()), form),
  ["Kind of connection", "Target", "Sign-in", "Filing", "More settings"]);
  check("the rest waits behind one button", await page.evaluate(() => [document.querySelector("[data-more-body]").hidden, document.querySelector("[data-more-toggle]").getAttribute("aria-expanded")]), [true, "false"]);
  check("the icons do not take the page: the choice is closed", await page.evaluate(() => document.querySelector("[data-icon-body]").hidden), true);
  check("the port field says which port an empty one means", await page.getAttribute("[data-port-input]", "placeholder"), "3389");
  await page.click("[data-protocol-option='Ssh']");
  check("and follows the kind of connection", await page.getAttribute("[data-port-input]", "placeholder"), "22");
  await page.click("[data-protocol-option='Website']");
  check("a website has an address instead of host and port - and a sign-in, for the pages that ask the browser for one", await page.evaluate(() => [
    document.querySelector("[name='host']").closest("label").hidden, document.querySelector("[name='websiteUrl']").closest("label").hidden,
    document.querySelector("[name='targetUserName']").closest("section").hidden]), [true, false, false]);
  await page.click("[data-protocol-option='Ssh']");
  await page.click("[data-more-toggle]");
  check("the button opens the rest, with what belongs to this kind", await page.evaluate(() => [
    document.querySelector("[data-more-body]").hidden,
    document.querySelector("[name='terminalFontSize']").closest("[data-protocols]").hidden,
    document.querySelector("[name='keyboardLayout']").closest("[data-protocols]").hidden]), [false, false, true]);
  await page.click("[data-icon-toggle]");
  await page.click("[data-icon-option='database']");
  check("choosing an icon shows it and closes the choice", await page.evaluate(() => [
    document.querySelector("[name='iconKey']").value, document.querySelector("[data-icon-body]").hidden, !!document.querySelector("[data-icon-shown] svg")]), ["database", true, true]);

  // --- A new folder, by name.
  await page.fill("[name='name']", NAMES[0]);
  await page.fill("[name='host']", "ssh");
  await page.fill("[name='port']", "2222");
  check("a folder is chosen; a new one is offered last", await page.evaluate(() => [...document.querySelectorAll("[data-folder-select] option")].map(o => o.value).slice(-1)), ["__new__"]);
  await page.selectOption("[data-folder-select]", "__new__");
  check("a new folder asks for its name", await page.evaluate(() => document.querySelector("[data-folder-new]").hidden), false);
  await page.fill("[data-folder-new-input]", FOLDER);
  check("what is sent is the folder's name", await page.inputValue("[name='folderName']"), FOLDER);
  await Promise.all([page.waitForURL(u => /\/admin\?tab=servers/.test(u.toString())), page.click("button[form='server-create-form']")]);
  let list = await servers();
  const first = list.find(s => s.name === NAMES[0]);
  check("from the administration, saving leads back to its list", [!!first, new URL(page.url()).pathname + new URL(page.url()).search.replace(/&?embed=1/, "")], [true, "/admin?tab=servers"]);

  // --- The second connection takes the folder from the list.
  await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
  await page.click("[data-protocol-option='Ssh']");
  await page.fill("[name='name']", NAMES[1]);
  await page.fill("[name='host']", "ssh");
  check("the folder just made is in the list", await page.evaluate(n => [...document.querySelectorAll("[data-folder-select] option")].some(o => o.value === n), FOLDER), true);
  await page.selectOption("[data-folder-select]", FOLDER);
  await Promise.all([page.waitForURL(u => /\/admin\?tab=servers/.test(u.toString())), page.click("button[form='server-create-form']")]);
  list = await servers();
  const second = list.find(s => s.name === NAMES[1]);
  await page.goto(BASE + "/admin/servers/" + second.id, { waitUntil: "networkidle" });
  check("an empty port is the default of the kind", await page.inputValue("[name='port']"), "22");

  // --- Editing: the form shows what is stored, and keeps what it does not show.
  await page.goto(BASE + "/admin/servers/" + first.id, { waitUntil: "networkidle" });
  check("the edit page shows the connection as it is", await page.evaluate(() => [
    document.querySelector("h1").textContent.trim(), document.querySelector("[name='host']").value, document.querySelector("[name='port']").value,
    document.querySelector("[data-folder-select]").value, document.querySelector("[name='iconKey']").value]), [NAMES[0], "ssh", "2222", FOLDER, "database"]);
  check("a password is never shown, only whether there is one", await page.getAttribute("[name='targetPassword']:not([disabled])", "placeholder"), "none stored");
  await page.fill("[name='notes']", "eine Notiz").catch(async () => {
    await page.click("[data-more-toggle]");
    await page.fill("[name='notes']", "eine Notiz");
  });
  await Promise.all([page.waitForURL(u => /\/admin\?tab=servers/.test(u.toString())), page.click("button[form='server-edit-form']")]);
  await page.goto(BASE + "/admin/servers/" + first.id, { waitUntil: "networkidle" });
  check("a connection with something set under \"more\" shows it right away", await page.evaluate(() => [document.querySelector("[data-more-body]").hidden, document.querySelector("[name='notes']").value]), [false, "eine Notiz"]);
  check("saving kept the folder and the icon", await page.evaluate(() => [document.querySelector("[name='folderName']").value, document.querySelector("[name='iconKey']").value]), [FOLDER, "database"]);

  // ============================================================================================ folders
  let all = await folders();
  let folder = all.find(f => f.name === FOLDER);
  check("the folder is in the administration, with what lies in it", folder && [folder.count, folder.inside], [2, NAMES[0] + ", " + NAMES[1]]);
  const plainIcon = folder.icon;

  await page.click("[data-folder-row='" + FOLDER + "'] a");
  await page.waitForSelector("#folder-form");
  check("a folder's page: its name and its connections", await page.evaluate(names => [
    document.querySelector("[name='name']").value,
    names.map(n => [...document.querySelectorAll(".conn-check-list .check")].find(c => c.textContent.includes(n)).querySelector("input").checked)],
  [NAMES[0], NAMES[1]]), [FOLDER, [true, true]]);
  await page.fill("[name='name']", RENAMED);
  await page.click("#folder-form [data-icon-option='cloud']");
  await Promise.all([page.waitForURL(u => /tab=folders/.test(u.toString())), page.click("button[form='folder-form']")]);
  all = await folders();
  folder = all.find(f => f.name === RENAMED);
  check("renaming renames it for every connection in it", [all.some(f => f.name === FOLDER), folder && folder.count], [false, 2]);
  check("and the icon is the folder's, not one connection's", folder && folder.icon !== plainIcon, true);

  // --- A third connection joins: it gets the folder's icon without being asked for one.
  await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
  await page.click("[data-protocol-option='Vnc']");
  await page.fill("[name='name']", NAMES[2]);
  await page.fill("[name='host']", "desktop");
  await page.selectOption("[data-folder-select]", RENAMED);
  await Promise.all([page.waitForURL(u => /\/admin\?tab=servers/.test(u.toString())), page.click("button[form='server-create-form']")]);
  all = await folders();
  folder = all.find(f => f.name === RENAMED);
  check("a connection that joins a folder is in it", folder && folder.count, 3);
  const cloudIcon = folder.icon;

  // --- The folder page moves connections: unticked ones leave.
  await page.click("[data-folder-row='" + RENAMED + "'] a");
  await page.waitForSelector("#folder-form");
  await page.evaluate(n => { [...document.querySelectorAll(".conn-check-list .check")].find(c => c.textContent.includes(n)).querySelector("input").checked = false; }, NAMES[0]);
  await Promise.all([page.waitForURL(u => /tab=folders/.test(u.toString())), page.click("button[form='folder-form']")]);
  all = await folders();
  folder = all.find(f => f.name === RENAMED);
  check("unticked, a connection leaves the folder", folder && [folder.count, folder.inside, folder.icon === cloudIcon], [2, NAMES[1] + ", " + NAMES[2], true]);

  // --- A folder without a connection is none: the page says so instead of saving nothing.
  await page.goto(BASE + "/admin/folders/new", { waitUntil: "networkidle" });
  await page.fill("[name='name']", "Leer");
  await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='folder-form']")]);
  check("a new folder without a connection is refused, with the reason", await page.evaluate(() => [!!document.querySelector("[data-folder-error]"), document.querySelector("[name='name']").value]), [true, "Leer"]);
  // With a connection that lies elsewhere: it moves.
  await page.evaluate(n => { [...document.querySelectorAll(".conn-check-list .check")].find(c => c.textContent.includes(n)).querySelector("input").checked = true; }, NAMES[2]);
  await Promise.all([page.waitForURL(u => /tab=folders/.test(u.toString())), page.click("button[form='folder-form']")]);
  all = await folders();
  check("a connection ticked in a new folder moves there", [all.find(f => f.name === "Leer").inside, all.find(f => f.name === RENAMED).count], [NAMES[2], 1]);

  // --- Dissolving asks first, with a dialog of the application; the connections stay.
  await page.click("[data-folder-row='Leer'] .danger-action");
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  await Promise.all([page.waitForLoadState("networkidle"), page.click("#confirm-dialog [data-confirm-ok]")]);
  await sleep(300);
  all = await folders();
  list = await servers();
  check("dissolving removes the folder and keeps its connection", [all.some(f => f.name === "Leer"), list.some(s => s.name === NAMES[2])], [false, true]);
  check("no dialog of the browser was involved", browserDialogs, []);

  // ============================================================================================ from the home page
  // The folders have to be on the home page for this: home-page.js switches that section off for this
  // user and leaves it that way.
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(500);
  if (await page.evaluate(() => {
    const list = document.getElementById("home-order-list");
    const box = list && Array.from(list.children).find(i => i.dataset.orderKey === "folders")?.querySelector("[data-order-visible]");
    if (!box || box.checked) { return false; }
    box.checked = true;
    box.dispatchEvent(new Event("change", { bubbles: true }));
    return true;
  })) {
    await sleep(300);
    await page.evaluate(() => document.querySelector("[name='homeSections']").form.requestSubmit()).catch(() => { });
    await sleep(1500);
  }
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1200);
  const formFrame = async pattern => {
    for (let i = 0; i < 40; i++) {
      const frame = page.frames().find(f => pattern.test(f.url()));
      if (frame) { return frame; }
      await sleep(250);
    }
    return null;
  };
  const tabCount = () => page.evaluate(() => document.querySelectorAll(".shell-page-panel iframe").length);
  const card = name => page.evaluate(n => [...document.querySelectorAll("[data-home2-card] h3")].some(h => h.textContent.trim() === n), name);
  check("the home page has a way to the folders", await page.evaluate(() => (document.querySelector(".home2-manage-link") || {}).getAttribute?.("href") || ""), "/admin?tab=folders");

  // The gear of a card opens the connection in a tab; "Back" closes it again.
  await page.evaluate(n => [...document.querySelectorAll("[data-home2-card]")].find(c => c.querySelector("h3").textContent.trim() === n).querySelector(".connection-choice-settings-corner").click(), NAMES[1]);
  let frame = await formFrame(/\/admin\/servers\/[0-9a-f-]{36}/);
  await frame.waitForSelector("#server-edit-form");
  check("the gear opens the connection in a tab of its own", await tabCount(), 1);
  await frame.fill("[name='name']", NAMES[1] + " x");
  await frame.click(".conn-actions a.button");
  for (let i = 0; i < 40 && (await tabCount()) > 0; i++) { await sleep(250); }
  check("\"Back\" closes that tab and changes nothing", [await tabCount(), await card(NAMES[1]), await card(NAMES[1] + " x")], [0, true, false]);

  // Saved from there: the tab closes and the home page shows the change without being loaded again.
  await page.evaluate(n => [...document.querySelectorAll("[data-home2-card]")].find(c => c.querySelector("h3").textContent.trim() === n).querySelector(".connection-choice-settings-corner").click(), NAMES[1]);
  frame = await formFrame(/\/admin\/servers\/[0-9a-f-]{36}/);
  await frame.waitForSelector("#server-edit-form");
  await frame.fill("[name='name']", NAMES[1] + " x");
  await frame.click("button[form='server-edit-form']");
  for (let i = 0; i < 60 && !((await tabCount()) === 0 && await card(NAMES[1] + " x")); i++) { await sleep(250); }
  check("saving closes the tab and the home page shows the new name", [await tabCount(), await card(NAMES[1] + " x")], [0, true]);
  // And back to its name, the same way.
  await page.evaluate(n => [...document.querySelectorAll("[data-home2-card]")].find(c => c.querySelector("h3").textContent.trim() === n).querySelector(".connection-choice-settings-corner").click(), NAMES[1] + " x");
  frame = await formFrame(/\/admin\/servers\/[0-9a-f-]{36}/);
  await frame.waitForSelector("#server-edit-form");
  await frame.fill("[name='name']", NAMES[1]);
  await frame.click("button[form='server-edit-form']");
  for (let i = 0; i < 60 && !((await tabCount()) === 0 && await card(NAMES[1])); i++) { await sleep(250); }
  check("a second time just the same", [await tabCount(), await card(NAMES[1])], [0, true]);

  // ============================================================================================ a phone
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, hasTouch: true, isMobile: true, storageState: await context.storageState() });
  const small = await phone.newPage();
  small.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await small.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
  check("on a phone the form is one column and nothing sticks out sideways", await small.evaluate(() => {
    const host = document.querySelector("[name='host']").getBoundingClientRect();
    const port = document.querySelector("[name='port']").getBoundingClientRect();
    return [document.documentElement.scrollWidth <= window.innerWidth, port.top > host.bottom - 1];
  }), [true, true]);
  check("and saving stays within reach while the form scrolls", await small.evaluate(() => {
    window.scrollTo(0, 200);
    const button = document.querySelector("button[form='server-create-form']").getBoundingClientRect();
    return button.top >= 0 && button.bottom <= window.innerHeight;
  }), true);
  await small.goto(BASE + "/admin/folders/edit?name=" + encodeURIComponent(RENAMED), { waitUntil: "networkidle" });
  check("the folder page fits a phone as well", await small.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true);

  await cleanUp();
  check("with its connections gone, the folder is gone", (await folders()).some(f => f.name === RENAMED), false);
  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
