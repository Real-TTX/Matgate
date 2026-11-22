// One press on a session's toolbar button has to be enough. It was not, sometimes: the toolbar was built anew
// whenever the session said that something had changed - and pressing a button IS such a change (the picture
// loses the cursor). The button under the pointer was replaced between "down" and "up", and the press went
// nowhere; the second one worked, because by then nothing changed any more.
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
  let b; try { b = await chromium.launch({ headless: true, args: ["--autoplay-policy=no-user-gesture-required"] }); }
  catch { b = await chromium.launch({ headless: true }); }
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
  // The connection display.js and gallery.js use - made here as they make it when neither left it behind
  // (display.js removes it at its end, so in a full run it is not there).
  let rdp = (await servers()).find(s => s.name === "Test-RDP");
  if (!rdp) {
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(() => {
      document.querySelector("[data-protocol-option='Rdp']").click();
      const set = (field, value) => {
        const all = [...document.querySelectorAll("[name='" + field + "']")];
        const el = all.find(e => !e.disabled) || all[0];
        el.value = value;
        el.dispatchEvent(new Event("change", { bubbles: true }));
      };
      Object.entries({ name: "Test-RDP", host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "demo-only-pw", keyboardLayout: "en-us-qwerty", engine: "" })
        .forEach(([field, value]) => set(field, value));
      document.querySelector("[name='name']").form.requestSubmit();
    }).catch(() => { /* the submit navigated before the call returned */ });
    await sleep(1500);
    rdp = (await servers()).find(s => s.name === "Test-RDP");
  }
  if (!rdp) { throw new Error("no connection Test-RDP, and making one failed"); }

  await page.goto(BASE + "/?open=" + rdp.id, { waitUntil: "networkidle" });
  let frame = null;
  for (let i = 0; i < 100; i++) {
    frame = page.frames().find(f => /\/display\/.+\/screen/.test(f.url()));
    if (frame && (await frame.evaluate(() => window.__screen ? window.__screen.frames : 0).catch(() => 0)) >= 2) { break; }
    await sleep(300);
  }
  for (let i = 0; i < 30 && !(await page.$("#connection-tab-actions [data-action='sound']")); i++) { await sleep(300); }

  const sound = () => frame.evaluate(() => window.MatgateScreen.state().sound);
  // A press as a hand makes it: down, a moment, up - at the place where the button is.
  const press = async key => {
    const box = await page.locator("#connection-tab-actions [data-action='" + key + "']").boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await sleep(120);
    await page.mouse.up();
    await sleep(500);
  };
  // The cursor is in the picture, as it is while one works in the session.
  const intoPicture = async () => {
    const box = await page.locator("iframe").first().boundingBox();
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await sleep(400);
  };

  await intoPicture();
  const before = await sound();
  await press("sound");
  check("one press on the sound button switches the sound", await sound(), !before);
  await intoPicture();
  await press("sound");
  check("and one more switches it back", await sound(), before);
  await intoPicture();
  await press("resolution");
  check("one press opens the resolution dialog", await page.evaluate(() => !document.getElementById("resolution-dialog").classList.contains("hidden")), true);

  // ---- the frame every sheet has: a head with the title and the way out, a body, a foot with the buttons
  const open = () => page.evaluate(() => !document.getElementById("resolution-dialog").classList.contains("hidden"));
  check("the dialog has a head with its title and an X, a body, and a foot with the button", await page.evaluate(() => {
    const sheet = document.getElementById("resolution-dialog");
    const parts = [...sheet.children].map(part => part.className.split(" ").filter(name => /^dialog-/.test(name))[0] || part.className);
    return { parts, title: !!sheet.querySelector(".dialog-head h2").textContent.trim(), x: !!sheet.querySelector(".dialog-head [data-dialog-x]"), button: sheet.querySelectorAll(".dialog-foot button").length };
  }), { parts: ["dialog-head", "dialog-body", "dialog-foot"], title: true, x: true, button: 1 });
  check("what is on or off is a switch that says which", await page.evaluate(() => {
    const perf = document.querySelector("#resolution-dialog [data-perf]");
    return [perf.getAttribute("role"), perf.getAttribute("aria-checked"), !!perf.querySelector(".switch-track")];
  }), ["switch", "false", true]);
  check("the size is one choice among a few, side by side - and the list of fixed sizes stays shut", await page.evaluate(() => {
    const row = document.querySelector("#resolution-options .resolution-seg");
    const tops = [...row.children].map(item => Math.round(item.getBoundingClientRect().top));
    return [row.children.length, new Set(tops).size, document.querySelector("#resolution-options .resolution-fixed-list").hidden];
  }), [3, 1, true]);
  check("picture quality: four choices in two rows of two, title and a line below, the fastest one last", await page.evaluate(() => {
    const row = document.querySelector("#resolution-extra .resolution-seg--cards");
    if (!row) return null;
    const items = [...row.children];
    return [items.length, new Set(items.map(i => Math.round(i.getBoundingClientRect().top))).size,
      items.every(i => !!i.querySelector(".resolution-option-title") && !!i.querySelector(".resolution-option-hint")),
      /Fastest response|Schnellste Reaktion/.test(items[3].textContent), !!document.querySelector("#resolution-extra .resolution-note")];
  }), [4, 2, true, true, true]);
  await page.click("#resolution-dialog [data-dialog-x]");
  await sleep(300);
  check("the X closes it", await open(), false);
  await press("resolution");
  check("it opens again", await open(), true);
  await page.keyboard.press("Escape");
  await sleep(300);
  check("and Escape closes it", await open(), false);
  await press("resolution");
  await page.evaluate(() => document.getElementById("resolution-close").click());

  // The toolbar still follows the session: what changed while a button was held shows afterwards.
  await intoPicture();
  const label = () => page.evaluate(() => document.querySelector("#connection-tab-actions [data-action='sound']").title);
  const first = await label();
  await press("sound");
  check("the button says the other thing afterwards", (await label()) !== first, true);
  await press("sound");

  await page.evaluate(() => document.querySelector("#connection-tab-actions [data-action='disconnect']").click());
  await sleep(800);
  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
