// Text from a phone's keyboard in a session of Matgate's own engine: dictation that revises what it said,
// an Android keyboard that composes word by word, Backspace, and Ctrl from the key bar with a typed letter.
// The keyboards are played by the test - the same events in the same order - against the hidden text field
// of the screen page; what counts is what arrives in the xterm of the test desktop, read there with
// docker exec. Every scenario ends in a file, so text that arrived twice is text that does not match.
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
const onDesktop = (command, display) => {
  try {
    return execFileSync("docker", ["exec", "-u", "demo", "-e", "DISPLAY=" + display, "matgate-tests-desktop-1", "sh", "-c", command], { encoding: "utf8" }).trim();
  } catch (e) { return "ERROR " + String(e.stderr || e.message).trim().split("\n").pop(); }
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const admin = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await admin.newPage();
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

  // A phone: touch, a coarse pointer, the same login.
  const phone = await b.newContext({ viewport: { width: 900, height: 700 }, hasTouch: true, isMobile: true, storageState: await admin.storageState() });
  const open = async (id) => {
    const view = await phone.newPage();
    view.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await view.goto(BASE + "/display/" + id + "/screen", { waitUntil: "domcontentloaded" });
    for (let i = 0; i < 200 && (await view.evaluate(() => window.__screen.frames)) < 2; i++) { await sleep(300); }
    // Into the remote xterm (it sits at 20,20), then the phone's keyboard.
    const box = await view.locator("#screen").boundingBox();
    const size = await view.evaluate(() => ({ width: document.getElementById("screen").width, height: document.getElementById("screen").height }));
    view.clickTerminal = () => view.mouse.click(box.x + 200 * box.width / size.width, box.y + 150 * box.height / size.height);
    await view.clickTerminal();
    await view.evaluate(() => document.getElementById("sink").focus());
    await sleep(300);
    return view;
  };
  // What a keyboard does to a text field when it is not composing: text at the end, or a stretch replaced.
  const insert = (view, text) => view.evaluate(t => {
    const sink = document.getElementById("sink");
    sink.setRangeText(t, sink.value.length, sink.value.length, "end");
    sink.dispatchEvent(new InputEvent("input", { inputType: "insertText", data: t, bubbles: true }));
  }, text);
  const replace = (view, old, text) => view.evaluate(([o, t]) => {
    const sink = document.getElementById("sink");
    const at = sink.value.lastIndexOf(o);
    if (at < 0) { throw new Error("the field does not hold " + o + ": " + JSON.stringify(sink.value)); }
    sink.setRangeText(t, at, at + o.length, "end");
    sink.setSelectionRange(sink.value.length, sink.value.length);
    sink.dispatchEvent(new InputEvent("input", { inputType: "insertReplacementText", data: t, bubbles: true }));
  }, [old, text]);
  const arrived = async (file, display) => {
    for (let i = 0; i < 40; i++) {
      const got = onDesktop("cat " + file + " 2>/dev/null", display);
      if (got && !/^ERROR/.test(got)) { return got; }
      await sleep(500);
    }
    return onDesktop("cat " + file, display);
  };
  onDesktop("rm -f /tmp/ti-*.txt", ":1");

  // ================================================================================================ VNC
  const vnc = await connection("Test-Text-VNC", "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "" });
  const view = await open(vnc.id);
  check("the field keeps a placeholder for Backspace", await view.evaluate(() => document.getElementById("sink").value), " ");

  // --- Dictation as an iPhone does it: no composition, words arrive, earlier ones are rewritten in place,
  //     the field loses and regains the focus in between, and at the end the whole phrase is replaced.
  await insert(view, "echo ");
  await insert(view, "wie");
  await insert(view, " ght es");
  await replace(view, "ght", "geht");
  await view.evaluate(() => { const sink = document.getElementById("sink"); sink.blur(); sink.focus(); });
  await insert(view, " dir");
  await replace(view, "wie geht es dir", "Wie geht es dir");
  await insert(view, " > /tmp/ti-a.txt");
  await insert(view, "\n");
  check("dictation that corrects itself arrives once, corrected", await arrived("/tmp/ti-a.txt", ":1"), "Wie geht es dir");

  // --- An Android keyboard: every word is a composition that grows, then is committed; a real IME through
  //     the browser's own input method interface.
  const cdp = await phone.newCDPSession(view);
  const compose = async word => {
    for (let i = 1; i <= word.length; i++) {
      await cdp.send("Input.imeSetComposition", { text: word.slice(0, i), selectionStart: i, selectionEnd: i });
      await sleep(30);
    }
    await cdp.send("Input.insertText", { text: word });
  };
  await compose("echo");
  await cdp.send("Input.insertText", { text: " " });
  await compose("Hallo");
  await cdp.send("Input.insertText", { text: " " });
  // Autocorrection: the composed word is replaced by another one on commit.
  for (const part of ["W", "We", "Wel", "Welr"]) { await cdp.send("Input.imeSetComposition", { text: part, selectionStart: part.length, selectionEnd: part.length }); await sleep(30); }
  await cdp.send("Input.insertText", { text: "Welt" });
  await cdp.send("Input.insertText", { text: " " });
  await compose(">");
  await cdp.send("Input.insertText", { text: " /tmp/ti-b.txt" });
  await view.keyboard.press("Enter");
  check("composed words arrive once, the corrected one corrected", await arrived("/tmp/ti-b.txt", ":1"), "Hallo Welt");

  // --- Backspace: within what was typed, past a tap on the screen, and in a field with nothing typed.
  await insert(view, "echo abcX");
  await view.clickTerminal();
  await view.evaluate(() => document.getElementById("sink").focus());
  await view.keyboard.press("Backspace");
  await sleep(2400);
  // Quiet for a while: the tap may empty the field now.
  await view.clickTerminal();
  await view.evaluate(() => document.getElementById("sink").focus());
  check("after a quiet moment a tap starts the field over", await view.evaluate(() => document.getElementById("sink").value), " ");
  await view.keyboard.press("Backspace");
  await sleep(200);
  check("the placeholder is back after Backspace", await view.evaluate(() => document.getElementById("sink").value), " ");
  await insert(view, "d > /tmp/ti-c.txt");
  await view.keyboard.press("Enter");
  check("Backspace deletes on the machine, also with nothing typed here", await arrived("/tmp/ti-c.txt", ":1"), "abd");
  await view.close();

  // ================================================================================================ RDP
  // --- Ctrl from the key bar with a typed letter is a shortcut on the machine, not a character.
  onDesktop("rm -f /tmp/ti-*.txt", ":10");
  const rdp = await connection("Test-Text-RDP", "Rdp", { host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "demo-only-pw", keyboardLayout: "en-us-qwerty", engine: "" });
  const remote = await open(rdp.id);
  // A session that has just signed in takes no input for a moment (xrdp, on a busy machine, for seconds):
  // the line is typed until it arrives. Each try is a line of its own, so a doubled one would not match.
  let first = "";
  for (let attempt = 0; attempt < 6 && first !== "Text 42"; attempt++) {
    await remote.clickTerminal();
    await remote.evaluate(() => document.getElementById("sink").focus());
    await insert(remote, "echo Text 42 > /tmp/ti-d.txt");
    await insert(remote, "\n");
    for (let i = 0; i < 8 && first !== "Text 42"; i++) { await sleep(500); first = onDesktop("cat /tmp/ti-d.txt 2>/dev/null", ":10"); }
  }
  check("RDP: typed text arrives as characters", first, "Text 42");
  await insert(remote, "sleep 300");
  await insert(remote, "\n");
  await sleep(800);
  await remote.evaluate(() => document.querySelector("#keys [data-sticky='1d']").click());
  await insert(remote, "c");
  await sleep(500);
  check("RDP: Ctrl lets go after the one key", await remote.evaluate(() => document.querySelector("#keys [data-sticky='1d']").classList.contains("on")), false);
  await insert(remote, "echo frei > /tmp/ti-e.txt");
  await insert(remote, "\n");
  check("RDP: Ctrl from the key bar plus a typed c interrupts the command", await arrived("/tmp/ti-e.txt", ":10"), "frei");
  await remote.close();

  // ================================================================================================ SSH
  // --- The terminal page has the same field: xterm.js alone loses what a keyboard corrects afterwards.
  const onSsh = command => {
    try { return execFileSync("docker", ["exec", "matgate-tests-ssh-1", "sh", "-c", command], { encoding: "utf8" }).trim(); }
    catch (e) { return "ERROR " + String(e.stderr || e.message).trim().split("\n").pop(); }
  };
  const sshFile = async file => {
    for (let i = 0; i < 30; i++) {
      const got = onSsh("cat " + file + " 2>/dev/null");
      if (got && !/^ERROR/.test(got)) { return got; }
      await sleep(400);
    }
    return onSsh("cat " + file);
  };
  onSsh("rm -f /tmp/ti-*.txt");
  const ssh = await connection("Test-Text-SSH", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "" });
  const term = await phone.newPage();
  term.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await term.goto(BASE + "/ssh/" + ssh.id + "/terminal", { waitUntil: "domcontentloaded" });
  const rows = () => term.evaluate(() => (document.querySelector(".xterm-rows") || {}).textContent || "");
  for (let i = 0; i < 80 && !/\$/.test(await rows()); i++) { await sleep(300); }
  await term.evaluate(() => window.MatgateTerminal.focus());
  check("SSH: on a phone the keyboard types into the page's own field", await term.evaluate(() => document.activeElement.id), "sink");
  await insert(term, "echo ");
  await insert(term, "wie");
  await insert(term, " ght es");
  await replace(term, "ght", "geht");
  await term.evaluate(() => { const sink = document.getElementById("sink"); sink.blur(); sink.focus(); });
  await insert(term, " dir");
  await replace(term, "wie geht es dir", "Wie geht es dir");
  await insert(term, " > /tmp/ti-s.txt");
  await insert(term, "\n");
  check("SSH: dictation that corrects itself arrives once, corrected", await sshFile("/tmp/ti-s.txt"), "Wie geht es dir");
  const termIme = await phone.newCDPSession(term);
  const composeWord = async (word, commit) => {
    for (let i = 1; i <= word.length; i++) {
      await termIme.send("Input.imeSetComposition", { text: word.slice(0, i), selectionStart: i, selectionEnd: i });
      await sleep(30);
    }
    await termIme.send("Input.insertText", { text: commit || word });
  };
  await composeWord("echo");
  await termIme.send("Input.insertText", { text: " " });
  await composeWord("Hallo");
  await termIme.send("Input.insertText", { text: " " });
  await composeWord("Welr", "Welt");
  await termIme.send("Input.insertText", { text: "X" });
  await term.keyboard.press("Backspace");
  await termIme.send("Input.insertText", { text: " >> /tmp/ti-t.txt" });
  await term.keyboard.press("Enter");
  check("SSH: composed words and Backspace arrive as typed", await sshFile("/tmp/ti-t.txt"), "Hallo Welt");
  // A key without text goes to the terminal as the key it is: the arrow brings the last command back.
  await term.keyboard.press("ArrowUp");
  await term.keyboard.press("Enter");
  await sleep(800);
  check("SSH: an arrow key is still the terminal's", onSsh("cat /tmp/ti-t.txt"), "Hallo Welt\nHallo Welt");
  // The key bar: Ctrl+C lets go of a running command.
  await insert(term, "sleep 300");
  await insert(term, "\n");
  await sleep(600);
  await term.evaluate(() => document.querySelector("#keys [data-send='03']").click());
  await insert(term, "echo frei > /tmp/ti-v.txt");
  await insert(term, "\n");
  check("SSH: the key bar still interrupts", await sshFile("/tmp/ti-v.txt"), "frei");
  await term.close();

  // The connections of this script go again.
  await page.evaluate(async () => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && /^Test-Text-(VNC|RDP|SSH)$/.test(cell)) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  });
  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
