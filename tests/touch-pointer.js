// Where does the remote pointer go when a finger taps, swipes or pinches? Asked of a VNC server that
// writes down what it receives (toy-vnc.js), so the answer is not guessed from the picture.
//
// Two things used to be wrong. The touchpad mode moved a pointer of its own that started in the top left
// corner and knew nothing of where the remote pointer was - after a tap in direct mode, after the mouse,
// the first swipe threw the cursor to the top. And a tap on a pinch-zoomed view landed where the same
// tap would have landed on the un-zoomed picture, because the emulators do their arithmetic from the
// layout and a pinch zoom is only a transform.
const { chromium } = require("playwright-core");
const rig = require("./toy-vnc-rig");
const { BASE, REMOTE, sleep } = rig;
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const near = (got, want, tolerance) => !!got && Math.abs(got.x - want.x) <= tolerance && Math.abs(got.y - want.y) <= tolerance;

(async () => {
  const stopServer = await rig.startServer();
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({
    viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, hasTouch: true, isMobile: true,
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
  });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);
  const id = await rig.ensureConnection(page);
  check("there is a connection to the remote", typeof id, "string");

  const cdp = await ctx.newCDPSession(page);
  const touch = (type, points) => cdp.send("Input.dispatchTouchEvent", { type, touchPoints: points.map((p, i) => ({ x: p.x, y: p.y, id: i })) });
  // The rendered picture: where the remote desktop really is on the screen right now.
  const picture = () => page.evaluate(() => {
    const r = document.querySelector(".connection-panel:not(.hidden) .guac-scaler > div").getBoundingClientRect();
    return { left: r.left, top: r.top, width: r.width, height: r.height };
  });
  const under = async (x, y) => {
    const r = await picture();
    return { x: Math.round((x - r.left) * REMOTE.width / r.width), y: Math.round((y - r.top) * REMOTE.height / r.height) };
  };
  const open = async mode => {
    await page.evaluate(m => { localStorage.setItem("matgate.pointer.mode.v2", m); }, mode);
    await rig.clear();
    await page.goto(BASE + "/connect/" + id, { waitUntil: "networkidle" });
    await sleep(7000);
  };
  const toggleMode = async () => {
    await page.locator("#connection-tab-actions .tab-action-more-trigger").tap();
    await sleep(500);
    await page.locator(".tab-action-overflow-panel button").filter({ hasText: /Pointer/ }).first().tap();
    await sleep(500);
  };

  // --- Direct touch: a tap goes where the finger is.
  await open("direct");
  const first = (await rig.pointers())[0];
  check("a session on a touch device puts its pointer in the middle when it comes up", near(first, { x: REMOTE.width / 2, y: REMOTE.height / 2 }, 4), true);

  await rig.clear();
  await page.touchscreen.tap(200, 480);
  await sleep(500);
  check("a tap goes where the finger is", near(await rig.lastPointer(), await under(200, 480), 3), true);

  // Pinch out around the middle of the screen: the picture is a transform now.
  await touch("touchStart", [{ x: 150, y: 420 }, { x: 240, y: 420 }]);
  await sleep(60);
  for (let i = 1; i <= 12; i++) { await touch("touchMove", [{ x: 150 - i * 8, y: 420 }, { x: 240 + i * 8, y: 420 }]); await sleep(30); }
  await touch("touchEnd", []);
  await sleep(500);
  const zoom = await page.evaluate(() => {
    const m = /scale[(]([0-9.]+)[)]/.exec(document.querySelector(".connection-panel:not(.hidden) .guac-scaler").style.transform || "");
    return m ? Number(m[1]) : 1;
  });
  check("the pinch zoomed the picture in", zoom > 1.5, true);

  await rig.clear();
  await page.touchscreen.tap(200, 300);
  await sleep(500);
  const wantZoomed = await under(200, 300);
  const gotZoomed = await rig.lastPointer();
  console.log("     zoomed tap: finger 200,300 -> expected " + JSON.stringify(wantZoomed) + " got " + JSON.stringify(gotZoomed));
  check("a tap on the zoomed picture goes where the finger is, not where it would be un-zoomed", near(gotZoomed, wantZoomed, 3), true);
  await rig.clear();
  await page.touchscreen.tap(60, 700);
  await sleep(500);
  check("and a tap in another corner of it as well", near(await rig.lastPointer(), await under(60, 700), 3), true);

  // --- Touchpad: the first swipe carries on from where the pointer is.
  await open("direct");
  await rig.clear();
  await page.touchscreen.tap(195, 480);
  await sleep(500);
  const tapped = await rig.lastPointer();
  await toggleMode();
  check("the pointer mode is the touchpad now", await page.evaluate(() => localStorage.getItem("matgate.pointer.mode.v2")), "touchpad");
  await rig.clear();
  await touch("touchStart", [{ x: 150, y: 480 }]);
  await sleep(60);
  for (let i = 1; i <= 6; i++) { await touch("touchMove", [{ x: 150 + i * 6, y: 480 }]); await sleep(40); }
  await touch("touchEnd", []);
  await sleep(500);
  const swipe = await rig.pointers();
  console.log("     tapped at " + JSON.stringify(tapped) + ", swipe " + swipe.map(e => e.x + "," + e.y).join(" "));
  check("a swipe after a tap in direct mode moves the pointer", swipe.length >= 2, true);
  check("it starts from where the pointer was - not from the top left corner", near(swipe[0], tapped, 80), true);
  check("and goes right with the finger", swipe.length > 1 && swipe[swipe.length - 1].x > swipe[0].x && Math.abs(swipe[swipe.length - 1].y - swipe[0].y) <= 3, true);

  // --- Two fingers scrolling: the wheel turns where the pointer is, and the pointer stays there.
  const before = await rig.lastPointer();
  await rig.clear();
  await touch("touchStart", [{ x: 150, y: 400 }, { x: 240, y: 400 }]);
  await sleep(60);
  for (let i = 1; i <= 10; i++) { await touch("touchMove", [{ x: 150, y: 400 + i * 14 }, { x: 240, y: 400 + i * 14 }]); await sleep(40); }
  await touch("touchEnd", []);
  await sleep(500);
  const ticks = (await rig.pointers()).filter(e => (e.buttons & 24) !== 0);
  const everything = await rig.pointers();
  console.log("     before " + JSON.stringify(before) + ", ticks " + ticks.length + " at " + [...new Set(ticks.map(e => e.x + "," + e.y))].join(" "));
  check("two fingers dragging turn the wheel", ticks.length > 0, true);
  check("at the pointer, not somewhere the fingers happen to be", ticks.every(e => near(e, before, 3)), true);
  check("and the pointer is still there afterwards", near(everything[everything.length - 1], before, 3), true);

  await rig.removeConnection(page);
  stopServer();
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
