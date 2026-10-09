// A key that is held when the keyboard leaves a remote session never reports its release to the
// page: Ctrl for Ctrl+Tab (which switches BROWSER tabs), Alt for Alt+Tab, the Windows key. The
// remote desktop then keeps it pressed - every click is a Ctrl+click, every letter a shortcut -
// until the key is pressed once more. A held letter even keeps repeating, because Guacamole's own
// auto-repeat runs until the release it never gets.
//
// The test watches what really goes down the tunnel: the "key" instructions of the Guacamole
// protocol, as the browser sends them over the WebSocket.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const CTRL = 65507;
const SHIFT = 65505;
const LETTER_A = 97;

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1400, height: 950 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });

  // Everything the browser sends through the tunnel, in order.
  const frames = [];
  page.on("websocket", ws => ws.on("framesent", f => frames.push(typeof f.payload === "string" ? f.payload : "")));
  const keysSince = mark => {
    const out = [];
    const re = /[0-9]+[.]key,[0-9]+[.]([0-9]+),[0-9]+[.]([01]);/g;
    let m;
    const text = frames.slice(mark).join("");
    while ((m = re.exec(text))) { out.push({ keysym: Number(m[1]), down: m[2] === "1" }); }
    return out;
  };
  // Is the key still down at the end of what was sent since the mark?
  const heldAfter = (mark, keysym) => {
    const events = keysSince(mark).filter(e => e.keysym === keysym);
    return events.length ? events[events.length - 1].down : false;
  };

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);

  // An earlier test hid sections of the home page - the connection card is needed here.
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(800);
  await page.evaluate(() => {
    document.querySelectorAll("#home-order-list [data-order-visible]").forEach(k => {
      k.checked = true;
      k.dispatchEvent(new Event("change", { bubbles: true }));
    });
    document.querySelector("[name='homeSections']").form.requestSubmit();
  });
  await sleep(1800);
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1200);

  const connect = page.locator("a,button").filter({ hasText: /^ *(Connect|Verbinden) *$/ }).first();
  check("there is a connection to open", await connect.count() > 0, true);
  await connect.click();
  await sleep(6000);
  const standing = await page.evaluate(() => Array.from(document.querySelectorAll(".connection-overlay")).filter(o => !o.classList.contains("hidden")).length);
  check("the session stands", standing, 0);

  // The surface of the session takes the keyboard.
  const surface = page.locator(".connection-panel:not(.hidden)").first();
  const focusSession = async () => {
    await surface.click({ position: { x: 300, y: 200 } });
    await sleep(300);
  };
  await focusSession();

  // --- Ordinary use is untouched: pressed once, released once.
  let mark = frames.length;
  await page.keyboard.down("Control");
  await sleep(200);
  await page.keyboard.up("Control");
  await sleep(300);
  const normal = keysSince(mark).filter(e => e.keysym === CTRL);
  check("Ctrl pressed and released goes down the tunnel as down, up", normal.map(e => e.down), [true, false]);

  // --- Each way the keyboard can leave the session while a key is down. Every one of them used to
  // leave the key down on the remote side.
  const leaveBy = {
    "the window loses focus (Alt+Tab, another app)": () => page.evaluate(() => window.dispatchEvent(new Event("blur"))),
    "the page is hidden (Ctrl+Tab to another browser tab)": () => page.evaluate(() => {
      Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
      document.dispatchEvent(new Event("visibilitychange"));
      delete document.hidden;
    }),
    "focus moves to something outside the session": () => page.evaluate(() => {
      const target = document.querySelector("header a, header button");
      target.focus();
    }),
    "another tab is opened - the new-connection tab": () => page.evaluate(() => {
      document.querySelector("#new-connection-tab .session-tab-main").click();
    }),
  };
  for (const [how, leave] of Object.entries(leaveBy)) {
    await page.evaluate(() => document.querySelector(".connection-panel") && document.querySelector(".connection-panel").focus());
    await focusSession();
    mark = frames.length;
    await page.keyboard.down("Control");
    await sleep(250);
    check("Ctrl goes down in the session (" + how + ")", heldAfter(mark, CTRL), true);
    await leave();
    await sleep(400);
    check("and is let go when " + how, heldAfter(mark, CTRL), false);
    await page.keyboard.up("Control");
    await sleep(200);
    // Back into the session, for the next round.
    await page.evaluate(() => {
      const tab = document.querySelector("#session-tabs .session-tab--connection .session-tab-main");
      if (tab) { tab.click(); }
    });
    await sleep(600);
  }

  // --- A letter held at that moment must not keep repeating on the remote side.
  await focusSession();
  mark = frames.length;
  await page.keyboard.down("a");
  await sleep(900);   // past Guacamole's half-second delay: the auto-repeat is running now
  await page.evaluate(() => window.dispatchEvent(new Event("blur")));
  await sleep(300);
  const afterBlur = frames.length;
  await sleep(700);
  const stillRepeating = keysSince(afterBlur).filter(e => e.keysym === LETTER_A).length;
  check("a held letter has stopped repeating once the keyboard has left", stillRepeating, 0);
  check("and it is not left down", heldAfter(mark, LETTER_A), false);
  await page.keyboard.up("a");
  await sleep(200);

  // --- Ctrl is STILL physically held when the keyboard comes back: its next key is Ctrl+key. Letting go
  // on the way out must not make the keyboard forget that it was pressed once, or Ctrl+C after
  // switching back would arrive as a bare C.
  await page.evaluate(() => document.querySelector("#session-tabs .session-tab--connection .session-tab-main").click());
  await sleep(500);
  await focusSession();
  mark = frames.length;
  await page.keyboard.down("Control");
  await sleep(200);
  await page.evaluate(() => window.dispatchEvent(new Event("blur")));
  await sleep(300);
  check("let go on the way out although the finger stays down", heldAfter(mark, CTRL), false);
  // Guacamole presses a modifier it only learns of from the flags of a key event for the length of that
  // chord and lets it go afterwards - so what counts is that Ctrl is down BEFORE the C arrives.
  const backMark = frames.length;
  await page.keyboard.press("c");
  await sleep(300);
  const chord = keysSince(backMark)
    .filter(e => e.keysym === CTRL || e.keysym === 99)
    .map(e => (e.keysym === CTRL ? "ctrl" : "c") + (e.down ? " down" : " up"));
  console.log("     the chord: " + JSON.stringify(chord));
  check("back, with Ctrl still held: the next key arrives as Ctrl+C", chord.slice(0, 3), ["ctrl down", "c down", "c up"]);
  await page.keyboard.up("Control");
  await sleep(300);
  check("and letting go of it lets go", heldAfter(mark, CTRL), false);

  // --- Coming back: the first key typed is just that key, not a shortcut.
  await page.evaluate(() => document.querySelector("#session-tabs .session-tab--connection .session-tab-main").click());
  await sleep(500);
  await focusSession();
  mark = frames.length;
  await page.keyboard.press("b");
  await sleep(300);
  const typed = keysSince(mark);
  check("the first letter after coming back goes without Ctrl", typed.some(e => e.keysym === CTRL && e.down), false);
  check("and it arrives", typed.filter(e => e.keysym === 98).map(e => e.down), [true, false]);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
