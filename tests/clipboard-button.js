// The clipboard of this device, for the session in front (0.44.0), on Matgate's own engine:
//   - the status bar shows a button only while something is on the clipboard, and says what ("PNG 40×30", "Text")
//   - the button (and the session toolbar's clipboard button) opens a panel: what is in it, and what to do with it
//       a picture  as a PNG or JPG file, as a picture, or downloaded
//       text       as text, typed, as a TXT file - edited first, or something else typed in
//   - where it goes:
//       SSH  files into the shell's current folder (when the shell says which, OSC 7) or the home, the path typed
//       RDP  a real file on the machine's clipboard (the session reads it through the clipboard's file streams)
//       VNC  no files - text only
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
    window.dispatchEvent(new Event("focus"));
  });
  const putText = text => page.evaluate(async t => { await navigator.clipboard.writeText(t); window.dispatchEvent(new Event("focus")); }, text);
  const button = async () => {
    await sleep(500);
    return page.evaluate(() => { const el = document.getElementById("status-clip"); return el ? { shown: !el.hidden, label: el.textContent.trim() } : null; });
  };
  const openPanel = async () => {
    await page.click("#status-clip");
    for (let i = 0; i < 20; i++) {
      const ready = await page.evaluate(() => { const p = document.getElementById("clip-panel"); return !!p && !p.classList.contains("hidden") && !/Lese|Reading/.test(p.querySelector("[data-clip-what]").textContent); });
      if (ready) { break; }
      await sleep(200);
    }
    return page.evaluate(() => ({
      what: document.querySelector("#clip-panel [data-clip-what]").textContent,
      actions: [...document.querySelectorAll("#clip-panel [data-clip-action]")].map(b => b.dataset.clipAction),
      preview: !!document.querySelector("#clip-panel [data-clip-preview] img"),
    }));
  };
  const act = action => page.click("#clip-panel [data-clip-action='" + action + "']");
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
    onSsh("rm -f /config/Bild-*.png /config/Image-*.png /config/Bild-*.jpg /config/Image-*.jpg /config/Text-*.txt /tmp/Bild-*.png /tmp/Image-*.png");
    const ssh = await connection("Clip-SSH", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "" });
    made.push(ssh.name);
    await page.goto(BASE + "/?open=" + ssh.id, { waitUntil: "networkidle" });
    let terminal = null;
    for (let i = 0; i < 40 && !terminal; i++) { terminal = page.frames().find(f => /\/ssh\/.+\/terminal/.test(f.url())); await sleep(300); }
    for (let i = 0; i < 40 && !/\$/.test(await terminal.evaluate(() => document.querySelector(".xterm-rows").innerText).catch(() => "")); i++) { await sleep(300); }

    await page.evaluate(() => navigator.clipboard.write([]).catch(() => navigator.clipboard.writeText("")));
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    check("with nothing on the clipboard there is no button", (await button()).shown, false);

    await putPicture();
    const pictureButton = await button();
    check("with a picture the button is there and says what it is", [pictureButton.shown, /PNG 40×30/.test(pictureButton.label)], [true, true]);
    const panel = await openPanel();
    check("the panel shows the picture and what can be done with it", [panel.preview, ["png", "jpg", "download"].every(a => panel.actions.includes(a))], [true, true]);
    await act("png");
    let home = "";
    for (let i = 0; i < 30 && !home; i++) { home = onSsh("ls /config | grep -E '^(Bild|Image)-.*\\.png$' | head -1"); await sleep(300); }
    check("SSH: as a PNG file in the home folder, with a name of its own", /^(Bild|Image)-\d{4}-\d\d-\d\d-\d{6}\.png$/.test(home), true);
    await sleep(800);
    check("SSH: and its path is on the command line", (await terminal.evaluate(() => document.querySelector(".xterm-rows").innerText)).includes("/config/" + home), true);
    check("the note says what was pasted", (await note()).includes(home), true);

    await terminal.evaluate(() => window.MatgateTerminal.paste("\u0015"));
    await putPicture();
    await openPanel();
    await act("jpg");
    let jpg = "";
    for (let i = 0; i < 30 && !jpg; i++) { jpg = onSsh("ls /config | grep -E '^(Bild|Image)-.*\\.jpg$' | head -1"); await sleep(300); }
    check("SSH: or as a JPG file", !!jpg, true);
    check("which is a JPEG", onSsh("head -c 3 /config/'" + jpg + "' | od -An -tx1 | tr -d ' '"), "ffd8ff");

    // Text: as text, or as a TXT file - edited first.
    await terminal.evaluate(() => window.MatgateTerminal.paste("\u0015"));
    await putText("hallo welt");
    const textButton = await button();
    check("with text the button says it is text", /Text · 10/.test(textButton.label), true);
    const textPanel = await openPanel();
    check("the panel offers text, a TXT file or a file of this device", ["text", "txt", "pick"].every(a => textPanel.actions.includes(a)), true);
    await page.fill("#clip-panel [data-clip-text]", "echo eingefuegt-$((40+2))");
    await act("text");
    await sleep(500);
    await terminal.evaluate(() => window.MatgateTerminal.paste("\r"));
    let typed = false;
    for (let i = 0; i < 20 && !typed; i++) { typed = /eingefuegt-42/.test(await terminal.evaluate(() => document.querySelector(".xterm-rows").innerText)); await sleep(300); }
    check("SSH: edited text pasted as text runs", typed, true);
    await putText("Notiz fuer den Server");
    await openPanel();
    await act("txt");
    let txt = "";
    for (let i = 0; i < 30 && !txt; i++) { txt = onSsh("ls /config | grep -E '^Text-.*\\.txt$' | head -1"); await sleep(300); }
    check("SSH: text as a TXT file", !!txt && onSsh("cat /config/'" + txt + "'") === "Notiz fuer den Server", true);

    // The shell says where it is (OSC 7): the next file goes there.
    await terminal.evaluate(() => window.MatgateTerminal.paste("\u0015cd /tmp && printf '\\033]7;file://ssh/tmp\\007'\r"));
    await sleep(1200);
    await putPicture();
    await openPanel();
    await act("png");
    let there = "";
    for (let i = 0; i < 30 && !there; i++) { there = onSsh("ls /tmp | grep -E '^(Bild|Image)-.*\\.png$' | head -1"); await sleep(300); }
    check("SSH: into the folder the shell is in, once it says so", !!there, true);
    // The session toolbar's clipboard button opens the same panel.
    await page.evaluate(() => document.querySelector("#connection-tab-actions [data-action='clipboard']").click());
    await sleep(800);
    check("the toolbar's clipboard button opens the panel", await page.evaluate(() => !document.getElementById("clip-panel").classList.contains("hidden")), true);
    await page.click("#clip-panel [data-clip-close]");
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
    await putPicture();
    check("RDP: the button is there with the picture", (await button()).shown, true);
    const rdpPanel = await openPanel();
    check("RDP: files and a picture can be pasted", ["png", "jpg", "image"].every(a => rdpPanel.actions.includes(a)), true);
    await act("png");
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
    await putPicture();
    const vncPanel = await openPanel();
    check("VNC: no files - only text (and the picture to download)", [vncPanel.actions.includes("png"), vncPanel.actions.includes("text"), vncPanel.actions.includes("download")], [false, true, true]);
    await page.click("#clip-panel [data-clip-close]");
    await closeTabs();
  }
  finally {
    for (const name of made) {
      await page.goto(BASE + "/admin?tab=servers", { waitUntil: "networkidle" });
      await page.evaluate(n => { const row = [...document.querySelectorAll("tr")].find(r => r.innerText.includes(n)); const form = row && row.querySelector("form[action*='/delete']"); if (form) { form.removeAttribute("data-confirm"); form.submit(); } }, name);
      await sleep(800);
    }
    onSsh("rm -f /config/Bild-*.png /config/Image-*.png /config/Bild-*.jpg /config/Image-*.jpg /config/Text-*.txt /tmp/Bild-*.png /tmp/Image-*.png");
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
