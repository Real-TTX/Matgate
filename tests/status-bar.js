// The status bar (0.55.0): a dot, the state and a few numbers as chips (frames a second, latency coloured, data
// rate); a click opens the details - frame time, picture, encoder, Matgate's own latency - with the switch for the
// numbers in the picture and their corner. In the settings it can be switched off altogether; the iPhone's home
// bar strip stays clear then.
const { chromium } = require("playwright-core");
const path = require("path");
const os = require("os");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const shots = process.env.SHOTS || os.tmpdir();

(async () => {
  const b = await chromium.launch({ headless: true });
  try {
    const context = await b.newContext({ viewport: { width: 1400, height: 900 } });
    const page = await context.newPage();
    page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name='username']", "admin");
    await page.fill("input[name='password']", "test-only-pw");
    await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
    await sleep(800);

    const servers = () => page.evaluate(async () => {
      const text = await (await fetch("/", { credentials: "same-origin" })).text();
      return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    });
    // The RDP connection of display.js, made here when it is not there.
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
        Object.entries({ name: "Test-RDP", host: "desktop", port: "3389", targetUserName: "demo", targetPassword: "demo-only-pw", keyboardLayout: "en-us-qwerty", engine: "" }).forEach(([f, v]) => set(f, v));
        document.querySelector("[name='name']").form.requestSubmit();
      }).catch(() => {});
      await sleep(1500);
      rdp = (await servers()).find(s => s.name === "Test-RDP");
    }

    // --- Nothing open: the state and Matgate's own latency.
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(2500);
    const bar = () => page.evaluate(() => ({
      dot: document.querySelector("#status-metrics .status-dot").dataset.tone,
      state: document.getElementById("status-state").textContent,
      chips: [...document.querySelectorAll("#status-chips .status-chip")].map(c => c.dataset.kind),
      old: !!document.getElementById("status-tunnel") || !!document.getElementById("status-sync"),
    }));
    const idle = await bar();
    check("without a session: the dot, the state and Matgate's latency - no \"Tunnel: -\" any more", [idle.dot, idle.chips, idle.old], ["idle", ["gateway"], false]);

    // --- A screen session: its numbers.
    await page.goto(BASE + "/?open=" + rdp.id, { waitUntil: "networkidle" });
    const frame = () => page.frames().find(f => /\/display\/.+\/screen/.test(f.url()));
    for (let i = 0; i < 150 && !(frame() && (await frame().evaluate(() => window.__screen && window.__screen.stats ? 1 : 0).catch(() => 0))); i++) { await sleep(300); }
    await sleep(2500);
    const live = await bar();
    check("in a session: frames a second, latency and data rate as chips", live.chips, ["fps", "latency", "rate"]);
    check("its dot is green", live.dot, "ok");
    check("the latency is coloured", await page.evaluate(() => ["ok", "warn", "bad"].includes(document.querySelector("#status-chips [data-kind='latency']").dataset.tone)), true);
    await page.locator("#session-statusbar").screenshot({ path: path.join(shots, "status-bar.png") });

    // --- A click: the details.
    await page.click("#status-metrics");
    await sleep(400);
    const details = await page.evaluate(() => {
      const panel = document.getElementById("status-metrics-panel");
      return { open: !panel.hidden, rows: [...panel.querySelectorAll("dt")].length, perf: !!panel.querySelector("[data-status-perf]") };
    });
    check("a click opens the details, with the switch for the numbers in the picture", [details.open, details.rows >= 8, details.perf], [true, true, true]);
    check("the frame time is among them", await page.evaluate(() => [...document.querySelectorAll("#status-metrics-panel dd")].some(d => / ms$/.test(d.textContent))), true);
    // The graph of the last minute: drawn, with its legend - and a small line in the bar itself.
    const inked = sel => page.evaluate(s => {
      const c = document.querySelector(s);
      if (!c || !c.width) return 0;
      const d = c.getContext("2d").getImageData(0, 0, c.width, c.height).data;
      let n = 0; for (let i = 3; i < d.length; i += 4) if (d[i] > 0) n++;
      return n;
    }, sel);
    check("the details show a graph of the last minute", (await inked("#status-metrics-panel [data-status-graph]")) > 50, true);
    check("with a legend", await page.evaluate(() => [...document.querySelectorAll("#status-metrics-panel .status-graph-legend span")].map(s => s.dataset.kind).filter(k => k !== "rtt")), ["fps", "kbps"]);
    check("and the bar has a small line of frames a second", await page.evaluate(() => !document.getElementById("status-spark").hidden), true);
    check("which is drawn", (await inked("#status-spark")) > 30, true);
    await page.screenshot({ path: path.join(shots, "status-panel.png"), clip: { x: 900, y: 420, width: 500, height: 480 } });
    // Switched on there, the numbers show in the picture; the corners appear and move them.
    await page.click("[data-status-perf]");
    await sleep(600);
    check("the switch shows the numbers in the picture", await frame().evaluate(() => !document.getElementById("perf").classList.contains("hidden")), true);
    await page.click("[data-status-perf-at='bottom-right']");
    await sleep(400);
    check("and a corner moves them", await frame().evaluate(() => document.getElementById("perf").classList.contains("at-bottom-right")), true);
    // Three sizes: small one line, medium two, large two and the graph.
    const perfLook = () => frame().evaluate(() => {
      const box = document.getElementById("perf");
      const graph = box.querySelector(".perf-graph");
      let ink = 0;
      if (graph.width && getComputedStyle(graph).display !== "none") {
        const d = graph.getContext("2d").getImageData(0, 0, graph.width, graph.height).data;
        for (let i = 3; i < d.length; i += 4) if (d[i] > 0) ink++;
      }
      return { lines: box.querySelector(".perf-text").textContent.split("\n").length, graph: getComputedStyle(graph).display !== "none", ink: ink > 50 };
    });
    check("medium is the default: two lines, no graph", await perfLook(), { lines: 2, graph: false, ink: false });
    await page.click("[data-status-perf-size='large']");
    await sleep(2500);
    check("large: two lines and the graph", await perfLook(), { lines: 2, graph: true, ink: true });
    await page.screenshot({ path: path.join(shots, "status-perf-large.png") });
    await page.click("[data-status-perf-size='small']");
    await sleep(1500);
    check("small: one line", await perfLook(), { lines: 1, graph: false, ink: false });
    check("the size is kept on this device", await frame().evaluate(() => localStorage.getItem("matgate.screen.perfSize")), "small");
    await page.click("[data-status-perf-size='medium']");
    await sleep(300);
    await page.screenshot({ path: path.join(shots, "status-panel-perf.png") });
    await page.click("[data-status-perf]");
    await sleep(300);
    check("switched off again", await frame().evaluate(() => document.getElementById("perf").classList.contains("hidden")), true);
    await frame().evaluate(() => window.MatgateScreen.setPerfPosition("top-left"));
    await page.keyboard.press("Escape");
    await sleep(300);
    check("Escape closes the details", await page.evaluate(() => document.getElementById("status-metrics-panel").hidden), true);
    await page.evaluate(() => { const a = document.querySelector("#connection-tab-actions [data-action='disconnect']"); if (a) a.click(); });
    await sleep(1000);

    // --- Switched off in the settings.
    const setBar = async on => {
      await page.goto(BASE + "/account?tab=session", { waitUntil: "networkidle" });
      await page.evaluate(value => { const box = document.querySelector("#set-form-session [name='statusBar']"); box.checked = value; box.dispatchEvent(new Event("change", { bubbles: true })); }, on);
      await Promise.all([page.waitForNavigation({ waitUntil: "networkidle" }), page.evaluate(() => document.getElementById("set-form-session").requestSubmit())]);
    };
    await setBar(false);
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(800);
    check("switched off: no status bar", await page.evaluate(() => { const bar = document.getElementById("session-statusbar"); return [bar.classList.contains("is-off"), Math.round(bar.getBoundingClientRect().height)]; }), [true, 0]);
    await setBar(true);
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(800);
    check("switched on again: it is back", await page.evaluate(() => document.getElementById("session-statusbar").getBoundingClientRect().height > 20), true);

    // --- A phone: the dot and the chips, no state text.
    const phone = await b.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, storageState: await context.storageState() });
    const small = await phone.newPage();
    await small.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(1500);
    check("on a phone: dot and chips, the state only in the details", await small.evaluate(() => [
      getComputedStyle(document.getElementById("status-metrics")).display !== "none",
      getComputedStyle(document.getElementById("status-state")).display,
    ]), [true, "none"]);
    await phone.close();
  }
  finally {
    await b.close();
  }
  console.log("screenshots in " + shots);
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
