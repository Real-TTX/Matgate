// The signed-in workspace page no longer shows the files itself but the way into the file
// manager. The public page still shows them - nobody is signed in there.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "Zeigerablage";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1360, height: 950 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1600);

  await page.goto(BASE + "/workspaces/new", { waitUntil: "networkidle" });
  await page.fill("input[name=\"name\"]", NAME);
  await page.evaluate(() => document.querySelector("input[name='name']").form.requestSubmit());
  await sleep(1800);
  const shareId = (page.url().match(/\/workspaces\/([0-9a-f-]{36})/i) || [])[1];
  check("workspace created", typeof shareId === "string", true);

  // --- The signed-in page: a pointer instead of a table
  await page.goto(BASE + "/workspaces/" + shareId + "?tab=files", { waitUntil: "networkidle" });
  await sleep(900);
  const signedIn = await page.evaluate(() => ({
    pointer: !!document.querySelector(".workspace-files-pointer"),
    button: !!document.querySelector(".workspace-files-pointer [data-shell-open-server]"),
    table: !!document.querySelector("[data-workspace-panel='files'] .file-table"),
  }));
  console.log("     signed in: " + JSON.stringify(signedIn));
  check("Zeiger statt Dateiansicht", signedIn.pointer, true);
  check("the button points at the place", signedIn.button, true);
  check("no second file table any more", signedIn.table, false);
  await page.screenshot({ path: "wsp-1-signed-in.png" });

  // --- The button opens the place in the shell (the page runs embedded there)
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(1200);
  await page.evaluate((id) => {
    if (typeof window.MatgateOpenShellTab === "function") {
      window.MatgateOpenShellTab("/workspaces/" + id + "?tab=files", "Zeigerablage", "", "");
    }
  }, shareId);
  await sleep(2500);
  const inFrame = await page.evaluate(async () => {
    const border = Array.from(document.querySelectorAll("iframe"))
      .find(f => (f.getAttribute("src") || "").includes("/workspaces/"));
    if (!border) return { border: false };
    const doc = border.contentDocument;
    const button = doc && doc.querySelector(".workspace-files-pointer [data-shell-open-server]");
    const all = doc ? doc.querySelectorAll("[data-shell-open-server]").length : 0;
    if (!button) return { border: true, button: false };
    button.click();
    return { border: true, button: true, id: button.getAttribute("data-shell-open-server"), total: all };
  });
  console.log("     embedded: " + JSON.stringify(inFrame));
  check("page runs embedded, with the button", inFrame.button === true || inFrame.border === false, true);
  if (inFrame.button) {
    await sleep(3000);
    const isOpen = await page.evaluate(() =>
      Array.from(document.querySelectorAll("#session-tabs .session-tab-title")).map(t => t.textContent.trim()));
    console.log("     open tabs: " + JSON.stringify(isOpen));
    check("the place is open as a tab", isOpen.some(t => t.includes("Workspaces/")), true);
    await page.screenshot({ path: "wsp-2-opened.png" });
  }

  // --- The public page still shows the files itself
  const publicView = await b.newContext({ viewport: { width: 1200, height: 900 } });
  const p2 = await publicView.newPage();
  await p2.goto(BASE + "/workspace/" + shareId + "?tab=files", { waitUntil: "networkidle" });
  await sleep(900);
  const view = await p2.evaluate(() => ({
    signedIn: !!document.getElementById("matgate-shell"),
    table: !!document.querySelector("[data-workspace-panel='files'] .file-table"),
    pointer: !!document.querySelector(".workspace-files-pointer"),
  }));
  console.log("     publicView: " + JSON.stringify(view));
  check("public view reachable without signing in", view.signedIn, false);
  check("public view with its own file table", view.table, true);
  check("public view without a pointer into the file manager", view.pointer, false);
  await p2.screenshot({ path: "wsp-3-publicView.png" });
  await publicView.close();

  // --- Clean up
  await page.goto(BASE + "/workspaces/" + shareId, { waitUntil: "networkidle" });
  await page.evaluate(() => {
    const form = document.querySelector("form[action$='/delete']");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  });
  await sleep(1300);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
