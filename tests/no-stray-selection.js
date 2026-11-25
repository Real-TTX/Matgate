// Nothing gets selected by accident (0.51.0): the app's chrome - buttons, cards, menus, the side panel, plain
// text - cannot be selected by a long press, a double click or a drag (and iOS shows no callout), while fields,
// editors, code, table cells and facts stay selectable. A field that inherits user-select:none would open the
// keyboard on iOS but take no characters - so fields are checked explicitly, and typed into.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

(async () => {
  const b = await chromium.launch({ headless: true });
  try {
    const p = await (await b.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true })).newPage();
    p.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await p.goto(BASE + "/login", { waitUntil: "networkidle" });
    const select = selector => p.evaluate(s => { const el = document.querySelector(s); return el ? getComputedStyle(el).userSelect : null; }, selector);
    check("the sign-in page's text is not selectable", await select("body"), "none");
    check("its fields are", [await select("input[name='username']"), await select("input[name='password']")], ["text", "text"]);
    await p.fill("input[name='username']", "admin");
    check("and take what is typed", await p.inputValue("input[name='username']"), "admin");
    await p.fill("input[name='password']", "test-only-pw");
    await Promise.all([p.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), p.click(".login-submit")]);

    await p.goto(BASE + "/", { waitUntil: "networkidle" });
    check("a card on the start page is not selectable", await select("[data-home2-card] h3"), "none");
    check("nor a button", await select("button"), "none");
    check("no callout on a long press (iOS)", await p.evaluate(() => getComputedStyle(document.body).webkitTouchCallout || "none"), "none");
    // A double click on a card's name selects nothing.
    const card = p.locator("[data-home2-card] h3").first();
    await card.dblclick({ force: true }).catch(() => {});
    check("a double click selects nothing", await p.evaluate(() => String(getSelection())), "");

    // A swipe on a control that cannot scroll does not move the page (on iOS it slid the whole app); where
    // something scrolls, it still does.
    const swipe = (selector, dy) => p.evaluate(([s, d]) => {
      const target = typeof s === "string" ? document.querySelector(s) : null;
      if (!target) { return null; }
      const box = target.getBoundingClientRect();
      const at = (y) => new Touch({ identifier: 1, target, clientX: box.left + 10, clientY: y });
      const y = box.top + Math.min(10, box.height / 2);
      target.dispatchEvent(new TouchEvent("touchstart", { touches: [at(y)], changedTouches: [at(y)], bubbles: true, cancelable: true }));
      const move = new TouchEvent("touchmove", { touches: [at(y + d)], changedTouches: [at(y + d)], bubbles: true, cancelable: true });
      target.dispatchEvent(move);
      target.dispatchEvent(new TouchEvent("touchend", { touches: [], changedTouches: [at(y + d)], bubbles: true, cancelable: true }));
      return move.defaultPrevented;
    }, [selector, dy]);
    check("a swipe on the app's bar does not move the page", await swipe(".app-header, header", -40), true);
    const scroller = await p.evaluate(() => {
      const node = [...document.querySelectorAll("*")].find(n => { const s = getComputedStyle(n); return /(auto|scroll)/.test(s.overflowY) && n.scrollHeight > n.clientHeight + 1 && n.offsetParent; });
      if (node) { node.setAttribute("data-test-scroller", "1"); }
      return !!node;
    });
    check("one that scrolls still scrolls", scroller ? await swipe("[data-test-scroller] > *", -40) : false, false);

    await p.goto(BASE + "/admin?tab=users", { waitUntil: "networkidle" });
    check("the search field in the administration is selectable", await select("[data-admin-users] [data-admp-filter]"), "text");
    await p.goto(BASE + "/admin?tab=connections&view=list", { waitUntil: "networkidle" });
    check("a table cell can be copied", await select("[data-admin-connections-list] td, table td"), "text");
  }
  finally {
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
