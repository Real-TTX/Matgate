// The pictures of the README (docs/images), made anew. Not a check: it starts a Matgate of its own (the test
// stack's image, an empty data directory, container "matgate-readme" on 18095) whose connections carry
// tidy names and lead to the test stack's machines - a remote desktop, an SSH server, the Linux agent, a
// database - so the pictures show real sessions without the leftovers of the tests. The container goes
// again at the end.
//
//   docker compose -f docker-compose.yml up -d --build    (the test stack, as for the tests)
//   node readme-pictures.js                               (every picture)
//   node readme-pictures.js database                      (only the pictures of one stop)
const { chromium } = require("playwright-core");
const { execFileSync } = require("child_process");
const path = require("path");
const BASE = "http://127.0.0.1:18095";
const OUT = path.join(__dirname, "..", "docs", "images");
const CONTAINER = "matgate-readme";
const only = process.argv[2] || "";
const sleep = ms => new Promise(r => setTimeout(r, ms));

const CONNECTIONS = [
  ["Workstation", "Rdp", { host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "demo-only-pw", keyboardLayout: "en-us-qwerty", engine: "", folderName: "Office" }],
  ["Kiosk display", "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "", folderName: "Office" }],
  ["Web server", "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "", folderName: "Servers" }],
  ["Build machine", "Agent", { host: "agent-linux", port: "7488", targetPassword: "test-only-linux-agent-token", folderName: "Servers" }],
  ["Backups", "Sftp", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", folderName: "Servers" }],
  ["Shop database", "Postgres", { host: "postgres", port: "5432", targetUserName: "demo", targetPassword: "demo-only-pw", databaseName: "shop", folderName: "Data" }],
  ["Router", "Website", { websiteUrl: "http://site:8080/", folderName: "Network" }],
];

const docker = args => execFileSync("docker", args, { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });
const startMatgate = async () => {
  try { docker(["rm", "-f", CONTAINER]); } catch { /* not there */ }
  docker(["run", "-d", "--name", CONTAINER, "--network", "matgate-tests_default", "-p", "127.0.0.1:18095:8080",
    "-e", "MATGATE_ADMIN_USER=anna", "-e", "MATGATE_ADMIN_PASSWORD=readme-only-pw", "-e", "MATGATE_ADMIN_EMAIL=anna@example.com",
    "-e", "MATGATE_GUACAMOLE=off",
    // Nowhere to look for agent updates: no "new version" banner on the agent's page.
    "-e", "MATGATE_AGENT_UPDATE_URL=http://127.0.0.1:9/",
    "matgate-tests-matgate"]);
  for (let i = 0; i < 60; i++) {
    try { if ((await fetch(BASE + "/login")).ok) { return; } } catch { /* starting */ }
    await sleep(500);
  }
  throw new Error("the Matgate for the pictures did not start");
};

(async () => {
  await startMatgate();
  const b = await chromium.launch({ headless: true });
  const shot = async (page, name) => { await sleep(400); await page.screenshot({ path: path.join(OUT, name + ".png") }); console.log("ok   " + name); };
  const signIn = async page => {
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name=\"username\"]", "anna");
    await page.fill("input[name=\"password\"]", "readme-only-pw");
    await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
    await sleep(1000);
  };
  const servers = page => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
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
        if (!el) { return; }
        if (el.type === "checkbox") { el.checked = !!value; } else { el.value = value; }
        el.dispatchEvent(new Event("input", { bubbles: true }));
        el.dispatchEvent(new Event("change", { bubbles: true }));
      };
      set("name", n);
      Object.entries(f).forEach(([field, value]) => set(field, value));
      document.querySelector("[name='name']").form.requestSubmit();
    }, [name, protocol, fields]).catch(() => { /* the submit navigated before the call returned */ });
    await sleep(1500);
    return (await servers(page)).find(s => s.name === name);
  };
  const waitPicture = async page => {
    for (let i = 0; i < 100; i++) {
      const frame = page.frames().find(f => /\/(display|agent)\/.+\/screen/.test(f.url()));
      if (frame && (await frame.evaluate(() => window.__screen ? window.__screen.frames : 0).catch(() => 0)) >= 2) { return frame; }
      await sleep(300);
    }
    throw new Error("no picture from the remote desktop");
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

  const run = async (kind, options) => {
    const context = await b.newContext({ ...options, colorScheme: "light", locale: "en-US" });
    const page = await context.newPage();
    const phone = kind === "phone";
    const step = async (name, fn) => {
      if (only && !name.includes(only)) { return; }
      try { await fn(); }
      catch (e) { console.log("FAIL " + name + "  " + String(e.message).split("\n")[0]); }
    };
    if (!phone) {
      await step("login", async () => { await page.goto(BASE + "/login", { waitUntil: "networkidle" }); await shot(page, "login"); });
    }
    await signIn(page);
    const c = {};
    for (const [name, protocol, fields] of CONNECTIONS) { c[name] = await connection(page, name, protocol, fields); }

    // The picture at the top of the README: a remote desktop, the connections beside it.
    await step("session", async () => {
      if (phone) { return; }
      await page.goto(BASE + "/?open=" + c["Workstation"].id, { waitUntil: "domcontentloaded" });
      const frame = await waitPicture(page);
      await sleep(1500);
      const area = await (await frame.$("#screen")).boundingBox();
      await page.mouse.click(area.x + area.width / 2, area.y + area.height / 3);
      await sleep(400);
      // The terminal on that desktop is the tests' as well: what they typed goes first.
      await page.keyboard.press("Control+C");
      await sleep(400);
      await page.keyboard.type("clear\n", { delay: 40 });
      await sleep(800);
      await page.keyboard.type("uname -sr; uptime -p; free -h; ls /\n", { delay: 30 });
      await sleep(2500);
      await shot(page, "session");
      await closeTabs(page);
    });
    await step("ssh", async () => {
      await page.goto(BASE + "/?open=" + c["Web server"].id, { waitUntil: "domcontentloaded" });
      await sleep(5000);
      if (phone) { await page.tap(".xterm-screen").catch(() => { }); }
      await page.keyboard.type(phone ? "clear; uname -sr; uptime; df -h /\n" : "clear; uname -a; uptime; df -h / ; ls -la ~\n", { delay: 20 });
      await sleep(2000);
      await shot(page, phone ? "mobile-session" : "ssh");
      await closeTabs(page);
    });
    await step("files", async () => {
      if (phone) { return; }
      await page.goto(BASE + "/?open=" + c["Backups"].id, { waitUntil: "domcontentloaded" });
      await sleep(5000);
      await shot(page, "files");
      await closeTabs(page);
    });
    await step("agent", async () => {
      if (phone) { return; }
      await page.goto(BASE + "/?open=" + c["Build machine"].id, { waitUntil: "networkidle" });
      await page.waitForSelector(".agent-hero h2", { timeout: 20000 });
      for (let i = 0; i < 40 && await page.evaluate(() => !!document.querySelector(".agent-hero.is-loading")); i++) { await sleep(250); }
      for (let i = 0; i < 40 && await page.evaluate(() => document.getElementById("status-system")?.hidden !== false); i++) { await sleep(250); }
      await sleep(4000);
      await shot(page, "agent-overview-desktop");
      await page.click("#status-system");
      await sleep(800);
      await shot(page, "agent-system");
      await page.keyboard.press("Escape");
      await closeTabs(page);
    });
    await step("home", async () => {
      await page.goto(BASE + "/", { waitUntil: "networkidle" });
      await sleep(800);
      if (phone) {
        await shot(page, "mobile-home");
        await page.click(".shell-burger-trigger");
        await sleep(300);
        await page.locator("[data-server-manager-menu]:visible").click();
        await sleep(600);
        await shot(page, "mobile-side-panel");
      } else {
        await page.evaluate(() => document.querySelector("[data-home2-more]").click());
        await sleep(500);
        await shot(page, "quick-connect");
        await page.keyboard.press("Escape");
      }
    });
    // Last of the sessions: a database tab has no "Disconnect" and stays open.
    await step("database", async () => {
      if (phone) { return; }
      await page.goto(BASE + "/?open=" + c["Shop database"].id, { waitUntil: "domcontentloaded" });
      await sleep(5000);
      const frame = page.frames().find(f => f.url().includes("/db/"));
      await frame.locator("text=customers >> visible=true").first().click({ timeout: 15000 });
      await sleep(2500);
      await shot(page, "database");
    });
    await step("settings", async () => {
      await page.goto(BASE + "/account?tab=session", { waitUntil: "networkidle" });
      await sleep(600);
      await shot(page, phone ? "mobile-settings" : "settings");
    });
    await step("connection", async () => {
      if (phone) { return; }
      await page.goto(BASE + "/admin/servers/" + c["Workstation"].id, { waitUntil: "networkidle" });
      await sleep(600);
      await shot(page, "new-server");
    });
    await context.close();
  };
  try {
    await run("desktop", { viewport: { width: 1300, height: 820 } });
    await run("phone", { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  }
  finally {
    await b.close();
    try { docker(["rm", "-f", CONTAINER]); } catch { /* gone already */ }
  }
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
