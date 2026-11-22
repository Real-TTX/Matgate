// A session in a window of its own, and back. The window shows that one session and nothing else - no
// header, no tab strip, no side panel - with a labelled button that hands it back. The main window lists
// the windows that are out beside its tabs, also after it was loaded anew; with no main window left, the
// popped window becomes the main window itself.
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
  let b = await chromium.launch({ headless: true });
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await context.newPage();
  const errors = p => p.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  errors(page);
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1200);
  await page.evaluate(() => window.MatgateChooseView("sidebar"));
  await sleep(400);

  const sessions = p => p.evaluate(() => document.querySelectorAll(".session-tab[data-tab-kind='connection'], .session-tab[data-tab-kind='page']").length);
  const popped = p => p.evaluate(() => [...document.querySelectorAll("#popped-windows .popped-window")].filter(item => item.offsetParent).map(item => item.textContent.trim()));
  const popOut = async p => {
    const [win] = await Promise.all([context.waitForEvent("page"), p.click("#connection-tab-actions [data-action='popOut']")]);
    errors(win);
    await win.waitForLoadState("networkidle");
    await sleep(3500);
    return win;
  };

  // ---- out
  await page.locator("#server-manager-tree [data-manager-in='folder']").filter({ has: page.locator("strong", { hasText: /^Linux-Testhost$/ }) }).click();
  await sleep(3500);
  check("a session is open", await sessions(page), 1);
  const win = await popOut(page);
  const alone = await win.evaluate(() => {
    const seen = selector => { const element = document.querySelector(selector); return !!element && !!element.offsetParent; };
    const back = document.querySelector("#connection-tab-actions [data-action='reattach']");
    return {
      popped: document.documentElement.hasAttribute("data-popped"),
      header: seen("header"), tabs: seen("#session-tabs"), side: seen("#server-manager"),
      back: back ? back.textContent.trim() : null,
      sessions: document.querySelectorAll(".session-tab[data-tab-kind='connection'], .session-tab[data-tab-kind='page']").length,
    };
  });
  check("the window shows the session alone: no header, no tabs, no side panel", [alone.popped, alone.header, alone.tabs, alone.side, alone.sessions], [true, false, false, false, 1]);
  check("with a labelled button back", /Re-attach|Zurück/.test(alone.back || ""), true);
  check("the main window gave the session away", await sessions(page), 0);
  check("and lists the window beside its tabs", await popped(page), ["Linux-Testhost"]);

  // ---- back by the window's button
  await Promise.all([win.waitForEvent("close", { timeout: 8000 }).catch(() => {}), win.click("#connection-tab-actions [data-action='reattach']")]);
  await sleep(3500);
  check("the window's button closes it and the session is back in the main window", [win.isClosed(), await sessions(page), await popped(page)], [true, 1, []]);

  // ---- back by the main window's arrow, after the main window was loaded anew
  const win2 = await popOut(page);
  await page.reload({ waitUntil: "networkidle" });
  await sleep(4500);
  check("loaded anew, the main window lists the window again", await popped(page), ["Linux-Testhost"]);
  await page.click("#popped-windows .popped-window-back");
  await sleep(3500);
  check("its arrow fetches the session back and the window goes", [await sessions(page), win2.isClosed(), await popped(page)], [1, true, []]);

  // ---- no main window left: the window becomes it
  const win3 = await popOut(page);
  await page.close();
  await sleep(500);
  await win3.click("#connection-tab-actions [data-action='reattach']");
  await win3.waitForURL(url => !/popped=1/.test(url.toString()), { timeout: 8000 }).catch(() => {});
  await win3.waitForLoadState("networkidle");
  await sleep(3500);
  check("with no main window the window becomes the main window, with the session in it",
    [await win3.evaluate(() => document.documentElement.hasAttribute("data-popped")), await win3.evaluate(() => !!document.querySelector("#session-tabs").offsetParent), await sessions(win3)], [false, true, 1]);
  await win3.evaluate(() => { const button = document.querySelector("#connection-tab-actions [data-action='disconnect']"); if (button) { button.click(); } });
  await sleep(800);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
