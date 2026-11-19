// Websites through Matgate. With host names for websites (MATGATE_WEBSITE_HOST, here *.mg.localhost) a
// natively opened website has an address of its own: its paths are its own, so an application that reads
// its own address, or sends the browser to "/login", works - which a proxy under a path of Matgate's
// cannot give it. The website shown is tests/site/server.js, which does exactly those things.
//
// Also here: what the shell needs from a page it cannot look into (where it is; back, forward, reload),
// that a website stays out of Matgate's origin and out of other people's reach, a browser tab without an
// address (quick connect), and that the old way under a path still works - by choice, and by itself where
// the browser does not keep a cookie for the website's host.
//
// Matgate is opened as mg.localhost: a frame keeps its cookies only within one site, and
// <label>.mg.localhost is the same site as mg.localhost (127.0.0.1 is not).
const { chromium } = require("playwright-core");
const crypto = require("crypto");
const BASE = "http://mg.localhost:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const SITE = /^http:\/\/[a-z2-7]{20}\.mg\.localhost:18091/;

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await context.newPage();
  const signIn = async (target, base) => {
    await target.goto(base + "/login", { waitUntil: "networkidle" });
    await target.fill("input[name=\"username\"]", "admin");
    await target.fill("input[name=\"password\"]", "test-only-pw");
    await Promise.all([target.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), target.click(".login-submit")]);
    await sleep(800);
  };
  await signIn(page, BASE);
  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  const connection = async (name, fields) => {
    const existing = (await servers()).find(s => s.name === name);
    await page.goto(BASE + (existing ? "/admin/servers/" + existing.id : "/admin/servers/new"), { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(([n, f]) => {
      document.querySelector("[data-protocol-option='Website']").click();
      const set = (field, value) => {
        const all = [...document.querySelectorAll("[name='" + field + "']")];
        const el = all.find(e => !e.disabled) || all[0];
        if (el.type === "checkbox") { el.checked = !!value; } else { el.value = value; }
        el.dispatchEvent(new Event("change", { bubbles: true }));
      };
      set("name", n);
      Object.entries(f).forEach(([field, value]) => set(field, value));
      document.querySelector("[name='name']").form.requestSubmit();
    }, [name, fields]).catch(() => { /* the submit navigated before the call returned */ });
    await sleep(1500);
    return (await servers()).find(s => s.name === name);
  };
  const remove = async names => page.evaluate(async wanted => {
    const doc = new DOMParser().parseFromString(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text(), "text/html");
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && wanted.includes(cell)) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  }, names);

  // A run that died halfway leaves its connections behind, and with them what Matgate remembers about each
  // (that it has signed in there once, for instance) - so every run starts with connections of its own.
  await remove(["Test-Web", "Test-Web-Basic", "Test-Web-PVE", "Test-Web-Path", "Test-Web-Down"]);
  const web = await connection("Test-Web", { websiteUrl: "http://site:8080/", websitePathMode: false });
  const basic = await connection("Test-Web-Basic", { websiteUrl: "http://site:8080/basic", targetUserName: "demo", targetPassword: "demo-pw" });
  const pve = await connection("Test-Web-PVE", { websiteUrl: "http://site:8082/", targetUserName: "root@pam", targetPassword: "pve-pw" });
  const byPath = await connection("Test-Web-Path", { websiteUrl: "http://site:8080/", websitePathMode: true });
  const down = await connection("Test-Web-Down", { websiteUrl: "http://site:9/" });
  check("the connections are there", [web, basic, pve, byPath, down].map(s => !!s), [true, true, true, true, true]);

  // A shell with one website tab; the frame of that tab, and what it shows.
  const open = async (server, base = BASE, inContext = context) => {
    const shell = await inContext.newPage();
    shell.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await shell.goto(base + "/?open=" + server.id, { waitUntil: "domcontentloaded" });
    await shell.waitForSelector(".website-frame", { timeout: 20000 });
    return shell;
  };
  const frameOf = async shell => {
    const handle = await shell.$(".website-frame");
    return handle.contentFrame();
  };
  const textOf = async (shell, selector, tries = 60) => {
    for (let i = 0; i < tries; i++) {
      try {
        const frame = await frameOf(shell);
        const text = await frame.evaluate(s => (document.querySelector(s) || {}).textContent, selector);
        if (typeof text === "string" && text.length) { return text; }
      }
      catch { /* navigating */ }
      await sleep(250);
    }
    return "";
  };
  const shown = async (shell, wanted, selector = "#what") => {
    for (let i = 0; i < 60; i++) {
      try {
        const frame = await frameOf(shell);
        const text = await frame.evaluate(s => (document.querySelector(s) || {}).textContent, selector);
        if (text === wanted) { return text; }
      }
      catch { /* navigating */ }
      await sleep(250);
    }
    return textOf(shell, selector, 1);
  };
  const address = shell => shell.inputValue(".website-address");
  const addressIs = async (shell, wanted) => { for (let i = 0; i < 40 && (await address(shell)) !== wanted; i++) { await sleep(200); } return address(shell); };
  const go = async (shell, to) => {
    await shell.fill(".website-address", to);
    await shell.press(".website-address", "Enter");
  };
  const click = async (shell, selector) => (await frameOf(shell)).click(selector);
  const frameUrl = async shell => (await frameOf(shell)).url();

  // ================================================================================== an address of its own
  const shell = await open(web);
  check("the website is shown", await shown(shell, "Fixture Home"), "Fixture Home");
  const firstSite = new URL(await frameUrl(shell)).origin;
  check("under a host name of its own", SITE.test(firstSite), true);
  check("where it is at the root: its path is its own", (await textOf(shell, "#where")).split("|")[0], "/");
  check("the address bar shows the website's real address", await addressIs(shell, "http://site:8080/"), "http://site:8080/");
  check("the frame cannot replace Matgate (sandbox without top navigation)", await shell.evaluate(() => {
    const sandbox = document.querySelector(".website-frame").getAttribute("sandbox") || "";
    return sandbox.includes("allow-scripts") && !sandbox.includes("allow-top-navigation");
  }), true);

  await click(shell, "#rooted");
  check("a link to a path at the root leads there", [await shown(shell, "page two"), await addressIs(shell, "http://site:8080/page2")], ["page two", "http://site:8080/page2"]);
  await shell.click("[data-website-action='back']");
  check("the shell's back button goes back in the page", await shown(shell, "Fixture Home"), "Fixture Home");
  await shell.click("[data-website-action='forward']");
  check("and forward again", await shown(shell, "page two"), "page two");
  await go(shell, "http://site:8080/");
  await shown(shell, "Fixture Home");
  await click(shell, "#absolute");
  check("a link that names the real host in full stays in the website", [await shown(shell, "?from=absolute", "#query"), new URL(await frameUrl(shell)).origin === firstSite], ["?from=absolute", true]);

  // The application that reads its own address.
  await go(shell, "http://site:8080/app/dashboard");
  check("an application finds its route in its own address", await shown(shell, "the dashboard", "#route"), "the dashboard");
  await click(shell, "#settings");
  check("a route the application pushes shows in the address bar", [await shown(shell, "the settings", "#route"), await addressIs(shell, "http://site:8080/app/settings")], ["the settings", "http://site:8080/app/settings"]);
  await shell.click("[data-website-action='back']");
  check("back takes the application's own step back", await shown(shell, "the dashboard", "#route"), "the dashboard");
  await click(shell, "#kick");
  check("sent to /login at the root, the browser lands at the WEBSITE's sign-in page", [await shown(shell, "the website's own sign-in page"), await textOf(shell, "#query")], ["the website's own sign-in page", "?redirect=/app/dashboard"]);
  check("and Matgate is still Matgate around it", new URL(shell.url()).origin, BASE);
  await shell.click("[data-website-action='reload']");
  await sleep(800);
  check("reload loads the page again", await shown(shell, "the website's own sign-in page"), "the website's own sign-in page");

  // Cookies, and what the website sees of the request.
  await go(shell, "http://site:8080/");
  await shown(shell, "Fixture Home");
  await click(shell, "#signin");
  await shown(shell, "who am i");
  let seen = JSON.parse(await textOf(shell, "#seen"));
  check("signing in: the website's cookies come back to it, Matgate's own does not", [Object.keys(seen.cookies).sort(), /^[0-9a-f]{32}$/.test(seen.cookies.sid || "")], [["sid", "theme"], true]);
  check("the website sees its own host, and nothing of how Matgate was reached", [seen.host, seen.forwarded, seen.referer.startsWith("http://site:8080/")], ["site:8080", "", true]);
  check("a script in the page sees the cookies a script may see", await (await frameOf(shell)).evaluate(() => document.cookie), "theme=dark");
  check("the cookie that lets the browser in is not the website's to set", (await context.cookies(firstSite)).find(c => c.name === "Matgate-Site").value !== "stolen", true);

  await go(shell, "http://site:8080/calls");
  await shown(shell, "calls");
  for (let i = 0; i < 40 && !((await textOf(shell, "#fetch", 1)) && (await textOf(shell, "#xhr", 1))); i++) { await sleep(200); }
  seen = JSON.parse((await textOf(shell, "#fetch")) || "{}");
  check("fetch to the real host in full arrives: method, header, body, the website's own origin", [seen.method, seen.custom, seen.body, seen.origin], ["POST", "by fetch", "posted", "http://site:8080"]);
  seen = JSON.parse((await textOf(shell, "#xhr")) || "{}");
  check("and XMLHttpRequest, with the cookies", [seen.custom, Object.keys(seen.cookies || {}).sort()], ["by xhr", ["sid", "theme"]]);

  await go(shell, "http://site:8080/socket");
  check("a web socket reaches the website - with its origin, its cookies, its sub protocol", await shown(shell, "ping through matgate | origin http://site:8080 | cookies sid,theme [echo]", "#reply"), "ping through matgate | origin http://site:8080 | cookies sid,theme [echo]");

  await go(shell, "http://site:8080/stream");
  await shown(shell, "stream");
  for (let i = 0; i < 40 && JSON.parse((await textOf(shell, "#got", 1)) || "[]").length < 4; i++) { await sleep(250); }
  const arrived = JSON.parse((await textOf(shell, "#got")) || "[]");
  check("events arrive one by one, not all at the end", [arrived.length, arrived.length === 4 && arrived[3] - arrived[0] > 700], [4, true]);

  await go(shell, "http://site:8080/redirect-absolute");
  check("a redirect that names the real host stays in the website", [await shown(shell, "?from=redirect", "#query"), new URL(await frameUrl(shell)).origin === firstSite], ["?from=redirect", true]);
  await go(shell, "http://site:8080/redirect-port");
  check("a redirect to the same machine under another port is followed through Matgate", [await shown(shell, "the same machine, another port"), await textOf(shell, "#host")], ["the same machine, another port", "site:8081"]);
  const secondSite = new URL(await frameUrl(shell)).origin;
  check("under another host name of its own, and the address bar says where", [SITE.test(secondSite) && secondSite !== firstSite, await addressIs(shell, "http://site:8081/second?from=redirect")], [true, "http://site:8081/second?from=redirect"]);
  await click(shell, "#first");
  check("a link back to the first port leads back", [await shown(shell, "?from=second", "#query"), new URL(await frameUrl(shell)).origin === firstSite], ["?from=second", true]);

  await go(shell, "http://site:8080/");
  await shown(shell, "Fixture Home");
  const [popup] = await Promise.all([context.waitForEvent("page", { timeout: 15000 }).catch(() => null), click(shell, "#external")]);
  check("a link to another website opens a browser tab of its own - not inside this connection", [popup ? popup.url().replace(/\/$/, "") === "http://other:8080/hello" || /other:8080/.test(popup.url()) || popup.url() === "about:blank" || popup.url().startsWith("chrome-error") : false, await shown(shell, "Fixture Home")], [true, "Fixture Home"]);
  if (popup) { await popup.close().catch(() => { }); }
  await click(shell, "#top");
  check("a link that wants the whole window gets the frame", [await shown(shell, "?from=top", "#query"), new URL(shell.url()).origin], ["?from=top", BASE]);

  await go(shell, "http://site:8080/framed");
  check("a page that forbids framing is shown all the same", await shown(shell, "not to be framed"), "not to be framed");
  check("its own script runs, and so does Matgate's (which takes the page's nonce)", [await textOf(shell, "#ran"), await (await frameOf(shell)).evaluate(() => window.__matgateSite === true)], ["the page's own script ran", true]);
  await go(shell, "http://site:8080/latin1");
  check("a page that is not UTF-8 arrives as it was written", await shown(shell, "Größe und Ärger"), "Größe und Ärger");
  await go(shell, "http://site:8080/packed");
  check("a page sent compressed is readable", await shown(shell, "this page was sent compressed"), "this page was sent compressed");
  check("a piece of a page fetched by a script gets nothing added", await (await frameOf(shell)).evaluate(async () => (await fetch("/fragment")).text()), "<div id=\"piece\">only a piece</div>");
  const expected = (() => {
    const key = crypto.createHash("sha256").update("matgate").digest();
    const data = Buffer.alloc(1024 * 1024);
    for (let i = 0; i < data.length; i++) { data[i] = key[i % key.length] ^ (i & 255); }
    return crypto.createHash("sha256").update(data).digest("hex");
  })();
  check("a download arrives whole", await (await frameOf(shell)).evaluate(async () => {
    const data = await (await fetch("/download")).arrayBuffer();
    const hash = await crypto.subtle.digest("SHA-256", data);
    return [data.byteLength, [...new Uint8Array(hash)].map(x => x.toString(16).padStart(2, "0")).join("")];
  }).catch(e => String(e)), [1024 * 1024, expected]);

  // The same website in a browser tab of its own.
  await go(shell, "http://site:8080/page2?from=own-tab");
  await shown(shell, "?from=own-tab", "#query");
  const [own] = await Promise.all([context.waitForEvent("page", { timeout: 15000 }).catch(() => null), shell.click("[data-website-action='open']")]);
  if (own) {
    for (let i = 0; i < 40 && !SITE.test(own.url()); i++) { await sleep(250); }
    await own.waitForSelector("#query", { timeout: 15000 }).catch(() => { });
    check("\"Open in new tab\" shows the page where it was, in a tab of the browser's", [SITE.test(own.url()), await own.evaluate(() => (document.querySelector("#query") || {}).textContent).catch(() => "")], [true, "?from=own-tab"]);
    await own.close();
  }
  else {
    check("\"Open in new tab\" opens a tab", false, true);
  }

  // ================================================================================== who gets in, and how far
  const inside = await frameOf(shell);
  check("the website cannot read Matgate's pages", await inside.evaluate(async base => {
    try { await (await fetch(base + "/", { credentials: "include" })).text(); return "read"; }
    catch { return "refused"; }
  }, BASE), "refused");
  check("nor reach into the window around it", await inside.evaluate(() => { try { return String(window.parent.document.title); } catch { return "refused"; } }), "refused");

  const enter = await shell.evaluate(async id => {
    const csrf = JSON.parse((await (await fetch("/")).text()).match(/const csrfToken = (".*?");/)[1]);
    return (await fetch("/api/website/" + id + "/site", { method: "POST", headers: { "X-Matgate-Csrf": csrf } })).json();
  }, web.id);
  const stranger = await b.newContext();
  const outside = await stranger.newPage();
  check("a browser that was never let in gets nothing", [(await outside.goto(firstSite + "/")).status(), await outside.evaluate(() => !!document.getElementById("matgate-site-denied"))], [403, true]);
  check("not even Matgate's script for the page", (await outside.goto(firstSite + "/__matgate/site.js")).status(), 403);
  check("the address the shell hands out lets a browser in", [(await outside.goto(enter.url)).status(), await outside.evaluate(() => (document.querySelector("#what") || {}).textContent)], [200, "Fixture Home"]);
  await stranger.close();
  const late = await b.newContext();
  const again = await late.newPage();
  check("once: the same address a second time is refused", (await again.goto(enter.url)).status(), 403);
  const cookie = (await context.cookies(firstSite)).find(c => c.name === "Matgate-Site");
  await late.addCookies([{ name: "Matgate-Site", value: cookie.value, url: secondSite }]);
  check("and one website's cookie opens no other", (await again.goto(secondSite + "/second")).status(), 403);
  await late.close();
  check("a connection opens only its own machine, whatever is typed", await shell.evaluate(async id => {
    const csrf = JSON.parse((await (await fetch("/")).text()).match(/const csrfToken = (".*?");/)[1]);
    return (await fetch("/api/website/" + id + "/site?url=" + encodeURIComponent("http://other:8080/hello"), { method: "POST", headers: { "X-Matgate-Csrf": csrf } })).status;
  }, web.id), 400);
  await shell.close();

  // ================================================================================== signing in for the user
  let other = await open(basic);
  check("a website that asks for name and password gets the connection's", await shown(other, "signed in with name and password"), "signed in with name and password");
  const basicSite = new URL(await frameUrl(other)).origin;
  await other.close();
  other = await open(pve);
  check("a Proxmox-style sign-in is done with the connection's name and password", await shown(other, "signed in as root@pam"), "signed in as root@pam");
  await other.close();
  // The ticket lives in the browser. Another browser of the same user - the phone after the desktop - has
  // none yet and gets its own; it used to find the sign-in dialog, because Matgate tried once for all.
  const second = await b.newContext({
    viewport: { width: 1300, height: 860 },
    storageState: { cookies: (await context.cookies()).filter(cookie => !/^(PVEAuthCookie|Matgate-SignIn|Matgate-Site)$/.test(cookie.name)), origins: [] },
  });
  other = await open(pve, BASE, second);
  check("another browser of the same user is signed in as well", await shown(other, "signed in as root@pam"), "signed in as root@pam");
  check("what Matgate keeps in the browser for it never reaches the website", [
    (await second.cookies()).some(cookie => cookie.name === "Matgate-SignIn"),
    await textOf(other, "#cookies"),
  ], [true, "PVEAuthCookie"]);
  await other.close();
  // Signed out there on purpose (the application drops its ticket): not signed in again behind one's back.
  await second.clearCookies({ name: "PVEAuthCookie" });
  other = await open(pve, BASE, second);
  check("a browser that signed out is left at the sign-in dialog", await shown(other, "the sign-in dialog"), "the sign-in dialog");
  await other.close();
  await second.close();

  // ================================================================================== when it does not work
  other = await open(down);
  check("a website that does not answer says so, in words", await (async () => {
    for (let i = 0; i < 80; i++) {
      try { if (await (await frameOf(other)).evaluate(() => !!document.getElementById("matgate-site-unreachable"))) { return true; } }
      catch { /* navigating */ }
      await sleep(300);
    }
    return false;
  })(), true);
  await other.close();

  // ================================================================================== the old way
  other = await open(byPath);
  check("a connection that asks for it is shown under Matgate's own address", [await shown(other, "Fixture Home"), /\/website\/[0-9a-f-]+\/[^/]+\/proxy\//.test(await frameUrl(other))], ["Fixture Home", true]);
  check("with an address bar that only shows", await other.evaluate(() => document.querySelector(".website-address").readOnly), true);
  // A page under a path that leaves for "/login" at the root means its own - and from then on is shown as if
  // it lay at the root of its website (tests/browser-under-path.js has the rest of that).
  check("under the path a page sees the path", await (await frameOf(other)).evaluate(() => /^\/website\//.test(location.pathname)), true);
  await (await frameOf(other)).evaluate(() => { location.href = "/login?redirect=/app/dashboard"; });
  check("sent to /login at the root it lands on the website's own sign-in page all the same", [await shown(other, "the website's own sign-in page"), await textOf(other, "#query")], ["the website's own sign-in page", "?redirect=/app/dashboard"]);
  check("and from then on the page believes it lies at the root", await (await frameOf(other)).evaluate(() => [location.pathname, typeof window.__matgateRealUrl === "function" && /\/website\/[0-9a-f-]+\/[^/]+\/proxy\/login/.test(window.__matgateRealUrl())]), ["/login", true]);
  await other.close();
  // Matgate reached under an address that is no site with the websites' hosts: the browser keeps no
  // cookie for them inside a frame, the shell notices and falls back by itself.
  const foreign = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const foreignPage = await foreign.newPage();
  await signIn(foreignPage, "http://127.0.0.1:18091");
  other = await open(web, "http://127.0.0.1:18091", foreign);
  check("where the browser keeps no cookie for the website's host, the shell falls back to the path", [await shown(other, "Fixture Home"), /^http:\/\/127\.0\.0\.1:18091\/website\//.test(await frameUrl(other))], ["Fixture Home", true]);
  // A browser tab does the same - it used to stay on "This address belongs to a Matgate session".
  await foreignPage.goto("http://127.0.0.1:18091/", { waitUntil: "networkidle" });
  await sleep(600);
  await foreignPage.click(".home2-quick-row [data-home2-qc='website']");
  await foreignPage.waitForSelector(".website-frame", { state: "attached", timeout: 20000 });
  await sleep(800);
  await go(foreignPage, "http://site:8080/page2");
  const underPath = async shell => /^http:\/\/127\.0\.0\.1:18091\/website\//.test(await (await frameOf(shell)).evaluate(() => typeof window.__matgateRealUrl === "function" ? window.__matgateRealUrl() : location.href));
  check("and so does a browser tab: the address typed there opens under the path", [await shown(foreignPage, "page two"), await underPath(foreignPage), await address(foreignPage)], ["page two", true, "http://site:8080/page2"]);
  await go(foreignPage, "other:8080/hello");
  check("and the next one as well, without trying the host name again", [await shown(foreignPage, "hello from other"), await address(foreignPage)], ["hello from other", "http://other:8080/hello"]);
  await foreign.close();

  // ================================================================================== a browser (quick connect)
  const home = await context.newPage();
  home.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await home.goto(BASE + "/", { waitUntil: "networkidle" });
  check("the home page offers a browser: the website chip by that name", await home.evaluate(() => [...document.querySelectorAll(".home2-quick-row [data-home2-qc]")].map(chip => chip.dataset.home2Qc + ":" + chip.textContent.trim()).filter(chip => /website|browser/i.test(chip))), ["website:Browser"]);
  // The chip itself opens the browser at once (tests/browser-under-path.js); the dialog under "More" is
  // where it takes a start address or a sign-in.
  await home.click(".home2-quick-row [data-home2-more]");
  await home.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  await home.click("[data-home2-qc-proto='website']");
  // With host names for websites a tab can go anywhere - so the browser may start without an address.
  check("it asks for a start address, and needs none", await home.evaluate(() => ({
    label: document.querySelector("[data-home2-qc-hostlabel]").textContent,
    required: document.querySelector("[data-home2-qc-host]").required,
    button: document.querySelector("[data-home2-qc-submit]").textContent,
    fields: [...document.querySelectorAll("[data-home2-qc-form] input:not([type='hidden'])")].filter(field => field.offsetParent !== null).map(field => field.name),
  })), { label: "Start URL", required: false, button: "Open browser", fields: ["host", "username", "password"] });
  await home.click("[data-home2-qc-form] button[type='submit']");
  await home.waitForSelector(".website-frame", { state: "attached", timeout: 20000 });
  await sleep(1000);
  check("it opens a browser: an empty address bar to type into", [await address(home), await home.evaluate(() => document.activeElement === document.querySelector(".website-address"))], ["", true]);
  await go(home, "http://site:8080/");
  check("an address typed there is opened", [await shown(home, "Fixture Home"), await addressIs(home, "http://site:8080/")], ["Fixture Home", "http://site:8080/"]);
  await click(home, "#external");
  check("a link to another website is followed through Matgate, in the same tab", [await shown(home, "hello from other"), await textOf(home, "#host"), await addressIs(home, "http://other:8080/hello")], ["hello from other", "other:8080", "http://other:8080/hello"]);
  check("each website under its own host name", SITE.test(new URL(await frameUrl(home)).origin) && new URL(await frameUrl(home)).origin !== firstSite, true);
  await click(home, "#back");
  check("and back to the first", await shown(home, "page two"), "page two");
  await go(home, "http://site:8080/redirect-out");
  check("a redirect to another website is followed as well", await shown(home, "hello from other"), "hello from other");
  await home.close();

  // ================================================================================== the end of it
  await remove(["Test-Web-Basic"]);
  check("a connection that was deleted: its website is closed", (await page.goto(basicSite + "/basic")).status(), 403);
  check("the others are still open to this browser", (await page.goto(firstSite + "/page2")).status(), 200);
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await page.evaluate(() => {
    const form = [...document.querySelectorAll("form")].find(f => /\/logout$/.test(f.getAttribute("action") || ""));
    form.requestSubmit();
  }).catch(() => { });
  await page.waitForURL(u => /login/.test(u.toString()), { timeout: 15000 }).catch(() => { });
  check("signed out: the website is closed to the browser that still holds its cookie", (await page.goto(firstSite + "/page2")).status(), 403);

  await signIn(page, BASE);
  await remove(["Test-Web", "Test-Web-PVE", "Test-Web-Path", "Test-Web-Down"]);
  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
