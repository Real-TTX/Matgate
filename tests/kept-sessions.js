// Sessions that outlive the browser (Account -> Session). With the option on, a session of Matgate's own
// engine keeps running when its browser goes away, and the page that comes back - reloaded, or on another
// device - gets that very session instead of a new connection; the open tabs are kept on the server, so the
// other device has them at all. A tab that is closed ends its session at once. With the option off (the
// default) everything is as before: a session ends with its browser, the tabs are the browser's own.
//
// What proves "the same session": for SSH a variable set in the shell before the reload is still there
// after it; for VNC the server's own log has no second connection.
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
const on = (container, command) => {
  try { return execFileSync("docker", ["exec", container, "sh", "-c", command], { encoding: "utf8" }).trim(); }
  catch (e) { return "ERROR " + String(e.stderr || e.message).trim().split("\n").pop(); }
};
const onSsh = command => on("matgate-tests-ssh-1", command);
const vncConnections = () => Number(on("matgate-tests-desktop-1", "grep -c 'Connections: Accepted' /tmp/xvnc.log || true"));
const arrived = async file => {
  for (let i = 0; i < 30; i++) {
    const got = onSsh("cat " + file + " 2>/dev/null");
    if (got && !/^ERROR/.test(got)) { return got; }
    await sleep(400);
  }
  return onSsh("cat " + file + " 2>/dev/null");
};

(async () => {
  let b = await chromium.launch({ headless: true });
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
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
  // The option, set the way a person sets it: the form of the account, with everything else as it is.
  const keepFor = async minutes => {
    await page.goto(BASE + "/account?tab=session", { waitUntil: "networkidle" });
    await page.selectOption("[name='keepSessionMinutes']", String(minutes));
    await Promise.all([
      page.waitForResponse(r => r.request().method() === "POST", { timeout: 20000 }),
      page.evaluate(() => document.querySelector("[name='keepSessionMinutes']").form.requestSubmit()).catch(() => { /* the submit navigated before the call returned */ }),
    ]);
    await page.waitForLoadState("networkidle");
    await page.goto(BASE + "/account?tab=session", { waitUntil: "networkidle" });
    return page.inputValue("[name='keepSessionMinutes']");
  };
  const csrf = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const csrfToken = (".*?");/)[1]);
  });

  const ssh = await connection("Test-Kept-SSH", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "" });
  const vnc = await connection("Test-Kept-VNC", "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "" });
  onSsh("rm -f /tmp/ks-*.txt");

  const openTerminal = async address => {
    const term = await context.newPage();
    term.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await term.goto(address || BASE + "/ssh/" + ssh.id + "/terminal", { waitUntil: "domcontentloaded" });
    await promptOf(term);
    return term;
  };
  const rowsOf = term => term.evaluate(() => (document.querySelector(".xterm-rows") || {}).textContent || "");
  const promptOf = async term => { for (let i = 0; i < 80 && !/\$/.test(await rowsOf(term)); i++) { await sleep(300); } };
  const type = async (term, line) => {
    await term.evaluate(() => window.MatgateTerminal.focus());
    await term.keyboard.type(line, { delay: 10 });
    await term.keyboard.press("Enter");
    await sleep(500);
  };
  const statusOf = term => term.evaluate(() => window.MatgateTerminal.state().status);

  // ============================================================================================ off (the default)
  check("the option starts off", await keepFor(0), "0");
  // Nothing of an earlier run that stopped half way: its tabs would come back in every shell page here.
  await page.evaluate(() => ["matgate.workspace.tabs.v2", "matgate.shell.tabs.v1", "matgate.tab.order.v1"].forEach(key => localStorage.removeItem(key)));
  let term = await openTerminal();
  await type(term, "X=kept42");
  await term.reload({ waitUntil: "domcontentloaded" });
  await promptOf(term);
  await type(term, "echo \"[$X]\" > /tmp/ks-a.txt");
  check("off: a reload is a new shell - what the old one knew is gone", await arrived("/tmp/ks-a.txt"), "[]");
  await term.close();

  // ============================================================================================ on: SSH
  check("the option is set in the account", await keepFor(5), "5");
  term = await openTerminal();
  const address = term.url();
  check("the session's id is part of the address", /[?&]session=[A-Za-z0-9-]{8,}/.test(address), true);
  await type(term, "X=kept42");
  await type(term, "echo first-line-of-the-shell");
  await term.reload({ waitUntil: "domcontentloaded" });
  await promptOf(term);
  await sleep(600);
  check("on: after a reload the page says it came back to the running session", /Reconnected|Wieder verbunden/.test(await statusOf(term)), true);
  check("and shows what the shell printed before", /first-line-of-the-shell/.test(await rowsOf(term)), true);
  await type(term, "echo \"[$X]\" > /tmp/ks-b.txt");
  check("it is the same shell: it still knows what it was told", await arrived("/tmp/ks-b.txt"), "[kept42]");

  // The browser goes away altogether; a page that comes later still finds the shell.
  await term.close();
  await sleep(1500);
  term = await openTerminal(address);
  await sleep(600);
  await type(term, "echo \"[$X]\" > /tmp/ks-c.txt");
  check("a page that was closed and opened again finds the shell as well", await arrived("/tmp/ks-c.txt"), "[kept42]");

  // Two pages on one shell: the later one has it, the earlier one is told.
  const second = await openTerminal(address);
  await sleep(800);
  check("another window takes the shell, and the first one says so", /Another window|anderes Fenster/i.test(await statusOf(term)), true);
  await type(second, "echo \"[$X]\" > /tmp/ks-d.txt");
  check("the shell is the same one for the window that took it", await arrived("/tmp/ks-d.txt"), "[kept42]");
  await term.close();

  // Closing the tab is what ends a session: the shell is gone at once, not when its time runs out.
  const session = new URL(address).searchParams.get("session");
  const token = await csrf();
  const ended = await page.evaluate(async ([s, t]) => (await (await fetch("/api/kept-sessions/end?session=" + s, { method: "POST", headers: { "X-Matgate-Csrf": t }, credentials: "same-origin" })).json()).ended, [session, token]);
  check("ending the session (what closing its tab does) ends the shell", ended, true);
  await sleep(1200);
  check("the page that showed it says it ended", /Disconnected|beendet/i.test(await statusOf(second)), true);
  await second.reload({ waitUntil: "domcontentloaded" });
  await promptOf(second);
  await type(second, "echo \"[$X]\" > /tmp/ks-e.txt");
  check("and a page that comes now gets a new shell", await arrived("/tmp/ks-e.txt"), "[]");
  await page.evaluate(async ([s, t]) => fetch("/api/kept-sessions/end?session=" + s, { method: "POST", headers: { "X-Matgate-Csrf": t }, credentials: "same-origin" }), [new URL(second.url()).searchParams.get("session"), token]);
  await second.close();

  // ============================================================================================ on: a screen (VNC)
  const view = await context.newPage();
  view.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const started = async () => { for (let i = 0; i < 200 && (await view.evaluate(() => window.__screen && window.__screen.frames)) < 2; i++) { await sleep(300); } };
  await view.goto(BASE + "/display/" + vnc.id + "/screen", { waitUntil: "domcontentloaded" });
  await started();
  const screenAddress = view.url();
  check("a screen's session id is part of its address too", /[?&]session=[A-Za-z0-9-]{8,}/.test(screenAddress), true);
  check("the first time it is a new connection", await view.evaluate(() => window.__screen.attached), false);
  const before = vncConnections();
  await view.reload({ waitUntil: "domcontentloaded" });
  await started();
  check("after a reload the page is back in the same session, with a picture", [view.url() === screenAddress, await view.evaluate(() => window.__screen.attached), (await view.evaluate(() => window.__screen.frames)) >= 2], [true, true, true]);
  check("and the VNC server saw no second connection", vncConnections() - before, 0);
  // The network goes and comes back (the socket closes under the page): the page reconnects by itself.
  await view.evaluate(() => { window.__screen.frames = 0; });
  await context.setOffline(true);
  await sleep(2500);
  await context.setOffline(false);
  for (let i = 0; i < 60 && !((await view.evaluate(() => window.__screen.attached)) && (await view.evaluate(() => window.__screen.frames)) >= 1); i++) { await sleep(500); }
  check("a network that came back brings the same session back, no new connection", [await view.evaluate(() => window.__screen.attached), vncConnections() - before], [true, 0]);

  // ============================================================================================ the tabs, on the server
  // In the shell the session is a tab; another browser (nothing of its own stored) finds the tab - and the session.
  // Done with this one: ended rather than left to run out (which it would, in the middle of what follows).
  await page.evaluate(async ([s, t]) => fetch("/api/kept-sessions/end?session=" + s, { method: "POST", headers: { "X-Matgate-Csrf": t }, credentials: "same-origin" }), [new URL(screenAddress).searchParams.get("session"), token]);
  await view.close();
  await sleep(500);
  const shell = await context.newPage();
  shell.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await shell.goto(BASE + "/connect/" + vnc.id, { waitUntil: "networkidle" });
  const frameOf = async target => { for (let i = 0; i < 60; i++) { const f = target.frames().find(x => /\/display\/[0-9a-f-]+\/screen/.test(x.url())); if (f) { return f; } await sleep(250); } return null; };
  let frame = await frameOf(shell);
  for (let i = 0; i < 200 && (await frame.evaluate(() => window.__screen && window.__screen.frames)) < 2; i++) { await sleep(300); }
  const tabSession = new URL(frame.url()).searchParams.get("session");
  await sleep(1500);
  const connectionsBefore = vncConnections();
  const other = await b.newContext({ viewport: { width: 1300, height: 860 }, storageState: { cookies: (await context.storageState()).cookies, origins: [] } });
  const elsewhere = await other.newPage();
  elsewhere.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await elsewhere.goto(BASE + "/", { waitUntil: "networkidle" });
  const theirs = await frameOf(elsewhere);
  check("another browser, which never opened anything, has the tab", !!theirs, true);
  if (theirs) {
    for (let i = 0; i < 200 && (await theirs.evaluate(() => window.__screen && window.__screen.frames)) < 2; i++) { await sleep(300); }
    check("with the same session behind it - not a new connection", [new URL(theirs.url()).searchParams.get("session") === tabSession, await theirs.evaluate(() => window.__screen.attached), vncConnections() - connectionsBefore], [true, true, 0]);
    await sleep(800);
    check("the browser that had it says another one shows it now", /anderes Fenster|Another window/.test(await frame.evaluate(() => document.getElementById("notice-text").textContent)), true);
    // Closing the tab there ends the session and takes the tab off the server's list.
    await elsewhere.evaluate(() => document.querySelectorAll(".session-tab--page .session-tab-close").forEach(x => x.click()));
    await sleep(2000);
    await elsewhere.reload({ waitUntil: "networkidle" });
    await sleep(1000);
    check("closing the tab takes it off the list for good", elsewhere.frames().some(x => /\/display\/[0-9a-f-]+\/screen/.test(x.url())), false);
    const fresh = await context.newPage();
    await fresh.goto(BASE + "/display/" + vnc.id + "/screen?session=" + tabSession, { waitUntil: "domcontentloaded" });
    for (let i = 0; i < 200 && (await fresh.evaluate(() => window.__screen && window.__screen.frames)) < 2; i++) { await sleep(300); }
    check("and its session is over: the same address is a new connection now", await fresh.evaluate(() => window.__screen.attached), false);
    await page.evaluate(async ([s, t]) => fetch("/api/kept-sessions/end?session=" + s, { method: "POST", headers: { "X-Matgate-Csrf": t }, credentials: "same-origin" }), [new URL(fresh.url()).searchParams.get("session"), token]);
    await fresh.close();
  }
  await other.close();
  await shell.close();

  // ============================================================================================ the time runs out
  // Nobody comes back: after the time that was set (seconds in this stack, see docker-compose.yml) the shell
  // is gone and the connection to the desktop is closed.
  term = await openTerminal();
  const leftAlone = term.url();
  await type(term, "X=kept42");
  const lonely = await context.newPage();
  await lonely.goto(BASE + "/display/" + vnc.id + "/screen", { waitUntil: "domcontentloaded" });
  for (let i = 0; i < 200 && (await lonely.evaluate(() => window.__screen && window.__screen.frames)) < 2; i++) { await sleep(300); }
  const lonelyAddress = lonely.url();
  const closedBefore = Number(on("matgate-tests-desktop-1", "grep -c 'Connections: Closed' /tmp/xvnc.log || true"));
  await term.close();
  await lonely.close();
  await sleep(4000);
  check("without a browser the connection to the desktop stays open at first", Number(on("matgate-tests-desktop-1", "grep -c 'Connections: Closed' /tmp/xvnc.log || true")) - closedBefore, 0);
  await sleep(13000);
  check("when the time has run out it is closed", Number(on("matgate-tests-desktop-1", "grep -c 'Connections: Closed' /tmp/xvnc.log || true")) - closedBefore, 1);
  term = await openTerminal(leftAlone);
  await type(term, "echo \"[$X]\" > /tmp/ks-f.txt");
  check("and the shell is gone too: the same address is a new shell", await arrived("/tmp/ks-f.txt"), "[]");
  const back = await context.newPage();
  await back.goto(lonelyAddress, { waitUntil: "domcontentloaded" });
  for (let i = 0; i < 200 && (await back.evaluate(() => window.__screen && window.__screen.frames)) < 2; i++) { await sleep(300); }
  check("the screen's address is a new connection as well", await back.evaluate(() => window.__screen.attached), false);
  for (const [p, a] of [[term, term.url()], [back, back.url()]]) {
    await page.evaluate(async ([s, t]) => fetch("/api/kept-sessions/end?session=" + s, { method: "POST", headers: { "X-Matgate-Csrf": t }, credentials: "same-origin" }), [new URL(a).searchParams.get("session"), token]);
    await p.close();
  }

  // ============================================================================================ off again
  check("the option goes off again", await keepFor(0), "0");
  const plain = await b.newContext({ viewport: { width: 1300, height: 860 }, storageState: { cookies: (await context.storageState()).cookies, origins: [] } });
  const home = await plain.newPage();
  await home.goto(BASE + "/", { waitUntil: "networkidle" });
  check("off: the server has forgotten the tabs - another browser starts empty", await home.evaluate(() => document.querySelectorAll(".shell-page-panel iframe").length), 0);
  await plain.close();

  await page.evaluate(async () => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && /^Test-Kept-(SSH|VNC)$/.test(cell)) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  });
  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
