// Copying between two file tabs, as in Explorer: Ctrl+C in one, Ctrl+V in the other (a folder goes with
// what is in it), and a row dragged onto the other tab's button. Between two places (Global and User), so
// the gateway copies from one connection to another.
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
  const context = await b.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1200);

  const FOLDER = "Kopiertest-" + Date.now().toString(36);
  const setup = await page.evaluate(async folder => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const csrf = JSON.parse(text.match(/const csrfToken = ("[^"]*")/)[1]);
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const global = list.find(s => s.name === "Global");
    const own = list.find(s => s.name === "User");
    const post = (id, what, body) => fetch("/api/files/" + id + "/" + what, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json", "X-Matgate-Csrf": csrf }, body: JSON.stringify(body) });
    await post(global.id, "mkdir", { path: "/", name: folder });
    await post(global.id, "create-file", { path: "/" + folder, name: "inside.txt" });
    return { csrf, global: global.id, own: own.id };
  }, FOLDER);
  const listing = (id, path) => page.evaluate(async ([i, p]) => (await (await fetch("/api/files/" + i + "/list?path=" + encodeURIComponent(p), { credentials: "same-origin" })).json()).entries.map(e => e.name), [id, path]);

  try {
    await page.evaluate(id => window.MatgateOpenServerTab(id), setup.global);
    await sleep(2500);
    await page.evaluate(id => window.MatgateOpenServerTab(id), setup.own);
    await sleep(2500);
    const tabButton = name => page.locator("#session-tabs .session-tab[data-tab-kind='connection']").filter({ hasText: name }).first();
    const visibleRow = name => page.locator(".session-panel:not(.hidden) tr[data-entry-path], tr[data-entry-path]").filter({ hasText: new RegExp("^\\s*" + name) }).filter({ visible: true }).first();

    // ---- Ctrl+C in Global, Ctrl+V in User
    await tabButton("Global").click();
    await sleep(800);
    await visibleRow(FOLDER).locator(".file-select-entry").check();
    await page.mouse.click(1300, 860);
    await page.keyboard.press("Control+c");
    await sleep(300);
    await tabButton("User").click();
    await sleep(800);
    await page.keyboard.press("Control+v");
    await sleep(2500);
    check("Ctrl+C in one file tab and Ctrl+V in another copies the folder there", (await listing(setup.own, "/")).includes(FOLDER), true);
    check("with what is in it", await listing(setup.own, "/" + FOLDER), ["inside.txt"]);
    check("and the tab shows it at once", await visibleRow(FOLDER).count(), 1);

    // ---- the same again: the name is taken, so it gets " (2)"
    await page.keyboard.press("Control+v");
    await sleep(2500);
    check("pasted once more it is a second copy beside the first, as in Explorer", (await listing(setup.own, "/")).includes(FOLDER + " (2)"), true);

    // ---- a row dragged onto the other tab's button
    await visibleRow(FOLDER + " \\(2\\)").dragTo(tabButton("Global"));
    await sleep(2500);
    check("a row dragged onto the other file tab is copied into the folder it shows", (await listing(setup.global, "/")).includes(FOLDER + " (2)"), true);
  }
  finally {
    await page.evaluate(async ([s, folder]) => {
      const post = (id, body) => fetch("/api/files/" + id + "/delete", { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json", "X-Matgate-Csrf": s.csrf }, body: JSON.stringify(body) });
      await post(s.global, { paths: ["/" + folder, "/" + folder + " (2)"] });
      await post(s.own, { paths: ["/" + folder, "/" + folder + " (2)"] });
    }, [setup, FOLDER]);
  }

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
