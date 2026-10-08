// What the file manager can do with what is in it: go up a level, rename an entry, download a
// whole selection, pack one. Each of these was missing or only reachable through a browser prompt,
// and a prompt is something a browser is allowed to refuse.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const FOLDER = "Werkstatt";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1400, height: 950 }, acceptDownloads: true });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  // Nothing may fall back to a browser prompt any more - if one opens, the test says so.
  let prompts = [];
  page.on("dialog", async d => { prompts.push(d.type()); await d.dismiss(); });

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
    return { id: hit ? hit.id : null, token };
  });

  // A folder of its own, so the test does not trip over what other runs left lying around.
  await page.evaluate(async ([id, token, folder]) => {
    await fetch("/api/files/" + id + "/mkdir", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ path: "/", name: folder }),
    });
    for (const name of ["eins.txt", "zwei.txt"]) {
      const data = new FormData();
      data.append("path", "/" + folder);
      data.append("file", new Blob(["content of " + name], { type: "text/plain" }), name);
      await fetch("/api/files/" + id + "/upload", {
        method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": token },
      });
    }
  }, [where.id, where.token, FOLDER]);

  await page.goto(BASE + "/connect/" + where.id, { waitUntil: "networkidle" });
  await sleep(2500);

  // --- The way up
  const upAtRoot = await page.evaluate(() => {
    const button = document.querySelector('[data-file-action="up"]');
    return { there: !!button, disabled: button ? button.disabled : null };
  });
  check("the bar has a way up", upAtRoot.there, true);
  check("and it is dead at the root", upAtRoot.disabled, true);

  await page.locator(".file-table tr").filter({ hasText: FOLDER }).first().locator("button").first().click();
  await sleep(2000);
  const inFolder = await page.evaluate(() => ({
    path: (document.querySelector(".file-path-input") || {}).value,
    upDisabled: document.querySelector('[data-file-action="up"]').disabled,
  }));
  console.log("     in the folder: " + JSON.stringify(inFolder));
  check("inside a folder it is alive", inFolder.upDisabled, false);
  await page.click('[data-file-action="up"]');
  await sleep(1800);
  check("and it goes up one level", await page.evaluate(() => (document.querySelector(".file-path-input") || {}).value), "/");

  // --- Renaming
  await page.locator(".file-table tr").filter({ hasText: FOLDER }).first().locator("button").first().click();
  await sleep(2000);
  const row = page.locator(".file-table tr").filter({ hasText: "eins.txt" }).first();
  const renameButton = row.locator('button[title="' + "Rename" + '"], button[title="Umbenennen"]').first();
  check("a row offers renaming", await renameButton.count() > 0, true);
  await renameButton.click();
  await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 10000 });
  await sleep(400);
  const prefilled = await page.evaluate(() => {
    const field = document.getElementById("name-dialog-input");
    return { value: field.value, selected: field.value.slice(field.selectionStart, field.selectionEnd) };
  });
  console.log("     the sheet: " + JSON.stringify(prefilled));
  check("prefilled with the current name", prefilled.value, "eins.txt");
  // The extension stays out of the selection: renaming means the name, not the kind of file.
  check("and the extension is not selected", prefilled.selected, "eins");
  await page.fill("#name-dialog-input", "erste-datei.txt");
  await page.click("#name-dialog button[type='submit']");
  await sleep(2500);
  const afterRename = await page.evaluate(() => Array.from(document.querySelectorAll(".file-table tbody tr"))
    .map(r => r.innerText.trim().split(/\s+/)[0]).filter(Boolean));
  console.log("     after: " + JSON.stringify(afterRename));
  check("the entry carries the new name", afterRename.includes("erste-datei.txt"), true);
  check("and the old one is gone", afterRename.includes("eins.txt"), false);

  // --- Downloading a selection
  await page.evaluate(() => {
    Array.from(document.querySelectorAll(".file-table tbody .file-select-entry")).forEach(box => {
      box.checked = true;
      box.dispatchEvent(new Event("change", { bubbles: true }));
    });
  });
  await sleep(600);
  const download = page.waitForEvent("download", { timeout: 20000 }).catch(() => null);
  await page.evaluate(() => document.querySelector('[data-file-action="download-selected"]').click());
  const file = await download;
  const name = file ? await file.suggestedFilename() : null;
  console.log("     download: " + JSON.stringify(name));
  check("a selection can be downloaded", !!file, true);
  // Several files come as one archive, named after the folder rather than after whichever file
  // happened to be first.
  check("as one archive named after the folder", name, FOLDER.toLowerCase() === FOLDER ? FOLDER + ".zip" : FOLDER + ".zip");

  // --- Packing
  await page.evaluate(() => document.querySelector('[data-file-action="zip"]').click());
  await page.waitForSelector("#name-dialog:not(.hidden)", { timeout: 10000 });
  const suggested = await page.evaluate(() => document.getElementById("name-dialog-input").value);
  console.log("     archive name: " + JSON.stringify(suggested));
  check("packing suggests the folder's name", suggested, FOLDER + ".zip");
  await page.click("#name-dialog button[type='submit']");
  await sleep(2500);
  const afterZip = await page.evaluate(() => Array.from(document.querySelectorAll(".file-table tbody tr"))
    .map(r => r.innerText.trim().split(/\s+/)[0]).filter(Boolean));
  console.log("     after packing: " + JSON.stringify(afterZip));
  check("the archive is in the folder", afterZip.includes(FOLDER + ".zip"), true);

  check("nothing fell back to a browser prompt", prompts, []);

  // --- Clean up
  await page.evaluate(async ([id, token, folder]) => {
    await fetch("/api/files/" + id + "/delete", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ paths: ["/" + folder] }),
    });
  }, [where.id, where.token, FOLDER]);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
