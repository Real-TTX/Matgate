// The key bar: the buttons under a session on a phone - keys its keyboard does not have, and the user's own
// shortcuts. One list per user (Account -> Key bar), shown by three kinds of session, each with its own idea
// of what a key is: the screen page (RDP and VNC through Matgate's own engine) takes scan codes and text,
// the terminal page (SSH) takes bytes, a Guacamole session takes keysyms.
//
// First the account: the list, a button of one's own, the form that refuses what is no key, order, removing,
// resetting. Then the sessions, with two own buttons added to the default. What counts there is what
// arrives: in the xterm of the test desktop and in the shell of the SSH server (read with docker exec), and
// for Guacamole the key events that go through the tunnel.
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
const run = (container, user, display) => command => {
  const args = ["exec"].concat(user ? ["-u", user] : [], display ? ["-e", "DISPLAY=" + display] : [], [container, "sh", "-c", command]);
  try { return execFileSync("docker", args, { encoding: "utf8" }).trim(); }
  catch (e) { return "ERROR " + String(e.stderr || e.message).trim().split("\n").pop(); }
};
const onVnc = run("matgate-tests-desktop-1", "demo", ":1");
const onRdp = run("matgate-tests-desktop-1", "demo", ":10");
const onSsh = run("matgate-tests-ssh-1");
const arrived = async (where, file) => {
  for (let i = 0; i < 40; i++) {
    const got = where("cat " + file + " 2>/dev/null");
    if (got && !/^ERROR/.test(got)) { return got; }
    await sleep(400);
  }
  return where("cat " + file);
};
const DEFAULT = ["esc", "tab", "shift-tab", "ctrl", "alt", "win", "delete", "left", "up", "down", "right", "ctrl-c", "ctrl-v", "enter", "ctrl-alt-delete"];
const ON_SCREEN = DEFAULT.filter(id => id !== "shift-tab" && id !== "enter");
const IN_TERMINAL = DEFAULT.filter(id => id !== "win" && id !== "delete" && id !== "ctrl-alt-delete");

(async () => {
  let b = await chromium.launch({ headless: true });
  const admin = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await admin.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  // The application asks with dialogs of its own; one of the browser's would be a mistake.
  const browserDialogs = [];
  page.on("dialog", async d => { browserDialogs.push(d.type()); await d.dismiss(); });
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

  // ============================================================================================ the account
  const list = async () => {
    await page.goto(BASE + "/account?tab=keys", { waitUntil: "networkidle" });
    return page.evaluate(() => [...document.querySelectorAll("[data-key-button]")].map(row => ({
      id: row.dataset.keyButton,
      label: row.querySelector(".key-cap").textContent.trim(),
      cells: [...row.querySelectorAll("td")].slice(1, 4).map(cell => cell.textContent.trim()),
    })));
  };
  // A form of the list, sent as it is - without the question a person gets first.
  const send = selector => page.evaluate(async s => {
    const form = document.querySelector(s);
    if (!form) { return false; }
    await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
    return true;
  }, selector);
  // A new button, straight through the address the form posts to.
  const create = fields => page.evaluate(async f => {
    const body = new FormData();
    body.set("_csrf", document.querySelector("input[name='_csrf']").value);
    Object.entries(f).forEach(([name, value]) => body.set(name, value));
    await fetch("/account/keys", { method: "POST", body, credentials: "same-origin", redirect: "manual" });
  }, fields);

  // A run that was cut short may have left a list of its own behind.
  await list();
  await send("form[action='/account/keys/reset']");

  let rows = await list();
  check("without a list of one's own the bar is the default", rows.map(r => r.id), DEFAULT);
  check("and there is nothing to reset", await page.locator("[data-key-reset]").count(), 0);
  check("the key bar is a tab of the account", await page.evaluate(() => document.querySelector(".tab-button.active").getAttribute("data-tab-target")), "keys");
  check("the first button cannot move further forward, the last not further back", await page.evaluate(() => {
    const all = [...document.querySelectorAll("[data-key-button]")];
    return [all[0].querySelector("[data-key-move='up']").disabled, all[0].querySelector("[data-key-move='down']").disabled,
      all[all.length - 1].querySelector("[data-key-move='down']").disabled];
  }), [true, false, true]);

  // --- A button of one's own, on a page of its own.
  await page.click("[data-key-new]");
  await page.waitForSelector("#key-button-form");
  check("a new button starts with no key, and a modifier alone may wait for the next key", await page.evaluate(() => [
    document.querySelector("[name='key']").value, document.querySelector("[data-key-sticky]").hidden, document.querySelector("[data-key-character]").hidden]), ["", false, true]);
  await page.selectOption("[name='key']", "Tab");
  check("with a key chosen, waiting is not offered", await page.evaluate(() => document.querySelector("[data-key-sticky]").hidden), true);
  await page.selectOption("[name='key']", "char");
  check("a character has a field of its own", await page.evaluate(() => document.querySelector("[data-key-character]").hidden), false);
  await page.check("[name='modCtrl']");
  await page.fill("[name='character']", "ab");
  await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='key-button-form']")]);
  check("two characters are no key: the page says so and keeps what was entered", await page.evaluate(() => [
    !!document.querySelector("[data-key-error]"), document.querySelector("[name='modCtrl']").checked, document.querySelector("[name='key']").value]), [true, true, "char"]);
  check("and nothing was stored", (await list()).length, DEFAULT.length);

  await page.click("[data-key-new]");
  await page.waitForSelector("#key-button-form");
  await page.check("[name='modCtrl']");
  await page.selectOption("[name='key']", "char");
  await page.fill("[name='character']", "l");
  await Promise.all([page.waitForURL(u => /tab=keys/.test(u.toString())), page.click("button[form='key-button-form']")]);
  rows = await list();
  const own = rows[rows.length - 1];
  check("the new button is at the end, named after its keys", [rows.length, /^(Strg|Ctrl)\+L$/.test(own.label), /^(Strg|Ctrl) \+ L$/.test(own.cells[0])], [DEFAULT.length + 1, true, true]);
  check("the bar is the user's own now: it can be reset", await page.locator("[data-key-reset]").count(), 1);

  // --- Editing: a label and a place.
  await page.click("[data-key-button='" + own.id + "'] .key-cap");
  await page.waitForSelector("#key-button-form");
  check("the page shows the button as it is", await page.evaluate(() => [
    document.querySelector("[name='modCtrl']").checked, document.querySelector("[name='modAlt']").checked,
    document.querySelector("[name='key']").value, document.querySelector("[name='character']").value]), [true, false, "char", "L"]);
  await page.fill("[name='label']", "Leeren");
  await page.selectOption("[name='scope']", "terminal");
  await Promise.all([page.waitForURL(u => /tab=keys/.test(u.toString())), page.click("button[form='key-button-form']")]);
  rows = await list();
  check("the label is what is written on it, the keys stay", [rows[rows.length - 1].label, rows[rows.length - 1].id, /^(Strg|Ctrl) \+ L$/.test(rows[rows.length - 1].cells[0]), /Terminal/.test(rows[rows.length - 1].cells[2])], ["Leeren", own.id, true, true]);

  // --- The order: one place at a time.
  await Promise.all([page.waitForLoadState("networkidle"), page.click("[data-key-button='" + own.id + "'] [data-key-move='up']")]);
  rows = await list();
  check("one place forward", rows.map(r => r.id).slice(-2), [own.id, "ctrl-alt-delete"]);

  // --- Removing asks first, with a dialog of the application.
  await page.click("[data-key-button='" + own.id + "'] .danger-action");
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  await page.click("#confirm-dialog [data-confirm-cancel]");
  await sleep(300);
  check("\"no\" keeps the button", (await list()).length, DEFAULT.length + 1);
  await page.click("[data-key-button='" + own.id + "'] .danger-action");
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  await Promise.all([page.waitForLoadState("networkidle"), page.click("#confirm-dialog [data-confirm-ok]")]);
  await sleep(300);
  rows = await list();
  check("\"yes\" removes it - the rest stays the user's own list", [rows.map(r => r.id), await page.locator("[data-key-reset]").count()], [DEFAULT, 1]);

  // --- A default button changed, then everything back.
  await send("form[action='/account/keys/win/delete']");
  check("a button of the default can go as well", (await list()).map(r => r.id).includes("win"), false);
  await page.click("[data-key-reset]");
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  await Promise.all([page.waitForLoadState("networkidle"), page.click("#confirm-dialog [data-confirm-ok]")]);
  await sleep(300);
  rows = await list();
  check("resetting brings the default back", [rows.map(r => r.id), await page.locator("[data-key-reset]").count()], [DEFAULT, 0]);
  check("no dialog of the browser was involved", browserDialogs, []);

  // --- Two own buttons for the sessions: a character, and the same with Shift.
  await create({ key: "char", character: "x", scope: "all" });
  await create({ modShift: "on", key: "char", character: "x", scope: "all" });
  rows = await list();
  const [lower, upper] = rows.slice(-2);
  check("a character and its Shift are two buttons", [lower.label, upper.label], ["X", "⇧X"]);

  // ============================================================================================ sessions
  // A phone: touch, a coarse pointer, the same login, and a clipboard it may read.
  const phone = await b.newContext({
    viewport: { width: 900, height: 700 }, hasTouch: true, isMobile: true, storageState: await admin.storageState(),
    permissions: ["clipboard-read", "clipboard-write"],
  });
  const bar = view => view.evaluate(() => [...document.querySelectorAll("#keys button")].map(button => button.dataset.id));
  const press = async (view, id) => {
    await view.evaluate(i => document.querySelector("#keys [data-id='" + i + "']").click(), id);
    await sleep(150);
  };
  const lit = (view, id) => view.evaluate(i => document.querySelector("#keys [data-id='" + i + "']").classList.contains("on"), id);
  // What a phone's keyboard does to the text field: text at the end.
  const insert = (view, text) => view.evaluate(t => {
    const sink = document.getElementById("sink");
    sink.setRangeText(t, sink.value.length, sink.value.length, "end");
    sink.dispatchEvent(new InputEvent("input", { inputType: "insertText", data: t, bubbles: true }));
  }, text);
  const openScreen = async id => {
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
  // A session that has just signed in takes no input for a moment: a line is typed until it arrives.
  const ready = async (view, where, file) => {
    let got = "";
    for (let attempt = 0; attempt < 6 && got !== "da"; attempt++) {
      await view.clickTerminal();
      await view.evaluate(() => document.getElementById("sink").focus());
      await insert(view, "echo da > " + file);
      await insert(view, "\n");
      for (let i = 0; i < 8 && got !== "da"; i++) { await sleep(500); got = where("cat " + file + " 2>/dev/null"); }
    }
    return got;
  };

  // ------------------------------------------------------------------------------------------------ VNC
  onVnc("rm -f /tmp/kb-*.txt");
  const vnc = await connection("Test-Keys-VNC", "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "" });
  let view = await openScreen(vnc.id);
  check("VNC: the bar has the buttons for desktops, the own ones at the end", await bar(view), ON_SCREEN.concat([lower.id, upper.id]));
  check("VNC: and it shows on a phone", await view.evaluate(() => getComputedStyle(document.getElementById("keys")).display), "flex");
  check("VNC: the session takes input", await ready(view, onVnc, "/tmp/kb-0.txt"), "da");
  await insert(view, "echo ");
  await press(view, lower.id);
  await press(view, upper.id);
  await insert(view, " > /tmp/kb-a.txt");
  await insert(view, "\n");
  check("VNC: a character button types its character, with Shift the upper-case one", await arrived(onVnc, "/tmp/kb-a.txt"), "xX");
  await insert(view, "sleep 300");
  await insert(view, "\n");
  await sleep(700);
  await press(view, "ctrl-c");
  await insert(view, "echo frei > /tmp/kb-b.txt");
  await insert(view, "\n");
  check("VNC: Ctrl+C from the bar interrupts the command", await arrived(onVnc, "/tmp/kb-b.txt"), "frei");
  await insert(view, "echo eins >> /tmp/kb-c.txt");
  await insert(view, "\n");
  await sleep(500);
  await press(view, "up");
  await insert(view, "\n");
  await sleep(900);
  check("VNC: the arrow brings the last command back", onVnc("cat /tmp/kb-c.txt"), "eins\neins");
  await press(view, "ctrl");
  check("VNC: Ctrl stays down ...", await lit(view, "ctrl"), true);
  await press(view, "left");
  check("VNC: ... for the one key that follows", await lit(view, "ctrl"), false);
  // Ctrl+V brings this device's clipboard along (an xterm does not paste on Ctrl+V - the clipboard shows it).
  await view.evaluate(() => navigator.clipboard.writeText("vom Telefon 42"));
  await press(view, "ctrl-v");
  let clip = "";
  for (let i = 0; i < 20 && clip !== "vom Telefon 42"; i++) { await sleep(400); clip = onVnc("timeout 3 xclip -o -selection clipboard"); }
  check("VNC: Ctrl+V puts this device's clipboard on the machine", clip, "vom Telefon 42");
  await view.close();

  // ------------------------------------------------------------------------------------------------ RDP
  onRdp("rm -f /tmp/kb-*.txt");
  const rdp = await connection("Test-Keys-RDP", "Rdp", { host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "demo-only-pw", keyboardLayout: "en-us-qwerty", engine: "" });
  view = await openScreen(rdp.id);
  check("RDP: the session takes input", await ready(view, onRdp, "/tmp/kb-0.txt"), "da");
  await insert(view, "echo ");
  await press(view, lower.id);
  await press(view, upper.id);
  await insert(view, " > /tmp/kb-a.txt");
  await insert(view, "\n");
  check("RDP: a character button types its character, with Shift the upper-case one", await arrived(onRdp, "/tmp/kb-a.txt"), "xX");
  await insert(view, "sleep 300");
  await insert(view, "\n");
  await sleep(800);
  await press(view, "ctrl-c");
  await insert(view, "echo frei > /tmp/kb-b.txt");
  await insert(view, "\n");
  check("RDP: Ctrl+C from the bar is a shortcut on the machine, not a letter", await arrived(onRdp, "/tmp/kb-b.txt"), "frei");
  await insert(view, "echo eins >> /tmp/kb-c.txt");
  await insert(view, "\n");
  await sleep(500);
  await press(view, "up");
  await insert(view, "\n");
  await sleep(900);
  check("RDP: the arrow brings the last command back", onRdp("cat /tmp/kb-c.txt"), "eins\neins");
  await view.close();

  // ------------------------------------------------------------------------------------------------ SSH
  onSsh("rm -f /tmp/kb-*.txt");
  const ssh = await connection("Test-Keys-SSH", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "" });
  const term = await phone.newPage();
  term.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await term.goto(BASE + "/ssh/" + ssh.id + "/terminal", { waitUntil: "domcontentloaded" });
  const screenText = () => term.evaluate(() => (document.querySelector(".xterm-rows") || {}).textContent || "");
  for (let i = 0; i < 80 && !/\$/.test(await screenText()); i++) { await sleep(300); }
  await term.evaluate(() => window.MatgateTerminal.focus());
  check("SSH: the bar has the buttons for terminals, the own ones at the end", await bar(term), IN_TERMINAL.concat([lower.id, upper.id]));
  await insert(term, "echo ");
  await press(term, lower.id);
  await press(term, upper.id);
  await insert(term, " > /tmp/kb-a.txt");
  await press(term, "enter");
  check("SSH: character buttons type, and Enter from the bar ends the line", await arrived(onSsh, "/tmp/kb-a.txt"), "xX");
  await insert(term, "sleep 300");
  await press(term, "enter");
  await sleep(600);
  await press(term, "ctrl-c");
  await insert(term, "echo frei > /tmp/kb-b.txt");
  await press(term, "enter");
  check("SSH: Ctrl+C from the bar interrupts the command", await arrived(onSsh, "/tmp/kb-b.txt"), "frei");
  // Ctrl that waits: the letter typed next becomes its control character.
  await insert(term, "sleep 300");
  await press(term, "enter");
  await sleep(600);
  await press(term, "ctrl");
  check("SSH: Ctrl stays down ...", await lit(term, "ctrl"), true);
  await insert(term, "c");
  await sleep(300);
  check("SSH: ... for the one letter that follows", await lit(term, "ctrl"), false);
  await insert(term, "echo wieder > /tmp/kb-c.txt");
  await press(term, "enter");
  check("SSH: and that letter was Ctrl+C", await arrived(onSsh, "/tmp/kb-c.txt"), "wieder");
  await insert(term, "echo eins >> /tmp/kb-d.txt");
  await press(term, "enter");
  await sleep(500);
  await press(term, "up");
  await press(term, "enter");
  await sleep(800);
  check("SSH: the arrow brings the last command back", onSsh("cat /tmp/kb-d.txt"), "eins\neins");
  // What a key sends is read back from a program that shows it: cat -v. Ctrl+C ends it again.
  const sends = async (file, keys, before) => {
    await insert(term, (before || "") + "cat -v > " + file);
    await press(term, "enter");
    await sleep(600);
    for (const id of keys) { await press(term, id); }
    await press(term, "enter");
    await sleep(400);
    await press(term, "ctrl-c");
    await sleep(300);
    return onSsh("cat " + file);
  };
  check("SSH: an arrow sends the cursor sequence", await sends("/tmp/kb-e.txt", ["up"]), "^[[A");
  check("SSH: Alt that waits goes into the sequence of the key that follows", await sends("/tmp/kb-f.txt", ["alt", "left"]), "^[[1;3D");
  check("SSH: Shift+Tab is the back-tab", await sends("/tmp/kb-g.txt", ["shift-tab"]), "^[[Z");
  // A program that switched the cursor keys to application mode (an editor, a pager) gets them that way.
  check("SSH: in application mode the arrow sends the application sequence", await sends("/tmp/kb-h.txt", ["up"], "printf '\\033[?1h'; "), "^[OA");
  await insert(term, "printf '\\033[?1l'");
  await press(term, "enter");
  await sleep(400);
  // Ctrl+V pastes what is on this device's clipboard - a terminal has no clipboard of its own to paste from.
  await term.evaluate(() => navigator.clipboard.writeText("echo eingefuegt > /tmp/kb-i.txt"));
  await press(term, "ctrl-v");
  await sleep(500);
  await press(term, "enter");
  check("SSH: Ctrl+V pastes this device's clipboard", await arrived(onSsh, "/tmp/kb-i.txt"), "eingefuegt");
  await term.close();

  // ------------------------------------------------------------------------------------------ Guacamole
  // The seeded SSH connection is a Guacamole session: there the bar is the top row of the on-screen keyboard.
  const seeded = (await servers()).find(s => s.protocol === "SSH" && s.engine === "guacamole");
  if (!seeded) {
    check("a Guacamole connection is seeded (node seed.js)", null, "a connection");
  }
  else {
    const shell = await phone.newPage();
    shell.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    const frames = [];
    shell.on("websocket", ws => ws.on("framesent", f => frames.push(typeof f.payload === "string" ? f.payload : "")));
    const keysSince = mark => {
      const out = [];
      const re = /[0-9]+[.]key,[0-9]+[.]([0-9]+),[0-9]+[.]([01]);/g;
      let m;
      const text = frames.slice(mark).join("");
      while ((m = re.exec(text))) { out.push(m[1] + (m[2] === "1" ? "+" : "-")); }
      return out;
    };
    await shell.goto(BASE + "/connect/" + seeded.id, { waitUntil: "networkidle" });
    const oskButton = () => shell.evaluate(() => {
      const label = x => x.title || x.getAttribute("aria-label") || "";
      const find = () => [...document.querySelectorAll("#connection-tab-actions button, .tab-action-overflow-panel button")].find(x => /on-screen keyboard|Bildschirmtastatur/i.test(label(x)));
      let found = find();
      if (!found) {
        const more = document.querySelector(".tab-action-more-trigger");
        if (more) { more.click(); }
        found = find();
      }
      if (!found) { return false; }
      found.click();
      return true;
    });
    let opened = false;
    for (let waited = 0; waited < 30000 && !opened; waited += 500) { opened = await oskButton(); if (!opened) { await sleep(500); } }
    await sleep(600);
    check("Guacamole: the on-screen keyboard opens", await shell.evaluate(() => !!document.querySelector(".matgate-osk.open")), true);
    check("Guacamole: its top row is the key bar, with the buttons for terminals", await shell.evaluate(() =>
      [...document.querySelectorAll(".matgate-osk-row.osk-own-row .matgate-osk-key")].map(key => key.dataset.id)), IN_TERMINAL.concat([lower.id, upper.id]));
    const tap = selector => shell.evaluate(s => {
      const key = typeof s === "string" && s.startsWith("id:")
        ? document.querySelector(".matgate-osk-row.osk-own-row [data-id='" + s.slice(3) + "']")
        : [...document.querySelectorAll(".matgate-osk-key")].find(k => k.textContent === s);
      key.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, cancelable: true }));
    }, selector);
    let mark = frames.length;
    await tap("id:ctrl-c");
    await sleep(400);
    check("Guacamole: Ctrl+C is Ctrl held around the letter", keysSince(mark), ["65507+", "99+", "99-", "65507-"]);
    mark = frames.length;
    await tap("id:shift-tab");
    await sleep(400);
    check("Guacamole: Shift+Tab is Shift held around Tab", keysSince(mark), ["65505+", "65289+", "65289-", "65505-"]);
    mark = frames.length;
    await tap("id:ctrl");
    await sleep(300);
    check("Guacamole: Ctrl that waits lights up, here and on the keyboard's own Ctrl key", await shell.evaluate(() => [
      document.querySelector(".osk-own-row [data-id='ctrl']").classList.contains("active"),
      [...document.querySelectorAll(".matgate-osk-key")].filter(k => /^(Strg|Ctrl)$/.test(k.textContent) && !k.dataset.id)[0].classList.contains("active")]), [true, true]);
    await tap("a");
    await sleep(400);
    check("Guacamole: and holds for the one key of the keyboard that follows", [keysSince(mark), await shell.evaluate(() => document.querySelector(".osk-own-row [data-id='ctrl']").classList.contains("active"))],
      [["65507+", "97+", "97-", "65507-"], false]);
    mark = frames.length;
    await tap("id:" + upper.id);
    await sleep(400);
    check("Guacamole: an own character button with Shift", keysSince(mark), ["65505+", "88+", "88-", "65505-"]);
    await shell.close();
  }

  // ============================================================================================ cleaning up
  await list();
  await send("form[action='/account/keys/reset']");
  check("the account has the default bar again", (await list()).map(r => r.id), DEFAULT);
  await page.evaluate(async () => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && /^Test-Keys-(VNC|RDP|SSH)$/.test(cell)) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  });
  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
