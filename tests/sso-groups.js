// Groups of the sign-in services (0.49.0), under Administration -> Sign-in:
//   - a group of a service (here an Entra object id) linked to a group of Matgate is listed, with nobody yet
//   - it gets a name, and the name is what the group's details show (the raw id as the tooltip)
//   - picking "no group" in the list saves at once and takes the link away
//   - the Entra card carries the steps for the app registration
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
  const p = await (await b.newContext({ viewport: { width: 1400, height: 950 } })).newPage();
  p.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await p.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p.fill("input[name='username']", "admin");
  await p.fill("input[name='password']", "test-only-pw");
  await Promise.all([p.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), p.click(".login-submit")]);
  const GROUP = "SSO-Gruppe-Test";
  const VALUE = "entra:3f2a9c1e-0000-4000-8000-00000000a11e";
  const row = "[data-sso-group='" + VALUE + "']";
  const deleteGroup = async () => {
    await p.goto(BASE + "/admin?tab=groups", { waitUntil: "networkidle" });
    const id = await p.evaluate(g => { const n = document.querySelector("[data-admp-group='" + g + "']"); return n ? new URL(n.href).searchParams.get("sel") : null; }, GROUP);
    if (id) {
      await p.goto(BASE + "/admin?tab=groups&sel=" + id, { waitUntil: "networkidle" });
      await p.evaluate(async () => { const form = document.querySelector("form[action$='/delete']"); await fetch(form.action, { method: "POST", body: new FormData(form), credentials: "same-origin" }); });
    }
  };
  await deleteGroup();
  try {
    await p.goto(BASE + "/admin/groups/new", { waitUntil: "networkidle" });
    await p.fill("input[name='name']", GROUP);
    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click("button[form='group-form']")]);
    const groupId = new URL(p.url()).searchParams.get("sel");
    check("the group is made", !!groupId, true);

    // linked the way the list itself links (without a service set up the list would have nothing to show)
    await p.goto(BASE + "/admin?tab=sso", { waitUntil: "networkidle" });
    await p.evaluate(async ([value, group]) => {
      const body = new FormData();
      body.append("_csrf", document.querySelector("input[name='_csrf']").value);
      body.append("value", value);
      body.append("name", "");
      body.append("group", group);
      await fetch("/admin/sso/group", { method: "POST", body, credentials: "same-origin" });
    }, [VALUE, groupId]);
    await p.goto(BASE + "/admin?tab=sso", { waitUntil: "networkidle" });
    check("the linked group of the service is listed", await p.locator(row).count(), 1);
    check("with nobody signed in through it yet", (await p.locator(row + " [data-sso-group-people]").innerText()).trim(), "nobody yet");
    check("and the group of Matgate picked", await p.locator(row + " select[name='group']").evaluate(s => s.options[s.selectedIndex].text), GROUP);
    check("the Entra card has the steps of the registration", await p.locator("[data-sso-card='entra'] .sso-steps li").count(), 5);

    // a name for the object id
    await p.fill(row + " input[name='name']", "Technik");
    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.click(row + " [data-sso-group-save]")]);
    check("the name is kept", await p.locator(row + " input[name='name']").inputValue(), "Technik");
    await p.goto(BASE + "/admin?tab=groups&sel=" + groupId, { waitUntil: "networkidle" });
    check("the group's details show the name", await p.evaluate(v => { const chip = document.querySelector("[data-admp-sso] [data-sso-value='" + v + "']"); return chip ? [chip.innerText.trim(), chip.title] : null; }, VALUE), ["Microsoft Entra ID · Technik", VALUE]);

    // "no group" saves at once and takes the link away
    await p.goto(BASE + "/admin?tab=sso", { waitUntil: "networkidle" });
    await Promise.all([p.waitForNavigation({ waitUntil: "networkidle" }), p.selectOption(row + " select[name='group']", "")]);
    await sleep(300);
    check("unlinked, it leaves the list (nobody named it)", await p.locator(row).count(), 0);
    await p.goto(BASE + "/admin?tab=groups&sel=" + groupId, { waitUntil: "networkidle" });
    check("and the group no longer stands for it", await p.locator("[data-admp-sso]").count(), 0);
  }
  finally {
    await deleteGroup();
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
