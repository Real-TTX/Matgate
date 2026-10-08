// The application's own colour picker, in place of the operating system's. What is checked is what a
// native one used to give for free and a rebuilt one has to earn: that dragging works even when the
// pointer leaves the square, that the keyboard can do everything the mouse can, that a wrong entry
// cannot leave a colour on the page that the box does not show, that "cancel" really undoes it all
// (including the "own colour" switch the first change turns on), that Enter does not send the whole
// settings form - and that on a phone the sheet fills the screen.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const ACCOUNT = BASE + "/account?tab=profile";
const ACCENT = { toggle: "accentOwn", value: "accentColor" };
const ACCENT2 = { toggle: "accent2Own", value: "accentColor2" };

// Sends the settings form and waits for the page that follows it - not for the clock.
const saveAccount = page => Promise.all([
  page.waitForNavigation({ waitUntil: "load", timeout: 20000 }),
  page.evaluate(() => { window.setTimeout(() => document.querySelector("[name='backgroundOwn']").form.requestSubmit(), 0); }),
]);

const field = (page, group) => page.evaluate(g => ({
  value: document.querySelector("[name='" + g.value + "']").value,
  own: document.querySelector("[name='" + g.toggle + "']").checked,
}), group);

const sheetHex = page => page.locator("[data-colour-hex]").inputValue();
const sheetOpen = page => page.evaluate(() => !document.getElementById("colour-dialog").classList.contains("hidden"));
const accentToken = page => page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue("--accent").trim().toLowerCase());
const setHue = (page, degrees) => page.evaluate(d => {
  const slider = document.querySelector("[data-colour-hue]");
  slider.value = String(d);
  slider.dispatchEvent(new Event("input", { bubbles: true }));
}, degrees);

// Presses on the square, then drags to a point that may lie OUTSIDE it - the pointer is captured,
// so the colour has to follow to the nearest edge instead of stopping where the square ends.
async function dragSquare(page, toX, toY) {
  const box = await page.locator("[data-colour-area]").boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + toX * box.width, box.y + toY * box.height, { steps: 6 });
  await page.mouse.up();
}

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const ctx = await b.newContext({ viewport: { width: 1300, height: 900 } });
  const page = await ctx.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  const browserDialogs = [];
  page.on("dialog", async d => { browserDialogs.push(d.type()); await d.dismiss(); });

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1200);

  // Start from a known place: no colour of one's own anywhere, saved.
  await page.goto(ACCOUNT, { waitUntil: "networkidle" });
  await page.evaluate(() => ["accentOwn", "accent2Own", "backgroundOwn"].forEach(n => { document.querySelector("[name='" + n + "']").checked = false; }));
  await saveAccount(page);
  await sleep(500);

  // ================= The field
  check("no native colour input is left", await page.locator("input[type='color']").count(), 0);
  check("three colour fields, each with a button that opens the sheet", await page.locator("[data-colour-open]").count(), 3);
  const themeAccent = (await field(page, ACCENT)).value;
  const chipDot = await page.evaluate(() => document.querySelector(".colour-chip-dot").style.getPropertyValue("--dot").trim());
  check("the button shows the colour it stands for", chipDot, themeAccent);
  check("and the sheet is not open yet", await sheetOpen(page), false);

  // ================= Opening
  await page.locator("[data-colour-open]").first().click();
  await sleep(500);
  const opened = await page.evaluate(() => {
    const sheet = document.getElementById("colour-dialog");
    const legend = document.querySelector("[data-colour-field] ").closest("fieldset").querySelector("legend");
    return {
      open: !sheet.classList.contains("hidden"),
      outsideTheForm: sheet.closest("form") === null && sheet.parentElement === document.body,
      title: sheet.querySelector("[data-colour-title]").textContent.trim(),
      legend: legend.textContent.trim(),
      dropper: !sheet.querySelector("[data-colour-eyedropper]").hidden,
      dropperKnown: "EyeDropper" in window,
    };
  });
  check("the sheet opens", opened.open, true);
  // Out of the form: Enter in its hex box must not send the whole settings form.
  check("outside the settings form", opened.outsideTheForm, true);
  check("it is titled after the colour being set", opened.title, opened.legend);
  check("the eyedropper is offered exactly where the browser has one", opened.dropper, opened.dropperKnown);
  check("it opens on the colour that was there", await sheetHex(page), themeAccent);
  check("opening alone does not switch the colour on", (await field(page, ACCENT)).own, false);

  // ================= The square, with the mouse
  await setHue(page, 240);
  await dragSquare(page, 1.4, -0.4);           // beyond the top right corner
  check("top right of the square is the pure hue", await sheetHex(page), "#0000ff");
  check("the value follows", (await field(page, ACCENT)).value, "#0000ff");
  check("and the first change switches the colour on", (await field(page, ACCENT)).own, true);
  await dragSquare(page, -0.4, -0.4);          // beyond the top left corner
  check("top left is white", await sheetHex(page), "#ffffff");
  await dragSquare(page, -0.4, 1.4);           // beyond the bottom left corner
  check("the bottom is black", await sheetHex(page), "#000000");

  // ================= The keyboard
  await dragSquare(page, 1.4, -0.4);
  await page.locator("[data-colour-area]").focus();
  for (let i = 0; i < 5; i++) { await page.keyboard.press("Shift+ArrowDown"); }
  check("Shift+arrow moves the square ten percent at a time", await sheetHex(page), "#000080");
  await page.keyboard.press("ArrowUp");
  const nudged = await sheetHex(page);
  check("an arrow alone moves one percent", /^#0000[0-9a-f]{2}$/.test(nudged) && nudged !== "#000080", true);
  await dragSquare(page, 1.4, -0.4);
  await page.locator("[data-colour-hue]").focus();
  await page.keyboard.press("End");
  check("the hue slider answers the keyboard", await sheetHex(page), "#ff0000");

  // ================= The hex box
  await page.fill("[data-colour-hex]", "#336699");
  check("typing a colour sets it", (await field(page, ACCENT)).value, "#336699");
  await sleep(900);
  check("and the page behind follows, live", await accentToken(page), "#336699");
  await page.fill("[data-colour-hex]", "#fa0");
  check("the short form is understood", (await field(page, ACCENT)).value, "#ffaa00");
  await page.fill("[data-colour-hex]", "zzz");
  check("nonsense changes nothing", (await field(page, ACCENT)).value, "#ffaa00");
  check("and the box says so", await page.locator("[data-colour-hex]").getAttribute("aria-invalid"), "true");
  await page.keyboard.press("Tab");
  const afterLeaving = await page.evaluate(() => ({
    text: document.querySelector("[data-colour-hex]").value,
    problemShown: !document.querySelector("[data-colour-problem]").classList.contains("hidden"),
  }));
  check("leaving the box puts the real colour back", afterLeaving.text, "#ffaa00");
  check("and says once what went wrong", afterLeaving.problemShown, true);

  // ================= Tab stays inside
  await page.focus("[data-colour-cancel]");
  await page.keyboard.press("Tab");
  check("Tab does not wander off into the page behind", await page.evaluate(() => document.getElementById("colour-dialog").contains(document.activeElement)), true);

  // ================= Done keeps it
  await page.click("[data-colour-done]");
  await sleep(400);
  check("Done closes the sheet", await sheetOpen(page), false);
  check("and keeps the colour", await field(page, ACCENT), { value: "#ffaa00", own: true });
  check("the button now shows it", await page.evaluate(() => document.querySelector(".colour-chip-dot").style.getPropertyValue("--dot").trim()), "#ffaa00");
  check("the focus goes back to the button that opened it", await page.evaluate(() => document.activeElement.hasAttribute("data-colour-open")), true);
  // What the page shows for it is NOT the colour itself: a pale amber cannot carry white text, so the
  // server darkens it and keeps the hue. Remembered here, because that - and not the raw value - is
  // what Cancel has to bring back.
  await sleep(900);
  const keptToken = await accentToken(page);
  check("the page shows it, corrected for readability", keptToken !== themeAccent && /^#[0-9a-f]{6}$/.test(keptToken), true);

  // ================= Cancel undoes it all
  await page.locator("[data-colour-open]").first().click();
  await sleep(400);
  check("reopened on the kept colour", await sheetHex(page), "#ffaa00");
  await page.fill("[data-colour-hex]", "#123456");
  check("changed while open", (await field(page, ACCENT)).value, "#123456");
  await sleep(900);
  check("and the page behind follows while it is open", await accentToken(page), "#123456");
  await page.click("[data-colour-cancel]");
  await sleep(500);
  check("Cancel puts the colour back", await field(page, ACCENT), { value: "#ffaa00", own: true });
  await sleep(900);
  check("and the page behind with it", await accentToken(page), keptToken);

  // The harder case: a colour that was NOT switched on. Changing it switches it on - Cancel has to
  // switch it off again, or the person ends up with a colour they never chose.
  const before2 = await field(page, ACCENT2);
  check("the second colour starts as the theme's", before2.own, false);
  await page.locator("[data-colour-open]").nth(1).click();
  await sleep(400);
  await page.locator("[data-colour-area]").focus();
  await page.keyboard.press("Shift+ArrowLeft");
  check("a change switches it on", (await field(page, ACCENT2)).own, true);
  await page.keyboard.press("Escape");
  await sleep(500);
  check("Escape closes the sheet", await sheetOpen(page), false);
  check("and undoes the change, the switch included", await field(page, ACCENT2), before2);

  // ================= Enter does not send the settings form
  await page.evaluate(() => { window.__stillHere = true; });
  await page.locator("[data-colour-open]").first().click();
  await sleep(400);
  await page.fill("[data-colour-hex]", "#445566");
  await page.press("[data-colour-hex]", "Enter");
  await sleep(1200);
  check("Enter in the hex box did not reload the page", await page.evaluate(() => window.__stillHere === true), true);
  check("it counts as Done", await sheetOpen(page), false);
  check("and keeps the entry", (await field(page, ACCENT)).value, "#445566");

  // ================= What is chosen is what is saved
  await saveAccount(page);
  await sleep(500);
  check("after saving, the colour is still there", await field(page, ACCENT), { value: "#445566", own: true });
  check("and the button shows it", await page.evaluate(() => document.querySelector(".colour-chip-dot").style.getPropertyValue("--dot").trim()), "#445566");

  // ================= A phone
  const phoneCtx = await b.newContext({
    viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true, storageState: await ctx.storageState(),
  });
  const phone = await phoneCtx.newPage();
  phone.on("pageerror", e => { console.log("JS ERROR (phone): " + e.message); failures.push("JS error phone"); });
  phone.on("dialog", async d => { browserDialogs.push("phone " + d.type()); await d.dismiss(); });
  await phone.goto(ACCOUNT, { waitUntil: "networkidle" });
  await sleep(800);
  await phone.locator("[data-colour-open]").first().click();
  await sleep(600);
  const onPhone = await phone.evaluate(() => {
    const sheet = document.getElementById("colour-dialog");
    const box = sheet.getBoundingClientRect();
    const area = sheet.querySelector("[data-colour-area]").getBoundingClientRect();
    const done = sheet.querySelector("[data-colour-done]").getBoundingClientRect();
    const title = sheet.querySelector("[data-colour-title]").getBoundingClientRect();
    const top = document.elementFromPoint(Math.round(title.left + title.width / 2), Math.round(title.top + title.height / 2));
    return {
      full: Math.round(box.width) >= window.innerWidth - 1 && Math.round(box.height) >= window.innerHeight - 1,
      areaWidth: Math.round(area.width), areaHeight: Math.round(area.height), screen: window.innerWidth,
      doneAtThumb: done.top > window.innerHeight * 0.7,
      titleOnTop: !!(top && top.closest("#colour-dialog")),
    };
  });
  console.log("     phone: " + JSON.stringify(onPhone));
  check("on a phone the sheet fills the screen", onPhone.full, true);
  check("the square takes the width the screen gives it", onPhone.areaWidth >= onPhone.screen - 40, true);
  check("and is big enough to hit with a finger", onPhone.areaHeight >= 250, true);
  check("the way out is at the bottom, where the thumb is", onPhone.doneAtThumb, true);
  check("the app bar does not cover the heading", onPhone.titleOnTop, true);

  const startHex = await phone.locator("[data-colour-hex]").inputValue();
  const box = await phone.locator("[data-colour-area]").boundingBox();
  await phone.touchscreen.tap(box.x + box.width * 0.25, box.y + box.height * 0.3);
  await sleep(300);
  const tapped = await phone.locator("[data-colour-hex]").inputValue();
  console.log("     tapped: " + startHex + " -> " + tapped);
  check("a tap on the square picks a colour", /^#[0-9a-f]{6}$/.test(tapped) && tapped !== startHex, true);
  check("and the value follows it", (await phone.evaluate(() => document.querySelector("[name='accentColor']").value)), tapped);
  await phone.click("[data-colour-cancel]");
  await sleep(400);
  check("Cancel on the phone puts it back", (await phone.evaluate(() => document.querySelector("[name='accentColor']").value)), "#445566");
  await phone.screenshot({ path: "colour-picker-phone.png" });
  await phoneCtx.close();

  // ================= Put the account back as it was found
  await page.goto(ACCOUNT, { waitUntil: "networkidle" });
  await page.evaluate(() => ["accentOwn", "accent2Own", "backgroundOwn"].forEach(n => { document.querySelector("[name='" + n + "']").checked = false; }));
  await saveAccount(page);

  check("the browser's own dialogs never opened", browserDialogs, []);
  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
