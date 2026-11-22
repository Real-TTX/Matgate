// A phone zooms into a field it is about to type in when the field's text is smaller than 16px - iOS does,
// in the browser and in the installed app - and it stays zoomed afterwards: the page is suddenly too wide,
// the dialog half off the screen. Quick connect did that the moment it opened, because it puts the cursor
// into its first field. No browser here zooms like an iPhone, so what is checked is the cause: on a touch
// screen no field anywhere has text below 16px. With a mouse the forms keep the sizes they were drawn with.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

// Every field one can type in or choose from, with the size of its text - the ones too small for a phone.
const small = page => page.evaluate(() => [...document.querySelectorAll("input, select, textarea")]
  .filter(field => !["checkbox", "radio", "range", "color", "hidden", "file", "submit", "button"].includes(field.type))
  .map(field => ({ field: field.name || field.className || field.type, size: parseFloat(getComputedStyle(field).fontSize) }))
  .filter(entry => entry.size < 16)
  .map(entry => entry.field + " " + entry.size));
const fields = page => page.evaluate(() => [...document.querySelectorAll("input, select, textarea")]
  .filter(field => !["checkbox", "radio", "range", "color", "hidden", "file", "submit", "button"].includes(field.type)).length);

(async () => {
  let b = await chromium.launch({ headless: true });
  const phone = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, hasTouch: true, isMobile: true });
  const page = await phone.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });

  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  check("this is a touch screen", await page.evaluate(() => matchMedia("(pointer: coarse)").matches), true);
  check("signing in: no field too small", [await fields(page) > 1, await small(page)], [true, []]);
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1200);

  // Quick connect: the dialog that zoomed. Every protocol shows other fields.
  await page.evaluate(() => document.querySelector("[data-home2-more]").click());
  await page.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  check("a phone that is no iPhone keeps its viewport as it was - there the same line would take pinching away", await page.evaluate(() => /maximum-scale/.test(document.querySelector("meta[name=viewport]").content)), false);
  // Opening it does not call the keyboard: a phone shoves and zooms the page to make room for one.
  check("on a touch screen the dialog opens without the cursor in a field", await page.evaluate(() => /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName)), false);
  const protocols = await page.evaluate(() => [...document.querySelectorAll("[data-home2-qc-proto]")].map(chip => chip.dataset.home2QcProto));
  const tooSmall = [];
  for (const protocol of protocols) {
    await page.evaluate(name => document.querySelector("[data-home2-qc-proto='" + name + "']").click(), protocol);
    (await small(page)).forEach(entry => tooSmall.push(protocol + ": " + entry));
  }
  check("quick connect: no field too small, whatever the protocol", [protocols.length > 3, tooSmall], [true, []]);
  await page.evaluate(() => document.querySelector("[data-home2-qc-proto='rdp']").click());
  check("the field the dialog puts the cursor into", await page.evaluate(() => getComputedStyle(document.querySelector("[data-home2-qc-host]")).fontSize), "16px");
  check("the dialog still fits the screen", await page.evaluate(() => {
    const box = document.querySelector("[data-home2-proto-dialog]").getBoundingClientRect();
    return box.left >= 0 && box.right <= innerWidth + 1 && document.documentElement.scrollWidth <= innerWidth + 1;
  }), true);
  await page.evaluate(() => document.querySelector("[data-home2-proto-close]").click());
  check("the search of the home page", await small(page), []);

  // The pages with the forms.
  for (const [name, path] of [
    ["a new connection", "/admin/servers/new"],
    ["the account", "/account"],
    ["the users", "/admin?tab=users"],
    ["the settings", "/admin?tab=settings"],
    ["a new workspace", "/workspaces/new"],
  ]) {
    const answer = await page.goto(BASE + path, { waitUntil: "networkidle" });
    await sleep(300);
    check(name + ": no field too small", [answer.status(), await fields(page) > 0, await small(page)], [200, true, []]);
  }

  // The file manager's fields and menus come and go with its script.
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(800);
  check("the home page as it stands", await small(page), []);
  const state = await phone.storageState();
  await phone.close();

  // With a mouse nothing changes.
  const desk = await b.newContext({ viewport: { width: 1300, height: 860 }, storageState: state });
  const wide = await desk.newPage();
  await wide.goto(BASE + "/", { waitUntil: "networkidle" });
  await wide.evaluate(() => document.querySelector("[data-home2-more]").click());
  await wide.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  check("with a mouse the cursor stands in the first field, and the page may be scaled as ever", await wide.evaluate(() => [document.activeElement === document.querySelector("[data-home2-qc-host]"), /maximum-scale/.test(document.querySelector("meta[name=viewport]").content)]), [true, false]);
  check("with a mouse the form keeps its own size", await wide.evaluate(() => [matchMedia("(pointer: coarse)").matches, getComputedStyle(document.querySelector("[data-home2-qc-host]")).fontSize]), [false, "13px"]);

  // An iPhone zooms into a field it focuses, whatever the field looks like. There the page has a largest scale.
  const apple = await b.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, hasTouch: true, isMobile: true, storageState: state,
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1" });
  const iphone = await apple.newPage();
  await iphone.goto(BASE + "/", { waitUntil: "networkidle" });
  check("on an iPhone the page cannot be zoomed into a field", await iphone.evaluate(() => document.querySelector("meta[name=viewport]").content), "width=device-width, initial-scale=1, viewport-fit=cover, maximum-scale=1");

  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
