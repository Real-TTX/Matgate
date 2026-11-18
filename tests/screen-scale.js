// A session of Matgate's own engine on a phone: the scale of "fit to window" and the device's keyboard.
//
// "Fit to window" used to mean the device's own pixels - on a phone a desktop of 1170 x 2532 with letters
// nobody can read, and the only way out was to pick a resolution by its numbers. Now it has a scale, as the
// Guacamole sessions had: 100 % is one pixel of the machine for one of the page, 50 % a desktop twice as
// large as the window, 150 % a smaller one shown enlarged.
//
// And the keyboard: a session is a page of its own inside the shell and never learned that the device's
// keyboard was up - it lay over the key bar and the lower part of the picture, and there was no way to what
// was behind it. Now the page ends above the keyboard: the key bar sits on top of it, the picture keeps its
// size and is moved with two fingers (or follows the pointer), or - an option - the machine gets a resolution
// that fits the room.
//
// Chromium has no soft keyboard, so the way an iPhone reports one is faked: the visual viewport shrinks.
// That tests what the application does with the report - not what a real iPhone reports.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const near = (a, b, tolerance) => Math.abs(a - b) <= tolerance;

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const admin = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await admin.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);
  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  const connection = async (name, protocol, fields) => {
    const existing = (await servers()).find(s => s.name === name);
    await page.goto(BASE + (existing ? "/admin/servers/" + existing.id : "/admin/servers/new"), { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(([n, p, f]) => {
      document.querySelector("[data-protocol-option='" + p + "']").click();
      const set = (field, value) => {
        const all = [...document.querySelectorAll("[name='" + field + "']")];
        const el = all.find(e => !e.disabled) || all[0];
        el.value = value;
        el.dispatchEvent(new Event("change", { bubbles: true }));
      };
      set("name", n);
      Object.entries(f).forEach(([field, value]) => set(field, value));
      document.querySelector("[name='name']").form.requestSubmit();
    }, [name, protocol, fields]).catch(() => { /* the submit navigated before the call returned */ });
    await sleep(1500);
    return (await servers()).find(s => s.name === name);
  };
  const vnc = await connection("Test-Scale-VNC", "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "" });

  const state = view => view.evaluate(() => window.MatgateScreen.state());
  const picture = view => view.evaluate(() => [document.getElementById("screen").width, document.getElementById("screen").height]);
  const pictureIs = async (view, width, height, tolerance) => {
    let size = [0, 0];
    for (let i = 0; i < 50; i++) {
      size = await picture(view);
      if (near(size[0], width, tolerance) && near(size[1], height, tolerance)) { return true; }
      await sleep(300);
    }
    return size;
  };
  const started = async view => { for (let i = 0; i < 200 && (await view.evaluate(() => window.__screen.frames)) < 2; i++) { await sleep(300); } };

  // ============================================================================================ a computer
  const view = await admin.newPage();
  view.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await view.goto(BASE + "/display/" + vnc.id + "/screen", { waitUntil: "domcontentloaded" });
  await started(view);
  const stage = await view.evaluate(() => { const box = document.getElementById("stage").getBoundingClientRect(); return [Math.round(box.width), Math.round(box.height)]; });
  let now = await state(view);
  check("on a computer the scale starts at the machine's pixels for this screen's", [now.scale, now.resolution], [100, "auto"]);
  check("fit to window at 100 % is the size of the picture area", await pictureIs(view, stage[0], stage[1], 2), true);
  await view.evaluate(() => window.MatgateScreen.setScale(50));
  check("50 %: the machine's screen is twice the window, shown at half size", await pictureIs(view, stage[0] * 2, stage[1] * 2, 4), true);
  check("and still fills the picture area", await view.evaluate(() => {
    const canvas = document.getElementById("screen").getBoundingClientRect();
    const area = document.getElementById("stage").getBoundingClientRect();
    return Math.abs(canvas.width - area.width) <= 2 && Math.abs(canvas.height - area.height) <= 2;
  }), true);
  await view.evaluate(() => window.MatgateScreen.setScale(150));
  check("150 %: a smaller screen, shown enlarged", await pictureIs(view, stage[0] / 1.5, stage[1] / 1.5, 4), true);
  check("the page says what the machine would be asked for at a scale", await view.evaluate(() => window.MatgateScreen.fitAt(75)).then(size => near(size.fitWidth, stage[0] / 0.75, 2) && near(size.fitHeight, stage[1] / 0.75, 2)), true);
  await view.evaluate(() => window.MatgateScreen.setResolution("native"));
  await sleep(600);
  await view.evaluate(() => window.MatgateScreen.setScale(100));
  check("choosing a scale chooses \"fit to window\" as well", (await state(view)).resolution, "auto");
  check("back at 100 %", await pictureIs(view, stage[0], stage[1], 2), true);
  await view.reload({ waitUntil: "domcontentloaded" });
  await started(view);
  check("the scale is kept for the connection", (await state(view)).scale, 100);
  await view.close();

  // ============================================================================================ a phone
  const PHONE = { width: 390, height: 844 };
  const phone = await b.newContext({ viewport: PHONE, deviceScaleFactor: 3, hasTouch: true, isMobile: true, storageState: await admin.storageState() });
  // The phone has not been here before: what the computer above chose is not its own.
  await phone.addInitScript(id => {
    if (!sessionStorage.getItem("fresh")) {
      sessionStorage.setItem("fresh", "1");
      localStorage.removeItem("matgate.screen.scale." + id);
      localStorage.removeItem("matgate.screen.keyboard.resize");
    }
  }, vnc.id);
  await phone.addInitScript(() => {
    // An iPhone's keyboard: the visual viewport shrinks, the page itself keeps its size.
    const visual = window.visualViewport;
    const seen = { height: null, top: 0 };
    Object.defineProperty(visual, "height", { configurable: true, get: () => (seen.height === null ? window.innerHeight : seen.height) });
    Object.defineProperty(visual, "offsetTop", { configurable: true, get: () => seen.top });
    window.__visual = (height, top) => { seen.height = height; seen.top = top || 0; visual.dispatchEvent(new Event("resize")); };
  });
  const shell = await phone.newPage();
  shell.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await shell.goto(BASE + "/connect/" + vnc.id, { waitUntil: "networkidle" });
  let frame = null;
  for (let i = 0; i < 60 && !frame; i++) { frame = shell.frames().find(f => /\/display\/[0-9a-f-]+\/screen/.test(f.url())); if (!frame) { await sleep(250); } }
  await started(frame);
  now = await state(frame);
  const phoneStage = await frame.evaluate(() => { const box = document.getElementById("stage").getBoundingClientRect(); return [Math.round(box.width), Math.round(box.height)]; });
  check("a phone starts at 50 %: twice its own points, not three times its pixels", [now.scale, near(now.fit.fitWidth, phoneStage[0] * 2, 2)], [50, true]);
  check("the machine's screen has the shape of the phone's", await pictureIs(frame, phoneStage[0] * 2, phoneStage[1] * 2, 6), true);

  // --- The dialog of the session toolbar: steps to tap, a slider, and what the keyboard does.
  const openDialog = () => shell.evaluate(() => {
    const find = () => document.querySelector("#connection-tab-actions [data-action='resolution'], .tab-action-overflow-panel [data-action='resolution']");
    let button = find();
    if (!button) {
      const more = document.querySelector(".tab-action-more-trigger");
      if (more) { more.click(); }
      button = find();
    }
    if (!button) { return false; }
    button.click();
    return true;
  });
  check("the session toolbar opens the resolution dialog", await openDialog(), true);
  await sleep(500);
  check("the dialog offers a scale in steps", await shell.evaluate(() => [...document.querySelectorAll(".resolution-scale-preset")].filter(x => x.offsetParent).map(x => x.textContent)), ["50 %", "75 %", "100 %", "125 %", "150 %", "200 %"]);
  check("with the current one marked, and what it comes to", await shell.evaluate(() => [
    (document.querySelector(".resolution-scale-preset.active") || {}).textContent || "",
    /\d+ × \d+/.test((document.querySelector(".resolution-scale-note") || {}).textContent || "")]), ["50 %", true]);
  check("and what the device's keyboard does to the picture", await shell.evaluate(() => (document.querySelector("[data-keyboard-resize]") || {}).dataset?.keyboardResize || "missing"), "0");
  await shell.evaluate(() => [...document.querySelectorAll(".resolution-scale-preset")].find(x => x.textContent === "75 %").click());
  await sleep(400);
  check("a step sets the scale and closes the dialog", [(await state(frame)).scale, await shell.evaluate(() => document.getElementById("resolution-dialog").classList.contains("hidden"))], [75, true]);
  check("the machine follows", await pictureIs(frame, phoneStage[0] / 0.75, phoneStage[1] / 0.75, 6), true);

  // --- The keyboard comes up: 336 points of the phone's 844.
  const before = {
    picture: await picture(frame),
    shown: await frame.evaluate(() => Math.round(document.getElementById("screen").getBoundingClientRect().height)),
    barShown: await frame.evaluate(() => getComputedStyle(document.getElementById("keys")).display),
  };
  await frame.evaluate(() => document.getElementById("sink").focus());
  await shell.evaluate(height => window.__visual(height, 0), PHONE.height - 336);
  await sleep(500);
  const keyboardTop = PHONE.height - 336;
  const withKeyboard = await frame.evaluate(() => {
    const frameTop = window.frameElement.getBoundingClientRect().top;
    const keys = document.getElementById("keys").getBoundingClientRect();
    const stage = document.getElementById("stage").getBoundingClientRect();
    const canvas = document.getElementById("screen").getBoundingClientRect();
    return {
      inset: window.MatgateScreen.state().keyboardInset,
      frameBottom: Math.round(window.frameElement.getBoundingClientRect().bottom),
      barDisplay: getComputedStyle(document.getElementById("keys")).display,
      barBottom: Math.round(frameTop + keys.bottom),
      stageBottom: Math.round(frameTop + stage.bottom),
      shown: Math.round(canvas.height),
      stageHeight: Math.round(stage.height),
    };
  });
  check("without the keyboard the key bar of an embedded session is away", before.barShown, "none");
  check("the page learns how much of it the keyboard covers", [withKeyboard.inset > 200, withKeyboard.inset], [true, withKeyboard.frameBottom - keyboardTop]);
  check("the key bar comes with it and sits on top of the keyboard", [withKeyboard.barDisplay, near(withKeyboard.barBottom, keyboardTop, 2)], ["flex", true]);
  check("the picture area ends above the key bar", withKeyboard.stageBottom <= withKeyboard.barBottom, true);
  check("the picture keeps its size - it is taller than what is seen of it now", [near(withKeyboard.shown, before.shown, 2), withKeyboard.shown > withKeyboard.stageHeight + 100], [true, true]);
  await sleep(1200);
  check("and the machine keeps its resolution", await picture(frame), before.picture);

  // --- What is behind the keyboard is reached with two fingers.
  const transformY = () => frame.evaluate(() => { const match = /translate\(([-\d.]+)px, ([-\d.]+)px\)/.exec(document.getElementById("frame").style.transform); return match ? Math.round(Number(match[2])) : null; });
  const twoFingers = (fromY, toY) => frame.evaluate(async ([from, to]) => {
    const stage = document.getElementById("stage");
    const box = stage.getBoundingClientRect();
    const fire = (type, id, x, y) => stage.dispatchEvent(new PointerEvent(type, { pointerId: id, pointerType: "touch", clientX: x, clientY: y, bubbles: true, cancelable: true, isPrimary: id === 1 }));
    const x = box.left + box.width / 2;
    fire("pointerdown", 1, x - 30, box.top + from);
    fire("pointerdown", 2, x + 30, box.top + from);
    for (let step = 1; step <= 8; step++) {
      const y = box.top + from + (to - from) * step / 8;
      fire("pointermove", 1, x - 30, y);
      fire("pointermove", 2, x + 30, y);
      await new Promise(r => setTimeout(r, 20));
    }
    fire("pointerup", 1, x - 30, box.top + to);
    fire("pointerup", 2, x + 30, box.top + to);
  }, [fromY, toY]);
  // (First a tap, which on the touchpad clicks where the cursor is - the middle of the screen, inside the
  // desktop's terminal. A view that is at its end scrolls on the machine, and a wheel over the bare desktop
  // makes the window manager of the test desktop switch to another workspace.)
  await frame.evaluate(async () => {
    const stage = document.getElementById("stage");
    const box = stage.getBoundingClientRect();
    const fire = type => stage.dispatchEvent(new PointerEvent(type, { pointerId: 1, pointerType: "touch", clientX: box.left + 100, clientY: box.top + 100, bubbles: true, cancelable: true, isPrimary: true }));
    fire("pointerdown");
    await new Promise(r => setTimeout(r, 60));
    fire("pointerup");
  });
  await sleep(400);
  const top0 = await transformY();
  await twoFingers(260, 60);
  await sleep(300);
  const top1 = await transformY();
  check("two fingers moving up move the view down the picture", top1 < top0 - 100, true);
  await twoFingers(60, 400);
  await sleep(300);
  check("and back up, as far as the picture goes", await transformY(), 0);
  check("the bottom of the picture can be brought above the keyboard", await (async () => {
    await twoFingers(300, 20);
    await twoFingers(300, 20);
    await sleep(300);
    return frame.evaluate(() => Math.round(document.getElementById("screen").getBoundingClientRect().bottom) <= Math.round(document.getElementById("stage").getBoundingClientRect().bottom) + 1);
  })(), true);

  // --- The keyboard goes: everything as it was.
  await shell.evaluate(() => window.__visual(null, 0));
  await sleep(500);
  check("the keyboard goes: the page has its room back and the picture fits again", await frame.evaluate(() => {
    const stage = document.getElementById("stage").getBoundingClientRect();
    const canvas = document.getElementById("screen").getBoundingClientRect();
    return [window.MatgateScreen.state().keyboardInset, document.body.style.paddingBottom, Math.round(canvas.height) <= Math.round(stage.height) + 1, getComputedStyle(document.getElementById("keys")).display];
  }), [0, "", true, "none"]);

  // --- The option: the machine's resolution follows the keyboard.
  await openDialog();
  await sleep(400);
  await shell.evaluate(() => document.querySelector("[data-keyboard-resize]").click());
  await sleep(300);
  check("the option can be switched on in the dialog", [(await state(frame)).keyboardResize, await shell.evaluate(() => document.querySelector("[data-keyboard-resize]").dataset.keyboardResize)], [true, "1"]);
  await shell.evaluate(() => document.getElementById("resolution-dialog").classList.add("hidden"));
  await shell.evaluate(height => window.__visual(height, 0), PHONE.height - 336);
  await sleep(600);
  const small = await frame.evaluate(() => { const box = document.getElementById("stage").getBoundingClientRect(); return [Math.round(box.width), Math.round(box.height)]; });
  check("with it, the machine gets a resolution that fits above the keyboard", await pictureIs(frame, small[0] / 0.75, small[1] / 0.75, 6), true);
  check("and the whole picture is seen", await frame.evaluate(() => Math.round(document.getElementById("screen").getBoundingClientRect().height) <= Math.round(document.getElementById("stage").getBoundingClientRect().height) + 1), true);
  await shell.evaluate(() => window.__visual(null, 0));
  check("without the keyboard it has the full size again", await pictureIs(frame, phoneStage[0] / 0.75, phoneStage[1] / 0.75, 6), true);
  await frame.evaluate(() => window.MatgateScreen.setKeyboardResize(false));
  await shell.close();

  // --- What a connection had as a Guacamole session is where it starts.
  const fresh = await b.newContext({ viewport: PHONE, deviceScaleFactor: 3, hasTouch: true, isMobile: true, storageState: await admin.storageState() });
  await fresh.addInitScript(id => {
    localStorage.removeItem("matgate.screen.scale." + id);
    localStorage.setItem("matgate.display.res.v3", JSON.stringify({ [id]: "fit75" }));
  }, vnc.id);
  const again = await fresh.newPage();
  again.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await again.goto(BASE + "/display/" + vnc.id + "/screen", { waitUntil: "domcontentloaded" });
  await started(again);
  check("a connection that was at \"fit 75 %\" as a Guacamole session starts at 75 %", (await state(again)).scale, 75);
  await again.close();

  await page.evaluate(async () => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && cell === "Test-Scale-VNC") {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  });
  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
