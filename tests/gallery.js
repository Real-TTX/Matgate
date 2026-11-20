// A tour with pictures. Every other script here checks one thing and says ok or FAIL; none of them shows how
// the application LOOKS. This one walks through it - the sign-in page, the start page, the dialogs, real
// sessions (RDP, VNC, SSH) with their toolbar and dialogs, the browser, the settings - on a desktop and on a
// phone, and writes a picture of every stop into tests/gallery/, with an index.html that shows them side by
// side. Nothing is compared: the pictures are for looking at, before and after a change to the interface.
//
//   node gallery.js            every stop
//   node gallery.js phone      only the stops whose name contains "phone"
//
// It only fails on what makes the pictures worthless: a script error in the page, or a stop that could not
// be reached.
const { chromium } = require("playwright-core");
const fs = require("fs");
const path = require("path");
// Matgate is opened as mg.localhost, as in website.js: a frame keeps a website's cookies only within one site.
const BASE = "http://mg.localhost:18091";
const OUT = path.join(__dirname, "gallery");
const only = process.argv[2] || "";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const shots = [];

(async () => {
  fs.mkdirSync(OUT, { recursive: true });
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true, args: ["--autoplay-policy=no-user-gesture-required"] }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }

  const shot = async (page, name, title) => {
    if (only && !name.includes(only)) { return; }
    await sleep(350);
    await page.screenshot({ path: path.join(OUT, name + ".png") });
    shots.push({ name, title });
    console.log("ok   " + name + "  " + title);
  };
  // A stop that cannot be reached is said, and the tour goes on: the other pictures are still worth having.
  const stop = async (name, run) => {
    if (only && !name.includes(only.split("-")[0])) { return; }
    try { await run(); }
    catch (e) { console.log("FAIL " + name + "  " + String(e.message).split("\n")[0]); failures.push(name); }
  };
  const signIn = async page => {
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name=\"username\"]", "admin");
    await page.fill("input[name=\"password\"]", "test-only-pw");
    await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
    await sleep(1000);
  };
  const servers = page => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  // The connections display.js makes for itself, under the same names: whichever runs first creates them.
  const connection = async (page, name, protocol, fields) => {
    const existing = (await servers(page)).find(s => s.name === name);
    if (existing) { return existing; }
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
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
    return (await servers(page)).find(s => s.name === name);
  };
  const screenFrame = page => page.frames().find(f => /\/display\/.+\/screen/.test(f.url()));
  const waitPicture = async page => {
    for (let i = 0; i < 100; i++) {
      const frame = screenFrame(page);
      if (frame && (await frame.evaluate(() => window.__screen ? window.__screen.frames : 0).catch(() => 0)) >= 2) { return frame; }
      await sleep(300);
    }
    throw new Error("no picture from the remote desktop");
  };
  // On a phone most of a session's actions sit behind "...": the panel is filled when it is opened.
  const action = async (page, key) => {
    const press = () => page.evaluate(k => {
      const button = document.querySelector("[data-action='" + k + "']");
      if (button) { button.click(); }
      return !!button;
    }, key);
    if (await press()) { return; }
    await page.evaluate(() => { const more = document.querySelector(".tab-action-more-trigger"); if (more) { more.click(); } });
    await sleep(300);
    if (!(await press())) { throw new Error("no action " + key); }
  };
  const closeTabs = async page => {
    for (let i = 0; i < 8; i++) {
      const more = await page.evaluate(() => {
        const button = document.querySelector("#connection-tab-actions [data-action='disconnect']");
        if (button) { button.click(); return true; }
        return false;
      });
      if (!more) { break; }
      await sleep(900);
    }
  };

  // The whole tour, once for each kind of device.
  const tour = async (kind, contextOptions) => {
    const context = await b.newContext({ ...contextOptions, permissions: ["clipboard-read", "clipboard-write"] });
    const page = await context.newPage();
    page.on("pageerror", e => { console.log("JS ERROR (" + kind + "): " + e.message); failures.push("JS error"); });
    const phone = kind === "phone";
    const p = name => kind + "-" + name;

    await stop(p("login"), async () => {
      await page.goto(BASE + "/login", { waitUntil: "networkidle" });
      await shot(page, p("login"), "Sign-in");
      await page.fill("input[name=\"username\"]", "admin");
      await page.fill("input[name=\"password\"]", "wrong-on-purpose");
      await page.click(".login-submit");
      await page.waitForLoadState("networkidle");
      await shot(page, p("login-refused"), "Sign-in, refused");
    });
    await signIn(page);
    const rdp = await connection(page, "Test-RDP", "Rdp", { host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "demo-only-pw", keyboardLayout: "en-us-qwerty", engine: "" });
    const vnc = await connection(page, "Test-VNC", "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "" });
    const ssh = await connection(page, "Test-SSH-eigen", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "" });

    await stop(p("home"), async () => {
      await page.goto(BASE + "/", { waitUntil: "networkidle" });
      await sleep(600);
      await shot(page, p("home"), "Start page");
      if (await page.locator(".shell-burger-trigger").isVisible()) {
        await page.click(".shell-burger-trigger");
        await sleep(400);
        await shot(page, p("home-menu"), "The menu");
        await page.evaluate(() => { const close = document.querySelector("[data-burger-close]"); if (close) { close.click(); } const open = document.querySelector(".shell-burger[open]"); if (open) { open.removeAttribute("open"); } });
        await sleep(300);
      }
      await page.evaluate(() => document.querySelector("[data-home2-more]").click());
      await sleep(500);
      await shot(page, p("home-quick-connect"), "Quick connect");
    });

    // The server manager is for a wide screen: the connections beside the sessions.
    if (!phone) {
      await stop(p("manager"), async () => {
        await page.goto(BASE + "/", { waitUntil: "networkidle" });
        await sleep(500);
        await page.click("#server-manager-toggle");
        await sleep(400);
        await shot(page, p("manager"), "The server manager, beside the start page");
        await page.click("#server-manager-tree [data-manager-server='" + rdp.id + "']");
        await waitPicture(page);
        await sleep(1500);
        await shot(page, p("manager-session"), "A session beside the server manager");
        await page.fill("#server-manager-filter", "ssh");
        await sleep(300);
        await shot(page, p("manager-search"), "Searching in the server manager");
        await page.fill("#server-manager-filter", "");
        await page.click("#server-manager-toggle");
        await closeTabs(page);
      });
    }

    await stop(p("connection"), async () => {
      await page.goto(BASE + "/admin/servers/" + rdp.id, { waitUntil: "networkidle" });
      await sleep(500);
      await shot(page, p("connection-edit"), "Editing a connection");
      await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
      await shot(page, p("connection-edit-end"), "Editing a connection, the end of the page");
      // The question every deletion asks.
      await page.evaluate(() => { const del = [...document.querySelectorAll("button.danger, .button.danger")].pop(); if (del) { del.click(); } });
      await sleep(500);
      await shot(page, p("confirm"), "A question before deleting");
      await page.evaluate(() => { const no = document.querySelector("#confirm-dialog:not(.hidden) [data-confirm-cancel]"); if (no) { no.click(); } });
      await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
      await sleep(500);
      await shot(page, p("connection-new"), "A new connection");
    });

    await stop(p("settings"), async () => {
      await page.goto(BASE + "/account", { waitUntil: "networkidle" });
      await sleep(500);
      await shot(page, p("settings"), "Settings");
      // The colour sheet, asked for from the settings.
      await page.locator("[data-colour-open]").first().click();
      await sleep(500);
      await shot(page, p("colour"), "Choosing a colour");
      await page.click("[data-colour-cancel]");
      for (const tab of ["security", "session", "keys", "home", "favorites"]) {
        await page.goto(BASE + "/account?tab=" + tab, { waitUntil: "networkidle" });
        await sleep(400);
        await shot(page, p("settings-" + tab), "Settings: " + tab);
      }
      await page.goto(BASE + "/admin", { waitUntil: "networkidle" });
      await sleep(400);
      await shot(page, p("admin"), "Administration");
      await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
      await sleep(400);
      await shot(page, p("admin-users"), "Administration: users");
      await page.goto(BASE + "/tools", { waitUntil: "networkidle" });
      await sleep(400);
      await shot(page, p("tools"), "Tools");
    });

    await stop(p("rdp"), async () => {
      await page.goto(BASE + "/?open=" + rdp.id, { waitUntil: "domcontentloaded" });
      const frame = await waitPicture(page);
      await sleep(1500);
      await shot(page, p("rdp"), "An RDP session");
      if (phone) {
        await page.evaluate(() => { const more = document.querySelector(".tab-action-more-trigger"); if (more) { more.click(); } });
        await sleep(400);
        await shot(page, p("rdp-more"), "An RDP session, the rest of its actions");
        await page.mouse.click(10, 400);
        // The list of open connections only exists in the compact view: switch into it, take the
        // picture, switch back.
        const compact = () => page.evaluate(() => { const toggle = document.getElementById("view-mode-toggle") || document.querySelector("[data-view-mode-toggle]"); if (toggle) { toggle.click(); } });
        await compact();
        await sleep(500);
        if (await page.locator(".mobile-tab-menu-trigger").isVisible()) {
          await page.click(".mobile-tab-menu-trigger");
          await sleep(400);
          await shot(page, p("rdp-connections"), "The open connections, in the compact view");
          await page.evaluate(() => { const close = document.querySelector(".mobile-tab-sheet-close"); if (close) { close.click(); } });
        }
        await compact();
        await sleep(500);
      }
      await action(page, "resolution");
      await sleep(500);
      await shot(page, p("rdp-resolution"), "The resolution dialog");
      await page.evaluate(() => { const open = document.querySelector("[data-fixed-sizes]"); if (open) { open.click(); } });
      await sleep(300);
      await shot(page, p("rdp-resolution-fixed"), "The resolution dialog, with the fixed sizes");
      await page.evaluate(() => document.getElementById("resolution-close").click());
      await sleep(300);
      // The sound button: one press has to be enough, also while the picture holds the cursor.
      await frame.evaluate(() => document.getElementById("sink") && document.getElementById("sink").focus());
      await closeTabs(page);
    });

    await stop(p("vnc"), async () => {
      await page.goto(BASE + "/?open=" + vnc.id, { waitUntil: "domcontentloaded" });
      await waitPicture(page);
      await sleep(1500);
      await shot(page, p("vnc"), "A VNC session");
      await closeTabs(page);
    });

    await stop(p("ssh"), async () => {
      await page.goto(BASE + "/?open=" + ssh.id, { waitUntil: "domcontentloaded" });
      await sleep(4000);
      await shot(page, p("ssh"), "An SSH session");
      await closeTabs(page);
    });

    // A session through Guacamole: the sheets for text and for files belong to it.
    await stop(p("guacamole"), async () => {
      const host = (await servers(page)).find(s => s.name === "Linux-Testhost");
      await page.goto(BASE + "/?open=" + host.id, { waitUntil: "domcontentloaded" });
      await sleep(5000);
      await shot(page, p("guacamole"), "An SSH session through Guacamole");
      await action(page, "clipboard");
      await sleep(600);
      await shot(page, p("guacamole-clipboard"), "Sending text into a session");
      await page.evaluate(() => document.getElementById("clipboard-close").click());
      await sleep(300);
      await action(page, "upload");
      await sleep(600);
      await shot(page, p("guacamole-send-files"), "Sending files into a session");
      await page.evaluate(() => document.getElementById("sftp-target-close").click());
      await sleep(300);
      await action(page, "resolution").catch(() => { });
      await sleep(500);
      await shot(page, p("guacamole-display"), "The display dialog of a Guacamole session");
      await page.evaluate(() => document.getElementById("resolution-close").click());
      await closeTabs(page);
    });

    await stop(p("browser"), async () => {
      await page.goto(BASE + "/", { waitUntil: "networkidle" });
      await sleep(500);
      await page.click(".home2-quick-row [data-home2-qc='website']");
      await page.waitForSelector(".website-frame", { state: "attached", timeout: 20000 });
      await sleep(600);
      await shot(page, p("browser-blank"), "The browser, empty");
      await page.fill(".website-address", "site:8080");
      await page.press(".website-address", "Enter");
      await sleep(3000);
      await shot(page, p("browser"), "The browser, on a page");
    });

    await context.close();
  };

  if (!only || !only.startsWith("phone")) {
    await tour("desktop", { viewport: { width: 1300, height: 860 } });
  }
  if (!only || !only.startsWith("desktop")) {
    await tour("phone", { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  }
  await b.close();

  // The index: desktop and phone of the same stop next to each other.
  const all = fs.readdirSync(OUT).filter(f => f.endsWith(".png")).map(f => f.slice(0, -4));
  const titles = Object.fromEntries(shots.map(s => [s.name, s.title]));
  const stops = [...new Set(all.map(n => n.replace(/^(desktop|phone)-/, "")))];
  const cell = name => all.includes(name) ? "<figure><a href=\"" + name + ".png\"><img loading=\"lazy\" src=\"" + name + ".png\" alt=\"\"></a><figcaption>" + name + "</figcaption></figure>" : "<figure class=\"none\"></figure>";
  const html = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Matgate - gallery</title><style>"
    + "body{background:#15171c;color:#e8eaee;font:14px system-ui,sans-serif;margin:24px}h1{font-size:20px}h2{font-size:15px;margin:28px 0 8px}"
    + ".row{align-items:start;display:grid;gap:16px;grid-template-columns:minmax(0,3fr) minmax(0,1fr)}figure{margin:0}"
    + "img{border:1px solid #333a45;border-radius:10px;display:block;max-width:100%}figcaption{color:#98a1b0;font-size:12px;margin-top:4px}</style>"
    + "<h1>Matgate - gallery (" + new Date().toISOString().slice(0, 16).replace("T", " ") + ")</h1>"
    + stops.map(s => "<h2>" + (titles["desktop-" + s] || titles["phone-" + s] || s) + "</h2><div class=\"row\">" + cell("desktop-" + s) + cell("phone-" + s) + "</div>").join("")
    + "</html>";
  fs.writeFileSync(path.join(OUT, "index.html"), html);
  console.log(failures.length ? "\n" + failures.length + " stop(s) failed: " + failures.join(", ") : "\n" + shots.length + " pictures in " + OUT);
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
