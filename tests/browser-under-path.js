// The browser of quick connect where Matgate has no host names for websites - the way it runs where nobody
// set them up (the second Matgate of the test stack, port 18092). It is Matgate's own browser there as well:
// the page is fetched by Matgate and shown in the tab, under a path of Matgate's address - never the browser
// farm's picture of one. And it is a browser: its chip opens it at once, the address bar takes any address,
// and a link or a redirect to another machine is followed through Matgate - each website in a frame of its
// own under the path. Anywhere: 0.27.0 stopped at the edge of the network, which the owner did not want.
const { chromium } = require("playwright-core");
const chooseKind = require("./choose-kind");
const BASE = "http://127.0.0.1:18092";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

(async () => {
  let b = await chromium.launch({ headless: true });
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

  // On a phone the chips are one row to swipe through. It once scrolled sideways with no sign of it, and the
  // browser stood behind the edge of the screen for anyone who did not think of swiping: now the tile at the
  // edge is cut, "More" stays in sight at the right end, and "Anpassen" leads to the order.
  await page.setViewportSize({ width: 390, height: 844 });
  await sleep(300);
  check("on a phone the chips are one row that scrolls sideways - the page itself does not", await page.evaluate(() => {
    const row = document.querySelector(".home2-quick-row");
    const boxes = [...row.querySelectorAll(".home2-proto-chip")].map(c => c.getBoundingClientRect());
    return [boxes.length, new Set(boxes.map(b => Math.round(b.top))).size, row.scrollWidth > row.clientWidth + 1,
      document.documentElement.scrollWidth <= innerWidth + 1, boxes.every(b => b.width > 40)];
  }), [12, 1, true, true, true]);
  // Since 0.43.2 "More" is not pinned: it is the last tile and scrolls with the others, as the folder tiles do.
  check("the tile at the right edge is cut, so that there is more to see", await page.evaluate(() => {
    const row = document.querySelector(".home2-quick-row"); row.scrollLeft = 0;
    return [...row.querySelectorAll(".home2-proto-chip")].some(c => { const b = c.getBoundingClientRect(); return b.left < innerWidth && b.right > innerWidth; });
  }), true);
  check("'More' is the last tile and scrolls with the others", await page.evaluate(async () => {
    const row = document.querySelector(".home2-quick-row"); row.scrollLeft = 0;
    const more = row.querySelector("[data-home2-more]");
    const before = more.getBoundingClientRect().left;
    const last = row.querySelector(".home2-proto-chip:last-child") === more;
    row.scrollLeft = row.scrollWidth; await new Promise(r => setTimeout(r, 300));
    const after = more.getBoundingClientRect();
    return [last, getComputedStyle(more).position !== "sticky", before > innerWidth - 10 || before > after.left, after.right <= innerWidth + 1];
  }), [true, true, true, true]);
  check("the browser chip scrolls into sight and is not covered", await page.evaluate(async () => {
    const chip = document.querySelector(".home2-quick-row [data-home2-qc='website']");
    chip.scrollIntoView({ inline: "nearest", block: "nearest" }); await new Promise(r => setTimeout(r, 400));
    const b = chip.getBoundingClientRect();
    const hit = document.elementFromPoint(b.left + b.width / 2, b.top + b.height / 2);
    return [b.left >= 0 && b.right <= innerWidth + 1, !!hit && chip.contains(hit)];
  }), [true, true]);
  check("'Anpassen' is there for a phone without a long press", await page.evaluate(() => !!document.querySelector(".home2-quick-section .home2-section-link")?.offsetParent), true);
  // The start page is built anew after a favourite, an edit or a change in the settings: the row stays where it
  // was left. A star clicked and clicked again builds it anew twice and leaves the favourites as they were.
  check("the row stays where it was left when the start page is built anew", await page.evaluate(async () => {
    const wait = ms => new Promise(r => setTimeout(r, ms));
    const star = () => document.querySelector("[data-home2-card] .favorite-toggle-form button");
    if (!star()) { return "no card with a star"; }
    const rebuilt = async () => {
      const row = document.querySelector(".home2-quick-row");
      star().click();
      for (let i = 0; i < 100 && document.querySelector(".home2-quick-row") === row; i++) { await wait(100); }
    };
    const before = document.querySelector(".home2-quick-row");
    before.scrollLeft = 120;
    await wait(300);
    const left = before.scrollLeft;
    await rebuilt();
    const after = document.querySelector(".home2-quick-row");
    const kept = [left > 60, after !== before, Math.abs(after.scrollLeft - left) <= 1, after.classList.contains("is-scrolled")];
    await rebuilt();
    return kept;
  }), [true, true, true, true]);
  check("and their names are not cut off", await page.evaluate(() => [...document.querySelectorAll(".home2-quick-row .home2-proto-chip-name")].filter(name => name.scrollWidth > name.clientWidth + 1).map(name => name.textContent + ":" + name.scrollWidth + "/" + name.clientWidth)), []);
  await page.setViewportSize({ width: 1300, height: 860 });
  await sleep(300);

  // The dialog (under "More") still has the browser, for a start address or a sign-in.
  await page.click(".home2-quick-row [data-home2-more]");
  await page.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  await chooseKind(page, "[data-home2-proto-dialog]", "website");
  const asked = () => page.evaluate(() => ({
    label: document.querySelector("[data-home2-qc-hostlabel]").textContent,
    required: document.querySelector("[data-home2-qc-host]").required,
    focused: document.activeElement === document.querySelector("[data-home2-qc-host]"),
    button: document.querySelector("[data-home2-qc-submit]").textContent,
    fields: [...document.querySelectorAll("[data-home2-qc-form] input:not([type='hidden'])")].filter(field => field.offsetParent !== null).map(field => field.name),
  }));
  check("the dialog asks for the address to start with, and takes none as well", (({ focused, ...rest }) => rest)(await asked()), { label: "Start URL", required: false, button: "Open browser", fields: ["host", "username", "password"] });
  check("and says so", await page.evaluate(() => /empty/i.test(document.querySelector("[data-home2-qc-host]").placeholder)), true);
  await chooseKind(page, "[data-home2-proto-dialog]", "rdp");
  check("another protocol has its own words again", [(await asked()).label, (await asked()).button, (await asked()).required], ["Host", "Connect", true]);
  await chooseKind(page, "[data-home2-proto-dialog]", "website");

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
  // Where the frame is under Matgate's address. The page itself pretends to lie at the root of its website
  // (so that an application which reads its own address finds what it knows), and says where it really is.
  const realUrl = async shell => {
    for (let i = 0; i < 40; i++) {
      try { return await (await frameOf(shell)).evaluate(() => typeof window.__matgateRealUrl === "function" ? window.__matgateRealUrl() : location.href); }
      catch { await sleep(150); }
    }
    return "";
  };
  const frameId = async shell => ((await realUrl(shell)).match(/\/website\/[0-9a-f-]+\/([0-9a-f-]+)\/proxy\//) || [])[1] || "";
  const address = shell => shell.inputValue(".website-address");
  const type = async (shell, to) => {
    await shell.fill(".website-address", to);
    await shell.press(".website-address", "Enter");
  };

  check("the address opens in Matgate's own browser: the page itself, under a path of Matgate's", [
    await shows(page, "Fixture Home"),
    /^http:\/\/127\.0\.0\.1:18092\/website\/[0-9a-f-]+\/[^/]+\/proxy\//.test(await realUrl(page)),
    await page.evaluate(() => !!document.querySelector(".guac-display canvas")),
  ], ["Fixture Home", true, false]);
  check("written without http:// it found that the machine speaks http", await address(page), "http://site:8080/");
  check("to itself the page lies at the root of its website", await shows(page, "/|127.0.0.1:18092", "#where"), "/|127.0.0.1:18092");
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

  // ---------------------------------------------------------------- an application that reads its own address
  // UniFi and its kind: the route comes from location.pathname, and whoever is not signed in is sent to
  // "/login" at the root. Under a path both went wrong - no route matched, and "/login" was Matgate's.
  await type(page, "http://site:8080/app/dashboard");
  check("an application that takes its route from its own address finds it", [await shows(page, "the dashboard", "#route"), await address(page)], ["the dashboard", "http://site:8080/app/dashboard"]);
  await (await frameOf(page)).click("#settings");
  await sleep(400);
  check("a route it pushes is its own path, and shows in the address bar", [await shows(page, "the settings", "#route"), await (await frameOf(page)).evaluate(() => location.pathname), await address(page)], ["the settings", "/app/settings", "http://site:8080/app/settings"]);
  await (await frameOf(page)).click("#kick");
  check("sent to /login at the root, the tab lands on the WEBSITE's sign-in page - not on Matgate's", [await shows(page, "the website's own sign-in page"), await shows(page, "?redirect=/app/settings", "#query")], ["the website's own sign-in page", "?redirect=/app/settings"]);
  await sleep(300);
  check("with its address in the bar", await address(page), "http://site:8080/login?redirect=/app/settings");
  // Cookies are bound to the proxy path; the page that pretends to be elsewhere still sees its own.
  await type(page, "http://site:8080/");
  await shows(page, "Fixture Home");
  await (await frameOf(page)).click("#signin");
  let seen = "";
  for (let i = 0; i < 60 && !/theme=dark/.test(seen); i++) {
    try { seen = await (await frameOf(page)).evaluate(() => document.cookie); } catch { /* loading */ }
    if (!/theme=dark/.test(seen)) { await sleep(250); }
  }
  check("the page's scripts see the cookies of their website - not the one meant for the server alone", [/theme=dark/.test(seen), /sid=/.test(seen)], [true, false]);
  await (await frameOf(page)).evaluate(() => { document.cookie = "mine=1; path=/"; });
  check("and one they set themselves", await (await frameOf(page)).evaluate(() => /mine=1/.test(document.cookie)), true);
  await type(page, "other:8080/hello");
  await shows(page, "hello from other");

  // ---------------------------------------------------------------- anywhere
  // A machine that is not inside the network is no different: the tab is taken there, with no page in between.
  const here = (await realUrl(page)).match(/\/website\/([0-9a-f-]+)\//)[1];
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
  await blank.waitForSelector(".website-frame", { state: "attached", timeout: 20000 });
  await sleep(800);
  check("the chip asked nothing", await blank.evaluate(() => !!document.querySelector("[data-home2-proto-dialog][open]")), false);
  check("it is an empty browser: an address bar to type into, focused", [
    await address(blank),
    await blank.evaluate(() => document.activeElement === document.querySelector(".website-address")),
    await blank.evaluate(() => document.querySelector(".website-address").readOnly),
    (await frameOf(blank)).url(),
  ], ["", true, false, "about:blank"]);
  check("the tab is called Browser", await blank.evaluate(() => [...document.querySelectorAll(".session-tab .session-tab-title")].some(title => title.textContent.trim() === "Browser")), true);
  // Where the website will be there is a start page, not an empty area.
  const startPage = shell => shell.evaluate(() => {
    const start = document.querySelector(".website-start");
    return { shown: !!start && !start.hidden && start.offsetHeight > 100, says: !!start && /nas.local/.test(start.textContent), frame: document.querySelector(".website-frame").offsetHeight };
  });
  check("the empty browser shows a start page that says where to type", await startPage(blank), { shown: true, says: true, frame: 0 });
  await type(blank, "http://site:8080/page2");
  check("the first address typed opens", [await shows(blank, "page two"), await address(blank)], ["page two", "http://site:8080/page2"]);
  check("and the start page makes room for the website", [(await startPage(blank)).shown, (await startPage(blank)).frame > 100], [false, true]);
  // The mark in front of the address: a globe for a plain address - the lock is for an encrypted one.
  check("the address bar carries a mark, and no lock for a plain http address", await blank.evaluate(() => {
    const bar = document.querySelector(".website-toolbar-address");
    const seen = name => getComputedStyle(bar.querySelector(name)).display !== "none";
    return [bar.classList.contains("is-secure"), seen(".website-address-globe"), seen(".website-address-lock")];
  }), [false, true, false]);
  // The next empty browser offers where this one has been.
  await blank.evaluate(() => document.querySelector("#connection-tab-actions [data-action='disconnect']").click());
  await sleep(800);
  await blank.click(".home2-quick-row [data-home2-qc='website']");
  await blank.waitForSelector(".website-start:not([hidden])", { timeout: 20000 });
  await sleep(500);
  const offered = await blank.evaluate(() => [...document.querySelectorAll("[data-website-start='recent'] .website-start-entry strong")].map(entry => entry.textContent));
  check("the next empty browser offers where this one has been - the last address first, each one once", [offered[0], offered.includes("other:8080/hello"), new Set(offered).size === offered.length, offered.length <= 8], ["site:8080/page2", true, true, true]);
  await blank.click("[data-website-start='recent'] .website-start-entry");
  check("and one click on it opens it", [await shows(blank, "page two"), await address(blank)], ["page two", "http://site:8080/page2"]);


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
