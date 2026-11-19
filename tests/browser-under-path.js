// The browser of quick connect where Matgate has no host names for websites - the way it runs where nobody
// set them up (the second Matgate of the test stack, port 18092). It is Matgate's own browser there as well:
// the page is fetched by Matgate and shown in the tab, under a path of Matgate's address - never the browser
// farm's picture of one. And it is a browser: its chip opens it at once, the address bar takes any address,
// and a link or a redirect to another machine is followed through Matgate - each website in a frame of its
// own under the path. Anywhere: 0.27.0 stopped at the edge of the network, which the owner did not want.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18092";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await context.newPage();
  const dialogs = [];
  page.on("dialog", async d => { dialogs.push(d.type()); await d.dismiss(); });
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const chips = await page.evaluate(() => [...document.querySelectorAll(".home2-quick-row [data-home2-qc]")].map(chip => chip.dataset.home2Qc + ":" + chip.textContent.trim()));
  check("the browser is one of the chips - where the website was, not in front", [chips.indexOf("website:Browser"), chips.some(chip => /^browser:/.test(chip)), chips[0]], [5, false, "rdp:RDP"]);

  // On a phone every chip is in sight: the row used to scroll sideways with no sign of it, and the browser
  // stood behind the edge of the screen.
  await page.setViewportSize({ width: 390, height: 844 });
  await sleep(300);
  check("on a phone all chips are on the screen, the browser among them", await page.evaluate(() => {
    const boxes = [...document.querySelectorAll(".home2-quick-row .home2-proto-chip")].map(chip => chip.getBoundingClientRect());
    const row = document.querySelector(".home2-quick-row");
    return [boxes.length, boxes.every(box => box.left >= 0 && box.right <= window.innerWidth && box.width > 40), row.scrollWidth <= row.clientWidth + 1];
  }), [9, true, true]);
  check("and their names are not cut off", await page.evaluate(() => [...document.querySelectorAll(".home2-quick-row .home2-proto-chip-name")].every(name => name.scrollWidth <= name.clientWidth + 1)), true);
  await page.setViewportSize({ width: 1300, height: 860 });
  await sleep(300);

  // The dialog (under "More") still has the browser, for a start address or a sign-in.
  await page.click(".home2-quick-row [data-home2-more]");
  await page.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  await page.click("[data-home2-qc-proto='website']");
  const asked = () => page.evaluate(() => ({
    label: document.querySelector("[data-home2-qc-hostlabel]").textContent,
    required: document.querySelector("[data-home2-qc-host]").required,
    focused: document.activeElement === document.querySelector("[data-home2-qc-host]"),
    button: document.querySelector("[data-home2-qc-submit]").textContent,
    fields: [...document.querySelectorAll("[data-home2-qc-form] input:not([type='hidden'])")].filter(field => field.offsetParent !== null).map(field => field.name),
  }));
  check("the dialog asks for the address to start with, and takes none as well", (({ focused, ...rest }) => rest)(await asked()), { label: "Start URL", required: false, button: "Open browser", fields: ["host", "username", "password"] });
  check("and says so", await page.evaluate(() => /empty/i.test(document.querySelector("[data-home2-qc-host]").placeholder)), true);
  await page.click("[data-home2-qc-proto='rdp']");
  check("another protocol has its own words again", [(await asked()).label, (await asked()).button, (await asked()).required], ["Host", "Connect", true]);
  await page.click("[data-home2-qc-proto='website']");

  // ---------------------------------------------------------------- started with an address, written without http://
  await page.fill("[data-home2-qc-host]", "site:8080");
  await page.click("[data-home2-qc-form] button[type='submit']");
  await page.waitForSelector(".website-frame", { timeout: 20000 });
  const frameOf = async shell => (await shell.$(".website-frame")).contentFrame();
  const shows = async (shell, wanted, selector = "#what") => {
    let text = "";
    for (let i = 0; i < 80 && text !== wanted; i++) {
      try { text = await (await frameOf(shell)).evaluate(sel => (document.querySelector(sel) || {}).textContent || "", selector); } catch { /* loading */ }
      if (text !== wanted) { await sleep(250); }
    }
    return text;
  };
  const frameId = async shell => ((await frameOf(shell)).url().match(/\/website\/[0-9a-f-]+\/([0-9a-f-]+)\/proxy\//) || [])[1] || "";
  const address = shell => shell.inputValue(".website-address");
  const type = async (shell, to) => {
    await shell.fill(".website-address", to);
    await shell.press(".website-address", "Enter");
  };

  check("the address opens in Matgate's own browser: the page itself, under a path of Matgate's", [
    await shows(page, "Fixture Home"),
    /^http:\/\/127\.0\.0\.1:18092\/website\/[0-9a-f-]+\/[^/]+\/proxy\//.test((await frameOf(page)).url()),
    await page.evaluate(() => !!document.querySelector(".guac-display canvas")),
  ], ["Fixture Home", true, false]);
  check("written without http:// it found that the machine speaks http", await address(page), "http://site:8080/");
  check("the tab is named after the address", await page.evaluate(() => [...document.querySelectorAll(".session-tab .session-tab-title")].some(title => /site:8080/.test(title.textContent))), true);
  check("the address bar is one to type into", await page.evaluate(() => document.querySelector(".website-address").readOnly), false);
  const firstFrame = await frameId(page);

  // ---------------------------------------------------------------- a link to another machine
  await (await frameOf(page)).click("#external");
  check("a link to another machine is followed through Matgate", [await shows(page, "hello from other"), await shows(page, "other:8080", "#host")], ["hello from other", "other:8080"]);
  await sleep(300);
  const otherFrame = await frameId(page);
  check("in a frame of its own under the path - what the browser keeps for one website is not sent to the next", [otherFrame.length > 0, otherFrame !== firstFrame], [true, true]);
  check("the address bar shows where the tab is", await address(page), "http://other:8080/hello");
  await (await frameOf(page)).click("#back");
  check("and a link back lands in the first website's frame again", [await shows(page, "page two"), await frameId(page) === firstFrame], ["page two", true]);
  await sleep(300);
  check("with its address", await address(page), "http://site:8080/page2");

  // ---------------------------------------------------------------- a redirect to another machine
  await type(page, "http://site:8080/redirect-port");
  check("a redirect to another port - what http-to-https looks like - is followed", [await shows(page, "the same machine, another port"), await shows(page, "site:8081", "#host"), await shows(page, "?from=redirect", "#query")], ["the same machine, another port", "site:8081", "?from=redirect"]);
  await sleep(300);
  check("the address bar shows where it led", await address(page), "http://site:8081/second?from=redirect");

  // ---------------------------------------------------------------- typed into the address bar
  await type(page, "other:8080/hello");
  check("an address typed into the bar opens, written without http:// as well", [await shows(page, "hello from other"), await frameId(page) === otherFrame], ["hello from other", true]);
  await type(page, "not an address");
  await sleep(800);
  check("what is no address changes nothing", [await shows(page, "hello from other"), dialogs], ["hello from other", []]);
  await (await frameOf(page)).click("#what");
  check("and once the bar is left it shows where the tab is again", await address(page), "http://other:8080/hello");

  // ---------------------------------------------------------------- anywhere
  // A machine that is not inside the network is no different: the tab is taken there, with no page in between.
  const here = (await frameOf(page)).url().match(/\/website\/([0-9a-f-]+)\//)[1];
  const hop = await context.request.get(BASE + "/website/" + here + "/" + firstFrame + "/go?u=" + encodeURIComponent("https://203.0.113.5/somewhere?x=1"), { maxRedirects: 0 });
  const led = hop.headers().location || "";
  check("a public address is followed as well - no page that stops the tab", [hop.status(), /^\/website\/[0-9a-f-]+\/[0-9a-f-]+\/proxy\/somewhere\?x=1$/.test(led), led.includes("/" + firstFrame + "/"), led.includes("/" + otherFrame + "/")], [302, true, false, false]);

  // What Matgate itself is asked: only a browser tab is taken elsewhere, and only its owner's.
  check("the way to another website is a browser tab's alone", await page.evaluate(async () => {
    const answer = await fetch("/website/" + crypto.randomUUID() + "/" + crypto.randomUUID() + "/go?u=" + encodeURIComponent("http://site:8080/"), { credentials: "same-origin", redirect: "manual" });
    return answer.status;
  }), 404);

  // ---------------------------------------------------------------- started without an address
  const blank = await context.newPage();
  blank.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await blank.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(800);
  // The chip opens the browser at once: no dialog, no address asked for - the tab has the address bar.
  await blank.click(".home2-quick-row [data-home2-qc='website']");
  await blank.waitForSelector(".website-frame", { timeout: 20000 });
  await sleep(800);
  check("the chip asked nothing", await blank.evaluate(() => !!document.querySelector("[data-home2-proto-dialog][open]")), false);
  check("it is an empty browser: an address bar to type into, focused", [
    await address(blank),
    await blank.evaluate(() => document.activeElement === document.querySelector(".website-address")),
    await blank.evaluate(() => document.querySelector(".website-address").readOnly),
    (await frameOf(blank)).url(),
  ], ["", true, false, "about:blank"]);
  check("the tab is called Browser", await blank.evaluate(() => [...document.querySelectorAll(".session-tab .session-tab-title")].some(title => title.textContent.trim() === "Browser")), true);
  await type(blank, "http://site:8080/page2");
  check("the first address typed opens", [await shows(blank, "page two"), await address(blank)], ["page two", "http://site:8080/page2"]);


  // On a phone the browser's bar is one row: back, forward, reload, the address, open.
  await blank.setViewportSize({ width: 390, height: 844 });
  await sleep(400);
  check("on a phone the browser's bar is a single row, with room for the address", await blank.evaluate(() => {
    const boxes = [...document.querySelectorAll(".website-toolbar button, .website-toolbar .website-address")].filter(item => item.offsetParent !== null).map(item => item.getBoundingClientRect());
    const tops = boxes.map(box => Math.round(box.top + box.height / 2));
    const bar = document.querySelector(".website-address").getBoundingClientRect();
    return [boxes.length, Math.max(...tops) - Math.min(...tops) <= 4, boxes.every(box => box.right <= window.innerWidth + 1), bar.width >= 140];
  }), [5, true, true, true]);

  check("no dialog of the browser was involved", dialogs, []);

  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
