// An agent's start page (with the machine's logo) and what a screen session takes from the browser besides
// mouse and keyboard: the microphone and game controllers.
//
// The real agent runs on this machine in test mode (MATGATE_AGENT_SCREEN_TEST=1): instead of playing the
// voice into an audio device and plugging a virtual controller into Windows, it reports what arrived - how
// many packets and how loud, which buttons and sticks. A test controller "rumbles" by echoing its two
// triggers as the two motors, so the way back is checked too. The browser's microphone is Chromium's fake
// one (a beep), its controller a stand-in for navigator.getGamepads().
const { chromium } = require("playwright-core");
const { spawn, execFileSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");
const BASE = "http://127.0.0.1:18091";
const NAME = "Test-Geraete";
const TOKEN = "test-devices-token-" + Date.now();
const PORT = 17495;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

const AGENT_EXE = path.join(__dirname, "..", "agent", "Matgate.Agent", "bin", "Debug", "net10.0-windows", "matgate-agent.exe");
const work = fs.mkdtempSync(path.join(os.tmpdir(), "matgate-devices-test-"));
for (const folder of ["Dokumente", "Projekte"]) { fs.mkdirSync(path.join(work, folder, "Unterordner"), { recursive: true }); }
fs.writeFileSync(path.join(work, "matgate-agent.json"), JSON.stringify({
  Port: PORT, ListenAddress: "127.0.0.1", Token: TOKEN, Name: "Werkstatt-PC",
  Roots: [{ Name: "Dokumente", Path: path.join(work, "Dokumente") }, { Name: "Projekte", Path: path.join(work, "Projekte") }],
  Terminals: [{ Id: "cmd", Title: "Eingabeaufforderung", Command: "cmd.exe" }],
}, null, 2));
const LOGO = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 240 80"><rect width="240" height="80" rx="14" fill="#0f6b5c"/><text x="20" y="52" font-size="30" fill="#fff">Werkstatt</text><script>window.hacked = true</script></svg>`;
fs.writeFileSync(path.join(work, "logo.svg"), LOGO);

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
  check("the agent starts", /listening on/.test(agentLog), true);

  const flags = ["--autoplay-policy=no-user-gesture-required", "--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream"];
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true, args: flags }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true, args: flags }); }
  const context = await b.newContext({ viewport: { width: 1400, height: 900 }, permissions: ["microphone"] });
  // A controller for the page to find: navigator.getGamepads() gives what the test puts here.
  await context.addInitScript(() => {
    window.__pads = [];
    Object.defineProperty(navigator, "getGamepads", { configurable: true, value: () => window.__pads });
  });
  const page = await context.newPage();
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
  const existing = (await servers()).find(s => s.name === NAME);
  await page.goto(BASE + (existing ? "/admin/servers/" + existing.id : "/admin/servers/new"), { waitUntil: "networkidle" });
  await sleep(400);
  await page.evaluate(([name, port, secret]) => {
    document.querySelector("[data-protocol-option='Agent']").click();
    const set = (field, value) => {
      const all = [...document.querySelectorAll("[name='" + field + "']")];
      const el = all.find(e => !e.disabled) || all[0];
      el.value = value;
      el.dispatchEvent(new Event("change", { bubbles: true }));
    };
    set("name", name); set("host", "host.docker.internal"); set("port", String(port)); set("targetPassword", secret);
    document.querySelector("[name='name']").form.requestSubmit();
  }, [NAME, PORT, TOKEN]).catch(() => { /* the submit navigated before the call returned */ });
  await sleep(1500);
  const server = (await servers()).find(s => s.name === NAME);
  check("the connection is there", !!server, true);

  // ================================================================================== the start page
  const home = () => page.evaluate(() => {
    const root = document.querySelector(".connection-panel:not(.hidden) .agent-home");
    if (!root || !root.querySelector(".agent-hero h2") || root.querySelector(".agent-hero.is-loading")) { return null; }
    const logo = root.querySelector("[data-agent-logo]");
    return {
      name: root.querySelector(".agent-hero h2").textContent,
      online: !!root.querySelector(".agent-home-state.is-online"),
      logo: logo ? [logo.complete && logo.naturalWidth > 0, new URL(logo.src).pathname + new URL(logo.src).search] : null,
      facts: [...root.querySelectorAll(".agent-hero-facts .agent-chip")].map(c => c.textContent),
      features: [...root.querySelectorAll(".agent-features .agent-tile strong")].map(t => t.textContent.trim()),
      capabilities: [...root.querySelectorAll(".agent-feature--screen .agent-chip")].map(c => c.textContent),
      folders: [...root.querySelectorAll("[data-agent-root]")].map(c => c.textContent.trim()),
      programs: [...root.querySelectorAll(".agent-home-grid .agent-tile strong")].map(t => t.textContent.trim()),
      sideways: document.documentElement.scrollWidth > window.innerWidth,
    };
  });
  const openHome = async () => {
    await page.goto(BASE + "/?open=" + server.id, { waitUntil: "networkidle" });
    for (let i = 0; i < 60 && !(await home()); i++) { await sleep(300); }
    for (let i = 0; i < 20 && (await home())?.logo && !(await home()).logo[0]; i++) { await sleep(200); }
    return home();
  };
  let seen = await openHome();
  check("the start page says which machine this is, and that it answers", [seen.name, seen.online], ["Werkstatt-PC", true]);
  check("the machine's logo is shown", [seen.logo && seen.logo[0], /^\/api\/agent\/[0-9a-f-]+\/logo\?v=[0-9a-f]+$/.test(seen.logo ? seen.logo[1] : "")], [true, true]);
  check("who is signed in there is on the page", seen.facts.some(f => /signed in as|angemeldet als/.test(f)), true);
  check("the screen and the files are the two large cards", seen.features.length, 2);
  check("the screen card says what it takes: microphone and controllers too", [seen.capabilities.some(c => /Mikrofon|Microphone/.test(c)), seen.capabilities.some(c => /Controller/.test(c))], [true, true]);
  check("each shared folder has a way in of its own", seen.folders, ["Dokumente", "Projekte"]);
  check("the programs follow", seen.programs, ["Eingabeaufforderung"]);
  check("nothing sticks out sideways", seen.sideways, false);

  const logoAddress = seen.logo[1];
  const served = await page.evaluate(async address => {
    const response = await fetch(address, { credentials: "same-origin" });
    return [response.status, response.headers.get("content-type"), response.headers.get("x-content-type-options"), /sandbox/.test(response.headers.get("content-security-policy") || ""), (await response.text()).length];
  }, logoAddress);
  check("the logo is served as a picture and nothing more (an SVG must not run its script here)", served, [200, "image/svg+xml", "nosniff", true, LOGO.length]);
  const direct = await context.newPage();
  await direct.goto(BASE + logoAddress);
  check("opened by its address, its script does not run", await direct.evaluate(() => window.hacked === true), false);
  await direct.close();
  const stranger = await b.newContext();
  const outside = await stranger.newPage();
  const refused = await outside.goto(BASE + logoAddress.split("?")[0]);
  check("without signing in there is no logo", refused.status() !== 200 || /login/.test(outside.url()), true);
  await stranger.close();

  await page.click(".agent-home [data-agent-root='/Projekte']");
  for (let i = 0; i < 40 && !(await page.evaluate(() => !!document.querySelector(".connection-panel:not(.hidden) .file-display"))); i++) { await sleep(250); }
  await sleep(1200);
  check("a folder's chip opens the files right there", await page.evaluate(() => {
    const panel = document.querySelector(".connection-panel:not(.hidden)");
    return [...panel.querySelectorAll(".file-name-button span, .file-name span")].some(s => s.textContent.trim() === "Unterordner")
      && /Projekte/.test((panel.querySelector(".file-path-input, [data-file-path], input.toolbar-input") || {}).value || panel.textContent);
  }), true);

  // The logo is a file in the agent's folder: taken away, it is gone from the page - without restarting anything.
  fs.rmSync(path.join(work, "logo.svg"));
  seen = await openHome();
  check("without the file the machine's icon is back", [seen.logo, seen.online], [null, true]);
  fs.writeFileSync(path.join(work, "logo.png"), Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAFElEQVR42mNkYPhfz0AEYBxVSF+FAP5FDvcfRYWgAAAAAElFTkSuQmCC", "base64"));
  seen = await openHome();
  check("another picture put there is the logo", [seen.logo && seen.logo[0], seen.logo && seen.logo[1] !== logoAddress], [true, true]);

  // A phone: the same page in one column.
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, storageState: await context.storageState() });
  const small = await phone.newPage();
  small.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await small.goto(BASE + "/?open=" + server.id, { waitUntil: "networkidle" });
  await small.waitForSelector(".agent-hero h2", { timeout: 20000 });
  await sleep(1200);
  check("on a phone the page fits the width, and the name is whole", await small.evaluate(() => {
    const name = document.querySelector(".agent-hero h2");
    const button = document.querySelector("[data-agent-screen]");
    return [document.documentElement.scrollWidth > window.innerWidth, name.scrollWidth > name.clientWidth + 1, button.getBoundingClientRect().height >= 44];
  }), [false, false, true]);
  await phone.close();

  // ================================================================================== the screen: what it takes
  const view = await context.newPage();
  view.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await view.goto(BASE + "/agent/" + server.id + "/screen", { waitUntil: "domcontentloaded" });
  for (let i = 0; i < 100 && (await view.evaluate(() => window.__screen && window.__screen.frames)) < 2; i++) { await sleep(300); }
  const state = () => view.evaluate(() => window.MatgateScreen.state());
  const debug = () => view.evaluate(() => window.__screen.debug.splice(0));
  const until = async (pattern, tries = 60) => {
    const lines = [];
    for (let i = 0; i < tries; i++) {
      lines.push(...await debug());
      if (lines.some(l => pattern.test(l))) { break; }
      await sleep(250);
    }
    return lines;
  };
  let now = await state();
  check("the machine says it takes a microphone and controllers", [now.canMic, now.micAvailability, now.padAvailability, now.mic, now.pads], [true, "ok", "ok", false, 0]);
  await debug();

  // ---- the microphone
  await view.evaluate(() => window.MatgateScreen.toggleMic());
  let lines = await until(/^mic packets=\d+/);
  const report = (lines.find(l => /^mic packets=/.test(l)) || "").match(/packets=(\d+) samples=(\d+) level=(\d+)/) || [];
  check("the voice arrives on the machine: packets of 20 ms, and not silence", [Number(report[1]) >= 25, Number(report[2]) === Number(report[1]) * 960, Number(report[3]) > 20], [true, true, true]);
  now = await state();
  check("the page shows the microphone as on", [now.mic, await view.evaluate(() => document.getElementById("mic").classList.contains("on")), /Mikrofon an|Microphone on/.test(now.status)], [true, true, true]);
  console.log("     microphone codec: " + await view.evaluate(() => window.__screen.mic.codec));
  await view.evaluate(() => window.MatgateScreen.toggleMic());
  lines = await until(/^mic closed/);
  check("switched off, it is off on the machine as well", [lines.some(l => /^mic closed after \d+ packets/.test(l)), (await state()).mic], [true, false]);
  await sleep(700);
  check("and nothing more is sent", (await debug()).filter(l => /^mic/.test(l)), []);

  // ---- a controller
  const pad = (buttons, axes) => view.evaluate(([pressed, sticks]) => {
    const calls = window.__rumbled = window.__rumbled || [];
    window.__pads = [{
      index: 0, connected: true, mapping: "standard", id: "Test controller",
      buttons: Array.from({ length: 17 }, (_, i) => ({ pressed: (pressed[i] || 0) > 0.5, value: pressed[i] || 0 })),
      axes: sticks,
      vibrationActuator: { playEffect: (kind, effect) => { calls.push([kind, effect.strongMagnitude, effect.weakMagnitude]); return Promise.resolve(); }, reset: () => { calls.push(["reset"]); return Promise.resolve(); } },
    }];
  }, [buttons, axes]);
  await pad({}, [0, 0, 0, 0]);
  await view.evaluate(() => window.dispatchEvent(new Event("gamepadconnected")));
  lines = await until(/^pad 0 buttons=/);
  check("a controller of the browser is plugged in on the machine", lines.filter(l => /^pad/.test(l)), ["pad 0 plugged", "pad 0 buttons=0000 lt=0 rt=0 l=0,0 r=0,0"]);
  check("the page counts it", (await state()).pads, 1);
  // A pressed, the left trigger half way, the left stick right and up, the right stick down.
  await pad({ 0: 1, 6: 0.5 }, [1, -1, 0, 1]);
  lines = await until(/^pad 0 buttons=1000/);
  check("buttons, triggers and sticks arrive as an Xbox controller has them", lines.filter(l => /^pad/.test(l)).pop(), "pad 0 buttons=1000 lt=128 rt=0 l=32767,32767 r=0,-32767");
  for (let i = 0; i < 20 && !(await view.evaluate(() => (window.__rumbled || []).length)); i++) { await sleep(200); }
  check("what the machine makes the controller do comes back: it rumbles", await view.evaluate(() => window.__rumbled.slice(-1)[0].map(v => typeof v === "number" ? Math.round(v * 100) / 100 : v)), ["dual-rumble", 0.5, 0]);
  // D-pad, shoulder buttons, start: every button has its own bit.
  await pad({ 12: 1, 15: 1, 4: 1, 5: 1, 9: 1, 3: 1 }, [0, 0, 0, 0]);
  lines = await until(/^pad 0 buttons=8319/);
  check("the other buttons too (up, right, both shoulders, start, Y)", lines.filter(l => /^pad/.test(l)).pop(), "pad 0 buttons=8319 lt=0 rt=0 l=0,0 r=0,0");
  // A stick held while the page loses the focus: let go, not left pushed.
  await pad({}, [0.8, 0, 0, 0]);
  await until(/^pad 0 buttons=0000 lt=0 rt=0 l=26214,0/);
  await view.evaluate(() => window.dispatchEvent(new Event("blur")));
  lines = await until(/^pad 0 buttons=0000 lt=0 rt=0 l=0,0 r=0,0/);
  check("a page that loses the focus lets the stick go", lines.some(l => l === "pad 0 buttons=0000 lt=0 rt=0 l=0,0 r=0,0"), true);
  await view.evaluate(() => { window.__pads = []; window.dispatchEvent(new Event("gamepaddisconnected")); window.dispatchEvent(new Event("focus")); });
  await pad({}, [0, 0, 0, 0]);
  await view.evaluate(() => { window.__pads = []; });
  lines = await until(/^pad 0 unplugged/);
  check("taken away in the browser, it is unplugged on the machine", [lines.some(l => l === "pad 0 unplugged"), (await state()).pads], [true, 0]);
  await view.close();

  // ================================================================================== in the shell's toolbar
  await page.goto(BASE + "/?open=" + server.id, { waitUntil: "networkidle" });
  await page.waitForSelector(".agent-home [data-agent-screen]", { timeout: 20000 });
  await page.click(".agent-home [data-agent-screen]");
  const frameOf = async () => { for (let i = 0; i < 60; i++) { const f = page.frames().find(x => /\/agent\/[0-9a-f-]+\/screen/.test(x.url())); if (f) { return f; } await sleep(250); } return null; };
  const frame = await frameOf();
  for (let i = 0; i < 100 && (await frame.evaluate(() => window.__screen && window.__screen.frames)) < 2; i++) { await sleep(300); }
  const action = name => page.evaluate(n => {
    const button = document.querySelector("#connection-tab-actions [data-action='" + n + "'], .tab-action-overflow-panel [data-action='" + n + "']");
    return button ? [true, button.classList.contains("active")] : [false, false];
  }, name);
  for (let i = 0; i < 20 && !(await action("mic"))[0]; i++) { await sleep(300); }
  check("the toolbar has a microphone button, off", await action("mic"), [true, false]);
  await page.evaluate(() => document.querySelector("#connection-tab-actions [data-action='mic'], .tab-action-overflow-panel [data-action='mic']").click());
  for (let i = 0; i < 30 && !(await action("mic"))[1]; i++) { await sleep(300); }
  check("pressed, it is on", [(await action("mic"))[1], await frame.evaluate(() => window.MatgateScreen.state().mic)], [true, true]);
  check("no controller, no controller sign", (await action("gamepad"))[0], false);
  await frame.evaluate(() => {
    window.__pads = [{ index: 0, connected: true, mapping: "standard", id: "Test controller", buttons: Array.from({ length: 17 }, () => ({ pressed: false, value: 0 })), axes: [0, 0, 0, 0] }];
    window.dispatchEvent(new Event("gamepadconnected"));
  });
  for (let i = 0; i < 30 && !(await action("gamepad"))[0]; i++) { await sleep(300); }
  check("with a controller the toolbar shows it", await action("gamepad"), [true, true]);
  await page.evaluate(() => document.querySelector("#connection-tab-actions [data-action='mic'], .tab-action-overflow-panel [data-action='mic']").click());

  await page.evaluate(async name => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && cell === name) { await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" }); }
    }
  }, NAME);
  await b.close();
  try { execFileSync("taskkill", ["/pid", String(agent.pid), "/t", "/f"], { stdio: "ignore" }); } catch { agent.kill(); }
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => {
  console.error("ABORTED " + e.stack);
  try { execFileSync("taskkill", ["/pid", String(agent.pid), "/t", "/f"], { stdio: "ignore" }); } catch { }
  process.exit(1);
});
