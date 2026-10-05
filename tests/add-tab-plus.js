// The plus in the "New connection" tab: it should sit in the middle - and the strip should
// still be the same height whether a tab is open or not. Both together, otherwise it is only
// moved instead of fixed.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

const measure = () => {
  const box = el => { if (!el) return null; const r = el.getBoundingClientRect();
    return { top: +r.top.toFixed(1), bottom: +r.bottom.toFixed(1), centre: +((r.top + r.bottom) / 2).toFixed(1), h: +r.height.toFixed(1) }; };
  const add = document.querySelector(".session-tab--add");
  const real = document.querySelector(".session-tab:not(.session-tab--add)");
  return {
    plusTab: box(add),
    plusIcon: box(add && add.querySelector(".session-tab-title svg")),
    realTab: box(real),
    bar: box(document.getElementById("session-tabs")),
  };
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1800);

  // --- Without tabs
  const without = await page.evaluate(measure);
  console.log("     without tabs: " + JSON.stringify(without));
  const offBy1 = +Math.abs(without.plusIcon.centre - without.plusTab.centre).toFixed(1);
  console.log("     plus off the centre by: " + offBy1);
  check("the plus sits in the middle (without tabs)", offBy1 <= 1, true);
  await page.screenshot({ path: "plus-without-tabs.png", clip: { x: 1180, y: 40, width: 220, height: 70 } });

  // --- With a tab: open Files through the menu
  await page.evaluate(() => {
    const a = document.querySelector('.shell-tabs a[data-shell-open-server], .shell-tabs a[data-shell-open-tab]');
    if (a) { a.click(); }
  });
  await sleep(2500);
  const withTab = await page.evaluate(measure);
  console.log("     with a tab:   " + JSON.stringify(withTab));
  check("a tab is open", !!withTab.realTab, true);
  const offBy2 = +Math.abs(withTab.plusIcon.centre - withTab.plusTab.centre).toFixed(1);
  console.log("     plus off the centre by: " + offBy2);
  check("the plus sits in the middle (with a tab)", offBy2 <= 1, true);
  check("the plus tab is as high as a real tab", Math.abs(withTab.plusTab.h - withTab.realTab.h) <= 0.5, true);
  check("the strip does not jump in height", Math.abs(withTab.bar.h - without.bar.h) <= 0.5, true);
  await page.screenshot({ path: "plus-with-tab.png", clip: { x: 0, y: 40, width: 1400, height: 70 } });

  // --- And on the phone, where the description line disappears entirely
  await page.setViewportSize({ width: 390, height: 844 });
  await sleep(1200);
  const small = await page.evaluate(measure);
  console.log("     phone:        " + JSON.stringify(small));
  if (small.plusIcon && small.plusTab && small.plusTab.h > 0) {
    const offBy3 = +Math.abs(small.plusIcon.centre - small.plusTab.centre).toFixed(1);
    console.log("     plus off the centre by: " + offBy3);
    check("the plus sits in the middle on the phone too", offBy3 <= 1, true);
    if (small.realTab && small.realTab.h > 0) {
      check("and is as high there as a real tab", Math.abs(small.plusTab.h - small.realTab.h) <= 0.5, true);
    }
  } else {
    console.log("     (on the phone the strip is inside the sheet - not visible, nothing to measure)");
  }

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
