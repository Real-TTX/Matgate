// The page of a shared workspace as a visitor sees it on a phone. Its "create" menu holds two
// forms - a field and a button each - so it is the one menu where the sheet must NOT close when
// its button is pressed: a form that is being sent has to stay put until the browser has read it.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "Handyseite";
const PHONE = { width: 390, height: 844 };

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const owner = await b.newContext({ viewport: { width: 1200, height: 900 } });
  const page = await owner.newPage();
  page.on("pageerror", e => { console.log("JS ERROR (owner): " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1200);

  // Any earlier run that died halfway leaves its workspace behind - take every one of that name.
  const removeAll = () => page.evaluate(async name => {
    const parse = html => new DOMParser().parseFromString(html, "text/html");
    const fetchText = async url => (await fetch(url, { credentials: "same-origin" })).text();
    const list = parse(await fetchText("/workspaces"));
    const ids = new Set();
    for (const link of list.querySelectorAll("tr td:first-child a[href^='/workspaces/']")) {
      if ((link.textContent || "").trim() === name) { ids.add(link.getAttribute("href").split("/").pop()); }
    }
    for (const id of ids) {
      const form = parse(await fetchText("/workspaces/" + id)).querySelector("form[action$='/delete']");
      if (form) { await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" }); }
    }
    return ids.size;
  }, NAME);
  await removeAll();

  await page.goto(BASE + "/workspaces/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"name\"]", NAME);
  await page.evaluate(() => document.querySelector("input[name='name']").form.requestSubmit());
  await page.waitForURL(/workspaces\/[0-9a-f-]{36}/i, { timeout: 15000 }).catch(() => {});
  await sleep(1500);
  const id = (page.url().match(/workspaces\/([0-9a-f-]{36})/i) || [])[1];
  check("a workspace to look at", typeof id === "string", true);
  const place = await page.evaluate(async name => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const token = (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
    const hit = list.find(s => s.name === "Workspaces/" + name);
    return { id: hit ? hit.id : null, token };
  }, NAME);
  await page.evaluate(async ([pid, token]) => {
    for (const n of ["bericht.txt", "ein-sehr-langer-dateiname-der-umbrechen-muss-1234567890.txt"]) {
      const data = new FormData();
      data.append("path", "/");
      data.append("file", new Blob(["inhalt von " + n], { type: "text/plain" }), n);
      await fetch("/api/files/" + pid + "/upload", { method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
    }
  }, [place.id, place.token]);

  // --- The visitor: nobody is signed in.
  const visitor = await b.newContext({ viewport: PHONE, deviceScaleFactor: 2, hasTouch: true, isMobile: true });
  const v = await visitor.newPage();
  v.on("pageerror", e => { console.log("JS ERROR (visitor): " + e.message); failures.push("JS error"); });
  await v.goto(BASE + "/w/" + id, { waitUntil: "networkidle" });
  await sleep(1000);
  const facts = await v.evaluate(() => {
    const bar = document.querySelector(".workspace-file-manager .file-toolbar");
    const rows = Array.from(document.querySelectorAll(".workspace-file-manager .file-table tbody tr"));
    const named = rows.filter(r => r.innerText.includes("bericht.txt") || r.innerText.includes("ein-sehr-langer"));
    return {
      toolbar: Math.round(bar.getBoundingClientRect().height),
      cells: named.map(r => getComputedStyle(r.querySelector("td:last-child")).display),
      sideways: document.querySelector(".workspace-file-manager .file-table-wrap").scrollWidth > document.querySelector(".workspace-file-manager .file-table-wrap").clientWidth + 2,
      iconOnly: named.every(r => Array.from(r.querySelectorAll(".file-action-button > span")).every(s => getComputedStyle(s).display === "none")),
    };
  });
  console.log("     public page: " + JSON.stringify(facts));
  check("the bar is two rows", facts.toolbar >= 80 && facts.toolbar <= 130, true);
  check("an actions cell is a table cell again", facts.cells, ["table-cell", "table-cell"]);
  check("the list needs no sideways scrolling", facts.sideways, false);
  check("the actions are icons you can hit", facts.iconOnly, true);
  await v.screenshot({ path: "phone-public-page.png" });

  // --- The create menu: a sheet with two forms
  await v.locator(".workspace-file-manager .toolbar-menu > .toolbar-menu-trigger").tap();
  await sleep(500);
  const sheet = await v.evaluate(() => {
    const p = document.querySelector(".menu-sheet");
    if (!p) { return null; }
    const r = p.getBoundingClientRect();
    return {
      box: Math.round(r.left) + "," + Math.round(r.top) + " " + Math.round(r.width) + "x" + Math.round(r.height),
      forms: p.querySelectorAll("form").length,
      fieldFont: getComputedStyle(p.querySelector("input[name='name']")).fontSize,
      title: (p.querySelector(".menu-sheet-head strong") || {}).textContent,
    };
  });
  console.log("     sheet: " + JSON.stringify(sheet));
  check("the create menu is a sheet over the whole screen", sheet && sheet.box, "0,0 " + PHONE.width + "x" + PHONE.height);
  check("with a form for a folder and one for a file", sheet && sheet.forms, 2);
  check("its fields are 16 pixels, so the phone does not zoom in on them", sheet && sheet.fieldFont, "16px");
  await v.screenshot({ path: "phone-public-menu.png" });

  // Tapping into a field is no choice: the sheet stays.
  await v.locator(".menu-sheet input[name='name']").first().tap();
  await sleep(300);
  check("tapping into a field leaves the sheet open", await v.locator(".menu-sheet").count(), 1);

  // Pressing the button of a form is no choice either. With the field still empty the browser refuses
  // to send it and wants to show its note beside the field - for which the sheet has to still be there.
  // (Sending a name would not show anything: a visitor may not create folders here, the server
  // answers 403, so this page's create menu is checked for how it behaves and not for what it creates.)
  await v.locator(".menu-sheet form").first().locator("button[type='submit']").tap();
  await sleep(500);
  const afterPress = await v.evaluate(() => ({
    sheets: document.querySelectorAll(".menu-sheet").length,
    refused: !!document.querySelector(".menu-sheet input[name='name']:invalid"),
  }));
  check("pressing a form's button does not close the sheet", afterPress.sheets, 1);
  check("the empty required field is what stopped it", afterPress.refused, true);
  await v.locator(".menu-sheet .menu-sheet-close").tap();
  await sleep(400);
  check("the cross closes it", await v.locator(".menu-sheet").count(), 0);

  const removed = await removeAll();
  check("the workspace is cleaned up", removed >= 1, true);
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
