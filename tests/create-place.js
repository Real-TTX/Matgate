// Making a place from the file manager: the last entry under the workspaces asks for a name and
// the manager then stands in the new place. Since the workspace page left the menu this is the
// only way to get a new one - sharing only ever turns an EXISTING place into a share.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "Frisch angelegt";
const MARKER = "__create-place__";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  let asked = null;
  page.on("dialog", async d => { asked = d.message(); await d.accept(NAME); });

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1800);

  // The file manager through the menu, so the select is the one in the toolbar.
  await page.evaluate(() => {
    const entry = document.querySelector('.shell-tabs a[data-shell-open-server]');
    if (entry) { entry.click(); }
  });
  await sleep(2500);

  const before = await page.evaluate(marker => {
    const select = document.querySelector("[data-file-place-select]");
    if (!select) { return null; }
    const groups = Array.from(select.querySelectorAll("optgroup"));
    const workspaces = groups.find(g => /Workspace/i.test(g.label));
    const options = workspaces ? Array.from(workspaces.querySelectorAll("option")) : [];
    return {
      hasWorkspaceGroup: !!workspaces,
      createIsLast: options.length > 0 && options[options.length - 1].value === marker,
      createCount: Array.from(select.querySelectorAll("option")).filter(o => o.value === marker).length,
      tabs: document.querySelectorAll("#session-tabs .session-tab").length,
    };
  }, MARKER);
  console.log("     select: " + JSON.stringify(before));
  check("the workspaces group is there", (before || {}).hasWorkspaceGroup, true);
  check("the create entry is its last one", (before || {}).createIsLast, true);
  check("and exists exactly once", (before || {}).createCount, 1);

  await page.selectOption("[data-file-place-select]", MARKER);
  await sleep(3500);

  console.log("     asked: " + JSON.stringify(asked));
  check("a name is asked for", typeof asked === "string" && asked.length > 0, true);

  const after = await page.evaluate(() => {
    const select = document.querySelector("[data-file-place-select]");
    const tab = document.querySelector("#session-tabs .session-tab.active .session-tab-title");
    return {
      selected: select ? (select.options[select.selectedIndex] || {}).textContent.trim() : null,
      tabTitle: tab ? tab.textContent.trim() : null,
      tabs: document.querySelectorAll("#session-tabs .session-tab").length,
    };
  });
  console.log("     after: " + JSON.stringify(after));
  check("the manager stands in the new place", /Frisch angelegt/.test(after.selected || ""), true);
  check("the tab carries its name", /Frisch angelegt/.test(after.tabTitle || ""), true);
  check("without opening a second tab", after.tabs, (before || {}).tabs);
  await page.screenshot({ path: "create-place.png", clip: { x: 0, y: 0, width: 1400, height: 300 } });

  // --- Clean up: the new place is a share like any other.
  const id = await page.evaluate(async name => {
    const text = await (await fetch("/workspaces", { credentials: "same-origin" })).text();
    const match = text.match(new RegExp('href="/workspaces/([0-9a-f-]{36})"[^>]*>[^<]*' + name));
    return match ? match[1] : null;
  }, NAME);
  if (id) {
    await page.goto(BASE + "/workspaces/" + id, { waitUntil: "networkidle" });
    await page.evaluate(() => {
      const form = document.querySelector("form[action$='/delete']");
      if (form) { form.removeAttribute("data-confirm"); form.submit(); }
    });
    await sleep(1300);
  }

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
