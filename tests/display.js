// Matgate's own engine for RDP, VNC and SSH - no Guacamole in between, all inside Matgate itself. RDP and
// VNC are Matgate's own clients (H.264 to the browser), shown by the screen page of the agent; SSH runs on
// SSH.NET and is shown by the terminal page. The machine to connect to is the test stack's "desktop"
// container: xrdp on 3389, TigerVNC on 5901, an xterm in each, and the VNC desktop once more on 3390 behind
// Network Level Authentication; and its "ssh" container. What the remote side really got is read there
// with docker exec.
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
// A command on the test desktop, as its user and on one of its displays.
const onDesktop = (command, display) => {
  try {
    return execFileSync("docker", ["exec", "-u", "demo", "-e", "DISPLAY=" + (display || ":10"), "matgate-tests-desktop-1", "sh", "-c", command], { encoding: "utf8" }).trim();
  } catch (e) { return "ERROR " + String(e.stderr || e.message).trim().split("\n").pop(); }
};
// A command on the test stack's SSH server.
const onSsh = command => {
  try {
    return execFileSync("docker", ["exec", "matgate-tests-ssh-1", "sh", "-c", command], { encoding: "utf8" }).trim();
  } catch (e) { return "ERROR " + String(e.stderr || e.message).trim().split("\n").pop(); }
};
const only = process.argv[2] || "";

(async () => {
  let b; try { b = await chromium.launch({ headless: true, args: ["--autoplay-policy=no-user-gesture-required"] }); }
  catch { b = await chromium.launch({ headless: true }); }
  const context = await b.newContext({ viewport: { width: 1300, height: 860 }, permissions: ["clipboard-read", "clipboard-write"] });
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
  // Creates the connection, or updates the one with this name.
  const connection = async (name, protocol, fields) => {
    const existing = (await servers()).find(s => s.name === name);
    await page.goto(BASE + (existing ? "/admin/servers/" + existing.id : "/admin/servers/new"), { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(([n, p, f]) => {
      document.querySelector("[data-protocol-option='" + p + "']").click();
      const set = (field, value) => {
        const all = [...document.querySelectorAll("[name='" + field + "']")];
        const el = all.find(e => !e.disabled) || all[0];
        if (el.type === "checkbox") { el.checked = !!value; }
        else { el.value = value; }
        el.dispatchEvent(new Event("change", { bubbles: true }));
      };
      set("name", n);
      Object.entries(f).forEach(([field, value]) => set(field, value));
      document.querySelector("[name='name']").form.requestSubmit();
    }, [name, protocol, fields]).catch(() => { /* the submit navigated before the call returned */ });
    await sleep(1500);
    return (await servers()).find(s => s.name === name);
  };
  const frames = view => view.evaluate(() => window.__screen.frames);
  // Generous: a busy machine takes a while to sign a session in.
  const waitFrames = async (view, count, tries = 150) => { for (let i = 0; i < tries && (await frames(view)) < count; i++) { await sleep(300); } };
  const picture = view => view.evaluate(() => {
    const canvas = document.getElementById("screen");
    const pixels = canvas.getContext("2d").getImageData(0, 0, canvas.width, canvas.height).data;
    let bright = 0;
    for (let i = 0; i < pixels.length; i += 4 * 53) { if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 90) { bright++; } }
    return { width: canvas.width, height: canvas.height, content: bright > 30 };
  });
  const pasteEvent = (view, kind, value) => view.evaluate(([k, v]) => {
    const dt = new DataTransfer();
    if (k === "text") { dt.setData("text/plain", v); }
    else { v.forEach(([name, size]) => dt.items.add(new File([new Uint8Array(size).fill(66)], name, { type: "application/octet-stream" }))); }
    // Copied somewhere else first: the page lost the focus (else Ctrl+V keeps what the machine copied itself).
    window.dispatchEvent(new Event("blur"));
    document.getElementById("sink").focus();
    document.getElementById("sink").dispatchEvent(new ClipboardEvent("paste", { clipboardData: dt, bubbles: true, cancelable: true }));
  }, [kind, value]);
  // Clicks into the remote xterm (it sits at 20,20) and types a line there.
  const typeLine = async (view, line) => {
    const box = await view.locator("#screen").boundingBox();
    const size = await picture(view);
    await view.mouse.click(box.x + 200 * box.width / size.width, box.y + 150 * box.height / size.height);
    await sleep(300);
    // Twice: a Ctrl+V left over from a paste would take the first one literally.
    await view.keyboard.press("Control+c");
    await view.keyboard.press("Control+c");
    // Key by key, with Shift where a US keyboard needs it - RDP sends key positions, not characters.
    const shifted = { ">": "Period", "|": "Backslash", "_": "Minus", "\"": "Quote", "$": "Digit4", "(": "Digit9", ")": "Digit0", "*": "Digit8" };
    const plain = { " ": "Space", "-": "Minus", ".": "Period", "/": "Slash", ";": "Semicolon", "=": "Equal", "'": "Quote" };
    for (const char of line) {
      const key = shifted[char] ? "Shift+" + shifted[char]
        : plain[char] ? plain[char]
        : /[A-Z]/.test(char) ? "Shift+Key" + char
        : /[a-z]/.test(char) ? "Key" + char.toUpperCase()
        : /[0-9]/.test(char) ? "Digit" + char : null;
      if (!key) { throw new Error("typeLine cannot type " + char); }
      await view.keyboard.press(key, { delay: 15 });
    }
    await view.keyboard.press("Enter");
    await sleep(900);
  };

  // The machine these tests run on is often busy with other things, and a remote desktop that is being resized
  // or signed in takes no input for a while (xrdp needed up to half a minute under load). So what is checked
  // here is that things arrive - tried again a few times - not that they arrive within a second. Something
  // that is broken never arrives, however often it is tried.
  const typed = async (view, line, file, display) => {
    for (let attempt = 0; attempt < 8; attempt++) {
      await typeLine(view, line);
      for (let i = 0; i < 6; i++) {
        const got = onDesktop("cat " + file + " 2>/dev/null", display);
        if (got && !/^ERROR/.test(got)) { return got; }
        await sleep(500);
      }
    }
    return onDesktop("cat " + file, display);
  };
  const remoteClipboard = (display, target) => onDesktop("timeout 3 xclip -o -selection clipboard" + (target ? " -t " + target : ""), display);
  const pasted = async (view, kind, value, display, arrived, target) => {
    let got = "";
    for (let attempt = 0; attempt < 4; attempt++) {
      await pasteEvent(view, kind, value);
      for (let i = 0; i < 12; i++) {
        await sleep(700);
        got = remoteClipboard(display, target);
        if (arrived(got)) { return got; }
      }
    }
    return got;
  };
  const copiedRemotely = async (view, text, display) => {
    const last = () => view.evaluate(() => window.__screen.clipboard.slice(-1)[0]);
    for (let attempt = 0; attempt < 5; attempt++) {
      onDesktop("printf '" + text + "' | timeout 20 xclip -i -selection clipboard -loops 0 >/dev/null 2>&1 &", display);
      for (let i = 0; i < 15; i++) {
        await sleep(400);
        if ((await last()) === text) { return text; }
      }
    }
    return last();
  };

  // =============================================================================================== RDP
  if (!only || only === "rdp") {
    const rdp = await connection("Test-RDP", "Rdp", { host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "demo-only-pw", keyboardLayout: "en-us-qwerty", engine: "" });
    check("RDP: the connection uses Matgate's own engine", rdp && rdp.engine, "matgate");
    // Clipboard owners left over from an earlier run.
    onDesktop("pkill xclip; true");

    // --- From the start page: a page tab with the screen, and the session toolbar of an RDP session.
    await page.goto(BASE + "/?open=" + rdp.id, { waitUntil: "networkidle" });
    await sleep(2500);
    const tabFrame = page.frames().find(f => /\/display\/.+\/screen/.test(f.url()));
    check("RDP: opening it shows the screen in a tab, without Guacamole", [!!tabFrame, page.frames().some(f => /guacamole/.test(f.url()))], [true, false]);
    if (tabFrame) {
      await waitFrames(tabFrame, 3);
      check("RDP: the picture arrives there", (await frames(tabFrame)) >= 3, true);
      for (let i = 0; i < 20 && !(await page.evaluate(() => !!document.querySelector("#connection-tab-actions [data-action='resolution']"))); i++) { await sleep(300); }
      const actions = await page.evaluate(() => [...document.querySelectorAll("#connection-tab-actions [data-action]")].map(x => x.dataset.action));
      check("RDP: the session toolbar steers it (no agent overview, but sound)", [["fullscreen", "resolution", "sound", "clipboard", "disconnect"].every(a => actions.includes(a)), actions.includes("agent-overview")], [true, false]);
      await page.evaluate(() => document.querySelector("#connection-tab-actions [data-action='disconnect']").click());
      await sleep(1500);
      check("RDP: Disconnect closes the tab", page.frames().some(f => /\/display\/.+\/screen/.test(f.url())), false);
    }

    // --- The page on its own.
    const view = await context.newPage();
    view.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await view.goto(BASE + "/display/" + rdp.id + "/screen", { waitUntil: "domcontentloaded" });
    // A still desktop is sent once and then once more, sharp - after that nothing until something changes.
    await waitFrames(view, 2);
    check("RDP: pictures arrive and are decoded", (await frames(view)) >= 2, true);
    // A session taken over from the last viewer draws itself again first.
    for (let i = 0; i < 20 && !(await picture(view)).content; i++) { await sleep(300); }
    const first = await picture(view);
    check("RDP: with the desktop on them", first.content, true);
    // Smooth and in full colour unless asked for otherwise - as every session was before the fastest response.
    for (let i = 0; i < 20 && !(await view.evaluate(() => !!window.__screen.stats)); i++) { await sleep(300); }
    check("RDP: a session opens smooth, in full colour", await view.evaluate(() => [window.__screen.stats.quality, window.__screen.stats.colours, window.__screen.stats.fpsLimit]), ["auto", 32, 60]);
    // Auto-resize: the remote desktop has the size of the picture area - no reconnect, as mstsc does it.
    const area = await view.evaluate(() => { const r = document.getElementById("stage").getBoundingClientRect(); return [Math.round(r.width) & ~1, Math.round(r.height) & ~1]; });
    check("RDP: the session has the size of the window", [first.width, first.height], area);
    const before = await frames(view);
    await view.setViewportSize({ width: 1000, height: 700 });
    const wanted = await (async () => { await sleep(1200); return view.evaluate(() => { const r = document.getElementById("stage").getBoundingClientRect(); return [Math.round(r.width) & ~1, Math.round(r.height) & ~1]; }); })();
    // xrdp takes the session down and up again for this - a tenth of a second on a quiet machine, half a minute
    // on a busy one.
    for (let i = 0; i < 300 && JSON.stringify(await picture(view).then(p => [p.width, p.height])) !== JSON.stringify(wanted); i++) { await sleep(300); }
    check("RDP: resizing the window resizes the session", await picture(view).then(p => [p.width, p.height]), wanted);
    check("RDP: without connecting again", [await view.evaluate(() => document.getElementById("status").classList.contains("bad")), (await frames(view)) > before], [false, true]);
    // The server takes the session down and up again for a resize; until it is done it takes no input.
    const remoteSize = () => onDesktop("xdpyinfo 2>/dev/null | grep dimensions | awk '{print $2}'");
    for (let i = 0; i < 120 && remoteSize() !== wanted.join("x"); i++) { await sleep(500); }
    check("RDP: the remote desktop itself has that size", remoteSize(), wanted.join("x"));

    // --- Keyboard and mouse: typed into the remote xterm, read back on the machine.
    onDesktop("rm -f /tmp/typed-rdp.txt");
    check("RDP: keys and clicks reach the session", await typed(view, "echo Hallo-RDP 42 > /tmp/typed-rdp.txt", "/tmp/typed-rdp.txt"), "Hallo-RDP 42");
    check("RDP: the remote cursor shape becomes the page's cursor", await view.evaluate(() => /^url\("?blob:/.test(document.getElementById("screen").style.cursor) || document.getElementById("screen").style.cursor === "default"), true);

    // --- Clipboard: text in both directions.
    check("RDP: text pasted here is on the remote clipboard", await pasted(view, "text", "Text vom Browser äöü €", ":10", got => got === "Text vom Browser äöü €"), "Text vom Browser äöü €");
    check("RDP: text copied in the session lands on this device's clipboard", await copiedRemotely(view, "Kopiert auf dem Server ß", ":10"), "Kopiert auf dem Server ß");

    // --- Clipboard: files. Pasted here, the session reads them through the clipboard's file streams.
    const uris = await pasted(view, "files", [["Bericht.txt", 7], ["Gross.bin", 700000]], ":10", got => /Bericht\.txt/.test(got) && /Gross\.bin/.test(got), "text/uri-list");
    console.log("     remote uri list: " + uris.replace(/\s+/g, " "));
    const paths = uris.split(/\r?\n/).filter(l => /^file:/.test(l)).map(l => decodeURIComponent(l.replace(/^file:\/\//, "")));
    check("RDP: pasted files are files on the remote clipboard", paths.map(p => p.split("/").pop()).sort(), ["Bericht.txt", "Gross.bin"]);
    if (paths.length === 2) {
      const sizes = paths.map(p => onDesktop("wc -c < '" + p + "'"));
      check("RDP: and the session reads them whole", sizes.sort(), ["7", "700000"]);
      check("RDP: with their content", onDesktop("head -c 7 '" + paths.find(p => /Bericht/.test(p)) + "'"), "BBBBBBB");
    }
    // The other way: files copied in the session are offered for download here.
    onDesktop("printf 'aus der Sitzung' > /tmp/Notiz.txt; head -c 300000 /dev/zero | tr '\\0' 'Z' > /tmp/Daten.bin");
    // Nothing opens by itself: the bar's button lists them.
    const downloadsOffered = () => view.evaluate(() => {
      const button = document.getElementById("clip-files");
      if (document.getElementById("toast").classList.contains("hidden") && !button.classList.contains("hidden")) { button.click(); }
      return [...document.querySelectorAll("#toast:not(.hidden) [data-download]")].map(a => a.getAttribute("download")).sort().join(",");
    });
    for (let attempt = 0; attempt < 5 && (await downloadsOffered()) !== "Daten.bin,Notiz.txt"; attempt++) {
      onDesktop("printf 'file:///tmp/Notiz.txt\\r\\nfile:///tmp/Daten.bin\\r\\n' | timeout 60 xclip -i -selection clipboard -t text/uri-list -loops 0 >/dev/null 2>&1 &");
      for (let i = 0; i < 20 && (await downloadsOffered()) !== "Daten.bin,Notiz.txt"; i++) { await sleep(400); }
    }
    const offered = await view.evaluate(() => [...document.querySelectorAll("#toast [data-download]")].map(a => ({ name: a.getAttribute("download"), href: a.getAttribute("href") })));
    check("RDP: files copied in the session are offered for download", offered.map(o => o.name).sort(), ["Daten.bin", "Notiz.txt"]);
    if (offered.length === 2) {
      const got = await view.evaluate(async list => Promise.all(list.map(async o => {
        const response = await fetch(o.href, { credentials: "same-origin" });
        const bytes = new Uint8Array(await response.arrayBuffer());
        return [o.name, response.status, bytes.length, o.name === "Daten.bin" ? bytes.every(x => x === 90) : new TextDecoder().decode(bytes)];
      })), offered);
      check("RDP: and download complete", got.sort((x, y) => x[0] < y[0] ? -1 : 1), [["Daten.bin", 200, 300000, true], ["Notiz.txt", 200, 15, "aus der Sitzung"]]);
      const forged = await view.evaluate(async href => (await fetch(href.replace(/token=[^&]+/, "token=falsch"), { credentials: "same-origin" })).status, offered[0].href);
      check("RDP: but not with a made-up token", forged, 404);
    }

    await view.close();

    // --- The fastest response: 16 bits a pixel and a desktop without what only costs pictures, asked for when the
    // session connects - here because this device chose it for this connection last time. "fresh": the session
    // above still runs (kept for a viewer to come back), and the page would ask whether to take it.
    // (xrdp keeps one session per user and colour depth: this is another X session than the one above.)
    const fast = await context.newPage();
    fast.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await fast.addInitScript(id => localStorage.setItem("matgate.screen.quality." + id, "fast"), rdp.id);
    await fast.goto(BASE + "/display/" + rdp.id + "/screen?fresh=1", { waitUntil: "domcontentloaded" });
    await waitFrames(fast, 2);
    check("RDP fast: pictures arrive", (await frames(fast)) >= 2, true);
    for (let i = 0; i < 20 && !(await picture(fast)).content; i++) { await sleep(300); }
    check("RDP fast: with the desktop on them", (await picture(fast)).content, true);
    const fastStats = () => fast.evaluate(() => window.__screen.stats ? [window.__screen.stats.quality, window.__screen.stats.colours, window.__screen.stats.fpsLimit] : null);
    for (let i = 0; i < 20 && !(await fastStats()); i++) { await sleep(300); }
    check("RDP fast: the session runs with 16-bit colours, sixty pictures a second", await fastStats(), ["fast", 16, 60]);
    check("RDP fast: the bar says so", /fewer colours|weniger Farben/.test(await fast.evaluate(() => document.getElementById("stats").textContent)), true);
    // Back to smooth on the running session: the pictures change at once, the colours with a new connection -
    // which the page offers, and makes when asked: the old session ends, a new one comes in full colour.
    await fast.evaluate(() => window.MatgateScreen.setQuality("auto"));
    await sleep(600);
    check("RDP fast -> smooth: the page offers a new connection for the colours", await fast.evaluate(() => [
      !document.getElementById("notice").classList.contains("hidden"),
      /colours|Farben/.test(document.getElementById("notice-text").textContent),
      !document.getElementById("notice-action").classList.contains("hidden"),
    ]), [true, true, true]);
    const fastSession = await fast.evaluate(() => new URLSearchParams(location.search).get("session"));
    await fast.evaluate(() => document.getElementById("notice-action").click());
    const reconnected = () => fast.evaluate(() => [new URLSearchParams(location.search).get("session"), window.__screen.stats ? window.__screen.stats.colours : 0]);
    for (let i = 0; i < 150; i++) {
      const [now, colours] = await reconnected();
      if (now !== fastSession && colours === 32) { break; }
      await sleep(300);
    }
    const [smoothSession, smoothColours] = await reconnected();
    check("RDP fast -> smooth: a new session, in full colour", [smoothSession !== fastSession, smoothColours], [true, 32]);
    check("RDP fast -> smooth: and the one with fewer colours is over", await fast.evaluate(async ([id, old]) =>
      (await (await fetch("/api/display/" + id + "/open-sessions", { credentials: "same-origin" })).json()).sessions.some(s => s.session === old), [rdp.id, fastSession]), false);
    for (let i = 0; i < 20 && !(await picture(fast)).content; i++) { await sleep(300); }
    check("RDP fast -> smooth: with the desktop on it", (await picture(fast)).content, true);
    await fast.close();

    // --- No stored password: the page asks, a wrong one is asked for again.
    const ask = await connection("Test-RDP-Frage", "Rdp", { host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "", keyboardLayout: "en-us-qwerty", engine: "" });
    const asking = await context.newPage();
    await asking.goto(BASE + "/display/" + ask.id + "/screen", { waitUntil: "domcontentloaded" });
    for (let i = 0; i < 300 && (await asking.evaluate(() => document.getElementById("auth").classList.contains("hidden"))); i++) { await sleep(300); }
    check("RDP without a password: the page asks for it", await asking.evaluate(() => [!document.getElementById("auth").classList.contains("hidden"), document.getElementById("auth-user").value]), [true, "demo"]);
    await asking.fill("#auth-password", "demo-only-pw");
    await asking.click("#auth-ok");
    await waitFrames(asking, 2);
    check("RDP without a password: and connects with what was typed", (await frames(asking)) >= 2, true);
    await asking.close();

    // --- Network Level Authentication: the server wants the password before the session.
    const nla = await connection("Test-RDP-NLA", "Rdp", { host: "desktop", port: "3390", targetUserName: "demo", targetPassword: "falsch-pw", keyboardLayout: "en-us-qwerty", engine: "" });
    const signing = await context.newPage();
    await signing.goto(BASE + "/display/" + nla.id + "/screen", { waitUntil: "domcontentloaded" });
    for (let i = 0; i < 300 && (await signing.evaluate(() => document.getElementById("auth").classList.contains("hidden"))); i++) { await sleep(300); }
    check("RDP with NLA: a refused password is asked for again", await signing.evaluate(() => !document.getElementById("auth").classList.contains("hidden")), true);
    await signing.fill("#auth-password", "demo-only-pw");
    await signing.click("#auth-ok");
    await waitFrames(signing, 3);
    check("RDP with NLA: and the right one signs in", (await frames(signing)) >= 3, true);
    await signing.close();
  }

  // =============================================================================================== VNC
  if (!only || only === "vnc") {
    const vnc = await connection("Test-VNC", "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "" });
    check("VNC: the connection uses Matgate's own engine", vnc && vnc.engine, "matgate");
    const view = await context.newPage();
    view.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await view.goto(BASE + "/display/" + vnc.id + "/screen", { waitUntil: "domcontentloaded" });
    // Two, not more: a desktop that already has the size of this window and does not change sends no third.
    await waitFrames(view, 2);
    check("VNC: pictures arrive and are decoded", (await frames(view)) >= 2, true);
    await sleep(1500);
    const first = await picture(view);
    check("VNC: with the desktop on them", first.content, true);
    const area = await view.evaluate(() => { const r = document.getElementById("stage").getBoundingClientRect(); return [Math.round(r.width) & ~1, Math.round(r.height) & ~1]; });
    for (let i = 0; i < 100 && JSON.stringify(await picture(view).then(p => [p.width, p.height])) !== JSON.stringify(area); i++) { await sleep(300); }
    check("VNC: the desktop takes the size of the window", await picture(view).then(p => [p.width, p.height]), area);
    onDesktop("rm -f /tmp/typed-vnc.txt", ":1");
    check("VNC: keys and clicks reach the desktop", await typed(view, "echo Hallo-VNC 42 > /tmp/typed-vnc.txt", "/tmp/typed-vnc.txt", ":1"), "Hallo-VNC 42");
    check("VNC: text pasted here is on the remote clipboard", await pasted(view, "text", "Text vom Browser äöü", ":1", got => got === "Text vom Browser äöü"), "Text vom Browser äöü");
    check("VNC: text copied on the desktop lands on this device's clipboard", await copiedRemotely(view, "Kopiert in VNC", ":1"), "Kopiert in VNC");
    await pasteEvent(view, "files", [["Bericht.txt", 7]]);
    await sleep(600);
    check("VNC: pasting files says that VNC cannot take them", /VNC/.test(await view.evaluate(() => document.getElementById("status").textContent)), true);
    check("VNC: no sound button to press for nothing", await view.evaluate(() => window.MatgateScreen.state().canSound), false);
    await view.close();
  }

  // =============================================================================================== SSH
  if (!only || only === "ssh") {
    const ssh = await connection("Test-SSH-eigen", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "" });
    check("SSH: the connection uses Matgate's own engine", ssh && ssh.engine, "matgate");
    const term = await context.newPage();
    term.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await term.goto(BASE + "/ssh/" + ssh.id + "/terminal", { waitUntil: "domcontentloaded" });
    const screenText = () => term.evaluate(() => document.querySelector(".xterm-rows") ? document.querySelector(".xterm-rows").textContent : "");
    for (let i = 0; i < 40 && !/\$/.test(await screenText()); i++) { await sleep(300); }
    check("SSH: a shell prompt appears", /\$/.test(await screenText()), true);
    await term.keyboard.type("echo eigene-$((6*7))-engine", { delay: 20 });
    await term.keyboard.press("Enter");
    for (let i = 0; i < 20 && !/eigene-42-engine/.test(await screenText()); i++) { await sleep(300); }
    check("SSH: typing reaches the shell and its answer comes back", /eigene-42-engine/.test(await screenText()), true);
    // --- Files: pasted into the terminal, they go to the server through SFTP, into the login's home folder.
    const fileName = "Bericht-" + Date.now() + ".txt";
    await term.evaluate(name => {
      const dt = new DataTransfer();
      dt.items.add(new File([new Uint8Array(5000).fill(67)], name, { type: "text/plain" }));
      document.querySelector("#screen textarea").dispatchEvent(new ClipboardEvent("paste", { clipboardData: dt, bubbles: true, cancelable: true }));
    }, fileName);
    for (let i = 0; i < 60 && !(await term.evaluate(() => window.__uploads.length)); i++) { await sleep(300); }
    const uploaded = await term.evaluate(() => window.__uploads[0] || null);
    check("SSH: a file pasted into the terminal is uploaded", uploaded && uploaded.name, fileName);
    if (uploaded) {
      const remote = "'" + uploaded.folder + "/" + uploaded.name + "'";
      check("SSH: and lies whole in the login's home folder on the server", onSsh("wc -c < " + remote + "; rm -f " + remote), "5000");
      check("SSH: the page says where it went", await term.evaluate(() => document.getElementById("note").textContent.includes(window.__uploads[0].folder)), true);
    }
    // Since 0.43.0 a pasted file's path is typed onto the command line (not sent).
    check("SSH: the pasted file's path is on the command line", await term.evaluate(() => {
      const last = window.__uploads[window.__uploads.length - 1];
      return !!last && document.querySelector(".xterm-rows").innerText.includes(last.name);
    }), true);
    await term.keyboard.press("Control+U");
    // Pasted text is still text for the terminal.
    await term.evaluate(() => {
      const dt = new DataTransfer();
      dt.setData("text/plain", "echo eingefuegt-$((40+2))");
      document.querySelector("#screen textarea").dispatchEvent(new ClipboardEvent("paste", { clipboardData: dt, bubbles: true, cancelable: true }));
    });
    await term.keyboard.press("Enter");
    for (let i = 0; i < 20 && !/eingefuegt-42/.test(await screenText()); i++) { await sleep(300); }
    check("SSH: pasted text is typed into the shell", /eingefuegt-42/.test(await screenText()), true);

    await term.setViewportSize({ width: 900, height: 500 });
    await sleep(800);
    await term.keyboard.type("stty size", { delay: 20 });
    await term.keyboard.press("Enter");
    await sleep(900);
    const size = await term.evaluate(() => { const s = window.MatgateTerminal.state(); return [s.rows, s.cols]; });
    // Row by row: the answer is a line of its own ("32 125").
    const rowsText = await term.evaluate(() => [...document.querySelectorAll(".xterm-rows > div")].map(d => d.textContent.trim()).filter(Boolean));
    const reported = (rowsText.filter(l => /^\d+ \d+$/.test(l)).pop() || "").split(" ").map(Number);
    check("SSH: the terminal's size follows the window", reported, size);
    await term.close();

    // --- No stored password: asked in the terminal, as an SSH client does.
    const ask = await connection("Test-SSH-Frage", "Ssh", { host: "ssh", port: "2222", targetUserName: "", targetPassword: "", engine: "" });
    const asking = await context.newPage();
    await asking.goto(BASE + "/ssh/" + ask.id + "/terminal", { waitUntil: "domcontentloaded" });
    const askText = () => asking.evaluate(() => document.querySelector(".xterm-rows") ? document.querySelector(".xterm-rows").textContent : "");
    for (let i = 0; i < 30 && !/login as:/.test(await askText()); i++) { await sleep(300); }
    check("SSH without a user: the terminal asks \"login as:\"", /login as:/.test(await askText()), true);
    await asking.keyboard.type("demo", { delay: 20 });
    await asking.keyboard.press("Enter");
    for (let i = 0; i < 20 && !/password:/.test(await askText()); i++) { await sleep(300); }
    await asking.keyboard.type("falsch", { delay: 20 });
    await asking.keyboard.press("Enter");
    for (let i = 0; i < 30 && !/Access denied/.test(await askText()); i++) { await sleep(300); }
    check("SSH: a wrong password is refused and asked for again, not shown", [/Access denied/.test(await askText()), /falsch/.test(await askText())], [true, false]);
    await asking.keyboard.type("demo-only-pw", { delay: 20 });
    await asking.keyboard.press("Enter");
    for (let i = 0; i < 40 && !/\$/.test((await askText()).split("Access denied").pop()); i++) { await sleep(300); }
    check("SSH: the right one opens the shell", /\$/.test((await askText()).split("Access denied").pop()), true);
    await asking.close();

    // --- From the start page: a page tab with the terminal and the toolbar's Disconnect.
    await page.goto(BASE + "/?open=" + ssh.id, { waitUntil: "networkidle" });
    await sleep(2500);
    check("SSH: opening it shows the terminal in a tab, without Guacamole", [page.frames().some(f => /\/ssh\/.+\/terminal/.test(f.url())), page.frames().some(f => /guacamole/.test(f.url()))], [true, false]);
    const actions = await page.evaluate(() => [...document.querySelectorAll("#connection-tab-actions [data-action]")].map(x => x.dataset.action));
    check("SSH: with fullscreen, paste, send files and disconnect in the toolbar", ["fullscreen", "clipboard", "upload", "disconnect"].every(a => actions.includes(a)) ? true : actions, true);
  }

  // =============================================================================================== Guacamole stays an option
  if (!only || only === "guac") {
    const guac = await connection("Test-SSH-Guacamole", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "guacamole" });
    check("Guacamole: a connection can still choose it", guac && guac.engine, "guacamole");
    await page.goto(BASE + "/?open=" + guac.id, { waitUntil: "networkidle" });
    await sleep(4000);
    check("Guacamole: and then opens as a Guacamole session, as before", [
      await page.evaluate(() => !!document.querySelector(".connection-panel:not(.hidden) .guac-display, .connection-panel:not(.hidden) canvas")),
      // In a connection tab of its own (the page tabs of the own engine may still be open beside it).
      await page.evaluate(() => !!document.querySelector(".session-tab--connection.active")),
    ], [true, true]);
  }

  // The connections of this script go again: other scripts open "the first connection" on the start page.
  await page.evaluate(async () => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && /^Test-(RDP|RDP-Frage|RDP-NLA|VNC|SSH-eigen|SSH-Frage|SSH-Guacamole)$/.test(cell)) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  });

  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
