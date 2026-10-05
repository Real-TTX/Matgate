// After the dissolution: no workspace entry in the menu any more, the section is called Shares,
// the gear opens the dialog, and the log is inside it.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "DissolvedShare";

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

  // --- No workspace entry in the menu any more
  const menu = await page.evaluate(() => ({
    bar: Array.from(document.querySelectorAll(".shell-tabs a")).map(a => a.textContent.trim()),
    workspaceLink: !!document.querySelector('.shell-tabs a[href="/workspaces"], .shell-burger a[href="/workspaces"]'),
  }));
  console.log("     menu: " + JSON.stringify(menu));
  check("no workspace entry in the menu any more", menu.workspaceLink, false);
  check("Files is still in it", menu.bar.some(x => /Files|Dateien/.test(x)), true);

  // --- Create a share, so the section has something to show
  await page.goto(BASE + "/workspaces/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"name\"]", NAME);
  await page.evaluate(() => document.querySelector("input[name='name']").form.requestSubmit());
  // Waiting for the address instead of a fixed pause: the redirect carries the id of the new
  // share, and reading it a moment too early left the cleanup with "undefined".
  await page.waitForURL(/\/workspaces\/[0-9a-f-]{36}/i, { timeout: 15000 }).catch(() => {});
  await sleep(1800);
  let shareId = (page.url().match(/\/workspaces\/([0-9a-f-]{36})/i) || [])[1];

  // --- The section is called Shares now
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1400);
  const section = await page.evaluate(() => {
    const s = document.querySelector("[data-home2-workspaces]");
    if (!s) return null;
    return {
      heading: (s.querySelector("h2") || {}).textContent.trim(),
      gearIsButton: !!s.querySelector("button[data-workspace-settings]"),
      gearIsLink: !!s.querySelector("a[href^='/workspaces/']"),
    };
  });
  console.log("     Abschnitt: " + JSON.stringify(section));
  check("the section is called Shares", /Freigaben|Shares/.test((section || {}).heading || ""), true);
  check("the gear is a button", (section || {}).gearIsButton, true);
  check("and not a link to the old page", (section || {}).gearIsLink, false);
  await page.screenshot({ path: "dissolved-home.png", clip: { x: 0, y: 0, width: 1400, height: 520 } });

  // --- The gear opens the dialog, and the log is inside it
  await page.locator("[data-home2-workspaces] button[data-workspace-settings]").first().click();
  await sleep(2000);
  const dialog = await page.evaluate(() => {
    const d = document.getElementById("workspace-settings-dialog");
    const texts = d ? Array.from(d.querySelectorAll("legend")).map(l => l.textContent.trim()) : [];
    return {
      isOpen: !!d && d.open,
      groups: texts,
      log: !!(d && (d.querySelector(".share-log") || /No activity|Noch keine/.test(d.textContent || ""))),
      url: location.pathname,
    };
  });
  console.log("     Dialog: " + JSON.stringify(dialog));
  check("the dialog opens", dialog.isOpen, true);
  check("with a Log group", dialog.groups.some(g => /Log|Protokoll/.test(g)), true);
  check("the log is there", dialog.log, true);
  check("without a page change", dialog.url, "/");
  await page.screenshot({ path: "dissolved-dialog.png" });

  // --- Clean up. If the id was missed, look it up by name: a share left behind would make the
  // next run find two cards of the same name.
  if (!shareId) {
    shareId = await page.evaluate(async name => {
      const text = await (await fetch("/workspaces", { credentials: "same-origin" })).text();
      const match = text.match(new RegExp('href="/workspaces/([0-9a-f-]{36})"[^>]*>[^<]*' + name));
      return match ? match[1] : null;
    }, NAME);
  }

  if (shareId) {
    await page.goto(BASE + "/workspaces/" + shareId, { waitUntil: "networkidle" });
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
