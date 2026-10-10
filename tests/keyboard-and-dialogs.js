// The keyboards of a session next to the dialogs, on a phone. The application's own keyboard sat on top
// of a dialog (it is in the session's box with z-index 30, the dialogs had 4): with it up, the paste
// dialog showed half of itself and the keyboard's space bar where its "send" button was. The device's
// keyboard covered the lower half of a dialog as well, because a dialog was as tall as the screen
// however much of it the keyboard took - the buttons at its bottom were behind the keyboard.
//
// Chromium has no soft keyboard, so the two ways a phone reports one are faked: iOS shrinks the visual
// viewport (and may pan it), Android lets the keyboard overlay the page and reports its rectangle through
// navigator.virtualKeyboard. That tests what the application does with those reports - not what a real
// iPhone or Android phone reports.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const PHONE = { width: 390, height: 844 };
const LABEL = {
  osk: /on-screen keyboard|Bildschirmtastatur/i,
  device: /device keyboard|Geraetetastatur/i,
  paste: /Paste to active tab|In aktiven Tab einfuegen/i,
  send: /Send files to the session|Dateien in die Sitzung/i,
  area: /Files for this connection|Dateien dieser Verbindung/i,
};
const DIALOGS = [
  { name: "paste dialog", open: LABEL.paste, id: "clipboard-dialog", close: "clipboard-close", scope: "button", wait: 700 },
  { name: "send-files dialog", open: LABEL.send, id: "sftp-target-dialog", close: "sftp-target-close", scope: "button", wait: 700 },
  { name: "files dialog", open: LABEL.area, id: "file-area-dialog", close: "file-area-dialog-close", scope: ".file-area-dialog-head button", wait: 1800 },
];

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({
    viewport: PHONE, deviceScaleFactor: 2, hasTouch: true, isMobile: true,
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
  });
  await ctx.addInitScript(() => {
    // "Installed" the way the application detects it.
    const original = window.matchMedia.bind(window);
    window.matchMedia = query => {
      if (!/display-mode/.test(query)) { return original(query); }
      return {
        matches: /standalone/.test(query), media: query, onchange: null,
        addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {},
        dispatchEvent() { return false; },
      };
    };
    Object.defineProperty(navigator, "standalone", { value: true });
    // iOS: the keyboard shrinks the visual viewport, and the page may be panned up.
    const visual = window.visualViewport;
    const seen = { height: null, top: 0 };
    Object.defineProperty(visual, "height", { configurable: true, get: () => (seen.height === null ? window.innerHeight : seen.height) });
    Object.defineProperty(visual, "offsetTop", { configurable: true, get: () => seen.top });
    window.__visual = (height, top) => { seen.height = height; seen.top = top || 0; visual.dispatchEvent(new Event("resize")); };
    // Android: the keyboard overlays the page and reports its own rectangle.
    const target = new EventTarget();
    const keyboard = {
      overlaysContent: false, size: 0,
      addEventListener: target.addEventListener.bind(target),
      removeEventListener: target.removeEventListener.bind(target),
      dispatchEvent: target.dispatchEvent.bind(target),
    };
    Object.defineProperty(keyboard, "boundingRect", { get: () => new DOMRect(0, window.innerHeight - keyboard.size, window.innerWidth, keyboard.size) });
    Object.defineProperty(navigator, "virtualKeyboard", { value: keyboard, configurable: true });
    window.__keyboard = size => { keyboard.size = size; target.dispatchEvent(new Event("geometrychange")); };
  });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const prompts = [];
  page.on("dialog", async d => { prompts.push(d.type()); await d.dismiss(); });

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);

  const ssh = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const hit = list.find(s => s.protocol === "SSH");
    return hit ? hit.id : null;
  });
  await page.goto(BASE + "/connect/" + ssh, { waitUntil: "networkidle" });

  // --- helpers
  const pressAction = pattern => page.evaluate(source => {
    const matcher = new RegExp(source, "i");
    const label = x => x.title || x.getAttribute("aria-label") || (x.textContent || "").trim();
    const find = () => [...document.querySelectorAll("#connection-tab-actions button, .tab-action-overflow-panel button")].find(x => matcher.test(label(x)));
    let found = find();
    if (!found) {
      const panel = document.querySelector(".tab-action-overflow-panel");
      const more = document.querySelector(".tab-action-more-trigger");
      if (more && !(panel && !panel.hidden && panel.offsetWidth > 0)) { more.click(); }
      found = find();
    }
    if (!found) { return false; }
    found.click();
    return true;
  }, pattern.source);
  const hasAction = pattern => page.evaluate(source => {
    const matcher = new RegExp(source, "i");
    return [...document.querySelectorAll("#connection-tab-actions button")].some(x => matcher.test(x.title || x.getAttribute("aria-label") || ""));
  }, pattern.source);
  const oskState = () => page.evaluate(source => {
    const osk = document.querySelector(".matgate-osk");
    const matcher = new RegExp(source, "i");
    const button = [...document.querySelectorAll("#connection-tab-actions button")].find(x => matcher.test(x.title || x.getAttribute("aria-label") || ""));
    return { open: !!(osk && osk.classList.contains("open")), lit: !!(button && button.classList.contains("active")) };
  }, LABEL.osk.source);
  const shown = id => page.evaluate(i => !document.getElementById(i).classList.contains("hidden"), id);
  const closeDialog = id => page.evaluate(i => document.getElementById(i).click(), id);
  const dialogInfo = (id, scope) => page.evaluate(([dialogId, selector]) => {
    const dialog = document.getElementById(dialogId);
    const r = dialog.getBoundingClientRect();
    const buttons = [...dialog.querySelectorAll(selector)].filter(x => x.offsetWidth && x.offsetHeight);
    const covered = buttons.filter(x => {
      const q = x.getBoundingClientRect();
      const hit = document.elementFromPoint(q.left + q.width / 2, q.top + q.height / 2);
      return !(hit === x || x.contains(hit));
    }).map(x => (x.textContent || "").trim().slice(0, 20) || x.id);
    // Whatever is on top there - the keyboard may lie below the dialog all it likes.
    const top = document.elementFromPoint(window.innerWidth / 2, window.innerHeight - 60);
    const keyboardAtTheBottom = !!(top && top.closest(".matgate-osk"));
    return { rect: [r.left, r.top, r.right, r.bottom].map(Math.round), buttons: buttons.length, covered, keyboardAtTheBottom };
  }, [id, scope]);
  const cssVar = name => page.evaluate(n => getComputedStyle(document.documentElement).getPropertyValue(n).trim(), name);
  const rectOf = id => page.evaluate(i => { const r = document.getElementById(i).getBoundingClientRect(); return [r.left, r.top, r.right, r.bottom].map(Math.round); }, id);

  // The session has to be up before its buttons are.
  for (let waited = 0; waited < 25000 && !(await hasAction(LABEL.osk)); waited += 500) { await sleep(500); }
  check("the session is up and the keyboard button is there", await hasAction(LABEL.osk), true);
  check("the device keyboard button is there too", await hasAction(LABEL.device), true);

  // --- 1. The keyboard of the application is up, then a dialog opens.
  for (const d of DIALOGS) {
    await pressAction(LABEL.osk);
    await sleep(500);
    check(d.name + ": the keyboard is up before", (await oskState()).open, true);
    check(d.name + ": opens", await pressAction(d.open), true);
    await sleep(d.wait);
    check(d.name + ": is shown", await shown(d.id), true);
    check(d.name + ": the keyboard went down with it, and its button is not lit", await oskState(), { open: false, lit: false });
    const info = await dialogInfo(d.id, d.scope);
    check(d.name + ": fills the whole screen", info.rect, [0, 0, PHONE.width, PHONE.height]);
    check(d.name + ": has buttons to test", info.buttons > 0, true);
    check(d.name + ": none of its buttons is covered", info.covered, []);
    check(d.name + ": nothing of the keyboard shows at its bottom", info.keyboardAtTheBottom, false);
    await closeDialog(d.close);
    await sleep(600);
    check(d.name + ": closed", await shown(d.id), false);
    check(d.name + ": closing it does not bring the keyboard back", (await oskState()).open, false);
    await pressAction(LABEL.osk);
    await sleep(500);
    check(d.name + ": the keyboard button works again afterwards", (await oskState()).open, true);
    await pressAction(LABEL.osk);
    await sleep(500);
  }

  // --- 2. Whatever path might leave the keyboard up with a dialog open: the dialog is above it. The
  //        sheets that are asked for from inside a dialog (confirm, name, colour) are above the dialog.
  await pressAction(LABEL.paste);
  await sleep(700);
  const stack = await page.evaluate(() => ({
    dialog: Number(getComputedStyle(document.getElementById("clipboard-dialog")).zIndex),
    files: Number(getComputedStyle(document.getElementById("file-area-dialog")).zIndex),
    keyboard: Number(getComputedStyle(document.querySelector(".matgate-osk")).zIndex),
    name: Number(getComputedStyle(document.getElementById("name-dialog")).zIndex),
  }));
  check("a dialog is stacked above the keyboard of the application", stack.dialog > stack.keyboard && stack.files > stack.keyboard, true);
  check("the name sheet, asked from inside a dialog, stays above the dialog", stack.name > stack.dialog && stack.name > stack.files, true);
  await page.evaluate(() => document.querySelector(".matgate-osk").classList.add("open"));
  await sleep(400);
  const forced = await dialogInfo("clipboard-dialog", "button");
  check("with the keyboard forced up anyway, no button of the dialog is covered", forced.covered, []);
  check("and nothing of the keyboard shows at the bottom of it", forced.keyboardAtTheBottom, false);
  await page.evaluate(() => document.querySelector(".matgate-osk").classList.remove("open"));
  await closeDialog("clipboard-close");
  await sleep(500);

  // --- 3. The device keyboard of the session: its hidden input has the focus. A dialog that opens without
  //        taking the focus itself must still make it let go.
  await pressAction(LABEL.device);
  await sleep(500);
  const raised = await page.evaluate(() => !!(document.activeElement && document.activeElement.classList.contains("osk-input")));
  check("the device keyboard is up: its input has the focus", raised, true);
  check("the files dialog opens", await pressAction(LABEL.area), true);
  await sleep(1800);
  const dropped = await page.evaluate(source => {
    const matcher = new RegExp(source, "i");
    const button = [...document.querySelectorAll("#connection-tab-actions button")].find(x => matcher.test(x.title || x.getAttribute("aria-label") || ""));
    return { focused: !!(document.activeElement && document.activeElement.classList.contains("osk-input")), lit: !!(button && button.classList.contains("active")) };
  }, LABEL.device.source);
  check("a dialog that does not take the focus lets the device keyboard go", dropped, { focused: false, lit: false });
  await closeDialog("file-area-dialog-close");
  await sleep(600);

  // --- 4. iOS: the keyboard shrinks the visual viewport and may pan it up. A dialog ends where the
  //        keyboard begins - it used to be as tall as the screen, with its buttons under the keyboard.
  await pressAction(LABEL.paste);
  await sleep(700);
  check("the paste dialog is open for the keyboard tests", await shown("clipboard-dialog"), true);
  await page.evaluate(() => window.__visual(508, 0));
  await sleep(300);
  check("iOS keyboard: the height for dialogs is what it leaves", await cssVar("--matgate-viewport-height"), "508px");
  check("iOS keyboard: the paste dialog ends where the keyboard begins", await rectOf("clipboard-dialog"), [0, 0, PHONE.width, 508]);
  const ends = await page.evaluate(() => [...document.querySelectorAll("#clipboard-dialog button")].map(x => Math.round(x.getBoundingClientRect().bottom)));
  check("iOS keyboard: every button of it is above the keyboard", ends.length > 0 && ends.every(y => y <= 508), true);
  const field = await page.evaluate(() => Math.round(document.getElementById("clipboard-text").getBoundingClientRect().height));
  check("iOS keyboard: the field gave way to them and is still big enough to write in", field >= 96 && field < 400, true);
  await page.evaluate(() => window.__visual(508, 120));
  await sleep(300);
  check("iOS keyboard, page panned up by 120: the dialog follows the visible part",
    [await cssVar("--matgate-viewport-top"), await rectOf("clipboard-dialog")], ["120px", [0, 120, PHONE.width, 628]]);
  await page.evaluate(() => window.__visual(null, 0));
  await sleep(300);
  check("iOS keyboard gone: the dialog fills the screen again", await rectOf("clipboard-dialog"), [0, 0, PHONE.width, PHONE.height]);

  // --- 5. Android: the keyboard overlays the page, the visual viewport stays as it is.
  await page.evaluate(() => window.__keyboard(300));
  await sleep(300);
  check("Android keyboard: the height for dialogs is what it leaves", await cssVar("--matgate-viewport-height"), "544px");
  check("Android keyboard: the paste dialog ends where the keyboard begins", await rectOf("clipboard-dialog"), [0, 0, PHONE.width, 544]);
  await page.evaluate(() => window.__keyboard(0));
  await sleep(300);
  check("Android keyboard gone: the dialog fills the screen again", await rectOf("clipboard-dialog"), [0, 0, PHONE.width, PHONE.height]);
  await closeDialog("clipboard-close");
  await sleep(500);

  // --- 6. Wider than a phone a dialog is a box in the middle - the keyboard still goes down for it.
  await page.setViewportSize({ width: 900, height: 1100 });
  await sleep(1500);
  await pressAction(LABEL.osk);
  await sleep(500);
  check("tablet: the keyboard is up", (await oskState()).open, true);
  await pressAction(LABEL.paste);
  await sleep(700);
  check("tablet: the paste dialog is shown", await shown("clipboard-dialog"), true);
  check("tablet: the keyboard went down for it", (await oskState()).open, false);
  const box = await rectOf("clipboard-dialog");
  check("tablet: the dialog is a box, not the whole screen", box[2] - box[0] < 900 && box[3] - box[1] < 1100, true);
  await closeDialog("clipboard-close");
  await sleep(500);
  await page.setViewportSize(PHONE);

  check("no dialog of the browser was raised", prompts, []);
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
