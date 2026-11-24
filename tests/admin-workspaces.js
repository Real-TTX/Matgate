// Administration -> Workspaces (0.50.0), in the shape of Users and Groups:
//   - without a pick the counts; the workspace is in the list with how long it is still open
//   - picked: the public link to copy, the facts, open / edit / log / delete
//   - "+7 days" extends from today's end, "End now" closes the link at once (marked expired), extending opens it
//   - deleting from there lands back on the list, the workspace gone
//   - on a phone: first the list, then the details
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

(async () => {
  const b = await chromium.launch({ headless: true });
  const context = await b.newContext({ viewport: { width: 1400, height: 950 } });
  const p = await context.newPage();
  p.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  p.on("dialog", d => d.accept());
  await p.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p.fill("input[name='username']", "admin");
  await p.fill("input[name='password']", "test-only-pw");
  await Promise.all([p.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), p.click(".login-submit")]);
  const NAME = "Admin-WS-Test";
  const node = "[data-admws-node='" + NAME + "']";
  const removeAll = () => p.evaluate(async name => {
    const parse = html => new DOMParser().parseFromString(html, "text/html");
    const list = parse(await (await fetch("/admin?tab=workspaces", { credentials: "same-origin" })).text());
    for (const link of list.querySelectorAll("[data-admws-node='" + name + "']")) {
      const id = new URL(link.href, location.href).searchParams.get("sel");
      const form = parse(await (await fetch("/workspaces/" + id, { credentials: "same-origin" })).text()).querySelector("form[action$='/delete']");
      if (form) { await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" }); }
    }
  }, NAME);
  // Own dialogs ask before deleting/ending: confirm them.
  const confirmOwn = async () => {
    await p.waitForSelector("#confirm-dialog:not(.hidden)", { timeout: 10000 });
    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click("#confirm-dialog [data-confirm-ok]")]);
    await sleep(300);
  };
  await removeAll();
  try {
    await p.goto(BASE + "/workspaces/new", { waitUntil: "networkidle" });
    await p.fill("input[name='name']", NAME);
    await p.evaluate(() => document.querySelector("input[name='name']").form.requestSubmit());
    await p.waitForURL(/workspaces\/[0-9a-f-]{36}/i, { timeout: 15000 });
    const id = p.url().match(/workspaces\/([0-9a-f-]{36})/i)[1];

    await p.goto(BASE + "/admin?tab=workspaces", { waitUntil: "networkidle" });
    check("without a pick, the counts", await p.locator("[data-admws-stats] > div").count(), 5);
    check("the workspace is in the list, open", await p.locator(node + " .admws-tag-open").count(), 1);
    check("the list can be searched", await p.evaluate(name => {
      const filter = document.querySelector("[data-admin-workspaces] [data-admp-filter]");
      filter.value = name.toLowerCase().slice(0, 9); filter.dispatchEvent(new Event("input"));
      const shown = [...document.querySelectorAll("[data-admws-node]")].filter(n => !n.hidden).map(n => n.dataset.admwsNode);
      filter.value = ""; filter.dispatchEvent(new Event("input"));
      return shown.includes(name);
    }, NAME), true);

    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click(node)]);
    check("picked, it is selected", new URL(p.url()).searchParams.get("sel"), id);
    check("the public link is there to copy", await p.locator("[data-admws-copy]").getAttribute("data-admws-copy"), BASE + "/w/" + id);
    check("open, edit, log and delete", await p.locator("[data-admws-open], [data-admws-edit], [data-admws-delete]").count(), 3);
    const before = await p.locator("[data-admws-until]").innerText();
    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click("[data-admws-extend='168']")]);
    check("+7 days stays on the details", new URL(p.url()).searchParams.get("sel"), id);
    const after = await p.locator("[data-admws-until]").innerText();
    check("and the end moved", before !== after && /left|noch/.test(after), true);

    await p.click("[data-admws-expire]");
    await confirmOwn();
    check("\"End now\" closes the link: expired", await p.locator(".admp-badge.is-off").count() >= 1 && await p.locator("[data-admws-expire]").count() === 0, true);
    check("and the public page says no", (await (await context.request.get(BASE + "/w/" + id, { maxRedirects: 0 })).status()) !== 200 || /expired|abgelaufen/i.test(await (await context.request.get(BASE + "/w/" + id)).text()), true);
    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click("[data-admws-extend='24']")]);
    check("extending opens it again", await p.locator("[data-admws-expire]").count(), 1);

    // ---- a phone: first the list, then the details
    const phone = await (await b.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, storageState: await context.storageState() })).newPage();
    await phone.goto(BASE + "/admin?tab=workspaces", { waitUntil: "networkidle" });
    check("phone: the list first", await phone.evaluate(() => [!!document.querySelector("[data-admin-workspaces] .admtree-side").offsetParent, getComputedStyle(document.querySelector("[data-admin-workspaces] .admdetail")).display === "none", document.documentElement.scrollWidth <= innerWidth + 1]), [true, true, true]);
    await phone.goto(BASE + "/admin?tab=workspaces&sel=" + id, { waitUntil: "networkidle" });
    check("phone: picked, the details and the way back", await phone.evaluate(() => [!!document.querySelector("[data-admws-link]").offsetParent, !!document.querySelector("[data-admin-workspaces] .admdetail-back").offsetParent, document.documentElement.scrollWidth <= innerWidth + 1]), [true, true, true]);
    await phone.context().close();

    await p.click("[data-admws-delete]");
    await confirmOwn();
    check("deleted, back on the list", [new URL(p.url()).searchParams.get("tab"), new URL(p.url()).searchParams.get("sel")], ["workspaces", null]);
    check("and it is gone", await p.locator(node).count(), 0);
  }
  finally {
    await removeAll();
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
