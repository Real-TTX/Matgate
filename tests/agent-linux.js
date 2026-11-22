// The Matgate agent on Linux: the same agent as on Windows, built for linux-x64 as the release is, running
// as root in a container of the test stack (agent-linux) the way the system's service does - with the
// machine's shared folder, its logo, and programs in a real pseudo terminal that run as the user "demo".
//
// What is checked is what differs from Windows: that Matgate reaches it at all, that a terminal IS a
// terminal there (the program's own user and folder, the window size, Ctrl+C, colours), that a session
// outlives its browser, and that the files are the machine's.
const { chromium } = require("playwright-core");
const { execFileSync } = require("child_process");
const http = require("http");
const crypto = require("crypto");
const fs = require("fs");
const os = require("os");
const path = require("path");
const BASE = "http://127.0.0.1:18091";
const NAME = "Test-Linux-Agent";
const CONTAINER = "matgate-tests-agent-linux-1";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const onMachine = command => {
  try { return execFileSync("docker", ["exec", CONTAINER, "sh", "-c", command], { encoding: "utf8" }).trim(); }
  catch (e) { return "ERROR " + String(e.stderr || e.message).trim().split("\n").pop(); }
};
// The programs the agent started in terminals: bash, as whom.
const shells = () => onMachine("ps -eo user:20,args | grep 'bash --noprofile' | grep -v -e grep -e as-user -e 'sh -c' | awk '{print $1}' | sort | uniq -c | awk '{print $2\":\"$1}'");

(async () => {
  // The machine as its image has it: an earlier run may have left it updated.
  execFileSync("docker", ["compose", "-f", path.join(__dirname, "docker-compose.yml"), "up", "-d", "--force-recreate", "agent-linux"], { stdio: "ignore" });
  await sleep(3000);
  let b = await chromium.launch({ headless: true });
  const context = await b.newContext({ viewport: { width: 1400, height: 900 } });
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
  await page.evaluate(name => {
    document.querySelector("[data-protocol-option='Agent']").click();
    const set = (field, value) => {
      const all = [...document.querySelectorAll("[name='" + field + "']")];
      const el = all.find(e => !e.disabled) || all[0];
      el.value = value;
      el.dispatchEvent(new Event("change", { bubbles: true }));
    };
    set("name", name); set("host", "agent-linux"); set("port", "7488"); set("targetPassword", "test-only-linux-agent-token");
    document.querySelector("[name='name']").form.requestSubmit();
  }, NAME).catch(() => { /* the submit navigated before the call returned */ });
  await sleep(1500);
  const server = (await servers()).find(s => s.name === NAME);
  check("the connection is there", !!server, true);
  onMachine("pkill -u demo bash; rm -f /srv/share/vom-terminal.txt /srv/share/hochgeladen.txt; true");

  // ================================================================================== what the machine says of itself
  const status = await page.evaluate(async id => (await fetch("/api/agent/" + id + "/status", { credentials: "same-origin" })).json(), server.id);
  check("Matgate reaches the agent on Linux", [status.online, status.name, /Linux/i.test(status.os || "")], [true, "Linux-Kiste", true]);
  check("it offers its files, its programs and its screen", status.plugins.map(p => p.kind).sort(), ["files", "screen", "terminal"]);
  check("its logo comes along", /\/logo\?v=/.test(status.logo || ""), true);
  await page.goto(BASE + "/?open=" + server.id, { waitUntil: "networkidle" });
  await page.waitForSelector(".agent-hero h2", { timeout: 20000 });
  for (let i = 0; i < 40 && await page.evaluate(() => !!document.querySelector(".agent-hero.is-loading")); i++) { await sleep(250); }
  await sleep(800);
  check("the start page shows the machine, online, with logo, files and the shell", await page.evaluate(() => {
    const root = document.querySelector(".connection-panel:not(.hidden) .agent-home");
    const logo = root.querySelector("[data-agent-logo]");
    return [
      root.querySelector(".agent-hero h2").textContent,
      !!root.querySelector(".agent-home-state.is-online"),
      !!logo && logo.complete && logo.naturalWidth > 0,
      [...root.querySelectorAll("[data-agent-root]")].map(c => c.textContent.trim()),
      [...root.querySelectorAll(".agent-home-grid .agent-tile strong")].map(t => t.textContent.trim()),
      !!root.querySelector("[data-agent-screen]"),
    ];
  }), ["Linux-Kiste", true, true, ["Freigabe"], ["Shell"], true]);

  // ================================================================================== files
  const listing = await page.evaluate(async id => (await fetch("/api/files/" + id + "/list?path=" + encodeURIComponent("/Freigabe"), { credentials: "same-origin" })).json(), server.id);
  const names = (listing.entries || listing.Entries || []).map(e => (e.name || e.Name) + ((e.isDirectory ?? e.IsDirectory) ? "/" : "")).sort();
  check("the shared folder is the machine's", names, ["Ordner/", "hallo.txt"]);

  // ================================================================================== a terminal
  const session = "linux-" + Date.now().toString(36) + "-abcdef";
  const address = BASE + "/agent/" + server.id + "/terminal?plugin=shell&title=Shell&session=" + session + "&cwd=" + encodeURIComponent("/Freigabe/Ordner");
  const term = await context.newPage();
  term.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await term.goto(address, { waitUntil: "domcontentloaded" });
  const rows = target => target.evaluate(() => (document.querySelector(".xterm-rows") || {}).textContent || "");
  const shows = async (target, pattern, tries = 40) => { for (let i = 0; i < tries && !pattern.test(await rows(target)); i++) { await sleep(250); } return pattern.test(await rows(target)); };
  const type = async (target, line) => {
    await target.evaluate(() => window.MatgateTerminal.focus());
    await target.keyboard.type(line, { delay: 8 });
    await target.keyboard.press("Enter");
  };
  check("the program starts and shows its prompt", await shows(term, /\$/), true);
  check("it runs on the machine as the user of the settings, not as root", shells(), "demo:1");
  await type(term, "echo \"wer=$(whoami) wo=$(pwd) term=$TERM\"");
  check("as that user, in the chosen folder, in a terminal that knows colours", await shows(term, /wer=demo wo=\/srv\/share\/Ordner term=xterm-256color/), true);
  await type(term, "tty | sed 's/^/tty=/'; stty size | sed 's/^/groesse=/'");
  check("it IS a terminal, of the size of the page", await shows(term, /tty=\/dev\/pts\/\d+/) && /groesse=\d+ \d+/.test(await rows(term)), true);
  const size = ((await rows(term)).match(/groesse=(\d+) (\d+)/) || []).slice(1).map(Number);
  await term.setViewportSize({ width: 900, height: 500 });
  await sleep(1200);
  await type(term, "stty size | sed 's/^/jetzt=/'");
  await shows(term, /jetzt=\d+ \d+/);
  const smaller = ((await rows(term)).match(/jetzt=(\d+) (\d+)/) || []).slice(1).map(Number);
  check("a smaller window is a smaller terminal for the program", [smaller[0] < size[0], smaller[1] < size[1]], [true, true]);

  // Ctrl+C reaches the program as it does in a real terminal.
  await type(term, "sleep 300");
  await sleep(800);
  await term.keyboard.press("Control+c");
  await sleep(400);
  await type(term, "echo ende=$?");
  check("Ctrl+C interrupts what runs - the program in front, not the shell", await shows(term, /ende=130/), true);
  await type(term, "printf '\\033[31mROT\\033[0m fertig\\n'; echo geschrieben > /srv/share/vom-terminal.txt; MERK=bleibt42");
  check("colours arrive as colours", [await shows(term, /ROT fertig/), await term.evaluate(() => [...document.querySelectorAll(".xterm-rows span")].some(s => s.textContent === "ROT" && /xterm-fg-1\b/.test(s.className)))], [true, true]);
  check("what the program writes is the user's file on the machine", onMachine("stat -c '%U %s' /srv/share/vom-terminal.txt"), "demo 12");
  await type(term, "echo 'Größe: äöü €'");
  check("umlauts and the euro sign come through", await shows(term, /Größe: äöü €/), true);

  // The browser goes; the program stays, and whoever comes back finds it.
  await term.close();
  await sleep(1500);
  check("the browser gone, the program still runs", shells(), "demo:1");
  const sessions = await page.evaluate(async id => (await (await fetch("/api/agent/" + id + "/sessions", { credentials: "same-origin" })).json()).sessions, server.id);
  check("the agent lists it as a running session without a window", sessions.map(s => [s.plugin, s.attached, s.cwd]), [["shell", false, "/Freigabe/Ordner"]]);
  const again = await context.newPage();
  await again.goto(address, { waitUntil: "domcontentloaded" });
  check("coming back shows what it showed", await shows(again, /Größe: äöü €/), true);
  await type(again, "echo \"merk=$MERK\"");
  check("and it is the same program: it still knows what it was told", await shows(again, /merk=bleibt42/), true);

  // A second way to start the same program.
  const variant = await context.newPage();
  await variant.goto(BASE + "/agent/" + server.id + "/terminal?plugin=shell&title=Shell&variant=hello&session=gruss-" + Date.now().toString(36) + "-abcdef&cwd=" + encodeURIComponent("/Freigabe"), { waitUntil: "domcontentloaded" });
  check("a variant starts the program with its own arguments", await shows(variant, /GRUSS-VOM-LINUX-AGENT/), true);
  check("as a second program beside the first", shells(), "demo:2");

  // Ending: asked for in Matgate, gone on the machine - with whatever it had started.
  await type(again, "sleep 500 &");
  await sleep(500);
  const csrf = await page.evaluate(async () => JSON.parse((await (await fetch("/")).text()).match(/const csrfToken = (".*?");/)[1]));
  const all = await page.evaluate(async id => (await (await fetch("/api/agent/" + id + "/sessions", { credentials: "same-origin" })).json()).sessions.map(s => s.session), server.id);
  for (const one of all) {
    await page.evaluate(async ([id, s, token]) => fetch("/api/agent/" + id + "/terminal/kill?session=" + encodeURIComponent(s), { method: "POST", credentials: "same-origin", headers: { "X-Matgate-Csrf": token } }), [server.id, one, csrf]);
  }
  for (let i = 0; i < 30 && shells() !== ""; i++) { await sleep(300); }
  check("ended in Matgate, the programs are gone on the machine", [shells(), onMachine("pgrep -u demo -f 'sleep 500' | wc -l")], ["", "0"]);
  check("the pages say that the program ended", /beendet|ended/i.test(await again.evaluate(() => window.MatgateTerminal.state().status)), true);
  await again.close();
  await variant.close();

  // ================================================================================== the agent's own commands
  check("the agent says which build it is", [onMachine("/opt/matgate-agent/matgate-agent --version"), status.version], ["0.10.0.1", "0.10.0.1"]);
  check("a setup that only says what it would do names the service and the user", (() => {
    const said = onMachine("SUDO_USER=demo /opt/matgate-agent/matgate-agent setup --dry-run --port 7600");
    return [/systemd service \(matgate-agent\)/.test(said), /run as demo/.test(said), /nothing was changed/.test(said), /placeholder/.test(onMachine("cat /etc/systemd/system/matgate-agent.service")), onMachine("test -e /etc/matgate-agent && echo da || echo nicht")];
  })(), [true, true, true, true, "nicht"]);
  check("its status answers on the machine", /"Port":7488/.test(onMachine("/opt/matgate-agent/matgate-agent status --port 7488")), true);

  // ================================================================================== the screen
  // The machine's desktop is an X server that runs as "demo" (tests/agent-linux/desktop.sh); the agent, as
  // root, finds it, gets in with its key file and shows it - here for real, not as a generated picture: what
  // is checked is looked up on the machine itself (where the pointer is, what the window got typed).
  const onDesktop = command => onMachine("su demo -c 'DISPLAY=:'$(ls /tmp/.X11-unix | tr -d X)' XAUTHORITY=$(ls /home/demo/.xauth* | tail -1) " + command + "'");
  check("the agent's own probe finds the display, its key, a picture, the encoder and the window",
    (said => [/display:\s+:7\s+key file: \/home\/demo\/\.xauth7/.test(said), /capture:\s+\d+ pictures of 1280x800/.test(said), /OpenH264/.test(said), /windows:\s+1/.test(said)])(onMachine("/opt/matgate-agent/matgate-agent screen-probe")),
    [true, true, true, true]);
  await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: BASE });
  await context.addInitScript(id => localStorage.setItem("matgate.screen.resolution." + id, "native"), server.id);
  const view = await context.newPage();
  view.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await view.goto(BASE + "/agent/" + server.id + "/screen", { waitUntil: "domcontentloaded" });
  for (let i = 0; i < 40 && (await view.evaluate(() => window.__screen.frames)) < 2; i++) { await sleep(300); }
  check("pictures of the desktop arrive and are decoded", (await view.evaluate(() => window.__screen.frames)) >= 2, true);
  await sleep(1300);
  check("encoded by OpenH264", /OpenH264/.test((await view.evaluate(() => window.__screen.stats || {})).encoder || ""), true);
  const box = await view.locator("#screen").boundingBox();
  const at = (x, y) => [box.x + box.width * (x + 0.5) / 1280, box.y + box.height * (y + 0.5) / 800];
  check("the picture has the desktop's shape", Math.abs(box.width / box.height - 1280 / 800) < 0.02, true);
  // Not one colour: the window is in it. (The video's own pixels, drawn onto a canvas.)
  check("and shows the window", await view.evaluate(() => {
    const source = document.getElementById("screen");
    const canvas = document.createElement("canvas");
    canvas.width = 320; canvas.height = 200;
    const pen = canvas.getContext("2d");
    pen.drawImage(source, 0, 0, 320, 200);
    const pixels = pen.getImageData(0, 0, 320, 200).data;
    const seen = new Set();
    for (let i = 0; i < pixels.length; i += 4) { seen.add((pixels[i] >> 4) + "," + (pixels[i + 1] >> 4) + "," + (pixels[i + 2] >> 4)); }
    return seen.size >= 2;
  }), true);
  await view.screenshot({ path: path.join(os.tmpdir(), "matgate-linux-screen.png") });

  // --- Mouse: where the viewer points is where the machine's pointer goes.
  await view.mouse.move(...at(300, 200));
  await sleep(500);
  check("the pointer follows the viewer's", onDesktop("xdotool getmouselocation").replace(/ screen.*/, ""), "x:300 y:200");
  // --- Keyboard: a click into the window, then keys - as scan codes, through the machine's own layout.
  const framesBefore = await view.evaluate(() => window.__screen.frames);
  await view.mouse.click(...at(300, 200));
  await sleep(300);
  // A capital letter the way a keyboard makes one: Shift and the key.
  await view.keyboard.type("hallo ");
  await view.keyboard.press("Shift+KeyL");
  await view.keyboard.type("inux 42");
  await view.keyboard.press("Enter");
  await sleep(800);
  check("keys typed here are typed there", onMachine("cat /tmp/typed.txt"), "hallo Linux 42");
  check("and what they change on the screen is shown", (await view.evaluate(() => window.__screen.frames)) > framesBefore, true);
  // --- Text as itself (a phone's keyboard, dictation): also characters this layout has no key for.
  await view.evaluate(() => {
    const sink = document.getElementById("sink");
    sink.focus();
    sink.setRangeText("Grüße für 5 €", sink.value.length, sink.value.length, "end");
    sink.dispatchEvent(new InputEvent("input", { inputType: "insertText", data: "Grüße für 5 €", bubbles: true }));
  });
  await sleep(1200);
  await view.keyboard.press("Enter");
  await sleep(600);
  check("text arrives as text, whatever the layout", onMachine("tail -n 1 /tmp/typed.txt"), "Grüße für 5 €");
  // --- Clipboard, machine -> this device.
  onDesktop("sh -c \"printf %s Kopie-vom-Linux | xclip -selection clipboard >/dev/null 2>&1\"");
  for (let i = 0; i < 20 && (await view.evaluate(() => window.__screen.clipboard.slice(-1)[0])) !== "Kopie-vom-Linux"; i++) { await sleep(250); }
  check("text copied on the machine lands on this device's clipboard", await view.evaluate(() => window.__screen.clipboard.slice(-1)[0]), "Kopie-vom-Linux");
  // --- Clipboard, this device -> machine: Ctrl+V carries it over and pastes it there.
  await view.evaluate(() => navigator.clipboard.writeText("Vom Browser ä€"));
  await view.locator("#screen").click({ position: { x: box.width * 300 / 1280, y: box.height * 200 / 800 } });
  await view.keyboard.press("Control+v");
  await sleep(1500);
  check("text pasted here is on the machine's clipboard", onDesktop("xclip -o -selection clipboard"), "Vom Browser ä€");

  // --- Somebody signs out and in again: the X server ends, another one starts (another display, another
  // key). The agent's helper ends with the first and a new one shows the second - the viewer stays.
  const seen = await view.evaluate(() => window.__screen.frames);
  onMachine("/desktop.sh 8");
  for (let i = 0; i < 60 && (await view.evaluate(() => window.__screen.frames)) < seen + 2; i++) { await sleep(500); }
  check("after the desktop was replaced by another one, the viewer sees that one", (await view.evaluate(() => window.__screen.frames)) >= seen + 2, true);
  await view.mouse.move(...at(500, 300));
  await sleep(600);
  check("and points in it", onDesktop("xdotool getmouselocation").replace(/ screen.*/, ""), "x:500 y:300");
  await view.close();

  // ================================================================================== a new version
  // Matgate brings it over its connection: the release for THIS system (linux-x64), checked, put in place of
  // the running program - which ends and is started again by whoever runs the service (here: a loop).
  const next = path.join(fs.mkdtempSync(path.join(os.tmpdir(), "matgate-linux-update-")), "matgate-agent-linux-x64");
  execFileSync("docker", ["cp", CONTAINER + ":/opt/next/matgate-agent", next]);
  const program = fs.readFileSync(next);
  const manifest = { version: "0.99.0.7", sha256: crypto.createHash("sha256").update(program).digest("hex"), size: program.length, platform: "linux-x64" };
  const asked = [];
  const feed = http.createServer((req, res) => {
    asked.push(req.url.split("/").pop());
    if (req.url.endsWith("/matgate-agent-linux-x64.json")) { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(manifest)); return; }
    if (req.url.endsWith("/matgate-agent-linux-x64")) { res.setHeader("Content-Length", program.length); res.end(program); return; }
    res.statusCode = 404;
    res.end();
  }).listen(17499, "127.0.0.1");
  await sleep(300);
  const before = await page.evaluate(async id => (await fetch("/api/agent/" + id + "/status", { credentials: "same-origin" })).json(), server.id);
  check("Matgate sees a newer agent for this system, and that this one can take it", [before.update.current, before.update.latest, before.update.available, before.update.canUpdate], ["0.10.0.1", "0.99.0.7", true, true]);
  const pushed = await page.evaluate(async ([id, token]) => {
    const response = await fetch("/api/agent/" + id + "/update", { method: "POST", credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
    return [response.status, (await response.json()).version];
  }, [server.id, csrf]);
  check("\"Jetzt aktualisieren\" sends it", pushed, [200, "0.99.0.7"]);
  let after = null;
  for (let i = 0; i < 40; i++) {
    await sleep(1500);
    after = await page.evaluate(async id => (await fetch("/api/agent/" + id + "/status", { credentials: "same-origin" })).json().catch(() => null), server.id);
    if (after && after.online && after.version === "0.99.0.7") { break; }
  }
  check("the agent comes back as the new version", [after && after.online, after && after.version, onMachine("/opt/matgate-agent/matgate-agent --version")], [true, "0.99.0.7", "0.99.0.7"]);
  // (The program itself is fetched once and kept; a second run finds it in Matgate.)
  check("it asked for the Linux release, not the Windows one", [asked.includes("matgate-agent-linux-x64.json"), asked.some(name => name === "matgate-agent.json" || name === "matgate-agent.exe")], [true, false]);
  check("the version before stays beside it", onMachine("/opt/matgate-agent/matgate-agent.previous --version"), "0.10.0.1");
  feed.close();

  // ================================================================================== installing it
  // A container has no systemd, so the service itself cannot start here - what the setup writes can be read:
  // the unit that starts the program as root, and the settings (for root only) with the user for programs.
  const installed = onMachine("/opt/matgate-agent/matgate-agent setup --port 7601 --user demo --root Daten=/srv/share --shell 2>&1; echo exit=$?");
  check("the setup needs root and says what it does", [/Installing Matgate Agent to \/opt\/matgate-agent, settings in \/etc\/matgate-agent/.test(installed), /Programs in a terminal run as demo/.test(installed)], [true, true]);
  check("it writes the unit that starts the agent with the machine, as root", (() => {
    const unit = onMachine("cat /etc/systemd/system/matgate-agent.service");
    return [/ExecStart=\/opt\/matgate-agent\/matgate-agent run --config \/etc\/matgate-agent\/matgate-agent\.json/.test(unit), /User=root/.test(unit), /Restart=always/.test(unit), /WantedBy=multi-user\.target/.test(unit)];
  })(), [true, true, true, true]);
  const written = JSON.parse(onMachine("cat /etc/matgate-agent/matgate-agent.json") || "{}");
  check("and the settings: port, user for programs, folders, a token of its own", [written.Port, written.RunAs, (written.Roots || []).map(r => r.Name + "=" + r.Path), (written.Terminals || []).filter(x => x.Enabled).map(x => x.Id).sort(), (written.Token || "").length >= 24],
    [7601, "demo", ["Daten=/srv/share"], ["claude", "shell"], true]);
  check("readable by root alone - the token is the key to the machine", onMachine("stat -c '%a %U' /etc/matgate-agent /etc/matgate-agent/matgate-agent.json | tr '\\n' ' '").trim(), "700 root 600 root");
  onMachine("/opt/matgate-agent/matgate-agent uninstall 2>&1");
  check("uninstalling takes it all away again", onMachine("ls /etc/matgate-agent /etc/systemd/system/matgate-agent.service /opt/matgate-agent 2>/dev/null | wc -l"), "0");
  execFileSync("docker", ["compose", "-f", path.join(__dirname, "docker-compose.yml"), "up", "-d", "--force-recreate", "agent-linux"], { stdio: "ignore" });

  await page.evaluate(async name => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && cell === name) { await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" }); }
    }
  }, NAME);
  onMachine("rm -f /srv/share/vom-terminal.txt; true");
  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
