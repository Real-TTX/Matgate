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
  // Connections with Matgate's own engine (as display.js makes them): VNC to the stack's desktop container, SSH
  // to its ssh container. The SSH one has a long name: a card has the width for it.
  const ensure = async (name, protocol, fields) => {
    if (!(await servers()).find(s => s.name === name)) {
      await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
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
    }
  };
  const vncName = "Test-VNC-Vorschau";
  const ownSshName = "Test-SSH-eigene-Engine-mit-langem-Namen";
  await ensure(vncName, "Vnc", { host: "desktop", port: "5901", targetPassword: "demo-pw1", engine: "" });
  await ensure(ownSshName, "Ssh", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", engine: "" });
  const all = await servers();
  const vnc = all.find(s => s.name === vncName);
  const ownSsh = all.find(s => s.name === ownSshName);
  const ssh = all.find(s => s.name === "Linux-Testhost");
  const db = all.find(s => s.name === "Test-Postgres");
  check("connections there", [!!vnc, !!ownSsh, !!ssh, !!db], [true, true, true, true]);

  // Each opened in a tab of its own; a session needs a moment to show something. The own SSH comes last: its
  // terminal is text, not a canvas, and is only laid out - so only pictured - while its tab is the open one.
  for (const s of [vnc, ssh, db, ownSsh].filter(Boolean)) {
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
        // The whole name readable: nothing cut off at the end.
        nameWhole: (n => n.scrollHeight <= n.clientHeight + 1 && n.scrollWidth <= n.clientWidth + 1)(c.querySelector(".mobile-tab-item-name")),
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
  check("own-engine SSH: its terminal as the picture", pick(first, ownSshName), { preview: "image", picture: true });
  check("a long name is shown whole", !!first[ownSshName] && first[ownSshName].nameWhole, true);
  check("one card a row", await page.evaluate(() => {
    const cards = Array.from(document.querySelectorAll(".mobile-tab-card")).map(c => c.getBoundingClientRect());
    return cards.length > 1 && cards.every((r, i) => i === 0 || r.top >= cards[i - 1].bottom);
  }), true);
  // Four cards are more than fit on the screen: each still shows its head - sign and name inside the card, the
  // picture below them - and the list scrolls instead of squeezing them.
  check("every card shows its head above the picture", await page.evaluate(() => Array.from(document.querySelectorAll(".mobile-tab-card")).every(c => {
    const card = c.getBoundingClientRect();
    const name = c.querySelector(".mobile-tab-item-name").getBoundingClientRect();
    const sign = c.querySelector(".mobile-tab-item-icon").getBoundingClientRect();
    const thumb = c.querySelector(".mobile-tab-item-thumb").getBoundingClientRect();
    return name.height > 0 && name.top >= card.top && sign.top >= card.top && thumb.top >= name.bottom - 1 && thumb.bottom <= card.bottom + 1;
  })), true);

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
  // The own SSH tab is in the background now, its terminal not laid out: its card keeps the last picture.
  check("a terminal in the background keeps its last picture", second[ownSshName] ? { preview: second[ownSshName].preview, same: second[ownSshName].src === first[ownSshName].src } : null, { preview: "image", same: true });
  await page.screenshot({ path: "tab-previews-2.png" });

  // The connections of this script go again: other scripts open "the first connection" on the start page.
  await page.evaluate(async names => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && names.includes(cell)) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  }, [vncName, ownSshName]);

  await b.close();
  console.log("failed: " + JSON.stringify(failures));
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
