// Ctrl+V in a session with files on the clipboard. The official RDP client pastes them into the folder
// that is open in the remote Explorer; Guacamole cannot (its clipboard channel is text only), so the
// files go into the session's drive - or, in an SSH session, to a path on the server - and a note says
// where they are. The browser only fires the paste event, which alone carries the files, for a key
// press that is not cancelled, and Guacamole.Keyboard cancels every key press it forwards: so the key
// press of a session is held back for the moment the event takes. The rest of this test is about
// everything that must NOT change - text, a picture of cells, no event at all, a quick tap.
//
// Files cannot be put on the operating system's clipboard from a test, so the key press and the paste
// event are dispatched as events; that the browser really fires the event for a plain focused element
// was measured separately (and with text on the real clipboard below).
const { chromium } = require("playwright-core");
const { execFileSync } = require("child_process");
const BASE = "http://127.0.0.1:18091";
const SSH = "matgate-tests-ssh-1";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const V = 118;      // the keysym of "v"
const CTRL = 65507;
const remoteLs = () => {
  try { return execFileSync("docker", ["exec", SSH, "sh", "-c", "ls /config"], { encoding: "utf8" }).split("\n"); }
  catch { return []; }
};
const remoteClean = () => {
  try { execFileSync("docker", ["exec", SSH, "sh", "-c", "rm -f /config/paste-test*.txt /config/Notiz.txt /config/Bild-*.png /config/Image-*.png"]); } catch { /* nothing to remove */ }
};

(async () => {
  remoteClean();
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1400, height: 950 }, permissions: ["clipboard-read", "clipboard-write"] });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const frames = [];
  const stamps = [];
  page.on("websocket", ws => ws.on("framesent", f => { frames.push(typeof f.payload === "string" ? f.payload : ""); stamps.push(Date.now()); }));
  const keysSince = mark => {
    const out = [];
    const re = /[0-9]+[.]key,[0-9]+[.]([0-9]+),[0-9]+[.]([01]);/g;
    let m;
    const text = frames.slice(mark).join("");
    while ((m = re.exec(text))) { out.push({ keysym: Number(m[1]), down: m[2] === "1" }); }
    return out;
  };
  const vEvents = mark => keysSince(mark).filter(e => e.keysym === V).map(e => (e.down ? "down" : "up"));

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(800);
  await page.evaluate(() => {
    document.querySelectorAll("#home-order-list [data-order-visible]").forEach(k => { k.checked = true; k.dispatchEvent(new Event("change", { bubbles: true })); });
    document.querySelector("[name='homeSections']").form.requestSubmit();
  });
  await sleep(1800);
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1200);
  const connect = page.locator("a,button").filter({ hasText: /^ *(Connect|Verbinden) *$/ }).first();
  await connect.click();
  await sleep(6000);
  const surface = page.locator(".connection-panel:not(.hidden)").first();
  await surface.click({ position: { x: 300, y: 200 } });
  await sleep(300);

  // What the clipboard looks like, as the paste event sees it.
  const press = () => page.evaluate(() => document.querySelector(".connection-panel:not(.hidden)").dispatchEvent(
    new KeyboardEvent("keydown", { key: "v", code: "KeyV", keyCode: 86, which: 86, ctrlKey: true, bubbles: true, cancelable: true })));
  const release = () => page.evaluate(() => document.querySelector(".connection-panel:not(.hidden)").dispatchEvent(
    new KeyboardEvent("keyup", { key: "v", code: "KeyV", keyCode: 86, which: 86, ctrlKey: true, bubbles: true, cancelable: true })));
  const paste = kind => page.evaluate(k => {
    const dt = new DataTransfer();
    const file = (name, type, body) => new File([body || "content of " + name], name, { type });
    if (k === "files") { dt.items.add(file("paste-test.txt", "text/plain")); dt.items.add(file("paste-test-2.txt", "text/plain")); }
    if (k === "files-with-names") { dt.items.add(file("Notiz.txt", "text/plain")); dt.setData("text/plain", "Notiz.txt"); }
    if (k === "text") { dt.setData("text/plain", "just some text"); }
    if (k === "cells") { dt.setData("text/plain", "a\tb"); dt.setData("text/html", "<table><tr><td>a</td><td>b</td></tr></table>"); dt.items.add(file("image.png", "image/png", "png")); }
    if (k === "picture") { dt.items.add(file("image.png", "image/png", "png")); }
    const event = new ClipboardEvent("paste", { clipboardData: dt, bubbles: true, cancelable: true });
    (document.activeElement || document.body).dispatchEvent(event);
    return event.defaultPrevented;
  }, kind);

  const dialogOpen = () => page.evaluate(() => !document.getElementById("sftp-target-dialog").classList.contains("hidden"));
  await page.keyboard.down("Control");
  await sleep(200);

  // --- Nothing to paste but a key press: it is held for the paste event and goes on without one.
  let mark = frames.length;
  await press();
  await sleep(50);
  check("Ctrl+V is held back for the moment the paste event takes", vEvents(mark), []);
  await sleep(450);
  check("and goes on to the remote when no paste event comes", vEvents(mark), ["down"]);
  await release();
  await sleep(150);
  check("followed by its release", vEvents(mark), ["down", "up"]);

  // --- Text on the clipboard: the key press goes on at once, as it always did.
  mark = frames.length;
  await press();
  await paste("text");
  await sleep(80);
  check("with text on the clipboard the key press is passed on straight away", vEvents(mark), ["down"]);
  await release();
  await sleep(100);
  check("no question is asked about it", await dialogOpen(), false);

  // --- Excel puts a picture of the cells next to their text: that is a text paste, and stays one.
  mark = frames.length;
  await press();
  const cellsPrevented = await paste("cells");
  await sleep(80);
  check("a picture of cells beside their text is not taken for files", [vEvents(mark), cellsPrevented, await dialogOpen()], [["down"], false, false]);
  await release();
  await sleep(100);

  // --- A quick tap on the key: released before the paste event came.
  mark = frames.length;
  await press();
  await release();
  await sleep(100);
  check("a key tapped faster than that still goes out, press and release in order", vEvents(mark), ["down", "up"]);

  // --- Files on the clipboard: the key press is dropped and they go to the server.
  mark = frames.length;
  await press();
  const prevented = await paste("files");
  await sleep(500);
  check("files on the clipboard: the paste is taken over", prevented, true);
  check("and no V reaches the remote - it would paste whatever the remote has", vEvents(mark).includes("down"), false);
  const asked = await page.evaluate(() => ({
    open: !document.getElementById("sftp-target-dialog").classList.contains("hidden"),
    names: document.getElementById("sftp-target-files").textContent,
  }));
  console.log("     the question: " + JSON.stringify(asked));
  check("an SSH session asks where on the server, once", asked.open, true);
  check("and says which files", asked.names, "paste-test.txt, paste-test-2.txt");
  await release();
  await page.keyboard.up("Control");
  await sleep(200);
  check("holding the key on does not make the remote paste", vEvents(mark).includes("down"), false);

  // --- Answering: the files arrive, and the note says where.
  await page.fill("#sftp-target-path", "/config");
  await page.locator("#sftp-target-dialog button[type='submit']").click();
  let arrived = false;
  for (let i = 0; i < 40 && !arrived; i++) { await sleep(250); const list = remoteLs(); arrived = list.includes("paste-test.txt") && list.includes("paste-test-2.txt"); }
  check("both files arrived on the server", arrived, true);
  await sleep(600);
  const note = await page.evaluate(() => {
    const n = document.querySelector(".connection-panel:not(.hidden) .session-note");
    return n ? { title: (n.querySelector("strong") || {}).textContent, detail: (n.querySelector(".session-note-detail") || {}).textContent } : null;
  });
  console.log("     the note: " + JSON.stringify(note));
  await page.screenshot({ path: "paste-note.png" });
  check("the note says what was pasted", note && /2 files pasted/.test(note.title || ""), true);
  check("and where it is", note && note.detail, "On the server: /config");

  // --- The second time nobody asks again.
  await page.keyboard.down("Control");
  await sleep(150);
  await press();
  await paste("files-with-names");
  await sleep(500);
  check("the answer is remembered for this tab: no second question", await dialogOpen(), false);
  let named = false;
  for (let i = 0; i < 40 && !named; i++) { await sleep(250); named = remoteLs().includes("Notiz.txt"); }
  check("files copied in a file manager come with their names as text and are still files", named, true);
  await release();

  // --- A screenshot alone is a file too, and gets a name of its own.
  await press();
  await paste("picture");
  let picture = false;
  for (let i = 0; i < 40 && !picture; i++) { await sleep(250); picture = remoteLs().some(name => /^(Bild|Image)-.*[.]png$/.test(name)); }
  check("a picture on the clipboard arrives as a file with a name of its own", picture, true);
  await release();
  await page.keyboard.up("Control");
  await sleep(300);

  // --- The real thing, with text: the browser really fires the paste event for the focused panel.
  await page.evaluate(() => navigator.clipboard.writeText("text from the real clipboard"));
  await surface.click({ position: { x: 300, y: 200 } });
  mark = frames.length;
  await page.keyboard.press("Control+v");
  await sleep(500);
  console.log("     real Ctrl+V: " + JSON.stringify(keysSince(mark).map(e => e.keysym + (e.down ? "+" : "-"))));
  check("a real Ctrl+V with text still reaches the remote: press and release of V", vEvents(mark), ["down", "up"]);
  check("and with Ctrl around it", keysSince(mark).some(e => e.keysym === CTRL && e.down) && keysSince(mark).some(e => e.keysym === CTRL && !e.down), true);
  check("and without a question", await dialogOpen(), false);

  remoteClean();
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); remoteClean(); process.exit(1); });
