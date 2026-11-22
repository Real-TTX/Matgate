// Saving one part of the settings must not reset another. The forms of the settings page carry their lists in
// hidden fields; those come from the server with their real values, and every handler writes a group only when it
// was sent. So a form sent without any script - here: fetched, parsed and posted as it is - changes nothing, a post
// that leaves a list out keeps it, the picture quality round-trips, and the quick-connect chips (now on the start
// page's form) are no longer reset by a save of the session settings. Last, a save in a settings tab reaches the
// shell around it without a reload: the theme chosen there is the shell's at once.
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
  const context = await b.newContext({ viewport: { width: 1300, height: 860 } });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  // What the settings page says the user has: every named field of the session form and of the start page's form,
  // as the server renders them (no script runs in a parsed page).
  const state = () => page.evaluate(async () => {
    const doc = new DOMParser().parseFromString(await (await fetch("/account?tab=session", { credentials: "same-origin" })).text(), "text/html");
    const read = form => [...new FormData(form)].filter(([name]) => name !== "_csrf" && name !== "asDefault").map(([name, value]) => name + "=" + value).sort();
    return {
      session: read(doc.querySelector("[name='keepSessionMinutes']").form),
      home: read(doc.querySelector("[name='homeSections']").form),
    };
  });
  // Fetches the page, takes one of its forms as the server rendered it, changes what is asked and posts it.
  // set: { name: value } (null removes the field), returns the status of the answer.
  const post = (which, set = {}) => page.evaluate(async ([w, s]) => {
    const doc = new DOMParser().parseFromString(await (await fetch("/account?tab=" + w, { credentials: "same-origin" })).text(), "text/html");
    const form = doc.querySelector(w === "session" ? "[name='keepSessionMinutes']" : "[name='homeSections']").form;
    const body = new FormData(form);
    Object.entries(s).forEach(([name, value]) => { if (value === null) { body.delete(name); } else { body.set(name, value); } });
    const answer = await fetch(form.getAttribute("action"), { method: "POST", body, credentials: "same-origin", redirect: "manual" });
    return answer.type === "opaqueredirect" ? 302 : answer.status;
  }, [which, set]);
  const field = (list, name) => (list.find(entry => entry.startsWith(name + "=")) || "").slice(name.length + 1);

  const original = await state();
  const servers = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });

  // ---- a state that is not the default anywhere, so that a reset would show
  await post("session", {
    actionOrder: "fullscreen,keyboard,osk,pointer", hiddenActions: "pointer", keepSessionMinutes: "15",
    screenQuality: "medium", edgePanning: null, functionKeys: "on",
  });
  await post("home", {
    homeSections: "recent,search,quick,folders,connections,places,workspaces,farm", hiddenHomeSections: "farm",
    quickProtocols: "ssh,rdp,vnc,sftp,smb,website,ftp,webdav,mssql,mysql,postgres", hiddenQuickProtocols: "ftp",
    actionBarServers: servers.length ? servers[0].id : "", hiddenActionBarServers: "",
  });
  const changed = await state();
  check("the settings took the changed state", [
    field(changed.session, "actionOrder").split(",").slice(0, 2), field(changed.session, "hiddenActions"), field(changed.session, "keepSessionMinutes"),
    field(changed.session, "screenQuality"), changed.session.some(e => e.startsWith("edgePanning=")), changed.session.some(e => e.startsWith("functionKeys=")),
    field(changed.home, "homeSections").split(",")[0], field(changed.home, "hiddenHomeSections"), field(changed.home, "quickProtocols").split(",")[0],
    field(changed.home, "hiddenQuickProtocols"), field(changed.home, "actionBarServers").split(",")[0] === (servers.length ? servers[0].id : ""),
  ], [["fullscreen", "keyboard"], "pointer", "15", "medium", false, true, "recent", "farm", "ssh", "ftp", true]);

  // ---- sent without a script: nothing changes
  check("the session form posted as the server rendered it", await post("session"), 302);
  check("changes nothing: switches, keep-running, the session bar and its hidden ones, the quality", (await state()).session, changed.session);
  check("the start page's form posted as the server rendered it", await post("home"), 302);
  check("changes nothing: the sections, the action bar, the chips", (await state()).home, changed.home);

  // ---- the picture quality
  await post("session", { screenQuality: null });
  check("a session post without the quality keeps it", field((await state()).session, "screenQuality"), "medium");
  await post("session", { screenQuality: "fast" });
  check("\"fastest response\" round-trips", field((await state()).session, "screenQuality"), "fast");
  await post("session", { screenQuality: "nonsense" });
  check("an unknown value means \"as last used on this device\"", field((await state()).session, "screenQuality"), "");

  // ---- lists that are not sent are kept
  await post("session", { actionOrder: null, hiddenActions: null });
  check("a session post without the bar's order keeps it", [field((await state()).session, "actionOrder"), field((await state()).session, "hiddenActions")],
    [field(changed.session, "actionOrder"), "pointer"]);
  check("the session form no longer carries the chips", (await state()).session.some(e => e.startsWith("quickProtocols=")), false);
  await post("session", {});
  check("so a session save leaves the chips as they are", field((await state()).home, "quickProtocols"), field(changed.home, "quickProtocols"));
  await post("session", { quickProtocols: "vnc,rdp", hiddenQuickProtocols: "" });
  check("an old session page that still sends the chips is heard", field((await state()).home, "quickProtocols").split(",").slice(0, 2), ["vnc", "rdp"]);
  await post("home", { quickProtocols: "rdp,ssh,smb", hiddenQuickProtocols: "smb" });
  check("the start page's form writes the chips", [field((await state()).home, "quickProtocols").split(",").slice(0, 3), field((await state()).home, "hiddenQuickProtocols")],
    [["rdp", "ssh", "smb"], "smb"]);
  await post("home", { actionBarServers: null, hiddenActionBarServers: null, homeSections: null, hiddenHomeSections: null, quickProtocols: null, hiddenQuickProtocols: null });
  const kept = await state();
  check("a start-page post without its lists keeps the sections, the action bar and the chips",
    [field(kept.home, "homeSections"), field(kept.home, "hiddenHomeSections"), field(kept.home, "actionBarServers").split(",")[0] === (servers.length ? servers[0].id : ""), field(kept.home, "quickProtocols").split(",")[0]],
    [field(changed.home, "homeSections"), "farm", true, "rdp"]);

  // ---- back as it was found
  const restore = async (which, entries) => {
    const set = {};
    entries.forEach(entry => { const at = entry.indexOf("="); set[entry.slice(0, at)] = entry.slice(at + 1); });
    // A switch that was off is absent: everything the form has but the original did not, goes.
    const names = (await state())[which].map(entry => entry.slice(0, entry.indexOf("=")));
    names.forEach(name => { if (!(name in set)) { set[name] = null; } });
    await post(which, set);
  };
  await restore("session", original.session);
  await restore("home", original.home);
  check("and everything is back as it was found", await state(), original);

  // ---- a save in a settings tab reaches the shell at once
  await page.goto(BASE + "/", { waitUntil: "networkidle" });
  await sleep(600);
  const themeBefore = await page.evaluate(() => document.documentElement.dataset.theme);
  const origin = await page.evaluate(() => performance.timeOrigin);
  await page.evaluate(() => window.MatgateOpenShellTab("/account?tab=appearance", "Settings"));
  let frame = null;
  for (let i = 0; i < 40 && !frame; i++) {
    await sleep(250);
    frame = page.frames().find(f => /\/account/.test(f.url()) && f !== page.mainFrame());
  }
  if (!frame) {
    check("the settings open as a tab of the shell", false, true);
  }
  else {
    await frame.waitForLoadState("load");
    await sleep(800);
    await frame.evaluate(() => {
      const dark = document.querySelector("input[name='preferredTheme'][value='dark']");
      dark.checked = true;
      dark.dispatchEvent(new Event("change", { bubbles: true }));
      dark.form.requestSubmit();
    });
    let theme = "";
    for (let i = 0; i < 40 && theme !== "dark"; i++) {
      await sleep(250);
      theme = await page.evaluate(() => document.documentElement.dataset.theme);
    }
    check("dark, chosen and saved in the tab, is the shell's at once - without a reload",
      [theme, await page.evaluate(() => performance.timeOrigin) === origin, !!(await page.$("#matgate-shell"))], ["dark", true, true]);
    // Back to what it was, the way the menu's switch saves it.
    const answer = await page.evaluate(async mode => {
      const body = new FormData();
      body.set("_csrf", document.querySelector("input[name='_csrf']").value);
      body.set("preferredTheme", mode);
      const r = await fetch("/account", { method: "POST", body, credentials: "same-origin", redirect: "manual", headers: { "X-Matgate-Inline": "1" } });
      return r.status;
    }, themeBefore || "system");
    check("the theme is put back", answer, 204);
  }

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
