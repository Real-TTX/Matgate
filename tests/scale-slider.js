// The scale of a remote desktop without steps. The picker offered Fit, 75 % and 50 % and nothing
// between; now a slider fills in every percent from 25 to 100. In a fixed resolution the same place
// holds a zoom slider instead, which follows the finger at once because it is only local.
const { chromium } = require("playwright-core");
const rig = require("./toy-vnc-rig");
const { BASE, sleep } = rig;
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

(async () => {
  const stopServer = await rig.startServer();
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const frames = [];
  page.on("websocket", ws => ws.on("framesent", f => frames.push(typeof f.payload === "string" ? f.payload : "")));
  const sizesSince = mark => {
    const out = [];
    const re = /[0-9]+[.]size,[0-9]+[.]([0-9]+),[0-9]+[.]([0-9]+)/g;
    let m;
    const text = frames.slice(mark).join("");
    while ((m = re.exec(text))) { out.push({ width: Number(m[1]), height: Number(m[2]) }); }
    return out;
  };

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1500);
  const id = await rig.ensureConnection(page);
  const open = async res => {
    await page.evaluate(([sid, value]) => {
      localStorage.setItem("matgate.display.res.v3", JSON.stringify(value ? { [sid]: value } : {}));
      localStorage.setItem("matgate.autoresize.v1", JSON.stringify({ [sid]: true }));
    }, [id, res]);
    await page.goto(BASE + "/connect/" + id, { waitUntil: "networkidle" });
    await sleep(6500);
  };
  const dialog = async () => {
    await page.locator("#connection-tab-actions button[title^='Scale'], #connection-tab-actions button[title^='Resolution']").first().click();
    await sleep(400);
    return page.evaluate(() => {
      const slider = document.querySelector("#resolution-dialog .resolution-scale-slider");
      const apply = document.querySelector("#resolution-dialog .resolution-scale-apply");
      return {
        open: !document.getElementById("resolution-dialog").classList.contains("hidden"),
        slider: slider ? { min: slider.min, max: slider.max, value: slider.value } : null,
        shown: (document.querySelector("#resolution-dialog .resolution-scale-head strong") || {}).textContent,
        name: (document.querySelector("#resolution-dialog .resolution-scale-head span") || {}).textContent,
        note: (document.querySelector("#resolution-dialog .resolution-scale-note") || {}).textContent || null,
        applyDisabled: apply ? apply.disabled : null,
        presets: Array.from(document.querySelectorAll("#resolution-dialog .resolution-option")).map(el => el.textContent.trim()),
      };
    });
  };

  // --- Fit: a slider between the presets.
  await open("fit");
  let d = await dialog();
  console.log("     dialog: " + JSON.stringify(d));
  check("the dialog opens", d.open, true);
  check("it has a slider from 25 to 100 percent", d.slider && [d.slider.min, d.slider.max], ["25", "100"]);
  check("which stands where the session is", d.slider && d.slider.value, "100");
  check("and says so", d.shown, "100 %");
  check("with nothing to apply yet", d.applyDisabled, true);
  check("the usual presets are still there", d.presets.some(p => /75/.test(p)) && d.presets.some(p => /50/.test(p)), true);

  await page.locator("#resolution-dialog .resolution-scale-slider").fill("63");
  await sleep(200);
  const panel = await page.evaluate(() => { const r = document.querySelector(".connection-panel:not(.hidden)").getBoundingClientRect(); return { w: r.width, h: r.height }; });
  d = await page.evaluate(() => ({
    shown: document.querySelector("#resolution-dialog .resolution-scale-head strong").textContent,
    note: document.querySelector("#resolution-dialog .resolution-scale-note").textContent,
    applyDisabled: document.querySelector("#resolution-dialog .resolution-scale-apply").disabled,
  }));
  console.log("     after moving to 63: " + JSON.stringify(d) + " panel " + Math.round(panel.w) + "x" + Math.round(panel.h));
  check("moving the slider shows the percent", d.shown, "63 %");
  check("and the size it asks of the remote", d.note.includes(Math.floor(panel.w / 0.63) + " \u00d7 " + Math.floor(panel.h / 0.63)), true);
  check("applying is possible now", d.applyDisabled, false);

  const mark = frames.length;
  await page.locator("#resolution-dialog .resolution-scale-apply").click();
  await sleep(1500);
  check("applying closes the dialog", await page.evaluate(() => document.getElementById("resolution-dialog").classList.contains("hidden")), true);
  const stored = await page.evaluate(sid => (JSON.parse(localStorage.getItem("matgate.display.res.v3") || "{}"))[sid], id);
  check("63 percent is what is remembered for this connection", stored, "fit63");
  const sizes = sizesSince(mark);
  console.log("     size instructions after applying: " + JSON.stringify(sizes));
  check("and the remote was asked for a desktop that much larger than the window", sizes.some(s => Math.abs(s.width - Math.floor(panel.w / 0.63)) <= 4), true);
  const label = await page.evaluate(() => (document.querySelector("#connection-tab-actions button[title^='Scale'], #connection-tab-actions button[title^='Resolution']") || {}).title);
  check("the button says 63%", /63%/.test(label || ""), true);

  // Back to the same connection by its address, the way a person comes back to it.
  await page.goto(BASE + "/connect/" + id, { waitUntil: "networkidle" });
  await sleep(6500);
  d = await dialog();
  check("after a reload the slider is still at 63", d.slider && d.slider.value, "63");
  await page.locator("#resolution-dialog .resolution-scale-slider").fill("100");
  await page.locator("#resolution-dialog .resolution-scale-apply").click();
  await sleep(1200);
  check("100 percent is stored as plain fit", await page.evaluate(sid => (JSON.parse(localStorage.getItem("matgate.display.res.v3") || "{}"))[sid], id), "fit");

  // --- A preset still works, and the slider follows it.
  d = await dialog();
  await page.locator("#resolution-dialog .resolution-option").filter({ hasText: /50/ }).first().click();
  await sleep(1200);
  check("a preset button sets its percent", await page.evaluate(sid => (JSON.parse(localStorage.getItem("matgate.display.res.v3") || "{}"))[sid], id), "fit50");
  d = await dialog();
  check("and the slider shows it", d.slider && d.slider.value, "50");
  await page.keyboard.press("Escape");
  await page.evaluate(() => document.getElementById("resolution-close").click());

  // --- A fixed resolution: the same place holds the zoom, which needs no applying.
  await open("1280x720");
  d = await dialog();
  console.log("     fixed resolution dialog: " + JSON.stringify({ slider: d.slider, name: d.name, applyDisabled: d.applyDisabled }));
  check("in a fixed resolution the slider is the zoom", d.name, "Zoom");
  check("from 25 to 300 percent", d.slider && [d.slider.min, d.slider.max], ["25", "300"]);
  check("with nothing to apply", d.applyDisabled, null);
  const widthAt = () => page.evaluate(() => document.querySelector(".connection-panel:not(.hidden) .guac-scaler").style.width);
  const before = await widthAt();
  await page.locator("#resolution-dialog .resolution-scale-slider").fill("50");
  await sleep(400);
  const after = await widthAt();
  console.log("     picture width " + before + " -> " + after);
  check("moving it zooms at once", before !== after && after === "800px", true);

  await rig.removeConnection(page);
  stopServer();
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
