// With a mouse on a remote screen bigger than the display: when the view moves under a mouse that
// does not, the remote pointer follows at once. It used to stay where it was until the mouse moved
// next - and then it jumped to where the mouse really was.
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
  const ctx = await b.newContext({ viewport: { width: 1000, height: 700 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);
  const id = await rig.ensureConnection(page);
  await page.evaluate(sid => { localStorage.setItem("matgate.display.res.v3", JSON.stringify({ [sid]: "1280x720" })); }, id);
  await page.goto(BASE + "/connect/" + id, { waitUntil: "networkidle" });
  await sleep(7000);

  // What the picture says is under a point of the screen, right now.
  const under = (x, y) => page.evaluate(([px, py, w, h]) => {
    const r = document.querySelector(".connection-panel:not(.hidden) .guac-scaler > div").getBoundingClientRect();
    return { x: Math.round((px - r.left) * w / r.width), y: Math.round((py - r.top) * h / r.height) };
  }, [x, y, REMOTE.width, REMOTE.height]);
  const scrollBy = (dx, dy) => page.evaluate(([x, y]) => {
    const d = document.querySelector(".connection-panel:not(.hidden) .guac-display");
    d.scrollLeft += x; d.scrollTop += y;
  }, [dx, dy]);

  await rig.clear();
  await page.mouse.move(500, 400);
  await page.mouse.move(520, 410);
  await sleep(300);
  check("the mouse is where the remote pointer is", near(await rig.lastPointer(), await under(520, 410), 2), true);

  // --- The view moves, the mouse does not.
  await rig.clear();
  await scrollBy(0, 150);
  await sleep(400);
  const afterScroll = await rig.lastPointer();
  const wantScroll = await under(520, 410);
  console.log("     after scrolling the view 150 down: pointer " + JSON.stringify(afterScroll && { x: afterScroll.x, y: afterScroll.y }) + ", under the mouse now " + JSON.stringify(wantScroll));
  check("scrolling the view under a still mouse moves the remote pointer with it", near(afterScroll, wantScroll, 2), true);

  // --- Panning with the middle button: the moves are the panning's, the pointer catches up when it ends.
  await rig.clear();
  await page.mouse.move(600, 450);
  await page.mouse.down({ button: "middle" });
  await page.mouse.move(540, 400, { steps: 6 });
  await page.mouse.move(480, 350, { steps: 6 });
  await page.mouse.up({ button: "middle" });
  await sleep(400);
  const afterPan = await rig.lastPointer();
  const wantPan = await under(480, 350);
  console.log("     after a middle-button pan ending at 480,350: pointer " + JSON.stringify(afterPan && { x: afterPan.x, y: afterPan.y }) + ", under the mouse now " + JSON.stringify(wantPan));
  check("after a pan the remote pointer is where the mouse is, without another movement", near(afterPan, wantPan, 2), true);

  // --- A mouse that is not over the picture has nothing to follow.
  await page.mouse.move(500, 20);
  await sleep(200);
  await rig.clear();
  await scrollBy(0, -80);
  await sleep(400);
  check("with the mouse elsewhere, scrolling sends nothing", (await rig.pointers()).length, 0);

  await rig.removeConnection(page);
  stopServer();
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
