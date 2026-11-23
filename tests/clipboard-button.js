// The clipboard button in the status bar (0.43.0): with a picture on this device's clipboard, a click makes a file
// of it ("Bild-<time>.png") and hands it to the session in front - on Matgate's own engine:
//   SSH  uploaded into the shell's current folder (when the shell says which, OSC 7) or the home, its path typed
//   RDP  a real file on the machine's clipboard (the session reads it through the clipboard's file streams)
//   VNC  cannot take files: no button
// Text alone is not taken over (Ctrl+V does that) - the button says so.
const { chromium } = require("playwright-core");
const { execFileSync } = require("child_process");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const onSsh = command => { try { return execFileSync("docker", ["exec", "matgate-tests-ssh-1", "sh", "-c", command], { encoding: "utf8" }).trim(); } catch { return ""; } };
const onDesktop = command => { try { return execFileSync("docker", ["exec", "-u", "demo", "-e", "DISPLAY=:10", "matgate-tests-desktop-1", "sh", "-c", command], { encoding: "utf8" }).trim(); } catch { return ""; } };

(async () => {
  const b = await chromium.launch({ headless: true });
  const context = await b.newContext({ viewport: { width: 1300, height: 860 }, permissions: ["clipboard-read", "clipboard-write"] });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name='username']", "admin");
  await page.fill("input[name='password']", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString())), page.click(".login-submit")]);
  await sleep(800);

  const servers = () => page.evaluate(async () => JSON.parse((await (await fetch("/", { credentials: "same-origin" })).text()).match(/const availableServers = (\[.*?\]);/s)[1]));
  const connection = async (name, protocol, fields) => {
    const existing = (await servers()).find(s => s.name === name);
    if (existing) { return existing; }
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(300);
    await page.evaluate(([n, p, f]) => {
      document.querySelector("[data-protocol-option='" + p + "']").click();
      const set = (field, value) => { const el = [...document.querySelectorAll("[name='" + field + "']")].find(e => !e.disabled); if (el) { el.value = value; el.dispatchEvent(new Event("input", { bubbles: true })); el.dispatchEvent(new Event("change", { bubbles: true })); } };
      set("name", n);
      Object.entries(f).forEach(([k, v]) => set(k, v));
      document.querySelector("[name='name']").form.requestSubmit();
    }, [name, protocol, fields]).catch(() => { });
    await sleep(1500);
    return (await servers()).find(s => s.name === name);
  };
  const putPicture = () => page.evaluate(async () => {
    const canvas = document.createElement("canvas");
    canvas.width = 40; canvas.height = 30;
    const g = canvas.getContext("2d"); g.fillStyle = "#c02"; g.fillRect(0, 0, 40, 30);
    const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/png"));
    await navigator.clipboard.write([new ClipboardItem({ "image/png": blob })]);
  });
  const button = () => page.evaluate(() => { const el = document.getElementById("status-clip"); return el ? { shown: !el.hidden, has: el.dataset.has } : null; });
  const note = () => page.evaluate(() => { const el = document.querySelector(".status-clip-note"); return el && !el.hidden ? el.textContent : ""; });
  const closeTabs = async () => {
    for (let i = 0; i < 6; i++) {
      const more = await page.evaluate(() => { const x = document.querySelector("#connection-tab-actions [data-action='disconnect']"); if (x) { x.click(); return true; } return false; });
      if (!more) { break; }
      await sleep(800);
    }
  };

  const made = [];
  try {
    // =========================================================================================== SSH
    onSsh("rm -f /config/Bild-*.png /config/Image-*.png /tmp/Bild-*.png /tmp/Image-*.png");
    const ssh = await connection("Clip-SSH", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "" });
    made.push(ssh.name);
    await page.goto(BASE + "/?open=" + ssh.id, { waitUntil: "networkidle" });
    let terminal = null;
    for (let i = 0; i < 40 && !terminal; i++) { terminal = page.frames().find(f => /\/ssh\/.+\/terminal/.test(f.url())); await sleep(300); }
    for (let i = 0; i < 40 && !/\$/.test(await terminal.evaluate(() => document.querySelector(".xterm-rows").innerText).catch(() => "")); i++) { await sleep(300); }
    await page.evaluate(() => window.updateStatusBar ? window.updateStatusBar() : null).catch(() => { });
    await sleep(300);
    check("SSH: the status bar has the clipboard button", (await button()).shown, true);

    await page.evaluate(() => navigator.clipboard.writeText("nur Text"));
    await page.click("#status-clip");
    await sleep(500);
    check("text alone is not taken over - the button says so", /Text|text/.test(await note()), true);

    await putPicture();
    await page.click("#status-clip");
    let home = "";
    for (let i = 0; i < 30 && !home; i++) { home = onSsh("ls /config | grep -E '^(Bild|Image)-.*\\.png$' | head -1"); await sleep(300); }
    check("SSH: the picture is a file with a name of its own in the home folder", /^(Bild|Image)-\d{4}-\d\d-\d\d-\d{6}\.png$/.test(home), true);
    await sleep(800);
    const line = await terminal.evaluate(() => document.querySelector(".xterm-rows").innerText);
    check("SSH: and its path is on the command line", line.includes("/config/" + home), true);
    check("the note says what was pasted", (await note()).includes(home), true);

    // The shell says where it is (OSC 7): the next picture goes there.
    await terminal.evaluate(() => window.MatgateTerminal.paste("\u0015cd /tmp && printf '\\033]7;file://ssh/tmp\\007'\r"));
    await sleep(1200);
    await putPicture();
    await page.click("#status-clip");
    let there = "";
    for (let i = 0; i < 30 && !there; i++) { there = onSsh("ls /tmp | grep -E '^(Bild|Image)-.*\\.png$' | head -1"); await sleep(300); }
    check("SSH: into the folder the shell is in, once it says so", !!there, true);
    await closeTabs();

    // =========================================================================================== RDP
    onDesktop("pkill xclip; true");
    const rdp = await connection("Clip-RDP", "Rdp", { host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "demo-only-pw", keyboardLayout: "en-us-qwerty", engine: "" });
    made.push(rdp.name);
    await page.goto(BASE + "/?open=" + rdp.id, { waitUntil: "networkidle" });
    let screen = null;
    for (let i = 0; i < 60; i++) {
      screen = page.frames().find(f => /\/display\/.+\/screen/.test(f.url()));
      if (screen && (await screen.evaluate(() => window.__screen ? window.__screen.frames : 0).catch(() => 0)) >= 3) { break; }
      await sleep(500);
    }
    await sleep(1500);
    check("RDP: the status bar has the clipboard button", (await button()).shown, true);
    await putPicture();
    await page.click("#status-clip");
    let uris = "";
    for (let i = 0; i < 40 && !/Bild-|Image-/.test(uris); i++) { await sleep(700); uris = onDesktop("timeout 3 xclip -o -selection clipboard -t text/uri-list"); }
    check("RDP: the picture is a file on the machine's clipboard", /(Bild|Image)-[0-9-]+\.png/.test(uris), true);
    const path = (uris.split(/\r?\n/).find(l => /^file:/.test(l)) || "").replace(/^file:\/\//, "");
    check("RDP: and the session reads it as a PNG", onDesktop("head -c 4 '" + decodeURIComponent(path) + "' | od -An -c | tr -d ' '"), "211PNG");
    await closeTabs();

    // =========================================================================================== VNC
    const vnc = await connection("Clip-VNC", "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "" });
    made.push(vnc.name);
    await page.goto(BASE + "/?open=" + vnc.id, { waitUntil: "networkidle" });
    for (let i = 0; i < 40; i++) {
      screen = page.frames().find(f => /\/display\/.+\/screen/.test(f.url()));
      if (screen && (await screen.evaluate(() => window.__screen ? window.__screen.frames : 0).catch(() => 0)) >= 2) { break; }
      await sleep(500);
    }
    await sleep(1000);
    check("VNC: cannot take files - no button", (await button()).shown, false);
    await closeTabs();
  }
  finally {
    for (const name of made) {
      await page.goto(BASE + "/admin?tab=servers", { waitUntil: "networkidle" });
      await page.evaluate(n => { const row = [...document.querySelectorAll("tr")].find(r => r.innerText.includes(n)); const form = row && row.querySelector("form[action*='/delete']"); if (form) { form.removeAttribute("data-confirm"); form.submit(); } }, name);
      await sleep(800);
    }
    onSsh("rm -f /config/Bild-*.png /config/Image-*.png /tmp/Bild-*.png /tmp/Image-*.png");
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
