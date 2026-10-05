// The scrollbar of the tab strip: invisible until the mouse is over it - and without the tabs
// jumping when it appears.
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
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  // Narrow, so that the tabs overflow at all.
  const page = await (await b.newContext({ viewport: { width: 760, height: 900 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1800);

  // Open several places until the strip overflows
  const places = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    return list.filter(s => (s.protocol || "") === "LOCAL").map(s => s.id).slice(0, 6);
  });
  for (const id of places) {
    await page.evaluate(i => {
      const el = document.querySelector('[data-server-id="' + i + '"]');
      if (el) { el.click(); }
    }, id);
    await sleep(900);
  }

  const position = await page.evaluate(() => {
    const l = document.getElementById("session-tabs");
    if (!l) return null;
    const s = getComputedStyle(l);
    return {
      overflows: l.scrollWidth > l.clientWidth + 2,
      width: l.scrollWidth + "/" + l.clientWidth,
      colour: s.scrollbarColor,
      innerHeight: l.clientHeight,
    };
  });
  console.log("     strip: " + JSON.stringify(position));
  check("the strip overflows", position.overflows, true);
  // The browser writes "transparent" back as rgba(0, 0, 0, 0) - both count.
  const transparent = value => {
    const parts = (value || "").trim().split(/\s+(?![^(]*\))/);
    const isEmpty = s => s === "transparent" || s === "rgba(0, 0, 0, 0)";
    return parts.length === 2 && isEmpty(parts[0]) && isEmpty(parts[1]);
  };
  check("without the mouse the bar is transparent", transparent(position.colour), true);

  // Mouse over it
  await page.hover("#session-tabs");
  await sleep(500);
  const onHover = await page.evaluate(() => {
    const l = document.getElementById("session-tabs");
    const s = getComputedStyle(l);
    return { colour: s.scrollbarColor, innerHeight: l.clientHeight };
  });
  console.log("     with the mouse: " + JSON.stringify(onHover));
  check("with the mouse it becomes visible", transparent(onHover.colour), false);
  check("the tabs do not jump", onHover.innerHeight, position.innerHeight);

  await page.screenshot({ path: "tab-scrollbar.png", clip: { x: 0, y: 0, width: 760, height: 120 } });
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
