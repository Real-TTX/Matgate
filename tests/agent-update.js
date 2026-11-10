// Updating a Matgate agent from Matgate. Two real single-file builds of the agent (an "old" and a "new"
// version) are published; this script serves the new one as the release (matgate-agent.json + exe - the
// test stack reads MATGATE_AGENT_UPDATE_URL instead of GitHub), runs the old one on 127.0.0.1 and lets
// Matgate update it through the overview's "update now". Before that: a release whose exe is not the
// version it claims is refused by the agent, and the old version keeps running.
const { chromium } = require("playwright-core");
const { spawn, execFileSync } = require("child_process");
const crypto = require("crypto");
const fs = require("fs");
const http = require("http");
const os = require("os");
const path = require("path");
const BASE = "http://127.0.0.1:18091";
const NAME = "Update-Agent";
const PORT = 17489;
const FEED_PORT = 17499;
const TOKEN = "update-test-token-" + Date.now();
const OLD = "0.3.90.1";
const NEW = "0.3.90.2";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

const PROJECT = path.join(__dirname, "..", "agent", "Matgate.Agent", "Matgate.Agent.csproj");
const work = fs.mkdtempSync(path.join(os.tmpdir(), "matgate-agent-update-"));
const publish = (version, folder) => {
  execFileSync("dotnet", ["publish", PROJECT, "-c", "Release", "-r", "win-x64", "-o", folder, "-nologo", "-v", "q",
    "-p:Version=" + version, "-p:InformationalVersion=" + version], { stdio: "inherit" });
  return path.join(folder, "matgate-agent.exe");
};
const running = exe => {
  try {
    return Number(execFileSync("powershell.exe", ["-NoProfile", "-Command",
      "Get-Process matgate-agent -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq '" + exe + "' } | Measure-Object | ForEach-Object Count"], { encoding: "utf8" }).trim());
  } catch { return -1; }
};
const stopAll = exe => {
  try {
    execFileSync("powershell.exe", ["-NoProfile", "-Command",
      "Get-Process matgate-agent -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '" + work + "*' } | Stop-Process -Force"]);
  } catch { /* nothing left */ }
};

let feedServer;
(async () => {
  console.log("     publishing two builds (takes a minute) ...");
  const installed = publish(OLD, path.join(work, "installed"));
  const release = publish(NEW, path.join(work, "release"));
  const releaseBytes = fs.readFileSync(release);
  let manifest = { version: NEW, sha256: crypto.createHash("sha256").update(releaseBytes).digest("hex"), size: releaseBytes.length };

  // The release feed, as GitHub would serve it.
  feedServer = http.createServer((req, res) => {
    if (req.url.endsWith("/matgate-agent.json")) { res.setHeader("Content-Type", "application/json"); res.end(JSON.stringify(manifest)); return; }
    if (req.url.endsWith("/matgate-agent.exe")) { res.setHeader("Content-Type", "application/octet-stream"); res.end(releaseBytes); return; }
    res.statusCode = 404; res.end();
  }).listen(FEED_PORT, "127.0.0.1");

  fs.mkdirSync(path.join(work, "share"));
  const config = path.join(work, "matgate-agent.json");
  fs.writeFileSync(config, JSON.stringify({
    Port: PORT, ListenAddress: "127.0.0.1", Token: TOKEN, Name: NAME,
    Roots: [{ Name: "Test", Path: path.join(work, "share") }], Terminals: [],
  }, null, 2));
  spawn(installed, ["run", "--config", config], { stdio: "ignore", detached: true }).unref();
  for (let i = 0; i < 40 && running(installed) < 1; i++) { await sleep(250); }
  await sleep(2000);

  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1300, height: 900 } })).newPage();
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
  await page.evaluate(([name, secret, port]) => {
    document.querySelector("[data-protocol-option='Agent']").click();
    const set = (field, value) => {
      const el = [...document.querySelectorAll("[name='" + field + "']")].find(e => !e.disabled);
      el.value = value;
      el.dispatchEvent(new Event("change", { bubbles: true }));
    };
    set("name", name);
    set("host", "host.docker.internal");
    set("port", String(port));
    set("targetPassword", secret);
    document.querySelector("[name='name']").form.requestSubmit();
  }, [NAME, TOKEN, PORT]);
  await sleep(2000);
  const server = (await servers()).find(s => s.name === NAME);
  const status = () => page.evaluate(async id => (await fetch("/api/agent/" + id + "/status", { credentials: "same-origin" })).json(), server.id);

  const before = await status();
  check("the old agent runs", [before.online, before.version], [true, OLD]);
  check("Matgate sees the newer release", [before.update.available, before.update.latest, before.update.canUpdate, before.update.allowed], [true, NEW, true, true]);

  // --- A release that is not what it says: the agent refuses it, and nothing changes.
  manifest = Object.assign({}, manifest, { version: "0.3.90.3" });
  const csrf = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
  });
  const refused = await page.evaluate(async ([id, t]) => {
    const r = await fetch("/api/agent/" + id + "/update", { method: "POST", credentials: "same-origin", headers: { "X-Matgate-Csrf": t } });
    return { status: r.status, body: await r.json() };
  }, [server.id, csrf]);
  check("a release that is not the version it claims is refused", [refused.status, /0\.3\.90\.2/.test(refused.body.error || "")], [400, true]);
  await sleep(3000);
  check("and the old version keeps running", (await status()).version, OLD);

  // --- The real thing, through the overview.
  manifest = Object.assign({}, manifest, { version: NEW });
  await page.goto(BASE + "/?open=" + server.id, { waitUntil: "networkidle" });
  await sleep(4000);
  const notice = await page.evaluate(() => {
    const box = document.querySelector(".agent-home [data-agent-update-box]");
    return box ? { text: box.textContent.replace(/\s+/g, " ").trim(), button: !!box.querySelector("[data-agent-update]") } : null;
  });
  check("the overview shows the new version, with a button", notice && [/0\.3\.90\.2/.test(notice.text), notice.button], [true, true]);
  const appIcon = await page.evaluate(() => [...document.querySelectorAll("#connection-tab-actions button")].some(b => /Agent-Übersicht|Agent overview/.test(b.title || b.getAttribute("aria-label") || "")));
  check("the toolbar has the agent's icon back to its start screen", appIcon, true);

  // A second process from the same file, as the tray icon is (in every signed-in session): a running program
  // cannot be overwritten, so the update has to move it aside instead. (screen-probe on the test picture holds
  // the file without showing anything.)
  const holder = spawn(installed, ["screen-probe", "--test", "--seconds", "150"], { stdio: "ignore", env: { ...process.env, MATGATE_AGENT_SCREEN_TEST: "1" } });
  await sleep(1500);
  await page.click(".agent-home [data-agent-update]");
  let after = null;
  for (let i = 0; i < 60; i++) {
    await sleep(3000);
    const now = await status().catch(() => null);
    if (now && now.online && now.version === NEW) { after = now; break; }
  }
  check("Matgate sent the update and the agent came back as the new version - while another process still ran the old file", after && after.version, NEW);
  holder.kill();
  await sleep(1000);
  check("the new program replaced the old one in place", running(installed), 1);
  await sleep(4000);
  const settled = await page.evaluate(() => ({
    notice: !!document.querySelector(".agent-home [data-agent-update-box]"),
    foot: (document.querySelector(".agent-home-foot") || {}).textContent || "",
  }));
  check("the overview no longer offers an update", [settled.notice, /0\.3\.90\.2/.test(settled.foot)], [false, true]);
  check("the previous version is kept as a backup", fs.existsSync(installed + ".bak"), true);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  stopAll();
  feedServer.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); stopAll(); if (feedServer) { feedServer.close(); } process.exit(1); });
