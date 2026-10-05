// Chosen connections as a button in the home page's action bar - one press connects.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const bar = page => page.evaluate(() =>
  Array.from(document.querySelectorAll("#connection-tab-actions .tab-action-shortcut"))
    .map(b => ({ id: b.dataset.serverId || "", name: (b.getAttribute("aria-label") || "").trim(), colour: b.style.getPropertyValue("--proto") })));

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1700);

  // --- Before: no buttons
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1300);
  check("no shortcuts before", (await bar(page)).length, 0);

  // --- Pick two connections
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(900);
  const selection = await page.evaluate(() => {
    const list = document.getElementById("action-bar-list");
    if (!list) return { missing: true };
    const entries = Array.from(list.querySelectorAll(".action-order-item[data-order-key]"));
    const names = entries.map(e => e.querySelector(".action-order-name").textContent.trim());
    entries.slice(0, 2).forEach(e => {
      const k = e.querySelector("[data-order-visible]");
      k.checked = true;
      k.dispatchEvent(new Event("change", { bubbles: true }));
    });
    // Drag the second one to the front, so the order is checked as well
    if (entries.length > 1) {
      list.insertBefore(entries[1], entries[0]);
      list.dispatchEvent(new Event("change", { bubbles: true }));
    }
    return { names, ids: entries.map(e => e.dataset.orderKey), sent: document.querySelector("[name='actionBarServers']").value };
  });
  console.log("     selectable: " + JSON.stringify(selection.names));
  check("the list offers the connections", Array.isArray(selection.names) && selection.names.length >= 2, true);
  await page.screenshot({ path: "bar-1-selection.png" });

  await page.evaluate(() => document.querySelector("[name='actionBarServers']").form.requestSubmit());
  await sleep(1800);

  // --- After: two buttons, in the order they were dragged into
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1500);
  const buttons = await bar(page);
  console.log("     bar: " + JSON.stringify(buttons));
  check("two buttons in the bar", buttons.length, 2);
  // Both test connections have the same name - the order can only be checked by id.
  check("the one dragged forward is first", buttons[0].id, selection.ids[1]);
  check("the other one is behind it", buttons[1].id, selection.ids[0]);
  check("the button carries the protocol colour", /^#|var\(/.test(buttons[0].colour || ""), true);
  await page.screenshot({ path: "bar-2-buttons.png", clip: { x: 0, y: 0, width: 1400, height: 110 } });

  // --- One press connects
  const before = await page.evaluate(() => document.querySelectorAll("#session-tabs .session-tab").length);
  await page.locator("#connection-tab-actions .tab-action-shortcut").first().click();
  await sleep(4000);
  const afterwards = await page.evaluate(() => ({
    tabs: document.querySelectorAll("#session-tabs .session-tab").length,
    tabTitle: Array.from(document.querySelectorAll("#session-tabs .session-tab-title")).map(t => t.textContent.trim()),
  }));
  console.log("     tabs " + before + " -> " + afterwards.tabs + " " + JSON.stringify(afterwards.tabTitle));
  check("one press opens a connection", afterwards.tabs > before, true);
  check("and it is the one that was clicked", afterwards.tabTitle.some(t => t === buttons[0].name), true);
  await page.screenshot({ path: "bar-3-connected.png", clip: { x: 0, y: 0, width: 1400, height: 110 } });

  // --- Inside an open session the bar belongs to the session again
  const inSession = await bar(page);
  check("no shortcuts inside a session", inSession.length, 0);

  // --- Unpick them
  await page.goto(BASE + "/account?tab=home", { waitUntil: "networkidle" });
  await sleep(900);
  await page.evaluate(() => {
    document.querySelectorAll("#action-bar-list [data-order-visible]").forEach(k => {
      k.checked = false;
      k.dispatchEvent(new Event("change", { bubbles: true }));
    });
    document.querySelector("[name='actionBarServers']").form.requestSubmit();
  });
  await sleep(1800);
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1400);
  check("unpicked, they are gone again", (await bar(page)).length, 0);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
