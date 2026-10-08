// On a phone a dialog takes the whole screen. A frame of unused screen around a document is the
// one thing a small display cannot afford - the PDF preview used to lose a quarter of its height
// to a border, rounded corners and two rows of labelled buttons.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const PHONE = { width: 390, height: 844 };
// A valid one-page PDF, small enough to keep in the test.
const PDF = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
  + "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n"
  + "4 0 obj<</Length 44>>stream\nBT /F1 36 Tf 60 700 Td (Matgate PDF) Tj ET\nendstream\nendobj\n"
  + "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: PHONE, hasTouch: true, isMobile: true });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(2200);

  const own = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const token = (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
    const hit = list.find(s => (s.areaKind || "") === "user");
    return { id: hit ? hit.id : null, token };
  });
  await page.evaluate(async ([id, token, body]) => {
    const data = new FormData();
    data.append("path", "/");
    data.append("file", new Blob([body], { type: "application/pdf" }), "beispiel.pdf");
    await fetch("/api/files/" + id + "/upload", {
      method: "POST", body: data, credentials: "same-origin", headers: { "X-Matgate-Csrf": token },
    });
  }, [own.id, own.token, PDF]);

  // Straight to the place. That this address works for a place at all is the point of the fix
  // below it: it used to answer "No access", because only saved connections were looked up.
  const response = await page.goto(BASE + "/connect/" + own.id, { waitUntil: "networkidle" });
  await sleep(3000);
  const arrived = await page.evaluate(() => ({
    manager: !!document.querySelector(".file-table"),
    message: (document.querySelector("h1, h2") || {}).textContent || "",
  }));
  check("a place can be opened by its address", arrived.manager, true);
  if (!arrived.manager) { console.log("     instead: " + JSON.stringify(arrived.message)); }

  const fills = async (label, selector) => {
    const r = await page.evaluate(sel => {
      const el = document.querySelector(sel);
      if (!el) { return null; }
      const box = el.getBoundingClientRect();
      return {
        full: Math.round(box.width) >= window.innerWidth - 1 && Math.round(box.height) >= window.innerHeight - 1,
        w: Math.round(box.width), h: Math.round(box.height),
        screen: window.innerWidth + "x" + window.innerHeight,
      };
    }, selector);
    console.log("     " + label + ": " + JSON.stringify(r));
    return r;
  };

  // --- The PDF preview
  const row = page.locator(".file-table tr").filter({ hasText: "beispiel.pdf" }).first();
  check("the pdf is listed", await row.count() > 0, true);
  if (await row.count()) {
    await row.locator("button").first().click();
    await sleep(3000);
    const viewer = await fills("viewer", "#file-viewer-dialog");
    check("the preview fills the screen", (viewer || {}).full, true);
    const head = await page.evaluate(() => {
      const bar = document.querySelector("#file-viewer-dialog .viewer-tab-row");
      const labels = Array.from(document.querySelectorAll("#file-viewer-dialog .viewer-action > span"));
      const frame = document.querySelector("#file-viewer-dialog .document-stage iframe");
      return {
        headHeight: bar ? Math.round(bar.getBoundingClientRect().height) : null,
        labelsHidden: labels.length > 0 && labels.every(el => getComputedStyle(el).display === "none"),
        frameHeight: frame ? Math.round(frame.getBoundingClientRect().height) : null,
        fitsWidth: frame ? /#view=FitH/.test(frame.getAttribute("src") || "") : false,
      };
    });
    console.log("     head: " + JSON.stringify(head));
    // One row, not two: with the labels gone the name and the three buttons fit side by side.
    check("the head is a single row", head.headHeight !== null && head.headHeight <= 56, true);
    check("the action labels give way to icons", head.labelsHidden, true);
    check("the document gets the rest of the screen", head.frameHeight >= 740, true);
    check("and opens fitted to the width", head.fitsWidth, true);
    await page.screenshot({ path: "mobile-pdf.png" });
    await page.keyboard.press("Escape");
    await sleep(800);
  }

  // --- The small form sheet
  await page.selectOption("[data-file-place-select]", "__create-place__").catch(() => {});
  await sleep(1200);
  const sheet = await fills("new place", "#name-dialog");
  check("a form sheet fills the screen too", (sheet || {}).full, true);
  const heading = await page.evaluate(() => {
    const title = document.querySelector("#name-dialog h2");
    if (!title) { return null; }
    const box = title.getBoundingClientRect();
    const top = document.elementFromPoint(Math.round(box.left + box.width / 2), Math.round(box.top + box.height / 2));
    return { visible: box.top >= 0, ownTop: !!(top && top.closest("#name-dialog")) };
  });
  console.log("     heading: " + JSON.stringify(heading));
  // The app bar used to paint over the sheet's own heading - it sits in a stacking context of its
  // own, so no z-index on the sheet could get above it.
  check("its heading is not covered by the app bar", (heading || {}).ownTop, true);
  await page.screenshot({ path: "mobile-new-place.png" });

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
