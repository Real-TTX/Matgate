// SSH keys: a connection (or stored credentials) signs in with a private key - one without a passphrase, one
// locked with it (the password is then the passphrase). Checked against the stack's ssh-key host, which lets in
// by key only: SFTP lists files, the own engine's terminal shows a shell. The key is never shown again.
const { chromium } = require("playwright-core");
const fs = require("fs");
const path = require("path");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const PLAIN = fs.readFileSync(path.join(__dirname, "ssh-key", "plain_ed25519"), "utf8");
const LOCKED = fs.readFileSync(path.join(__dirname, "ssh-key", "locked_ed25519"), "utf8");

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const context = await b.newContext({ viewport: { width: 1300, height: 900 } });
  const page = await context.newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const TAG = Date.now().toString(36);
  const made = [];
  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  const connection = async (name, protocol, fields) => {
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await sleep(400);
    await page.evaluate(([n, p, f]) => {
      document.querySelector("[data-protocol-option='" + p + "']").click();
      const set = (field, value) => { const all = [...document.querySelectorAll("[name='" + field + "']")]; const el = all.find(e => !e.disabled) || all[0]; if (el.type === "checkbox") { el.checked = !!value; } else { el.value = value; } el.dispatchEvent(new Event("change", { bubbles: true })); };
      set("name", n);
      Object.entries(f).forEach(([field, value]) => set(field, value));
      document.querySelector("[name='name']").form.requestSubmit();
    }, [name, protocol, fields]).catch(() => {});
    await sleep(1500);
    const server = (await servers()).find(s => s.name === name);
    if (server) { made.push(server.id); }
    return server;
  };
  const canList = id => page.evaluate(async i => (await fetch("/api/files/" + i + "/list?path=/", { credentials: "same-origin" })).ok, id);
  const base = { host: "ssh-key", port: "2222", targetUserName: "demo" };
  let credential = null;

  try {
    // ---- the form
    await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
    await page.evaluate(() => document.querySelector("[data-protocol-option='Sftp']").click());
    await sleep(200);
    check("SFTP asks for a key", await page.evaluate(() => !!document.querySelector("[name='privateKey']").offsetParent), true);
    await page.evaluate(() => document.querySelector("[data-protocol-option='Rdp']").click());
    await sleep(200);
    check("RDP does not", await page.evaluate(() => !!document.querySelector("[name='privateKey']").offsetParent), false);

    // ---- SFTP with keys
    const plain = await connection("Key-plain-" + TAG, "Sftp", { ...base, privateKey: PLAIN });
    check("a key without a passphrase signs in", !!plain && await canList(plain.id), true);
    const locked = await connection("Key-locked-" + TAG, "Sftp", { ...base, privateKey: LOCKED, targetPassword: "test-only-passphrase" });
    check("a locked key signs in with its passphrase as the password", !!locked && await canList(locked.id), true);
    const lockedNoPass = await connection("Key-nopass-" + TAG, "Sftp", { ...base, privateKey: LOCKED });
    check("without its passphrase it does not", !!lockedNoPass && await canList(lockedNoPass.id), false);
    const noKey = await connection("Key-none-" + TAG, "Sftp", { ...base, targetPassword: "demo-only-pw" });
    check("the server lets in by key only (a password does not)", !!noKey && await canList(noKey.id), false);

    // ---- never shown again; kept when saved without; removed when asked
    await page.goto(BASE + "/admin/servers/" + plain.id, { waitUntil: "networkidle" });
    check("the key is not on the connection's page again", await page.evaluate(() => [document.documentElement.outerHTML.includes("b3BlbnNzaC1rZXktdjE"), document.querySelector("[name='privateKey']").value, !!document.querySelector("[name='clearPrivateKey']")]), [false, "", true]);
    await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='server-edit-form']")]);
    await sleep(500);
    check("saved without one, the connection keeps its key", await canList(plain.id), true);
    await page.goto(BASE + "/admin/servers/" + plain.id, { waitUntil: "networkidle" });
    await page.check("[name='clearPrivateKey']");
    await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='server-edit-form']")]);
    await sleep(500);
    check("removed, it does not sign in any more", await canList(plain.id), false);

    // ---- stored credentials with a key
    const NAME = "Keylogin-" + TAG;
    await page.goto(BASE + "/credentials/new", { waitUntil: "networkidle" });
    await page.fill("#credential-form [name='name']", NAME);
    await page.fill("#credential-form [name='userName']", "demo");
    await page.fill("#credential-form [name='password']", "test-only-passphrase");
    await page.fill("#credential-form [name='privateKey']", LOCKED);
    await Promise.all([page.waitForLoadState("networkidle"), page.click("button[form='credential-form']")]);
    await sleep(500);
    credential = await page.evaluate(n => {
      const row = [...document.querySelectorAll("[data-credential-row]")].find(r => r.querySelector("strong").textContent === n);
      const link = row && row.querySelector("a[href^='/credentials/']");
      return link ? link.getAttribute("href").split("/").pop() : null;
    }, NAME);
    check("credentials take a key", !!credential, true);
    const viaCredential = await connection("Key-cred-" + TAG, "Sftp", { host: "ssh-key", port: "2222", credentialId: credential });
    check("a connection with those credentials signs in by their key", !!viaCredential && await canList(viaCredential.id), true);
    await page.goto(BASE + "/credentials/" + credential, { waitUntil: "networkidle" });
    check("the credentials' page does not show the key", await page.evaluate(() => [document.documentElement.outerHTML.includes("b3BlbnNzaC1rZXktdjE"), !!document.querySelector("[name='clearPrivateKey']")]), [false, true]);

    // ---- the own engine's terminal: by key, straight to the shell
    const ssh = await connection("Key-ssh-" + TAG, "Ssh", { ...base, privateKey: LOCKED, targetPassword: "test-only-passphrase", engine: "" });
    const term = await context.newPage();
    await term.goto(BASE + "/ssh/" + ssh.id + "/terminal", { waitUntil: "domcontentloaded" });
    const screenText = () => term.evaluate(() => document.querySelector(".xterm-rows") ? document.querySelector(".xterm-rows").textContent : "");
    for (let i = 0; i < 80 && !/\$/.test(await screenText()); i++) { await sleep(300); }
    check("SSH: by key the shell opens without asking", [/\$/.test(await screenText()), /password|Passphrase/i.test(await screenText())], [true, false]);
    await term.close();

    // ---- a locked key without the passphrase: the terminal asks for it
    const asking = await connection("Key-ssh-ask-" + TAG, "Ssh", { ...base, privateKey: LOCKED, engine: "" });
    const ask = await context.newPage();
    await ask.goto(BASE + "/ssh/" + asking.id + "/terminal", { waitUntil: "domcontentloaded" });
    const askText = () => ask.evaluate(() => document.querySelector(".xterm-rows") ? document.querySelector(".xterm-rows").textContent : "");
    for (let i = 0; i < 40 && !/Passphrase/.test(await askText()); i++) { await sleep(300); }
    check("SSH: a locked key without its passphrase - the terminal asks for it", /Passphrase/.test(await askText()), true);
    await ask.keyboard.type("test-only-passphrase", { delay: 15 });
    await ask.keyboard.press("Enter");
    for (let i = 0; i < 40 && !/\$/.test(await askText()); i++) { await sleep(300); }
    check("SSH: and opens the shell with it", /\$/.test(await askText()), true);
    await ask.close();

    // ---- Guacamole gets the key too (its log says whether the key was taken and the login worked)
    const guac = await connection("Key-guac-" + TAG, "Ssh", { ...base, privateKey: LOCKED, targetPassword: "test-only-passphrase", engine: "guacamole" });
    const since = new Date().toISOString();
    await page.goto(BASE + "/?open=" + guac.id, { waitUntil: "networkidle" });
    // guacd writes its log to stderr.
    const guacLog = () => { const r = require("child_process").spawnSync("docker", ["logs", "--since", since, "matgate-tests-guacd-1"], { encoding: "utf8" }); return String(r.stdout || "") + String(r.stderr || ""); };
    let log = "";
    for (let i = 0; i < 40 && !/SSH connection successful|Public key authentication failed|Unable to authenticate/i.test(log); i++) { await sleep(500); log = guacLog(); }
    check("Guacamole: signs in by the key", [/Auth key successfully imported/i.test(log), /SSH connection successful/i.test(log)], [true, true]);
  }
  finally {
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    for (const id of made) {
      await page.evaluate(async sid => {
        const html = await (await fetch("/admin/servers/" + sid, { credentials: "same-origin" })).text();
        const doc = new DOMParser().parseFromString(html, "text/html");
        const form = doc.querySelector("form[action$='/delete']");
        if (form) { await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin" }); }
      }, id);
    }
    if (credential) {
      await page.evaluate(async cid => {
        const html = await (await fetch("/credentials/" + cid, { credentials: "same-origin" })).text();
        const doc = new DOMParser().parseFromString(html, "text/html");
        const form = doc.querySelector("form[action$='/delete']");
        if (form) { await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin" }); }
      }, credential);
    }
  }

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
