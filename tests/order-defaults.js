// The order of things, and the way back to it. On a phone the device keyboard is the first button of a
// session's bar - also for someone who sorted the actions otherwise. In the settings every sortable list
// (quick connect, the session bar, the start page) has "Restore default", which puts the built-in order back,
// shows everything again and saves.
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
  let b = await chromium.launch({ headless: true });
  const desk = await b.newContext({ viewport: { width: 1300, height: 900 } });
  const page = await desk.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const order = id => page.evaluate(i => [...document.querySelectorAll("#" + i + " > li")].map(li => li.dataset.orderKey || li.dataset.action), id);
  const settings = async () => { await page.goto(BASE + "/account?tab=session", { waitUntil: "networkidle" }); await sleep(500); };
  // The quick-connect chips are arranged with the start page.
  const home = async () => { await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" }); await sleep(500); };

  // ---- the session bar's list: sorted by hand, then back to the default
  // The built-in order (SessionPreferences.SortableActions): the keyboards first.
  const builtIn = ["keyboard", "osk", "pointer", "rightClick", "fullscreen", "popOut", "reattach", "resolution", "autoResize", "zoomOut", "zoomIn", "copyUrl", "clipboard", "cad", "upload", "fileArea"];
  await settings();
  // Fullscreen to the front and the keyboard hidden, saved.
  await page.evaluate(() => {
    const list = document.getElementById("action-order-list");
    list.prepend(list.querySelector("[data-action='fullscreen']"));
    list.querySelector("[data-action='keyboard'] [data-action-visible]").checked = false;
    list.dispatchEvent(new Event("change", { bubbles: true }));
    list.closest("form").requestSubmit();
  });
  await sleep(1500);
  await settings();
  check("sorted by hand it is saved that way", (await order("action-order-list"))[0], "fullscreen");

  // ---- on a phone the keyboard still comes first
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, storageState: await desk.storageState() });
  const small = await phone.newPage();
  small.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  // The desktop of the test stack over VNC, with Matgate's own engine - made here when it is not there yet.
  if (!(await servers()).some(s => s.name === "Test-VNC")) {
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(() => {
      document.querySelector("[data-protocol-option='Vnc']").click();
      const set = (field, value) => { const all = [...document.querySelectorAll("[name='" + field + "']")]; const el = all.find(e => !e.disabled) || all[0]; el.value = value; el.dispatchEvent(new Event("change", { bubbles: true })); };
      set("name", "Test-VNC"); set("host", "desktop"); set("port", "5901"); set("targetPassword", "demo-pw1");
      document.querySelector("[name='name']").form.requestSubmit();
    }).catch(() => {});
    await sleep(1500);
  }
  const vnc = (await servers()).find(s => s.name === "Test-VNC");
  if (vnc) {
    await small.goto(BASE + "/", { waitUntil: "networkidle" });
    await small.evaluate(id => window.MatgateOpenServerTab(id), vnc.id);
    await sleep(6000);
    const bar = await small.evaluate(() => [...document.querySelectorAll("#connection-tab-actions > [data-action]")].map(button => button.dataset.action));
    check("on a phone the device keyboard is the session bar's first button - though sorted to the back and hidden it is not", bar[0], "keyboard");
    await small.evaluate(() => { const button = document.querySelector("#connection-tab-actions [data-action='disconnect']"); if (button) { button.click(); } });
    await sleep(800);
  }
  else {
    console.log("     (no RDP or VNC connection in this stack: the phone check is skipped)");
  }

  // ---- restore default: built-in order, everything shown, saved
  await settings();
  await page.click("#action-order-list ~ [data-order-reset]");
  await sleep(1500);
  await settings();
  check("\"Restore default\" puts the built-in order back and saves it", await order("action-order-list"), builtIn);
  check("and shows what was hidden", await page.evaluate(() => document.querySelector("#action-order-list [data-action='keyboard'] [data-action-visible]").checked), true);

  // ---- the other lists have it as well
  check("quick connect and the start page have the button too", await page.evaluate(() => ["quick-order-list", "home-order-list"].map(id => !!document.querySelector("#" + id + " ~ [data-order-reset]"))), [true, true]);
  await page.evaluate(() => { const list = document.getElementById("quick-order-list"); list.append(list.firstElementChild); list.dispatchEvent(new Event("change", { bubbles: true })); list.closest("form").requestSubmit(); });
  await sleep(1500);
  await home();
  const moved = await order("quick-order-list");
  await page.click("#quick-order-list ~ [data-order-reset]");
  await sleep(1500);
  await home();
  check("the quick-connect chips go back to their built-in order", [moved[0] !== "rdp", (await order("quick-order-list"))[0]], [true, "rdp"]);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
