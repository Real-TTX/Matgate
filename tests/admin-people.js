// Administration -> Users and -> Groups (0.47.0): a list on the left, the chosen one beside it.
//   - a new user lands on its own entry; the list can be searched
//   - a group's members are added and taken away right on the group, and on the user
//   - what a group may do: added, its level changed (saved at once), taken away - and the user's
//     "What may ... do?" follows each step with the reason
//   - on a phone the list and the details come one after the other
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
  await p.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p.fill("input[name='username']", "admin");
  await p.fill("input[name='password']", "test-only-pw");
  await Promise.all([p.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), p.click(".login-submit")]);
  const USER = "personen-test";
  const GROUP = "Personen-Gruppe";
  const GROUP2 = "Personen-Gruppe-2";
  const sel = () => new URL(p.url()).searchParams.get("sel");
  const submit = async selector => { await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click(selector)]); await sleep(300); };
  const makeGroup = async name => {
    await p.goto(BASE + "/admin/groups/new", { waitUntil: "networkidle" });
    await p.fill("input[name='name']", name);
    await submit("button[form='group-form']");
    return sel();
  };
  let userId = null, groupId = null, group2Id = null;
  // Leftovers of a run that stopped half-way.
  await p.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  await p.evaluate(async u => {
    const row = [...document.querySelectorAll("tr")].find(r => r.innerText.includes(u));
    const form = row && row.querySelector("form[action*='/delete']");
    if (form) { await fetch(form.action, { method: "POST", body: new FormData(form), credentials: "same-origin" }); }
  }, USER);
  for (const name of [GROUP, GROUP2]) {
    await p.goto(BASE + "/admin?tab=groups", { waitUntil: "networkidle" });
    const leftover = await p.evaluate(g => { const n = document.querySelector("[data-admp-group='" + g + "']"); return n ? new URL(n.href).searchParams.get("sel") : null; }, name);
    if (leftover) {
      await p.goto(BASE + "/admin?tab=groups&sel=" + leftover, { waitUntil: "networkidle" });
      await p.evaluate(async () => { const form = document.querySelector("form[action$='/delete']"); await fetch(form.action, { method: "POST", body: new FormData(form), credentials: "same-origin" }); });
    }
  }
  try {
    // ---- a new user, and the list
    await p.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
    await p.fill("input[name='username']", USER);
    await p.fill("input[name='password']", "test-only-pw-8642");
    await Promise.all([p.waitForURL(u => /sel=/.test(u.toString()), { timeout: 15000 }), p.evaluate(() => document.querySelector("input[name='username']").form.requestSubmit())]);
    await p.waitForLoadState("networkidle");
    userId = sel();
    check("a new user lands on its own entry in the tab", [new URL(p.url()).searchParams.get("tab"), !!userId], ["users", true]);
    check("which is chosen in the list", await p.evaluate(u => !!document.querySelector(".admp-node.is-selected[data-admp-user='" + u + "']"), USER), true);
    check("and shows the user", await p.evaluate(() => document.querySelector(".admpeople .admdetail h2").textContent.trim()), USER);
    await p.fill("[data-admp-filter]", "personen");
    check("the search keeps only who matches", await p.evaluate(() => [...document.querySelectorAll("[data-admp-user]")].filter(n => !n.hidden).map(n => n.dataset.admpUser)), [USER]);
    await p.fill("[data-admp-filter]", "zzz-niemand");
    check("and says when nobody does", await p.evaluate(() => !document.querySelector("[data-admp-none]").hidden), true);
    check("with no group nothing is given", await p.evaluate(() => document.querySelector("[data-admp-summary]").textContent.includes(" 0 ")), true);

    // ---- a group: members added and taken away right there
    groupId = await makeGroup(GROUP);
    check("a saved group opens in the groups tab", [new URL(p.url()).searchParams.get("tab"), !!groupId], ["groups", true]);
    const option = await p.evaluate(u => [...document.querySelectorAll("[data-admp-add-member] option")].find(o => o.textContent.includes(u)).value, USER);
    await p.selectOption("[data-admp-add-member] select", option);
    await submit("[data-admp-add-member] button");
    check("a member is added on the group", await p.evaluate(u => !!document.querySelector("[data-admp-member='" + u + "']"), USER), true);
    check("and the page stays on the group", sel(), groupId);

    // ---- what the group may do
    await p.selectOption("[data-admp-add-grant] select[name='target']", "all");
    await p.selectOption("[data-admp-add-grant] select[name='level']", "ReadOnly");
    await submit("[data-admp-add-grant] button");
    check("an entry on all connections is added", await p.evaluate(() => (document.querySelector("[data-admp-grant='all'] select") || {}).value), "ReadOnly");
    check("and is no longer offered", await p.evaluate(() => !!document.querySelector("[data-admp-add-grant] option[value='all']")), false);
    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.selectOption("[data-admp-grant='all'] select", "Connect")]);
    await sleep(300);
    check("a changed level is saved at once", await p.evaluate(() => (document.querySelector("[data-admp-grant='all'] select") || {}).value), "Connect");

    // ---- the user sees it, with the reason
    await p.click("[data-admp-member='" + USER + "'] .acc-who a");
    await p.waitForLoadState("networkidle");
    check("the member's name leads to the user", sel(), userId);
    check("the user is in the group", await p.evaluate(g => !!document.querySelector("[data-admp-group-row='" + g + "']"), GROUP), true);
    check("and may connect to all connections, through the group", await p.evaluate(() => {
      const row = document.querySelector("[data-admp-reach='all:All connections'], [data-admp-reach='all:Alle Verbindungen']");
      return row ? [row.dataset.effectiveLevel, /Personen-Gruppe/.test(row.innerText)] : null;
    }), ["Connect", true]);

    // ---- out of the group on the user, into another one there
    group2Id = await makeGroup(GROUP2);
    await p.goto(BASE + "/admin?tab=users&sel=" + userId, { waitUntil: "networkidle" });
    await submit("[data-admp-group-row='" + GROUP + "'] [data-admp-leave]");
    check("taken out of a group on the user", await p.evaluate(g => !!document.querySelector("[data-admp-group-row='" + g + "']"), GROUP), false);
    check("and with it what the group gave", await p.evaluate(() => document.querySelectorAll("[data-admp-reach]").length), 0);
    await p.selectOption("[data-admp-join] select", group2Id);
    await submit("[data-admp-join] button");
    check("put into another group on the user", await p.evaluate(g => !!document.querySelector("[data-admp-group-row='" + g + "']"), GROUP2), true);

    // ---- an entry taken away
    await p.goto(BASE + "/admin?tab=groups&sel=" + groupId, { waitUntil: "networkidle" });
    await submit("[data-admp-grant='all'] [data-admp-remove-grant]");
    check("an entry is taken away on the group", await p.evaluate(() => document.querySelectorAll("[data-admp-grant]").length), 0);

    // ---- the list of everyone is still there
    await p.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
    check("/admin/users is the list of every user", await p.evaluate(u => [...document.querySelectorAll("tr")].some(r => r.innerText.includes(u)), USER), true);

    // ---- on a phone: first the list, then the details
    await p.setViewportSize({ width: 390, height: 844 });
    await p.goto(BASE + "/admin?tab=users", { waitUntil: "networkidle" });
    check("on a phone without a choice the list shows, not the details", await p.evaluate(() => [getComputedStyle(document.querySelector(".admpeople .admtree-side")).display !== "none", getComputedStyle(document.querySelector(".admpeople .admdetail")).display]), [true, "none"]);
    await p.goto(BASE + "/admin?tab=users&sel=" + userId, { waitUntil: "networkidle" });
    check("with a choice the details and a way back", await p.evaluate(() => [getComputedStyle(document.querySelector(".admpeople .admtree-side")).display, getComputedStyle(document.querySelector(".admpeople .admdetail-back")).display !== "none"]), ["none", true]);
    check("nothing is wider than the phone", await p.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
  }
  finally {
    await p.setViewportSize({ width: 1400, height: 950 });
    for (const id of [groupId, group2Id].filter(Boolean)) {
      await p.goto(BASE + "/admin?tab=groups&sel=" + id, { waitUntil: "networkidle" });
      await p.evaluate(() => { const form = document.querySelector("form[action$='/delete']"); if (form) { form.removeAttribute("data-confirm"); form.submit(); } });
      await sleep(700);
    }
    if (userId) {
      await p.goto(BASE + "/admin?tab=users&sel=" + userId, { waitUntil: "networkidle" });
      await p.evaluate(() => { const form = document.querySelector("form[action$='/delete']"); if (form) { form.removeAttribute("data-confirm"); form.submit(); } });
      await sleep(700);
    }
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
