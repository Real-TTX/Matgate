// The phone's tab overview shows every tab as a card with the last picture of its session, as a phone browser
// shows its tabs: a VNC session of Matgate's own engine (its screen page in a frame), an SSH session through
// Guacamole (its display flattened), and a database tab that draws nothing and shows its sign instead.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

(async () => {
  const b = await chromium.launch({ headless: true });
  const ctx = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  await ctx.addInitScript(() => { try { localStorage.setItem("matgate.view.mode.v1", "minimal"); } catch (e) {} });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), page.click(".login-submit")]);
  await sleep(1200);

  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  // A VNC connection with Matgate's own engine, to the stack's desktop container (as display.js makes it).
  const vncName = "Test-VNC-Vorschau";
  if (!(await servers()).find(s => s.name === vncName)) {
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(n => {
      document.querySelector("[data-protocol-option='Vnc']").click();
      const set = (field, value) => {
        const all = [...document.querySelectorAll("[name='" + field + "']")];
        const el = all.find(e => !e.disabled) || all[0];
        el.value = value;
        el.dispatchEvent(new Event("change", { bubbles: true }));
      };
      set("name", n); set("host", "desktop"); set("port", "5901"); set("targetPassword", "demo-pw1"); set("engine", "");
      document.querySelector("[name='name']").form.requestSubmit();
    }, vncName).catch(() => { /* the submit navigated before the call returned */ });
    await sleep(1500);
  }
  const all = await servers();
  const vnc = all.find(s => s.name === vncName);
  const ssh = all.find(s => s.name === "Linux-Testhost");
  const db = all.find(s => s.name === "Test-Postgres");
  check("connections there", [!!vnc, !!ssh, !!db], [true, true, true]);

  // Each opened in a tab of its own; a session needs a moment to show something.
  for (const s of [vnc, ssh, db].filter(Boolean)) {
    await page.goto(BASE + "/?open=" + s.id, { waitUntil: "networkidle" });
    await sleep(s === db ? 2500 : 9000);
  }

  const cards = async () => {
    await page.locator(".mobile-tab-menu-trigger").tap();
    await sleep(800);
    const got = await page.evaluate(() => Object.fromEntries(Array.from(document.querySelectorAll(".mobile-tab-card")).map(c => {
      const thumb = c.querySelector(".mobile-tab-item-thumb");
      const img = thumb.querySelector("img");
      return [c.querySelector(".mobile-tab-item-name").textContent.trim(), {
        preview: thumb.getAttribute("data-preview"),
        // A real picture, not an empty frame: a JPEG of some size.
        picture: !!img && img.src.startsWith("data:image/jpeg") && img.src.length > 3000,
        src: img ? img.src : "",
      }];
    })));
    return got;
  };
  const first = await cards();
  await page.screenshot({ path: "tab-previews-1.png" });
  const pick = (got, name) => got[name] ? { preview: got[name].preview, picture: got[name].picture } : null;
  check("own-engine VNC: its screen as the picture", pick(first, vncName), { preview: "image", picture: true });
  check("Guacamole SSH: its terminal as the picture", pick(first, "Linux-Testhost"), { preview: "image", picture: true });
  check("database: its sign, no picture", pick(first, "Test-Postgres"), { preview: "icon", picture: false });

  // Shut the sheet, type into the SSH session, open it again: the picture is taken anew each time.
  await page.locator(".mobile-tab-sheet-close").tap();
  await sleep(300);
  await page.locator(".mobile-tab-menu-trigger").tap();
  await sleep(600);
  await page.locator(".mobile-tab-card", { hasText: "Linux-Testhost" }).locator(".mobile-tab-item-main").tap();
  await sleep(1200);
  await page.keyboard.type("echo tab-preview-$((40+2)) && ls /", { delay: 20 });
  await page.keyboard.press("Enter");
  await sleep(1500);
  const second = await cards();
  check("the picture is fresh after the session changed", !!second["Linux-Testhost"] && second["Linux-Testhost"].src !== first["Linux-Testhost"].src, true);
  await page.screenshot({ path: "tab-previews-2.png" });

  // The VNC connection goes again: other scripts open "the first connection" on the start page.
  await page.evaluate(async n => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && cell === n) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  }, vncName);

  await b.close();
  console.log("failed: " + JSON.stringify(failures));
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
