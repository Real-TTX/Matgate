// Sharing a place that already exists - and the guard: someone else's place only as an
// administrator, and only with a password.
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
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1400, height: 1000 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1800);

  const token = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
  });
  const places = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const pick = kind => (list.find(x => (x.areaKind || "") === kind) || {}).id || null;
    return { own: pick("user"), shared: pick("global"), workspace: pick("workspace") };
  });
  console.log("     places: " + JSON.stringify(places));
  check("own and shared place found", !!(places.own && places.shared), true);

  const fetchForm = id => page.evaluate(async ([i, m]) => {
    const r = await fetch("/api/files/" + i + "/share-form", { headers: { "X-Matgate-Csrf": m }, credentials: "same-origin" });
    return { status: r.status, text: await r.text() };
  }, [id, token]);
  const share = (id, data) => page.evaluate(async ([i, m, d]) => {
    const f = new FormData();
    Object.entries(d).forEach(([k, v]) => f.append(k, v));
    const r = await fetch("/api/files/" + i + "/share", { method: "POST", body: f, credentials: "same-origin", headers: { "X-Matgate-Csrf": m } });
    return { status: r.status, text: (await r.text()).slice(0, 200) };
  }, [id, token, data]);

  // --- Your own place: a form without a mandatory password
  const f1 = await fetchForm(places.own);
  check("form for your own place", f1.status, 200);
  check("there the password is optional", /name="password"[^>]*required/.test(f1.text), false);

  // --- The shared place: password mandatory
  const f2 = await fetchForm(places.shared);
  check("form for the shared place", f2.status, 200);
  check("there the password is mandatory", /name="password"[^>]*required/.test(f2.text), true);

  // --- Refused without a password
  const without = await share(places.shared, { name: "Attempt", publicAccessHours: "24" });
  console.log("     shared without a password: " + JSON.stringify(without));
  check("the shared place without a password is refused", without.status, 400);

  // --- A too short password as well
  const short = await share(places.shared, { name: "Attempt", password: "tiny", publicAccessHours: "24" });
  check("a too short password is refused", short.status, 400);

  // --- With a password it works
  const withPassword = await share(places.shared, { name: "Shared place shared", password: "test-only-pw", publicAccessHours: "24" });
  console.log("     shared with a password: " + JSON.stringify(withPassword));
  check("with a password the share succeeds", withPassword.status, 200);
  const sharedId = (() => { try { return JSON.parse(withPassword.text).id; } catch { return null; } })();

  // --- Your own one works without a password too
  const own = await share(places.own, { name: "My place shared", publicAccessHours: "24" });
  console.log("     own without a password: " + JSON.stringify(own));
  check("your own place without a password succeeds", own.status, 200);
  const ownId = (() => { try { return JSON.parse(own.text).id; } catch { return null; } })();

  // --- A workspace place is a share already
  if (places.workspace) {
    const already = await fetchForm(places.workspace);
    check("a share cannot be shared a second time", already.status, 400);
  }

  // --- The link really points at the place's files
  if (ownId) {
    const publicView = await b.newContext({ viewport: { width: 1100, height: 800 } });
    const p2 = await publicView.newPage();
    await p2.goto(BASE + "/workspace/" + ownId, { waitUntil: "networkidle" });
    await sleep(900);
    const view = await p2.evaluate(() => ({
      signedIn: !!document.getElementById("matgate-shell"),
      table: !!document.querySelector(".file-table"),
      text: (document.body.innerText || "").slice(0, 120).replace(/\s+/g, " "),
    }));
    console.log("     publicView: " + JSON.stringify(view));
    check("the link is reachable without signing in", view.signedIn, false);
    check("and shows the files", view.table, true);
    await p2.screenshot({ path: "share-publicView.png" });
    await publicView.close();
  }

  // --- The one with a password asks for it
  if (sharedId) {
    const c3 = await b.newContext({ viewport: { width: 1100, height: 800 } });
    const p3 = await c3.newPage();
    await p3.goto(BASE + "/workspace/" + sharedId, { waitUntil: "networkidle" });
    await sleep(800);
    const asks = await p3.evaluate(() => !!document.querySelector("input[type='password']"));
    check("the protected share asks for the password", asks, true);
    await c3.close();
  }

  // --- And the more important half of the guard: an ordinary user must not share someone
  //     else's place at all, not even with a password.
  const NEU = "FreigabeTester";
  const PW = "test-only-pw-1234";
  await page.goto(BASE + "/admin/users/new", { waitUntil: "networkidle" });
  await page.fill("input[name='username']", NEU);
  await page.fill("input[name='password']", PW);
  await page.evaluate(() => {
    const f = document.querySelector("input[name='username']").form;
    const g = f.querySelector("[name='fileShareGlobal']");
    if (g) { g.checked = true; }
    const p = f.querySelector("[name='fileSharePersonal']");
    if (p) { p.checked = true; }
    f.requestSubmit();
  });
  await sleep(1800);

  const c4 = await b.newContext({ viewport: { width: 1200, height: 900 } });
  const p4 = await c4.newPage();
  await p4.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p4.fill("input[name='username']", NEU);
  await p4.fill("input[name='password']", PW);
  await p4.click(".login-submit");
  await sleep(1800);
  const token4 = await p4.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return (text.match(/const csrfToken = "([^"]+)"/) || [])[1] || "";
  });
  const foreign = await p4.evaluate(async ([i, m]) => {
    const f = new FormData();
    f.append("name", "Forbidden attempt");
    f.append("password", "a-long-test-only-password");
    f.append("publicAccessHours", "24");
    const r = await fetch("/api/files/" + i + "/share", { method: "POST", body: f, credentials: "same-origin", headers: { "X-Matgate-Csrf": m } });
    return { status: r.status, text: (await r.text()).slice(0, 160) };
  }, [places.shared, token4]);
  console.log("     non-admin on the shared place: " + JSON.stringify(foreign));
  check("an ordinary user must not share the shared place", foreign.status, 403);
  const formForbidden = await p4.evaluate(async ([i, m]) => {
    const r = await fetch("/api/files/" + i + "/share-form", { headers: { "X-Matgate-Csrf": m }, credentials: "same-origin" });
    return r.status;
  }, [places.shared, token4]);
  check("and does not even get the form for it", formForbidden, 403);
  await c4.close();

  await page.goto(BASE + "/admin/users", { waitUntil: "networkidle" });
  await page.evaluate(n => {
    const row = Array.from(document.querySelectorAll("tr")).find(r => r.innerText.includes(n));
    const form = row && row.querySelector("form[action*='/delete']");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  }, NEU);
  await sleep(1300);

  // --- Clean up
  for (const id of [sharedId, ownId].filter(Boolean)) {
    await page.goto(BASE + "/workspaces/" + id, { waitUntil: "networkidle" });
    await page.evaluate(() => {
      const form = document.querySelector("form[action$='/delete']");
      if (form) { form.removeAttribute("data-confirm"); form.submit(); }
    });
    await sleep(1200);
  }

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
