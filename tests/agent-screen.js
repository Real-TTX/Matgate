// The agent's screen: the machine's desktop as H.264 video in the browser, with mouse, keyboard, touch,
// sound and a clipboard that works both ways. The real agent runs on this machine, bound to 127.0.0.1,
// in test mode (MATGATE_AGENT_SCREEN_TEST=1): it shows a generated picture on two "monitors" instead of
// the real desktop, plays a test tone instead of the machine's sound, and reports every input and every
// clipboard change back instead of doing it - a test must not move anyone's mouse or overwrite anyone's
// clipboard. What it checks is the whole way: agent -> Matgate (Docker) -> browser (WebCodecs) and back.
// The screen runs in a helper process of the agent, as it does for a LocalSystem service (MATGATE_AGENT_SCREEN_HOST=1;
// SCREEN_DIRECT=1 keeps it in the agent's own process instead).
const { chromium } = require("playwright-core");
const { spawn, execFileSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");
const BASE = "http://127.0.0.1:18091";
const NAME = "Test-Bildschirm";
const TOKEN = "test-screen-token-" + Date.now();
const PORT = 17489;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

const AGENT_EXE = path.join(__dirname, "..", "agent", "Matgate.Agent", "bin", "Debug", "net10.0-windows", "matgate-agent.exe");
const work = fs.mkdtempSync(path.join(os.tmpdir(), "matgate-screen-test-"));
fs.writeFileSync(path.join(work, "matgate-agent.json"), JSON.stringify({
  Port: PORT, ListenAddress: "127.0.0.1", Token: TOKEN, Name: NAME, Roots: [], Terminals: [],
}, null, 2));

let agent;
(async () => {
  execFileSync("dotnet", ["build", path.join(__dirname, "..", "agent", "Matgate.Agent", "Matgate.Agent.csproj"), "-nologo", "-v", "q"], { stdio: "inherit" });
  agent = spawn(AGENT_EXE, ["--config", path.join(work, "matgate-agent.json")], {
    stdio: ["ignore", "pipe", "pipe"],
    env: { ...process.env, MATGATE_AGENT_SCREEN_TEST: "1", MATGATE_AGENT_SCREEN_HOST: process.env.SCREEN_DIRECT === "1" ? "0" : "1" },
  });
  let agentLog = "";
  agent.stdout.on("data", d => { agentLog += d; });
  agent.stderr.on("data", d => { agentLog += d; });
  for (let i = 0; i < 40 && !/listening on/.test(agentLog); i++) { await sleep(250); }
  check("the agent starts on loopback", /listening on:\s+127\.0\.0\.1:17489/.test(agentLog), true);
  if (!/listening on/.test(agentLog)) { console.log("agent output: " + agentLog.slice(-2000)); }

  let b; try { b = await chromium.launch({ channel: "msedge", headless: true, args: ["--autoplay-policy=no-user-gesture-required"] }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const context = await b.newContext({ viewport: { width: 1400, height: 900 }, permissions: ["clipboard-read", "clipboard-write"] });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1200);

  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  const existing = (await servers()).find(s => s.name === NAME);
  await page.goto(BASE + (existing ? "/admin/servers/" + existing.id : "/admin/servers/new"), { waitUntil: "networkidle" });
  await sleep(500);
  await page.evaluate(([name, secret, port]) => {
    const set = (field, value) => {
      const el = [...document.querySelectorAll("[name='" + field + "']")].find(e => !e.disabled) || document.querySelector("[name='" + field + "']");
      el.value = value;
      el.dispatchEvent(new Event("change", { bubbles: true }));
    };
    document.querySelector("[data-protocol-option='Agent']").click();
    set("name", name);
    set("host", "host.docker.internal");
    set("port", String(port));
    set("targetPassword", secret);
    document.querySelector("[name='name']").form.requestSubmit();
  }, [NAME, TOKEN, PORT]);
  await sleep(2000);
  const server = (await servers()).find(s => s.name === NAME);
  check("the connection is saved", !!server, true);

  // --- The overview offers the screen.
  const status = await page.evaluate(async id => (await fetch("/api/agent/" + id + "/status", { credentials: "same-origin" })).json(), server.id);
  check("the agent offers its screen", (status.plugins || []).filter(p => p.kind === "screen").map(p => p.title), ["Bildschirm"]);
  await page.goto(BASE + "/?open=" + server.id, { waitUntil: "networkidle" });
  await sleep(3000);
  check("the overview shows a Bildschirm tile", await page.evaluate(() => !!document.querySelector(".connection-panel:not(.hidden) .agent-home [data-agent-screen]")), true);
  if (await page.evaluate(() => !!document.querySelector(".connection-panel:not(.hidden) .agent-home [data-agent-screen]"))) {
    await page.locator(".connection-panel:not(.hidden) .agent-home [data-agent-screen]").click();
    await sleep(2500);
    const tabFrame = page.frames().find(f => /\/agent\/.+\/screen/.test(f.url()));
    check("which opens the screen in a tab of its own", !!tabFrame, true);
    const allowed = await page.evaluate(() => [...document.querySelectorAll(".shell-page-panel iframe")].map(f => f.allow).find(a => /clipboard-write/.test(a)) || "");
    check("the tab may use the clipboard, full screen and sound", /clipboard-read/.test(allowed) && /fullscreen/.test(allowed) && /autoplay/.test(allowed), true);
    if (tabFrame) {
      for (let i = 0; i < 20 && (await tabFrame.evaluate(() => window.__screen.frames)) < 3; i++) { await sleep(300); }
      check("and shows the picture there", (await tabFrame.evaluate(() => window.__screen.frames)) >= 3, true);
      // In the shell the page has no bar of its own: Matgate's session toolbar steers it, as for RDP.
      check("inside the shell the page shows no bar of its own", await tabFrame.evaluate(() => getComputedStyle(document.querySelector(".bar")).display), "none");
      for (let i = 0; i < 20 && !(await page.evaluate(() => !!document.querySelector("#connection-tab-actions [data-action='resolution']"))); i++) { await sleep(300); }
      const actions = await page.evaluate(() => [...document.querySelectorAll("#connection-tab-actions [data-action]")].map(b => b.dataset.action));
      await page.screenshot({ path: path.join(work, "screen-shell.png") });
      check("the session toolbar carries the screen's actions", ["agent-overview", "fullscreen", "resolution", "sound", "clipboard", "disconnect"].every(a => actions.includes(a)) ? true : actions, true);
      await page.evaluate(() => { const b = document.querySelector("#connection-tab-actions [data-action='resolution']"); b ? b.click() : document.querySelector("[data-action='more'], .tab-action-overflow-trigger")?.click(); });
      await sleep(400);
      const dialog = await page.evaluate(() => [...document.querySelectorAll(".resolution-option")].filter(b => b.offsetParent).map(b => b.textContent));
      check("its resolution button opens the resolution dialog with the machine's modes", dialog.includes("An Fenster anpassen") || dialog.includes("Fit to window") ? dialog.includes("1920 × 1080") : dialog, true);
      await page.evaluate(() => [...document.querySelectorAll(".resolution-option")].find(b => /Wie am Rechner|As on the machine/.test(b.textContent))?.click());
      await sleep(600);
      check("and a choice there reaches the page", await tabFrame.evaluate(() => window.MatgateScreen.state().resolution), "native");
      const soundBefore = await tabFrame.evaluate(() => window.MatgateScreen.state().sound);
      await page.evaluate(() => document.querySelector("#connection-tab-actions [data-action='sound']")?.click());
      await sleep(800);
      check("the sound button switches the page's sound", await tabFrame.evaluate(() => window.MatgateScreen.state().sound), !soundBefore);
    }
  }

  // --- The screen page on its own (as the tab shows it): picture first. At the machine's own resolution for now;
  // fitting it to the window comes further down.
  await context.addInitScript(id => localStorage.setItem("matgate.screen.resolution." + id, "native"), server.id);
  const view = await context.newPage();
  view.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await view.goto(BASE + "/agent/" + server.id + "/screen", { waitUntil: "domcontentloaded" });
  for (let i = 0; i < 30 && (await view.evaluate(() => window.__screen.frames)) < 5; i++) { await sleep(300); }
  check("a second window takes the screen over and the first is told so", await page.evaluate(() => {
    const frame = [...document.querySelectorAll(".shell-page-panel iframe")].find(f => /\/screen/.test(f.src));
    return frame ? frame.contentDocument.getElementById("notice-text").textContent : "";
  }).then(t => /anderes Fenster|Another window/.test(t)), true);
  check("pictures arrive and are decoded", (await view.evaluate(() => window.__screen.frames)) >= 5, true);
  const picture = () => view.evaluate(() => {
    const canvas = document.getElementById("screen");
    const pixels = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
    let bright = 0;
    for (let i = 0; i < pixels.length; i += 4 * 97) { if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 120) { bright++; } }
    return { width: canvas.width, height: canvas.height, content: bright > 50 };
  });
  check("in the monitor's size, with the generated picture on it", await picture(), { width: 1280, height: 720, content: true });
  const count = await view.evaluate(() => window.__screen.frames);
  await sleep(1500);
  check("and keep coming while the picture moves", (await view.evaluate(() => window.__screen.frames)) - count >= 5, true);
  check("the notice is gone once the picture is there", await view.evaluate(() => document.getElementById("notice").classList.contains("hidden")), true);
  check("the machine's cursor shape becomes the page's cursor", await view.evaluate(() => /^url\("?blob:/.test(document.getElementById("screen").style.cursor)), true);

  // --- Two monitors: a choice, and switching shows the other one.
  check("two monitors: the choice is offered", await view.evaluate(() => [...document.querySelectorAll("#monitor option")].map(o => o.textContent)), ["1: 1280×720", "2: 800×600"]);
  await view.selectOption("#monitor", "1");
  for (let i = 0; i < 20 && (await picture()).width !== 800; i++) { await sleep(300); }
  check("choosing the second shows the second", (await picture()).width + "x" + (await picture()).height, "800x600");

  const debug = () => view.evaluate(() => window.__screen.debug.splice(0));
  await debug();
  // --- Mouse: a click in the middle lands in the middle of that monitor (it starts at x=1280).
  const box = await view.locator("#screen").boundingBox();
  await view.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await sleep(600);
  let reported = await debug();
  const move = reported.find(line => /^move /.test(line)) || "";
  const [mx, my] = move.replace("move ", "").split(",").map(Number);
  check("a click moves the machine's mouse to the same spot", Math.abs(mx - 1680) <= 2 && Math.abs(my - 300) <= 2, true);
  check("and clicks there", reported.filter(line => /^button/.test(line)), ["button 0 down", "button 0 up"]);
  await view.mouse.click(box.x + 10, box.y + 10, { button: "right" });
  await sleep(400);
  check("the right button is the right button", (await debug()).filter(line => /^button/.test(line)), ["button 2 down", "button 2 up"]);
  await view.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await view.mouse.wheel(0, 100);
  await sleep(400);
  check("one wheel notch scrolls one notch", (await debug()).filter(line => /^wheel/.test(line)), ["wheel -120,0"]);

  // --- Keyboard: as key positions, so the machine's layout applies.
  await view.keyboard.press("a");
  await view.keyboard.press("ArrowLeft");
  await view.keyboard.press("Shift+Digit7");
  await sleep(400);
  check("keys go as scan codes (A, arrow left extended, Shift+7)", (await debug()).filter(line => /^key/.test(line)),
    ["key 1e down", "key 1e up", "key 4be down", "key 4be up", "key 2a down", "key 08 down", "key 08 up", "key 2a up"]);
  await view.keyboard.down("Control");
  await sleep(150);
  await view.evaluate(() => window.dispatchEvent(new Event("blur")));
  await sleep(400);
  await view.keyboard.up("Control");
  check("leaving the page lets go of a held key (no stuck Ctrl)", (await debug()).filter(line => /^key/.test(line)), ["key 1d down", "key 1d up"]);

  // --- Clipboard, this device -> machine: Ctrl+V with text puts it on the machine's clipboard and pastes it there.
  const pasteEvent = (kind, value) => view.evaluate(([k, v]) => {
    const dt = new DataTransfer();
    if (k === "text") { dt.setData("text/plain", v); }
    else { v.forEach(([name, size]) => dt.items.add(new File([new Uint8Array(size).fill(65)], name, { type: "application/octet-stream" }))); }
    document.getElementById("sink").dispatchEvent(new ClipboardEvent("paste", { clipboardData: dt, bubbles: true, cancelable: true }));
  }, [kind, value]);
  // Text on this device's clipboard, then Ctrl+V: the browser's own paste event carries it.
  await view.evaluate(() => navigator.clipboard.writeText("Hallo vom Browser ä€"));
  await view.keyboard.press("Control+v");
  await sleep(1200);
  reported = await debug();
  check("text pasted here lands on the machine's clipboard", reported.filter(line => /^clipboard/.test(line)), ["clipboard text Hallo vom Browser ä€"]);
  check("and is pasted there once with Ctrl+V", reported.filter(line => /^key 2f/.test(line)), ["key 2f down", "key 2f up"]);
  check("Ctrl itself goes down and up again", reported.filter(line => /^key 1d/.test(line)).length % 2 === 0 && reported.includes("key 1d down"), true);
  // The test clipboard hands it back as if a program on the machine had copied it: machine -> this device.
  check("text copied on the machine lands on this device's clipboard", await view.evaluate(() => window.__screen.clipboard.slice(-1)[0]), "Hallo vom Browser ä€");
  check("really on the clipboard", await view.evaluate(() => navigator.clipboard.readText()), "Hallo vom Browser ä€");

  // --- Ctrl+V with nothing on this device's clipboard: the key press goes on as it is.
  await view.evaluate(() => navigator.clipboard.writeText(""));
  await debug();
  await view.keyboard.press("Control+v");
  await sleep(700);
  check("Ctrl+V without a paste event goes on as a key press", (await debug()).filter(line => /^key/.test(line)), ["key 1d down", "key 2f down", "key 2f up", "key 1d up"]);

  // --- Files: pasted here, they become files on the machine's clipboard (CF_HDROP), pasted where the machine is.
  // Files cannot be put on a clipboard from a test: the paste event is made by hand, as the browser would send it.
  await view.evaluate(() => document.getElementById("sink").focus());
  await pasteEvent("files", [["Bericht.txt", 5], ["Gross.bin", 600000]]);
  for (let i = 0; i < 40 && !(await view.evaluate(() => window.__screen.debug.some(l => /^clipboard files/.test(l)))); i++) { await sleep(300); }
  reported = await debug();
  check("pasted files arrive whole on the machine and go on its clipboard", reported.find(line => /^clipboard files/.test(line)), "clipboard files Bericht.txt:5|Gross.bin:600000");
  check("and are pasted there", reported.filter(line => /^key 2f/.test(line)), ["key 2f down", "key 2f up"]);
  // Back again: files copied on the machine are offered for download (a browser cannot put files on a clipboard).
  await sleep(800);
  const offered = await view.evaluate(() => [...document.querySelectorAll("#toast [data-download]")].map(a => ({ name: a.getAttribute("download"), href: a.getAttribute("href") })));
  check("files copied on the machine are offered for download", offered.map(o => o.name), ["Bericht.txt", "Gross.bin"]);
  if (offered.length === 2) {
    const sizes = await view.evaluate(async list => Promise.all(list.map(async o => {
      const response = await fetch(o.href, { credentials: "same-origin" });
      const bytes = new Uint8Array(await response.arrayBuffer());
      return [response.status, bytes.length, bytes.every(b => b === 65), response.headers.get("content-disposition") || ""];
    })), offered);
    check("and download complete, in pieces from the machine", sizes.map(s => s.slice(0, 3)), [[200, 5, true], [200, 600000, true]]);
    check("under their own names", /Bericht\.txt/.test(sizes[0][3]), true);
    const forged = await view.evaluate(async href => (await fetch(href.replace(/token=[^&]+/, "token=falsch"), { credentials: "same-origin" })).status, offered[0].href);
    check("but not with a made-up token", forged, 404);
  }

  // --- Sound: on by default, it starts with the first click into the page (browsers want one).
  const packets = () => view.evaluate(() => window.__screen.audio);
  check("sound is on after the first click", await view.evaluate(() => document.getElementById("sound").classList.contains("on")), true);
  let before = await packets();
  await sleep(1000);
  check("and the test tone plays", (await packets()) - before >= 20, true);
  await view.click("#sound");
  await sleep(500);
  before = await packets();
  await sleep(800);
  check("the speaker button switches it off", [await view.evaluate(() => document.getElementById("sound").classList.contains("on")), (await packets()) - before], [false, 0]);
  await view.click("#sound");
  await sleep(1000);
  before = await packets();
  await sleep(800);
  check("and on again", (await packets()) - before >= 20, true);

  // --- Statistics: pictures per second and the way there and back, in the bar.
  const stats = await view.evaluate(() => window.__screen.stats || {});
  check("the bar shows pictures per second and the round trip", [stats.fps > 0, typeof stats.rtt === "number", stats.fpsLimit, /\d+ (B\/s|fps)/.test(await view.evaluate(() => document.getElementById("stats").textContent))], [true, true, 60, true]);
  check("and which encoder works", /^(hardware: .+|software)$/.test(stats.encoder || ""), true);

  // --- Resolution, as RDP does it: "fit to window" sets the machine's monitor to the largest mode that fits.
  const resolutionPicture = async (want, label) => {
    for (let i = 0; i < 25 && JSON.stringify(await picture().then(p => [p.width, p.height])) !== JSON.stringify(want); i++) { await sleep(300); }
    check(label, await picture().then(p => [p.width, p.height]), want);
  };
  check("the monitor's modes are offered", await view.evaluate(() => [...document.querySelectorAll("#resolution option")].map(o => o.value)),
    ["auto", "native", "1920x1080", "1600x900", "1280x1024", "1280x720", "1024x768", "800x600"]);
  await debug();
  await view.selectOption("#resolution", "auto");
  await resolutionPicture([1280, 720], "fit to window: 1400x864 takes 1280x720 on the second monitor");
  check("the monitor really changed", (await debug()).filter(l => /^display/.test(l)), ["display TEST2 1280x720"]);
  await view.setViewportSize({ width: 1000, height: 700 });
  await resolutionPicture([800, 600], "a smaller window: the monitor goes back to 800x600");
  check("its own mode again", (await debug()).filter(l => /^display/.test(l)), ["display TEST2 restored"]);
  await view.setViewportSize({ width: 1400, height: 900 });
  await resolutionPicture([1280, 720], "and grows with the window");
  await view.selectOption("#resolution", "1600x900");
  await resolutionPicture([1600, 900], "a mode of one's own");
  check("the choice is kept for this machine", await view.evaluate(id => localStorage.getItem("matgate.screen.resolution." + id), server.id), "1600x900");
  await debug();
  await view.selectOption("#resolution", "native");
  await resolutionPicture([800, 600], "as on the machine: its own mode");
  check("put back", (await debug()).filter(l => /^display/.test(l)), ["display TEST2 restored"]);
  await view.selectOption("#resolution", "auto");
  await resolutionPicture([1280, 720], "fit to window once more");

  // --- Full screen: the picture takes the whole screen, the bar waits above it.
  await view.click("#fullscreen");
  await sleep(1200);
  const full = await view.evaluate(() => ({ full: !!document.fullscreenElement, fs: document.body.classList.contains("fs"), handle: getComputedStyle(document.getElementById("handle")).display !== "none" }));
  check("full screen: page in full screen, bar tucked away, a handle to bring it", full, { full: true, fs: true, handle: true });
  await sleep(1200);
  check("the bar goes up after a moment", await view.evaluate(() => document.querySelector(".bar").classList.contains("show")), false);
  await view.mouse.move(700, 1);
  await sleep(600);
  check("and comes down at the top edge", await view.evaluate(() => document.querySelector(".bar").classList.contains("show")), true);
  await view.evaluate(() => document.exitFullscreen());
  await sleep(800);
  check("leaving full screen brings the bar back", await view.evaluate(() => document.body.classList.contains("fs")), false);

  // --- Ctrl+Alt+Del: from the bar (and from a phone's key bar).
  await debug();
  await view.click("#cad");
  await sleep(500);
  check("Ctrl+Alt+Del reaches the machine", (await debug()).filter(l => l === "sas"), ["sas"]);

  // --- Touch: a tap is a click, a long press the right button, two fingers scroll.
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
  const tapPage = await phone.newPage();
  await tapPage.goto(BASE + "/login", { waitUntil: "networkidle" });
  await tapPage.fill("input[name=\"username\"]", "admin");
  await tapPage.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([tapPage.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), tapPage.click(".login-submit")]);
  await tapPage.goto(BASE + "/agent/" + server.id + "/screen", { waitUntil: "domcontentloaded" });
  for (let i = 0; i < 30 && (await tapPage.evaluate(() => window.__screen.frames)) < 3; i++) { await sleep(300); }
  check("on a phone the picture arrives too", (await tapPage.evaluate(() => window.__screen.frames)) >= 3, true);
  // The window on the computer lost the screen to the phone: its monitor got its own mode back first.
  check("taking over puts the last viewer's resolution back", await tapPage.evaluate(() => [...document.querySelectorAll("#monitor option")].map(o => o.textContent)).then(list => list[1]), "2: 800×600");
  check("with the key bar and the keyboard button", await tapPage.evaluate(() => [getComputedStyle(document.getElementById("keys")).display !== "none", !document.getElementById("keyboard").classList.contains("hidden")]), [true, true]);
  const touchDebug = () => tapPage.evaluate(() => window.__screen.debug.splice(0));
  await touchDebug();
  const touchBox = await tapPage.locator("#screen").boundingBox();
  const touch = (type, points) => tapPage.evaluate(([t, list]) => {
    const stage = document.getElementById("stage");
    list.forEach(([id, x, y]) => stage.dispatchEvent(new PointerEvent(t, { pointerId: id, pointerType: "touch", clientX: x, clientY: y, bubbles: true, cancelable: true, isPrimary: id === 1 })));
  }, [type, points]);
  const cx = touchBox.x + touchBox.width / 2;
  const cy = touchBox.y + touchBox.height / 2;
  await touch("pointerdown", [[1, cx, cy]]);
  await sleep(80);
  await touch("pointerup", [[1, cx, cy]]);
  await sleep(500);
  check("a tap is a left click", (await touchDebug()).filter(l => /^button/.test(l)), ["button 0 down", "button 0 up"]);
  await touch("pointerdown", [[1, cx, cy]]);
  await sleep(800);
  await touch("pointerup", [[1, cx, cy]]);
  await sleep(400);
  check("a long press is the right button", (await touchDebug()).filter(l => /^button/.test(l)), ["button 2 down", "button 2 up"]);
  await touch("pointerdown", [[1, cx - 30, cy], [2, cx + 30, cy]]);
  for (let step = 1; step <= 5; step++) { await touch("pointermove", [[1, cx - 30, cy - step * 12], [2, cx + 30, cy - step * 12]]); await sleep(30); }
  await touch("pointerup", [[1, cx - 30, cy - 60], [2, cx + 30, cy - 60]]);
  await sleep(500);
  const wheel = (await touchDebug()).filter(l => /^wheel/.test(l));
  check("two fingers moving up scroll", wheel.length > 0 && wheel.every(l => /^wheel -\d+,0$/.test(l)) ? true : wheel, true);
  await touch("pointerdown", [[1, cx - 20, cy], [2, cx + 20, cy]]);
  for (let step = 1; step <= 6; step++) { await touch("pointermove", [[1, cx - 20 - step * 20, cy], [2, cx + 20 + step * 20, cy]]); await sleep(30); }
  await touch("pointerup", [[1, cx - 140, cy], [2, cx + 140, cy]]);
  await sleep(300);
  const transform = await tapPage.evaluate(() => document.getElementById("frame").style.transform);
  check("pinching zooms in", /scale\((?!1\))/.test(transform) ? true : transform, true);

  // --- The touchpad (the phone's default, as for RDP): the screen is one large trackpad.
  check("a phone starts in touchpad mode", await tapPage.evaluate(() => window.MatgateScreen.state().pointerMode), "touchpad");
  await tapPage.evaluate(() => { document.getElementById("frame").style.transform = ""; });
  await tapPage.reload({ waitUntil: "domcontentloaded" });
  for (let i = 0; i < 30 && (await tapPage.evaluate(() => window.__screen.frames)) < 3; i++) { await sleep(300); }
  await touchDebug();
  const padBox = await tapPage.locator("#screen").boundingBox();
  const px = padBox.x + padBox.width / 2;
  const py = padBox.y + padBox.height / 2;
  const lastMove = lines => (lines.filter(l => /^move /.test(l)).pop() || "move -1,-1").replace("move ", "").split(",").map(Number);
  // A swipe moves the cursor on from where it is - without clicking.
  for (let step = 0; step <= 6; step++) { await touch(step === 0 ? "pointerdown" : "pointermove", [[1, px + step * 10, py]]); await sleep(30); }
  await touch("pointerup", [[1, px + 60, py]]);
  await sleep(500);
  const swiped = await touchDebug();
  const [ex, ey] = lastMove(swiped);
  check("a swipe moves the cursor, without clicking", [ex > 0, swiped.some(l => /^button/.test(l))], [true, false]);
  check("and the page shows the cursor there", await tapPage.evaluate(() => !document.getElementById("pointer").classList.contains("hidden")), true);
  // A tap clicks where the cursor is, not where the finger touches (here: the far left edge).
  await touch("pointerdown", [[1, padBox.x + 3, padBox.y + padBox.height - 3]]);
  await sleep(60);
  await touch("pointerup", [[1, padBox.x + 3, padBox.y + padBox.height - 3]]);
  await sleep(500);
  const tapped = await touchDebug();
  check("a tap clicks at the cursor", [lastMove(tapped), tapped.filter(l => /^button/.test(l))], [[ex, ey], ["button 0 down", "button 0 up"]]);
  await touch("pointerdown", [[1, px - 30, py], [2, px + 30, py]]);
  await sleep(60);
  await touch("pointerup", [[1, px - 30, py], [2, px + 30, py]]);
  await sleep(500);
  const twoTap = await touchDebug();
  check("two fingers tapped: the right button, at the cursor", [twoTap.filter(l => /^button/.test(l)), (twoTap.filter(l => /^move /.test(l)).pop() || "").replace("move ", "").split(",").map(Number)[0] === ex],
    [["button 2 down", "button 2 up"], true]);
  // Direct touch, as the toolbar's pointer button switches it: a tap clicks where the finger is.
  await tapPage.evaluate(() => window.MatgateScreen.setPointerMode("direct"));
  await touch("pointerdown", [[1, padBox.x + 5, padBox.y + 5]]);
  await sleep(60);
  await touch("pointerup", [[1, padBox.x + 5, padBox.y + 5]]);
  await sleep(500);
  const direct = await touchDebug();
  const [dx, dy] = (direct.filter(l => /^move /.test(l)).pop() || "move 9999,9999").replace("move ", "").split(",").map(Number);
  check("direct touch clicks where the finger is", [dx < 40 && dy < 40, direct.filter(l => /^button/.test(l))], [true, ["button 0 down", "button 0 up"]]);
  await tapPage.screenshot({ path: path.join(work, "screen-phone.png") });
  await view.screenshot({ path: path.join(work, "screen-desktop.png") });
  console.log("screenshots in " + work);

  await b.close();
  agent.kill();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); if (agent) { agent.kill(); } process.exit(1); });
