// Workspaces as a place in the file manager: visible, in a group of their own, writable - and
// not writable when the workspace says so.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "Pruefablage";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1400, height: 950 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1600);

  // --- Create a workspace
  await page.goto(BASE + "/workspaces/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"name\"]", NAME);
  await page.evaluate(() => document.querySelector("input[name='name']").form.requestSubmit());
  // Waiting for the address instead of a fixed pause: the redirect carries the id of the new
  // share, and reading it a moment too early left the cleanup with "undefined".
  await page.waitForURL(/\/workspaces\/[0-9a-f-]{36}/i, { timeout: 15000 }).catch(() => {});
  await sleep(1800);
  let shareId = (page.url().match(/\/workspaces\/([0-9a-f-]{36})/i) || [])[1];
  check("workspace created", typeof shareId === "string", true);

  // --- Does it show up as a place?
  const places = await page.evaluate(async () => {
    const r = await fetch("/", { credentials: "same-origin" });
    const text = await r.text();
    const hits = text.match(/const availableServers = (\[.*?\]);/s);
    if (!hits) return { failures: "no list" };
    const list = JSON.parse(hits[1]);
    return list.filter(s => (s.areaKind || "") === "workspace")
      .map(s => ({ name: s.name, readOnly: s.readOnly, source: s.areaSourceId }));
  });
  console.log("     workspace places: " + JSON.stringify(places));
  const mine = Array.isArray(places) ? places.find(a => a.name === "Workspaces/" + NAME) : null;
  check("appears as Workspaces/<name>", !!mine, true);
  check("writable while uploads are allowed", mine ? mine.readOnly : null, false);
  check("kennt seinen Workspace", mine ? mine.source : null, shareId);

  // --- Fetch the place's id and write into it
  const placeId = await page.evaluate(async (n) => {
    const r = await fetch("/", { credentials: "same-origin" });
    const text = await r.text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const hits = list.find(s => s.name === "Workspaces/" + n);
    return hits ? hits.id : null;
  }, NAME);

  // The token against foreign forms is a constant in the page, not on the window object.
  const token = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const hits = text.match(/const csrfToken = "([^"]+)"/);
    return hits ? hits[1] : "";
  });
  check("CSRF token found", token.length > 0, true);

  const write = async () => page.evaluate(async ([id, token]) => {
    const data = new FormData();
    data.append("path", "/");
    data.append("file", new Blob(["hallo"], { type: "text/plain" }), "check.txt");
    const r = await fetch("/api/files/" + id + "/upload", { method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": token } });
    return { status: r.status, text: (await r.text()).slice(0, 120) };
  }, [placeId, token]);

  const firstWrite = await write();
  console.log("     Schreiben allowed: " + JSON.stringify(firstWrite));
  check("upload succeeds", firstWrite.status < 400, true);

  const listing = await page.evaluate(async (id) => {
    const r = await fetch("/api/files/" + id + "/list?path=/", { credentials: "same-origin" });
    return (await r.text()).includes("check.txt");
  }, placeId);
  check("the file is in the place", listing, true);

  // --- Switch uploads off in the workspace
  await page.goto(BASE + "/workspaces/" + shareId, { waitUntil: "networkidle" });
  await sleep(800);
  const toggled = await page.evaluate(() => {
    const box = document.querySelector("[name='allowUploads']");
    if (!box) return false;
    box.checked = false;
    box.form.requestSubmit();
    return true;
  });
  check("uploads can be switched off", toggled, true);
  await sleep(1800);

  const afterwards = await page.evaluate(async (n) => {
    const r = await fetch("/", { credentials: "same-origin" });
    const text = await r.text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const hits = list.find(s => s.name === "Workspaces/" + n);
    return hits ? hits.readOnly : null;
  }, NAME);
  check("the place is read-only now", afterwards, true);

  const secondWrite = await write();
  console.log("     write refused: " + JSON.stringify(secondWrite));
  check("the upload is rejected", secondWrite.status >= 400, true);

  // --- The UI: its own group, and the way to the settings
  // It is opened where you see it: through the card on the home page.
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1200);
  const card = page.locator(".home2-workspaces-section .connection-choice").filter({ hasText: NAME });
  check("workspace card on the home page", await card.count(), 1);
  // Since the dissolution the gear is a button that opens the dialog - no longer a link to the
  // old page.
  check("the card shows the way to the settings",
    await card.locator("button[data-workspace-settings]").count() > 0, true);
  await card.locator(".connection-choice-open").click();
  await sleep(3000);
  const view = await page.evaluate(() => {
    const root = document.querySelector(".file-display");
    const selection = document.querySelector("[data-file-place-select]");
    const groups = selection ? Array.from(selection.querySelectorAll("optgroup")).map(g => g.label) : [];
    // What used to be a link to a page of its own is now a button that opens the dialog.
    const button = document.querySelector(".file-manager [data-workspace-settings]");
    const upload = document.querySelector(".file-upload-button");
    return {
      readOnlyPlace: root ? root.classList.contains("file-display--readonly") : null,
      groups,
      button: !!button,
      uploadVisible: upload ? getComputedStyle(upload).display !== "none" : null,
      deleteVisible: Array.from(document.querySelectorAll(".file-action-delete"))
        .some(el => getComputedStyle(el).display !== "none"),
    };
  });
  console.log("     Oberflaeche: " + JSON.stringify(view));
  check("place marked as read-only", view.readOnlyPlace, true);
  check("the upload button is gone", view.uploadVisible, false);
  check("the way to the workspace settings", view.button, true);
  check("its own group in the select", view.groups.includes("Workspaces"), true);
  check("delete in the row is gone", view.deleteVisible, false);
  await page.screenshot({ path: "ws-1-place.png" });

  // --- Clean up
  await page.goto(BASE + "/workspaces/" + shareId, { waitUntil: "networkidle" });
  await page.evaluate(() => {
    const form = document.querySelector("form[action$='/delete']");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  });
  await sleep(1500);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
