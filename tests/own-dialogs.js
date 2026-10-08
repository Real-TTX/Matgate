// The application asks its questions itself. A browser's own confirm/prompt/alert box cannot be
// styled, cannot be told which word belongs on the button, can be switched off, and in an installed
// app it reads like something went wrong. This script walks every place that used to open one and
// fails if the browser's dialog event ever fires - and checks that the application's own sheet
// opened in its place, that "no" really means no, and that "yes" really deletes.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const FOLDER = "Eigene-Dialoge";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  // Any browser dialog at all is a failure - and it is dismissed so the page does not hang on it.
  const browserDialogs = [];
  page.on("dialog", async d => { browserDialogs.push(d.type() + ": " + d.message()); await d.dismiss(); });

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(2000);

  const where = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const token = (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
    const hit = list.find(s => (s.areaKind || "") === "user");
    return { id: hit ? hit.id : null, token, kinds: list.filter(s => s.areaKind).map(s => s.areaKind) };
  });
  // The session folder is not a place any more - it belongs to the session, not to the list.
  check("a session folder is not offered as a place", where.kinds.includes("session"), false);

  const upload = (path, name) => page.evaluate(async ([id, token, path, name]) => {
    const data = new FormData();
    data.append("path", path);
    data.append("file", new Blob(["content of " + name], { type: "text/plain" }), name);
    await fetch("/api/files/" + id + "/upload", {
      method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": token },
    });
  }, [where.id, where.token, path, name]);
  await page.evaluate(async ([id, token, folder]) => {
    await fetch("/api/files/" + id + "/mkdir", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ path: "/", name: folder }),
    });
  }, [where.id, where.token, FOLDER]);
  for (const name of ["a.txt", "b.txt", "c.txt", "d.txt"]) { await upload("/" + FOLDER, name); }

  await page.goto(BASE + "/connect/" + where.id, { waitUntil: "networkidle" });
  await sleep(2500);
  await page.locator(".file-table tr").filter({ hasText: FOLDER }).first().locator("button").first().click();
  await sleep(2000);

  const rowNames = () => page.evaluate(() => Array.from(document.querySelectorAll(".file-table tbody tr"))
    .map(r => r.innerText.trim().split(/\s+/)[0]).filter(n => n && n !== ".."));
  const sheetOpen = () => page.evaluate(() => {
    const d = document.getElementById("confirm-dialog");
    return !!d && !d.classList.contains("hidden");
  });

  // --- Deleting one entry: the sheet asks, "no" keeps it, "yes" deletes it
  const row = name => page.locator(".file-table tr").filter({ hasText: name }).first();
  await row("a.txt").locator("button.danger, button[title='Delete'], button[title='Löschen']").first().click();
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  await sleep(300);
  const asked = await page.evaluate(() => ({
    text: (document.querySelector("#confirm-dialog [data-confirm-text]") || {}).textContent || "",
    focused: document.activeElement ? document.activeElement.hasAttribute("data-confirm-cancel") : false,
  }));
  console.log("     the question: " + JSON.stringify(asked));
  check("it names what is about to go", /a\.txt/.test(asked.text), true);
  // The safe button has the focus: a stray Enter must not be what empties a folder.
  check("the safe button has the focus", asked.focused, true);
  await page.click("#confirm-dialog [data-confirm-cancel]");
  await sleep(500);
  check("closing the sheet means no", (await rowNames()).includes("a.txt"), true);
  check("and it is gone from the screen", await sheetOpen(), false);

  await row("a.txt").locator("button.danger, button[title='Delete'], button[title='Löschen']").first().click();
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  await page.keyboard.press("Escape");
  await sleep(400);
  check("Escape means no as well", (await rowNames()).includes("a.txt"), true);

  await row("a.txt").locator("button.danger, button[title='Delete'], button[title='Löschen']").first().click();
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  await page.click("#confirm-dialog [data-confirm-ok]");
  await sleep(2200);
  check("confirming deletes the entry", (await rowNames()).includes("a.txt"), false);

  // --- Deleting a selection
  await page.evaluate(() => {
    ["b.txt", "c.txt"].forEach(name => {
      const tr = Array.from(document.querySelectorAll(".file-table tbody tr")).find(r => r.innerText.includes(name));
      const box = tr && tr.querySelector(".file-select-entry");
      if (box) { box.checked = true; box.dispatchEvent(new Event("change", { bubbles: true })); }
    });
  });
  await sleep(500);
  await page.evaluate(() => document.querySelector('[data-file-action="delete-selected"]').click());
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  const selectionText = await page.evaluate(() => document.querySelector("#confirm-dialog [data-confirm-text]").textContent);
  check("the selection's question counts what goes", /^2\b/.test(selectionText.trim()), true);
  await page.click("#confirm-dialog [data-confirm-ok]");
  await sleep(2200);
  const left = await rowNames();
  console.log("     left: " + JSON.stringify(left));
  check("both are deleted, the rest stays", left.includes("b.txt") || left.includes("c.txt"), false);
  check("and the unselected one is untouched", left.includes("d.txt"), true);

  // --- Where to move or copy to
  await page.evaluate(() => {
    const tr = Array.from(document.querySelectorAll(".file-table tbody tr")).find(r => r.innerText.includes("d.txt"));
    const box = tr && tr.querySelector(".file-select-entry");
    if (box) { box.checked = true; box.dispatchEvent(new Event("change", { bubbles: true })); }
  });
  await sleep(400);
  await page.evaluate(() => document.querySelector('[data-file-action="copy"]').click());
  await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 10000 });
  await sleep(400);
  const dest = await page.evaluate(() => {
    const f = document.getElementById("name-dialog-input");
    return { value: f.value, selected: f.value.slice(f.selectionStart, f.selectionEnd) };
  });
  console.log("     destination sheet: " + JSON.stringify(dest));
  check("asks where to, prefilled with the current folder", dest.value, "/" + FOLDER);
  // A path is selected as a whole - it has no extension to keep out of the selection.
  check("and selects all of it", dest.selected, "/" + FOLDER);
  await page.fill("#name-dialog-input", "/");
  await page.click("#name-dialog button[type='submit']");
  await sleep(2200);
  await page.click('[data-file-action="up"]');
  await sleep(1800);
  check("the copy arrived at the destination", (await rowNames()).includes("d.txt"), true);

  // --- Forms that confirm: a user is deleted from the user list
  await page.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
  await page.fill("input[name='username']", "DialogTest");
  await page.fill("input[name='password']", "test-only-pw-1234");
  await page.locator("button[type='submit']:visible").last().click();
  await sleep(1800);
  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  const userRow = page.locator("tr").filter({ hasText: "DialogTest" }).first();
  check("the test user exists", await userRow.count() > 0, true);
  await userRow.locator("form[action*='/delete'] button").first().click();
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 }).catch(() => {});
  check("a delete form asks in the application's sheet", await sheetOpen(), true);
  await page.keyboard.press("Escape");
  await sleep(500);
  check("declining keeps the user", await page.locator("tr").filter({ hasText: "DialogTest" }).count() > 0, true);
  await userRow.locator("form[action*='/delete'] button").first().click();
  await page.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
  await page.click("#confirm-dialog [data-confirm-ok]");
  await sleep(2200);
  check("confirming removes the user", await page.locator("tr").filter({ hasText: "DialogTest" }).count(), 0);

  // --- The point of all of it
  console.log("     browser dialogs: " + JSON.stringify(browserDialogs));
  check("the browser's own dialogs never opened", browserDialogs, []);

  // --- Clean up
  await page.goto(BASE + "/connect/" + where.id, { waitUntil: "networkidle" });
  await sleep(2000);
  await page.evaluate(async ([id, token, folder]) => {
    await fetch("/api/files/" + id + "/delete", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ paths: ["/" + folder, "/d.txt"] }),
    });
  }, [where.id, where.token, FOLDER]);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
