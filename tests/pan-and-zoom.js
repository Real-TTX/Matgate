// A remote screen bigger than the display (a fixed resolution) on a touch device. There was no way
// around it: one finger moves the pointer and only pushes the view along when it reaches the edge, and
// two fingers could zoom but not move. Now two fingers pinch around themselves and drag the view - and
// when there is nothing to drag, because the whole screen fits, the same drag scrolls the remote.
const { chromium } = require("playwright-core");
const rig = require("./toy-vnc-rig");
const { BASE, REMOTE, sleep } = rig;
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const within = (got, want, tolerance) => Math.abs(got - want) <= tolerance;

(async () => {
  const stopServer = await rig.startServer();
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({
    viewport: { width: 768, height: 800 }, deviceScaleFactor: 2, hasTouch: true, isMobile: true,
    userAgent: "Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1",
  });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);
  const id = await rig.ensureConnection(page);
  await page.evaluate(sid => {
    localStorage.setItem("matgate.display.res.v3", JSON.stringify({ [sid]: "1280x720" }));
    localStorage.setItem("matgate.pointer.mode.v2", "direct");
  }, id);
  await page.goto(BASE + "/connect/" + id, { waitUntil: "networkidle" });
  await sleep(7000);

  const cdp = await ctx.newCDPSession(page);
  const touch = (type, points) => cdp.send("Input.dispatchTouchEvent", { type, touchPoints: points.map((p, i) => ({ x: p.x, y: p.y, id: i })) });
  const view = () => page.evaluate(() => {
    const d = document.querySelector(".connection-panel:not(.hidden) .guac-display");
    const scaler = document.querySelector(".connection-panel:not(.hidden) .guac-scaler");
    const r = d.getBoundingClientRect();
    return { left: d.scrollLeft, top: d.scrollTop, boxLeft: r.left, boxTop: r.top, sw: d.scrollWidth, cw: d.clientWidth, sh: d.scrollHeight, ch: d.clientHeight, scalerWidth: parseFloat(scaler.style.width) || 0 };
  });
  const twoFingers = async (from, to, steps) => {
    await touch("touchStart", from);
    await sleep(60);
    for (let i = 1; i <= steps; i++) {
      await touch("touchMove", from.map((p, k) => ({ x: p.x + (to[k].x - p.x) * i / steps, y: p.y + (to[k].y - p.y) * i / steps })));
      await sleep(30);
    }
    await touch("touchEnd", []);
    await sleep(400);
  };

  // --- The screen is bigger than the display.
  const start = await view();
  console.log("     the view: " + JSON.stringify(start));
  check("the remote screen is bigger than the display", start.sw > start.cw && start.sh > start.ch, true);

  // --- Two fingers dragging pan the view.
  await twoFingers([{ x: 400, y: 600 }, { x: 480, y: 600 }], [{ x: 280, y: 500 }, { x: 360, y: 500 }], 10);
  const panned = await view();
  console.log("     after dragging 120 left and 100 up: " + JSON.stringify({ left: panned.left, top: panned.top }));
  check("dragging two fingers left moves the view right by as much", within(panned.left, 120, 6), true);
  check("and up moves it down by as much", within(panned.top, 100, 6), true);

  // --- Pinching zooms around the fingers: what is under them stays under them.
  const contentUnder = (v, x, y) => ({ x: (v.left + x - v.boxLeft) / (v.scalerWidth / REMOTE.width), y: (v.top + y - v.boxTop) / (v.scalerWidth / REMOTE.width) });
  const before = await view();
  const wasUnder = contentUnder(before, 390, 600);
  await twoFingers([{ x: 350, y: 600 }, { x: 430, y: 600 }], [{ x: 310, y: 600 }, { x: 470, y: 600 }], 12);
  const zoomed = await view();
  const nowUnder = contentUnder(zoomed, 390, 600);
  console.log("     zoomed: picture " + before.scalerWidth + " -> " + zoomed.scalerWidth + "; under the fingers " + JSON.stringify(wasUnder) + " -> " + JSON.stringify(nowUnder));
  check("pinching apart zoomed in", zoomed.scalerWidth > before.scalerWidth * 1.6, true);
  check("and what was under the fingers is still under them", within(nowUnder.x, wasUnder.x, 6) && within(nowUnder.y, wasUnder.y, 6), true);

  // --- Pinching together far enough that the whole screen fits: nothing left to pan.
  await twoFingers([{ x: 200, y: 600 }, { x: 560, y: 600 }], [{ x: 385, y: 600 }, { x: 395, y: 600 }], 14);
  await twoFingers([{ x: 200, y: 600 }, { x: 560, y: 600 }], [{ x: 385, y: 600 }, { x: 395, y: 600 }], 14);
  const small = await view();
  console.log("     zoomed out: " + JSON.stringify({ picture: small.scalerWidth, container: small.cw }));
  check("zoomed out until the whole screen fits", small.sw <= small.cw + 2 && small.sh <= small.ch + 2, true);

  // --- ... and then the same drag turns the wheel of the remote, where the fingers are.
  await rig.clear();
  const picture = await page.evaluate(() => { const r = document.querySelector(".connection-panel:not(.hidden) .guac-scaler > div").getBoundingClientRect(); return { left: r.left, top: r.top, width: r.width, height: r.height }; });
  const fx = Math.round(picture.left + picture.width / 2);
  const fy = Math.round(picture.top + picture.height / 2);
  await twoFingers([{ x: fx - 40, y: fy - 20 }, { x: fx + 40, y: fy - 20 }], [{ x: fx - 40, y: fy + 80 }, { x: fx + 40, y: fy + 80 }], 10);
  const ticks = (await rig.pointers()).filter(e => (e.buttons & 24) !== 0);
  const remoteAt = (x, y) => ({ x: Math.round((x - picture.left) * REMOTE.width / picture.width), y: Math.round((y - picture.top) * REMOTE.height / picture.height) });
  const want = remoteAt(fx, fy - 20);
  const wantEnd = remoteAt(fx, fy + 80);
  console.log("     wheel ticks: " + ticks.length + " at " + [...new Set(ticks.map(e => e.x + "," + e.y))].join(" ") + " (the fingers went from " + JSON.stringify(want) + " to " + JSON.stringify(wantEnd) + ")");
  check("with nothing to pan, two fingers dragging turn the wheel", ticks.length > 0, true);
  check("over the picture, on the way the fingers went", ticks.every(e => within(e.x, want.x, 30) && e.y >= want.y - 30 && e.y <= wantEnd.y + 30), true);

  await rig.removeConnection(page);
  stopServer();
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
