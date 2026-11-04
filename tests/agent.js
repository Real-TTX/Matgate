// The Matgate agent: a program on a machine that Matgate connects to like to an RDP server (host, port,
// token) and that offers what it has - files, programs in a terminal. This starts the real agent on this
// machine (Windows: the terminal needs ConPTY), bound to 127.0.0.1 only, makes an "Agent" connection to it
// through the admin form and checks what a person would do with it: browse, upload, download, create,
// delete, a wrong token, and a program in a terminal tab that survives a reload and ends with its tab.
//
// The gateway runs in Docker and reaches the agent as host.docker.internal (Docker Desktop).
const { chromium } = require("playwright-core");
const { spawn, execFileSync } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");
const BASE = "http://127.0.0.1:18091";
const NAME = "Test-Agent";
const TOKEN = "test-agent-token-" + Date.now();
const PORT = 17488;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

const AGENT_EXE = path.join(__dirname, "..", "agent", "Matgate.Agent", "bin", "Debug", "net10.0-windows", "matgate-agent.exe");
const work = fs.mkdtempSync(path.join(os.tmpdir(), "matgate-agent-test-"));
const share = path.join(work, "share");
fs.mkdirSync(path.join(share, "Ordner"), { recursive: true });
fs.writeFileSync(path.join(share, "hallo.txt"), "hallo vom agent\n");
const MARK = "AGENT-READY-" + process.pid;
fs.writeFileSync(path.join(work, "matgate-agent.json"), JSON.stringify({
  Port: PORT,
  ListenAddress: "127.0.0.1",
  Token: TOKEN,
  Name: NAME,
  Roots: [{ Name: "Test", Path: share }],
  Terminals: [{ Id: "cmd", Title: "Eingabeaufforderung", Command: "cmd.exe /k echo " + MARK,
    Variants: [{ Id: "hello", Title: "Mit Gruss", Arguments: "& echo GRUSS-" + MARK }] }],
}, null, 2));

const runningTerminals = () => {
  try {
    const out = execFileSync("powershell.exe", ["-NoProfile", "-Command",
      "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'cmd.exe' -and $_.CommandLine -like '*" + MARK + "*' } | Measure-Object | ForEach-Object Count"], { encoding: "utf8" });
    return Number(out.trim() || 0);
  } catch { return -1; }
};

let agent;
(async () => {
  // Always built: an old exe would test old code.
  execFileSync("dotnet", ["build", path.join(__dirname, "..", "agent", "Matgate.Agent", "Matgate.Agent.csproj"), "-nologo", "-v", "q"], { stdio: "inherit" });
  // --- The setup, as a dry run: it says what it would do and changes nothing on this machine.
  const plan = execFileSync(AGENT_EXE, ["setup", "--dry-run", "--port", "7600", "--no-firewall"], { encoding: "utf8" });
  check("setup --dry-run plans the install without changing anything", [/Program Files.+Matgate Agent/.test(plan), /Starts at logon as/.test(plan), /Dry run - nothing changed/.test(plan)], [true, true, true]);
  const servicePlan = execFileSync(AGENT_EXE, ["setup", "--dry-run", "--mode", "service", "--account", "LocalSystem"], { encoding: "utf8" });
  check("and as a service under the account it is given", /Windows service under LocalSystem/.test(servicePlan), true);
  check("--version answers", /^[0-9]+[.][0-9]+[.][0-9]+/.test(execFileSync(AGENT_EXE, ["--version"], { encoding: "utf8" }).trim()), true);

  agent = spawn(AGENT_EXE, ["--config", path.join(work, "matgate-agent.json")], { stdio: ["ignore", "pipe", "pipe"] });
  let agentLog = "";
  agent.stdout.on("data", d => { agentLog += d; });
  agent.stderr.on("data", d => { agentLog += d; });
  for (let i = 0; i < 40 && !/listening on/.test(agentLog); i++) { await sleep(250); }
  check("the agent starts and listens on loopback only", /listening on:\s+127\.0\.0\.1:17488/.test(agentLog), true);

  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 900 } })).newPage();
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
  const token = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
  });

  // --- The connection, through the admin form like any other. Replaced on every run (the token changes).
  const saveConnection = async password => {
    const existing = (await servers()).find(s => s.name === NAME);
    await page.goto(BASE + (existing ? "/admin/servers/" + existing.id : "/admin/servers/new"), { waitUntil: "networkidle" });
    await sleep(500);
    await page.evaluate(([name, secret, port]) => {
      const set = (field, value) => {
        // The form has one field per protocol under some names; the one in use is the enabled one.
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
    }, [NAME, password, PORT]);
    await sleep(2000);
    return (await servers()).find(s => s.name === NAME);
  };
  const status = id => page.evaluate(async i => (await fetch("/api/agent/" + i + "/status", { credentials: "same-origin" })).json(), id);

  let server = await saveConnection("wrong-token");
  check("the agent connection is saved as protocol AGENT", server && server.protocol, "AGENT");
  const refused = await status(server.id);
  check("a wrong token is refused, and Matgate says so", [refused.online, /token/i.test(refused.error || "")], [false, true]);

  server = await saveConnection(TOKEN);
  const hello = await status(server.id);
  check("with the right token the agent is online", hello.online, true);
  const tray = JSON.parse(execFileSync(AGENT_EXE, ["status", "--port", String(PORT)], { encoding: "utf8" }));
  check("what the tray icon sees: the port and the open terminals (not whether Matgate is connected)", [tray.Port, tray.Terminals, "Connections" in tray], [PORT, 0, false]);
  check("and says what it offers", (hello.plugins || []).map(p => p.kind + ":" + p.id), ["files:files", "terminal:cmd"]);
  check("and who it is", [hello.name, /Windows/i.test(hello.os || "")], [NAME, true]);

  // --- Files, through the normal file API.
  const api = (route, init) => page.evaluate(async ([id, r, i, csrf]) => {
    const response = await fetch("/api/files/" + id + r, Object.assign({ credentials: "same-origin" }, i || {}, { headers: Object.assign({ "X-Matgate-Csrf": csrf }, (i && i.headers) || {}) }));
    const type = response.headers.get("content-type") || "";
    return { status: response.status, body: type.includes("json") ? await response.json() : await response.text() };
  }, [server.id, route, init, token]);
  const names = list => (list.body.entries || list.body.Entries || []).map(e => (e.name || e.Name) + ((e.isDirectory || e.IsDirectory) ? "/" : ""));

  const top = await api("/list?path=/");
  check("the top level lists the shared folders", names(top), ["Test/"]);
  const inside = await api("/list?path=/Test");
  check("a shared folder lists its content, folders first", names(inside), ["Ordner/", "hallo.txt"]);
  const download = await api("/download?path=" + encodeURIComponent("/Test/hallo.txt"));
  check("a file downloads with its content", download.body, "hallo vom agent\n");

  const big = "x".repeat(700 * 1024) + "ende";
  const uploaded = await page.evaluate(async ([id, csrf, content]) => {
    const data = new FormData();
    data.append("path", "/Test/Ordner");
    data.append("file", new Blob([content], { type: "text/plain" }), "gross.txt");
    return (await fetch("/api/files/" + id + "/upload", { method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": csrf } })).status;
  }, [server.id, token, big]);
  check("an upload bigger than one chunk arrives", uploaded, 200);
  const onDisk = fs.existsSync(path.join(share, "Ordner", "gross.txt")) ? fs.readFileSync(path.join(share, "Ordner", "gross.txt"), "utf8") : "";
  check("complete and under its own name on the machine", [onDisk.length, onDisk.endsWith("ende")], [big.length, true]);
  check("no half-written leftover beside it", fs.readdirSync(path.join(share, "Ordner")).filter(n => n.startsWith(".matgate-upload-")), []);
  const back = await api("/download?path=" + encodeURIComponent("/Test/Ordner/gross.txt"));
  check("and comes back the same, read in chunks", [back.body.length, back.body.endsWith("ende")], [big.length, true]);

  const post = (route, body) => api(route, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  check("a folder can be created", (await post("/mkdir", { path: "/Test", name: "Neu" })).status, 200);
  check("it exists on the machine", fs.existsSync(path.join(share, "Neu")), true);
  check("an empty file can be created", (await post("/create-file", { path: "/Test/Neu", name: "leer.txt" })).status, 200);
  check("deleting removes it", (await post("/delete", { paths: ["/Test/Neu"] })).status, 200);
  check("gone on the machine", fs.existsSync(path.join(share, "Neu")), false);
  check("the shared folder itself cannot be deleted", (await post("/delete", { paths: ["/Test"] })).status !== 200 && fs.existsSync(share), true);
  const escape = await api("/list?path=" + encodeURIComponent("/Test/../../.."));
  check("climbing out with .. ends at the top, not outside", names(escape), ["Test/"]);

  // --- A program in a terminal tab.
  await page.goto(BASE + "/?open=" + server.id, { waitUntil: "networkidle" });
  await sleep(3000);
  // The agent opens on its overview: the machine, its plugins as tiles.
  const overview = () => page.evaluate(() => {
    const home = document.querySelector(".connection-panel:not(.hidden) .agent-home");
    return home ? {
      online: !!home.querySelector(".agent-home-state.is-online"),
      tiles: [...home.querySelectorAll(".agent-tile strong")].map(t => t.textContent.trim()),
      sessions: [...home.querySelectorAll(".agent-sessions li strong")].map(t => t.textContent.trim()),
      folder: (home.querySelector("[data-agent-program='cmd']") || { dataset: {} }).dataset.agentFolder,
      variants: [...home.querySelectorAll("[data-agent-program='cmd'] [data-agent-variant]")].map(b => b.textContent.trim()),
    } : null;
  });
  const first = await overview();
  check("opening the agent shows its overview, online", first && first.online, true);
  check("with its plugins as tiles", first && first.tiles, ["Files", "Eingabeaufforderung"]);
  check("a program starts in the first shared folder unless chosen otherwise", first && first.folder, "/Test");
  check("and offers its own ways to start (variants)", first && first.variants, ["Mit Gruss"]);

  // Down into a subfolder with the folder browser in the tile.
  await page.locator(".agent-home [data-agent-program='cmd'] [data-agent-browse]").click();
  await sleep(1500);
  const browser = () => page.evaluate(() => {
    const box = document.querySelector(".agent-home [data-agent-program='cmd'] [data-agent-browser]");
    return box && !box.hidden ? {
      crumbs: [...box.querySelectorAll(".agent-crumbs button")].map(b => b.textContent.trim()),
      folders: [...box.querySelectorAll(".agent-folder-list button")].map(b => b.textContent.trim()),
    } : null;
  });
  check("the folder browser shows the subfolders of the chosen folder", await browser(), { crumbs: ["Shared folders", "Test"], folders: ["Ordner"] });
  await page.locator(".agent-home [data-agent-browser] [data-agent-go='/Test/Ordner']").click();
  await sleep(1500);
  check("and goes down into one", (await browser()).crumbs, ["Shared folders", "Test", "Ordner"]);
  await page.locator(".agent-home [data-agent-browser] [data-agent-pick]").click();
  check("a closed folder browser takes no room", await page.evaluate(() => [...document.querySelectorAll(".agent-home [data-agent-browser]")].filter(box => box.hidden && box.offsetHeight > 0).length), 0);
  check("taking it makes it the start folder", await page.evaluate(() => document.querySelector(".agent-home [data-agent-program='cmd']").dataset.agentFolder), "/Test/Ordner");
  check("nothing is running yet", first && first.sessions, []);
  await page.locator(".agent-home [data-agent-start='cmd']:not([data-agent-variant])").click();
  await sleep(3000);
  const frame = () => page.frames().find(f => /\/agent\/.+\/terminal/.test(f.url()));
  const screen = async () => {
    const f = frame();
    return f ? f.evaluate(() => (document.querySelector(".xterm-rows") || {}).textContent || "") : "";
  };
  for (let i = 0; i < 20 && !(await screen()).includes(MARK); i++) { await sleep(500); }
  if (!(await screen()).includes(MARK)) {
    const f = frame();
    console.log("     frame: " + (f ? f.url() : "none") + " status=" + (f ? JSON.stringify(await f.evaluate(() => document.getElementById("status") && document.getElementById("status").textContent)) : ""));
    console.log("     agent log: " + agentLog.split(/\r?\n/).slice(-8).join(" | "));
  }
  check("the program starts in a terminal tab and its output shows", (await screen()).includes(MARK), true);
  check("it runs on the machine", runningTerminals(), 1);
  check("in the chosen subfolder", /share[\\/]Ordner>/i.test(await screen()), true);

  await frame().locator(".xterm-helper-textarea").focus();
  await page.keyboard.type("echo eingabe-%COMPUTERNAME%-ok");
  await page.keyboard.press("Enter");
  await sleep(1500);
  // The program expands the variable - only its answer can contain the machine's name.
  check("typing reaches the program and its answer comes back",
    (await screen()).toUpperCase().includes(("eingabe-" + os.hostname() + "-ok").toUpperCase()), true);

  const sessionUrl = frame().url();
  await frame().goto(sessionUrl);
  for (let i = 0; i < 20 && !(await screen()).includes(MARK); i++) { await sleep(500); }
  check("after a reload the same program is there again, with what it showed", (await screen()).includes(MARK), true);
  check("and it is still one program, not a second", runningTerminals(), 1);

  // Back on the overview, the running program is listed and can be resumed.
  await page.locator(".session-tab--connection .session-tab-main").first().click();
  await sleep(800);
  await page.evaluate(() => [...document.querySelectorAll("#connection-tab-actions button")].find(b => /Übersicht|overview/i.test(b.title || b.getAttribute("aria-label") || "")).click());
  await sleep(2500);
  check("the overview lists the running program", (await overview()).sessions, ["Eingabeaufforderung"]);

  // Files are the other plugin, in this same tab, with the way back.
  await page.locator(".agent-home [data-agent-files]").click();
  await sleep(2500);
  const inFiles = await page.evaluate(() => ({
    rows: [...document.querySelectorAll(".connection-panel:not(.hidden) .file-table tbody tr")].map(r => r.textContent).join(" "),
    back: [...document.querySelectorAll("#connection-tab-actions button")].some(b => /Übersicht|overview/i.test(b.title || b.getAttribute("aria-label") || b.textContent)),
    places: !!document.querySelector(".connection-panel:not(.hidden) [data-file-place-select]"),
  }));
  check("the Dateien tile opens the file manager in the same tab", /Test/.test(inFiles.rows), true);
  check("with the way back to the overview, and no gateway places in the way", [inFiles.back, inFiles.places], [true, false]);

  await page.locator(".session-tab--page .session-tab-close").first().click();
  await sleep(2000);
  check("closing the tab does NOT end the program - the work stays", runningTerminals(), 1);
  // Back to the agent tab (closing a page tab may front another one); it shows its overview fresh.
  await page.locator(".session-tab--connection .session-tab-main").first().click();
  await sleep(800);
  await page.evaluate(() => [...document.querySelectorAll("#connection-tab-actions button")].find(b => /Übersicht|overview/i.test(b.title || b.getAttribute("aria-label") || "")).click());
  await sleep(2500);
  check("the overview still lists it", (await overview()).sessions, ["Eingabeaufforderung"]);
  await page.locator(".agent-home [data-agent-resume]").first().click();
  await sleep(1500);
  for (let i = 0; i < 20 && !(await screen()).includes(MARK); i++) { await sleep(500); }
  check("Fortsetzen opens it again, with what it showed before", (await screen()).toUpperCase().includes(("eingabe-" + os.hostname() + "-ok").toUpperCase()), true);
  check("still the same one program", runningTerminals(), 1);
  await page.locator(".session-tab--page .session-tab-close").first().click();
  await sleep(1500);

  // A variant: the same program, started with its extra arguments.
  // Back to the agent tab (closing a page tab may front another one); it shows its overview fresh.
  await page.locator(".session-tab--connection .session-tab-main").first().click();
  await sleep(800);
  await page.evaluate(() => [...document.querySelectorAll("#connection-tab-actions button")].find(b => /Übersicht|overview/i.test(b.title || b.getAttribute("aria-label") || "")).click());
  await sleep(2500);
  await page.locator(".agent-home [data-agent-variant='hello']").click();
  await sleep(1500);
  for (let i = 0; i < 20 && !(await screen()).includes("GRUSS-" + MARK); i++) { await sleep(500); }
  check("a variant starts the program with its own arguments", (await screen()).includes("GRUSS-" + MARK), true);
  check("as a second session beside the first", runningTerminals(), 2);
  await page.locator(".session-tab--page .session-tab-close").first().click();
  await sleep(1500);

  // Ending both from the overview.
  // Back to the agent tab (closing a page tab may front another one); it shows its overview fresh.
  await page.locator(".session-tab--connection .session-tab-main").first().click();
  await sleep(800);
  await page.evaluate(() => [...document.querySelectorAll("#connection-tab-actions button")].find(b => /Übersicht|overview/i.test(b.title || b.getAttribute("aria-label") || "")).click());
  await sleep(2500);
  check("the overview lists both", (await overview()).sessions.length, 2);
  for (let round = 0; round < 2; round++) {
    await page.locator(".agent-home [data-agent-kill]").first().click();
    await sleep(500);
    await page.evaluate(() => { const ok = document.querySelector("#confirm-dialog [data-confirm-ok]"); if (ok) { ok.click(); } });
    await sleep(2500);
  }
  for (let i = 0; i < 20 && runningTerminals() !== 0; i++) { await sleep(500); }
  check("Beenden in the overview ends them on the machine", runningTerminals(), 0);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  agent.kill();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); if (agent) { agent.kill(); } process.exit(1); });
