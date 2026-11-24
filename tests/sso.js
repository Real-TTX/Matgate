// Signing in through MatIdentity (0.42.0), end to end against a real MatIdentity: the script builds its image from
// the MatIdentity checkout next to this one (../../MatIdentity), starts it and a Matgate of its own on the test
// stack's network, registers Matgate as an app there (with the groups sent), and goes through it in a browser:
//   - a Matgate user with the same address as the MatIdentity account is linked only after its password,
//   - after that the account signs in through MatIdentity alone,
//   - the MatIdentity group puts the user into the Matgate group linked to it - which gives a connection,
//   - out of the MatIdentity group, at the next sign-in out of the Matgate group, and the connection is gone,
//   - someone unknown to Matgate gets a user of their own, with no rights.
// Both containers go again at the end.
//
//   docker compose -f docker-compose.yml up -d --build     (the test stack, as for the tests)
//   node sso.js
const { chromium } = require("playwright-core");
const { execFileSync } = require("child_process");
const path = require("path");
const fs = require("fs");

const GATE = "http://127.0.0.1:18096";
const IDP = "http://127.0.0.1:18097";
const IDP_CONTAINER = "matgate-sso-idp";
const GATE_CONTAINER = "matgate-sso";
const IDP_KEY = "sso-test-only-key";
const MATIDENTITY_SOURCE = path.join(__dirname, "..", "..", "MatIdentity");
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const docker = args => execFileSync("docker", args, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
const waitFor = async (url, what) => {
  for (let i = 0; i < 120; i++) {
    try { if ((await fetch(url)).ok) { return; } } catch { /* starting */ }
    await sleep(500);
  }
  throw new Error(what + " did not start");
};
const idp = async (method, url, body) => {
  const response = await fetch(IDP + url, {
    method,
    headers: { "X-Api-Key": IDP_KEY, "Content-Type": url.startsWith("/scim") ? "application/scim+json" : "application/json" },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  if (!response.ok) { throw new Error(method + " " + url + " -> " + response.status + " " + text); }
  return text ? JSON.parse(text) : null;
};

(async () => {
  if (!fs.existsSync(path.join(MATIDENTITY_SOURCE, "Dockerfile"))) {
    console.log("skipped: no MatIdentity checkout at " + MATIDENTITY_SOURCE);
    return;
  }

  console.log("building MatIdentity ...");
  execFileSync("docker", ["build", "-q", "-t", "matgate-test-matidentity", MATIDENTITY_SOURCE], { stdio: ["ignore", "ignore", "inherit"] });
  for (const name of [IDP_CONTAINER, GATE_CONTAINER]) { try { docker(["rm", "-f", name]); } catch { /* not there */ } }
  docker(["run", "-d", "--name", IDP_CONTAINER, "--network", "matgate-tests_default", "-p", "127.0.0.1:18097:2300",
    "-e", "ASPNETCORE_ENVIRONMENT=Development", "-e", "MAT_API_KEY=" + IDP_KEY, "-e", "MatIdentity__Issuer=" + IDP + "/",
    "matgate-test-matidentity"]);
  await waitFor(IDP + "/.well-known/openid-configuration", "MatIdentity");

  const b = await chromium.launch({ headless: true });
  try {
    // ---- Matgate as an app of MatIdentity, with the groups sent; a group "Technik" with the dev account in it
    const app = await idp("PUT", "/api/v1/clients/matgate", {
      displayName: "Matgate", homeUrl: GATE + "/", redirectUris: [GATE + "/signin-oidc"], allowSignIn: true,
      renewSecret: true, openToEveryone: true, sendsGroups: true,
    });
    check("MatIdentity sends groups to the app", app.sendsGroups, true);
    const users = await idp("GET", "/scim/v2/Users?filter=" + encodeURIComponent('userName eq "admin@example.com"'));
    const person = (users.Resources || [])[0] || (await idp("GET", "/scim/v2/Users")).Resources.find(u => /admin/.test(u.userName));
    const group = await idp("POST", "/scim/v2/Groups", {
      schemas: ["urn:ietf:params:scim:schemas:core:2.0:Group"], displayName: "Technik", members: [{ value: person.id }],
    });

    docker(["run", "-d", "--name", GATE_CONTAINER, "--network", "matgate-tests_default", "-p", "127.0.0.1:18096:8080",
      "-e", "MATGATE_ADMIN_USER=root", "-e", "MATGATE_ADMIN_PASSWORD=sso-test-only-pw", "-e", "MATGATE_ADMIN_EMAIL=root@example.com",
      "-e", "MATGATE_GUACAMOLE=off", "-e", "MATGATE_AGENT_UPDATE_URL=http://127.0.0.1:9/",
      "matgate-tests-matgate"]);
    await waitFor(GATE + "/login", "Matgate");

    // ---- the Matgate side: a local user with the account's address, a connection, a group linked to "Technik"
    const root = await (await b.newContext({ viewport: { width: 1300, height: 900 } })).newPage();
    const signInLocal = async (page, user, password) => {
      await page.goto(GATE + "/login?local=1", { waitUntil: "networkidle" });
      await page.fill("input[name='username']", user);
      await page.fill("input[name='password']", password);
      await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
      await sleep(800);
    };
    await signInLocal(root, "root", "sso-test-only-pw");

    // ---- set up in the administration (Administration -> Sign-in), not in the environment
    await root.goto(GATE + "/admin?tab=sso", { waitUntil: "networkidle" });
    check("the administration has the sign-in page with both services", await root.evaluate(() => [...document.querySelectorAll("[data-sso-card]")].map(c => c.dataset.ssoCard)), ["matIdentity", "entra"]);
    check("it shows the address to give MatIdentity", await root.evaluate(() => [...document.querySelectorAll("[data-sso-copy]")].map(b => b.dataset.ssoCopy)[0]), GATE + "/signin-oidc");
    await root.check("[data-sso-card='matIdentity'] [data-sso-enabled]", { force: true });
    await root.fill("[name='matIdentityAuthority']", IDP + "/");
    await root.fill("[name='matIdentityClientId']", "matgate");
    await root.fill("[name='matIdentityClientSecret']", app.clientSecret);
    await root.fill("[name='matIdentityInternalUrl']", "http://" + IDP_CONTAINER + ":2300");
    await root.click("[data-sso-test='matIdentity']");
    let tested = "";
    for (let i = 0; i < 30 && !/Erreicht|Reached|Nicht|Not/.test(tested); i++) { await sleep(300); tested = await root.evaluate(() => document.querySelector("[data-sso-card='matIdentity'] [data-sso-test-result]").textContent); }
    check("\"Check the connection\" reaches MatIdentity", /Erreicht|Reached/.test(tested), true);
    await Promise.all([root.waitForLoadState("networkidle"), root.click("[data-sso-form] button[type='submit']")]);
    await sleep(600);
    await root.goto(GATE + "/admin?tab=sso", { waitUntil: "networkidle" });
    check("saved, it is on and the secret is not shown again", await root.evaluate(() => [
      document.querySelector("[data-sso-card='matIdentity']").classList.contains("is-on"),
      document.querySelector("[name='matIdentityClientSecret']").value,
      /Gespeichert|Stored/.test(document.querySelector("[name='matIdentityClientSecret']").placeholder)]), [true, "", true]);
    await root.goto(GATE + "/admin/users/new", { waitUntil: "networkidle" });
    await root.fill("input[name='username']", "anna");
    await root.fill("input[name='email']", person.emails ? person.emails[0].value : "admin@example.com");
    await root.fill("input[name='password']", "anna-local-pw-1234");
    await root.evaluate(() => document.querySelector("input[name='username']").form.requestSubmit());
    await sleep(1200);
    await root.goto(GATE + "/admin/servers/new", { waitUntil: "networkidle" });
    await root.evaluate(() => {
      document.querySelector("[data-protocol-option='Ssh']").click();
      const set = (field, value) => { const el = [...document.querySelectorAll("[name='" + field + "']")].find(e => !e.disabled); if (el) { el.value = value; el.dispatchEvent(new Event("input", { bubbles: true })); } };
      set("name", "Technik-Server"); set("host", "ssh"); set("port", "2222");
      document.querySelector("[name='name']").form.requestSubmit();
    }).catch(() => { });
    await sleep(1500);
    await root.goto(GATE + "/admin/groups/new", { waitUntil: "networkidle" });
    check("the group page has the field for linked groups", await root.evaluate(() => !!document.querySelector("[data-group-external-field]")), true);
    await root.fill("input[name='name']", "Technik-Team");
    await root.fill("[data-group-external-field]", "matidentity:Technik");
    await root.selectOption("[data-group-all] select", "Connect");
    await Promise.all([root.waitForLoadState("networkidle"), root.click("button[form='group-form']")]);
    await sleep(800);

    // ---- the person, in another browser
    const anna = await (await b.newContext({ viewport: { width: 1300, height: 900 } })).newPage();
    anna.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await anna.goto(GATE + "/login", { waitUntil: "networkidle" });
    check("the sign-in page offers MatIdentity", await anna.evaluate(() => !!document.querySelector("[data-sso='matidentity']")), true);
    await Promise.all([anna.waitForURL(u => u.toString().startsWith(IDP), { timeout: 20000 }), anna.click("[data-sso='matidentity']")]);
    await anna.fill("input[name='Input.Username']", "admin@example.com");
    await anna.fill("input[name='Input.Password']", "Start12345");
    await Promise.all([anna.waitForURL(u => u.toString().startsWith(GATE), { timeout: 30000 }), anna.click("form button[type='submit']:not([data-passkey-signin])")]);
    await anna.waitForLoadState("networkidle");
    check("an existing address asks for that account's password first", await anna.evaluate(() => !!document.querySelector("[data-sso-link-notice]") && !document.querySelector(".login-form").hidden), true);
    await anna.fill("input[name='username']", "anna");
    await anna.fill("input[name='password']", "anna-local-pw-1234");
    await Promise.all([anna.waitForURL(u => !/\/login/.test(u.toString()), { timeout: 20000 }), anna.click(".login-submit")]);
    await sleep(800);
    const servers = page => page.evaluate(async () => {
      const text = await (await fetch("/", { credentials: "same-origin" })).text();
      return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]).filter(s => !s.areaKind && (s.protocol || "").toUpperCase() !== "LOCAL").map(s => s.name);
    });
    check("linked, and in the group through MatIdentity's group: the connection is there", await servers(anna), ["Technik-Server"]);

    // ---- from now on MatIdentity alone (its session is still open: straight through)
    const signOut = async page => {
      await page.evaluate(async () => {
        const text = await (await fetch("/", { credentials: "same-origin" })).text();
        const token = JSON.parse(text.match(/const csrfToken = ("[^"]*")/)[1]);
        const body = new FormData(); body.append("_csrf", token);
        await fetch("/logout", { method: "POST", body, credentials: "same-origin" });
      });
      await page.context().clearCookies({ name: "Matgate.Auth" });
    };
    const signInSso = async page => {
      await page.goto(GATE + "/login", { waitUntil: "networkidle" });
      await Promise.all([page.waitForURL(u => u.toString().startsWith(GATE) && !/\/login/.test(u.toString()), { timeout: 30000 }), page.click("[data-sso='matidentity']")]);
      await sleep(800);
    };
    await signOut(anna);
    await signInSso(anna);
    check("signed in through MatIdentity alone, as the linked user", await anna.evaluate(async () => (await (await fetch("/account", { credentials: "same-origin" })).text()).includes("anna")), true);

    await root.goto(GATE + "/admin?tab=users&view=list", { waitUntil: "networkidle" });
    const annaId = await root.evaluate(() => { const a = [...document.querySelectorAll("a[href^='/admin/users/']")].find(x => x.textContent.trim() === "anna"); return a ? a.getAttribute("href").split("/").pop() : null; });
    await root.goto(GATE + "/admin/users/" + annaId, { waitUntil: "networkidle" });
    check("the user page shows the linked account and the group MatIdentity named", await root.evaluate(() => {
      const row = document.querySelector("[data-external-login='matidentity']");
      return !!row && row.innerText.includes("matidentity:Technik");
    }), true);

    // ---- out of the MatIdentity group: out of the Matgate group at the next sign-in
    await idp("PUT", "/scim/v2/Groups/" + group.id, { schemas: ["urn:ietf:params:scim:schemas:core:2.0:Group"], displayName: "Technik", members: [] });
    await signOut(anna);
    await signInSso(anna);
    check("out of MatIdentity's group, out of the linked group - the connection is gone", await servers(anna), []);

    // ---- someone Matgate does not know yet gets a user of their own, without rights
    const created = await idp("POST", "/scim/v2/Users", {
      schemas: ["urn:ietf:params:scim:schemas:core:2.0:User"], userName: "bernd", displayName: "Bernd Neu",
      emails: [{ value: "bernd@example.com", primary: true }], password: "Kx7-test-only-9531", active: true,
    });
    const bernd = await (await b.newContext({ viewport: { width: 1300, height: 900 } })).newPage();
    await bernd.goto(GATE + "/login", { waitUntil: "networkidle" });
    await Promise.all([bernd.waitForURL(u => u.toString().startsWith(IDP), { timeout: 20000 }), bernd.click("[data-sso='matidentity']")]);
    await bernd.fill("input[name='Input.Username']", "bernd");
    await bernd.fill("input[name='Input.Password']", "Kx7-test-only-9531");
    await Promise.all([bernd.waitForURL(u => u.toString().startsWith(GATE), { timeout: 30000 }), bernd.click("form button[type='submit']:not([data-passkey-signin])")]);
    await bernd.waitForLoadState("networkidle");
    await sleep(800);
    check("a new person is signed in straight away", !/\/login/.test(bernd.url()), true);
    check("with no connection", await servers(bernd), []);
    await root.goto(GATE + "/admin?tab=users&view=list", { waitUntil: "networkidle" });
    check("and is a user of Matgate now", await root.evaluate(() => [...document.querySelectorAll("a[href^='/admin/users/']")].some(a => a.textContent.trim() === "bernd")), true);
    check("SCIM made the person", !!created.id, true);
  }
  finally {
    await b.close();
    for (const name of [IDP_CONTAINER, GATE_CONTAINER]) { try { docker(["rm", "-f", name]); } catch { /* gone */ } }
  }

  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); for (const name of [IDP_CONTAINER, GATE_CONTAINER]) { try { docker(["rm", "-f", name]); } catch { } } process.exit(1); });
