// A middle click on a tab closes it, as in a browser. It is the same thing as the cross on the tab,
// without having to hit the cross. What must NOT happen: the tab being switched to first, the "new
// connection" tab (the plus) closing, the button press starting the browser's autoscroll, and the
// left button changing in any way.
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
  const page = await (await b.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);

  const ids = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]).filter(s => s.protocol === "SSH").map(s => s.id);
  });
  check("two SSH connections to open", ids.length >= 2, true);

  // A page tab first, then two sessions: the last one opened is the active one.
  await page.evaluate(() => window.MatgateOpenShellTab("/about", "About"));
  await sleep(1200);
  await page.evaluate(id => window.MatgateOpenServerTab(id), ids[0]);
  await sleep(1500);
  await page.evaluate(id => window.MatgateOpenServerTab(id), ids[1]);
  await sleep(3000);

  const tabs = () => page.evaluate(() => [...document.querySelectorAll("#session-tabs .session-tab")].map(t => ({
    kind: t.dataset.tabKind, id: t.dataset.tabId || "", active: t.classList.contains("active"),
  })));
  const kinds = async () => (await tabs()).map(t => t.kind);
  const panels = () => page.evaluate(() => ({
    sessions: document.querySelectorAll(".connection-panel:not(.file-area-host)").length,
    pages: document.querySelectorAll("[data-shell-panel-id]").length,
  }));
  const middleClick = async (selector, n = 0) => {
    const box = await page.locator(selector).nth(n).boundingBox();
    const at = { x: box.x + box.width / 2, y: box.y + box.height / 2 };
    await page.mouse.move(at.x, at.y);
    await page.mouse.click(at.x, at.y, { button: "middle" });
    await sleep(500);
  };

  check("page tab, two sessions and the plus are in the strip", await kinds(), ["page", "connection", "connection", "add"]);
  const start = await tabs();
  check("the session opened last is the active one", start.map(t => t.active), [false, false, true, false]);
  const connectionTabs = start.filter(t => t.kind === "connection");
  const tabOf = id => `#session-tabs .session-tab[data-tab-id="${id}"]`;

  // --- The left button is as it was.
  await page.locator(tabOf(connectionTabs[0].id) + " .session-tab-main").click();
  await sleep(400);
  check("a left click still switches to the tab", (await tabs()).filter(t => t.active).map(t => t.id), [connectionTabs[0].id]);
  await page.locator(tabOf(connectionTabs[1].id) + " .session-tab-main").click();
  await sleep(400);
  check("and back", (await tabs()).filter(t => t.active).map(t => t.id), [connectionTabs[1].id]);

  // --- The button press itself is cancelled, or Windows starts the autoscroll over the strip.
  const pressed = await page.evaluate(id => {
    const main = document.querySelector(`#session-tabs .session-tab[data-tab-id="${id}"] .session-tab-main`);
    const press = button => { const e = new MouseEvent("mousedown", { button, bubbles: true, cancelable: true }); main.dispatchEvent(e); return e.defaultPrevented; };
    const plus = document.querySelector("#new-connection-tab .session-tab-main");
    const e = new MouseEvent("mousedown", { button: 1, bubbles: true, cancelable: true });
    plus.dispatchEvent(e);
    return { middle: press(1), left: press(0), plusMiddle: e.defaultPrevented };
  }, connectionTabs[0].id);
  check("pressing the middle button on a tab is cancelled (no autoscroll)", pressed.middle, true);
  check("pressing the left button is not", pressed.left, false);
  check("pressing the middle button on the plus is not either", pressed.plusMiddle, false);

  // --- Middle click on a session that is not the active one.
  await middleClick(tabOf(connectionTabs[0].id) + " .session-tab-main");
  check("the session tab is gone", (await tabs()).some(t => t.id === connectionTabs[0].id), false);
  check("and nothing else closed", await kinds(), ["page", "connection", "add"]);
  check("the active tab did not change on the way", (await tabs()).filter(t => t.active).map(t => t.id), [connectionTabs[1].id]);
  check("its panel is gone, the other session's is not", (await panels()).sessions, 1);

  // --- Middle click on a page tab.
  await middleClick('#session-tabs .session-tab--page .session-tab-main');
  check("the page tab is gone", await kinds(), ["connection", "add"]);
  check("and its page with it", (await panels()).pages, 0);

  // --- The plus stays.
  await middleClick("#new-connection-tab .session-tab-main");
  check("a middle click on the plus closes nothing", await kinds(), ["connection", "add"]);
  check("and does not switch to it", (await tabs()).filter(t => t.active).map(t => t.id), [connectionTabs[1].id]);

  // --- On the cross of a tab it is the same, and it closes it once.
  await middleClick(tabOf(connectionTabs[1].id) + " .session-tab-close");
  check("a middle click on the cross closes the tab", await kinds(), ["add"]);
  check("the plus is what is shown now", await page.evaluate(() => document.getElementById("new-connection-tab").classList.contains("active")), true);

  // --- A narrow window lists the tabs instead of showing a strip: an entry closes the same way.
  await page.evaluate(id => window.MatgateOpenServerTab(id), ids[0]);
  await sleep(1500);
  await page.evaluate(id => window.MatgateOpenServerTab(id), ids[1]);
  await sleep(2500);
  await page.setViewportSize({ width: 600, height: 800 });
  await sleep(1000);
  // The list is what the compact view has instead of the strip (the phone's default, a button elsewhere).
  await page.evaluate(() => document.getElementById("view-mode-toggle").click());
  await sleep(800);
  check("the compact view is on", await page.evaluate(() => document.documentElement.dataset.viewMode), "minimal");
  await page.locator("#mobile-tab-menu .mobile-tab-menu-trigger").click();
  await sleep(500);
  check("the list shows both sessions and the plus", await page.evaluate(() => document.querySelectorAll("[data-mobile-tab-panel] .mobile-tab-item").length), 3);
  const second = (await tabs()).filter(t => t.kind === "connection");
  await middleClick("[data-mobile-tab-panel] .mobile-tab-item-main", 0);
  check("a middle click on the first entry closes that session", (await tabs()).some(t => t.id === second[0].id), false);
  check("and only that one", await kinds(), ["connection", "add"]);
  check("the list is still open for the next one", await page.evaluate(() => getComputedStyle(document.querySelector("[data-mobile-tab-panel]")).display), "flex");
  check("and has what is left in it", await page.evaluate(() => document.querySelectorAll("[data-mobile-tab-panel] .mobile-tab-item").length), 2);
  await middleClick("[data-mobile-tab-panel] .mobile-tab-item-main", 1);
  check("a middle click on the plus entry closes nothing", await kinds(), ["connection", "add"]);
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
