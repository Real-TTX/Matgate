// On a phone the device keyboard stays open when a button of the key bar under a session is tapped (Esc,
// Ctrl, ...): the hidden field it types into keeps the focus. Tapped for real - touch events, as a finger
// gives them - in a VNC session and an SSH terminal of Matgate's own engine.
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
  const ctx = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);
  const servers = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });

  const round = async (name, label, framePattern) => {
    const server = servers.find(s => s.name === name);
    if (!server) {
      console.log("     (no connection " + name + " in this stack: skipped)");
      return;
    }

    await page.evaluate(id => window.MatgateOpenServerTab(id), server.id);
    let frame = null;
    for (let i = 0; i < 40 && !frame; i++) {
      await sleep(500);
      frame = page.frames().find(f => framePattern.test(f.url()));
    }
    await sleep(3000);
    if (!frame) {
      check(label + "the session opens", false, true);
      return;
    }

    // The device keyboard up: the hidden field has the focus.
    // A headless browser has no keyboard that would bring the bar up: it is shown as its button does.
    await frame.evaluate(() => { document.getElementById("keys").classList.add("open"); const sink = document.getElementById("sink") || document.querySelector(".xterm-helper-textarea"); sink.focus(); });
    await sleep(300);
    const field = () => frame.evaluate(() => { const active = document.activeElement; return active ? (active.id || active.className || active.tagName) : ""; });
    const before = await field();
    const button = frame.locator("#keys button:not(.keys-setup)").first();
    check(label + "there is a key bar with buttons", await button.count(), 1);
    await button.tap();
    await sleep(400);
    check(label + "a tap on a button of the bar keeps the keyboard (the field keeps the focus)", await field(), before);
    await frame.locator("#keys button:not(.keys-setup)").nth(1).tap();
    await sleep(400);
    check(label + "and a second one as well", await field(), before);
    await page.evaluate(() => { const button = document.querySelector("#connection-tab-actions [data-action='disconnect']"); if (button) { button.click(); } });
    await sleep(800);
  };

  await round("Test-VNC", "VNC: ", /\/display\/.+\/screen/);
  // An SSH connection that opens with Matgate's own terminal (not through Guacamole).
  const ownSsh = servers.find(s => (s.protocol || "").toUpperCase() === "SSH" && s.engine === "matgate");
  await round(ownSsh ? ownSsh.name : "(none)", "SSH: ", /\/ssh\/.+\/terminal/);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
