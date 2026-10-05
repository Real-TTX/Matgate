// Puts into a fresh stack what the tests expect to find: one SSH connection to the stack's own
// ssh container, and one big file in the global place. Both are idempotent - running this again
// on a stack that already has them changes nothing.
//
//   node tests/seed.js
//
// run-all.js calls this first, so a fresh stack needs no manual preparation.
const { chromium } = require("playwright-core");
const { execFileSync } = require("child_process");

const BASE = "http://127.0.0.1:18091";
const CONTAINER = "matgate-tests-matgate-1";
// Two connections, because action-bar.js picks two of them and checks their order. The second
// one is never connected to - it only has to exist.
const CONNECTIONS = ["Linux-Testhost", "Linux-Testhost 2"];
const BIG_FILE = "large.bin";
const BIG_FILE_MB = 180;
const sleep = ms => new Promise(r => setTimeout(r, ms));

async function seedConnection(page, connectionName) {
  const existing = await page.evaluate(async name => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const match = text.match(/const availableServers = (\[.*?\]);/s);
    if (!match) { return "no server list"; }
    return JSON.parse(match[1]).some(s => s.name === name) ? "present" : "missing";
  }, connectionName);
  if (existing === "present") { console.log("connection " + connectionName + ": already there"); return; }
  if (existing === "no server list") { console.log("connection " + connectionName + ": cannot read the list"); return; }

  await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
  await sleep(600);
  const created = await page.evaluate(name => {
    const set = (field, value) => {
      const el = document.querySelector("[name='" + field + "']");
      if (!el) { return false; }
      el.value = value;
      el.dispatchEvent(new Event("change", { bubbles: true }));
      return true;
    };
    if (!set("name", name) || !set("protocol", "Ssh")) { return "fields missing"; }
    set("host", "ssh");
    set("port", "2222");
    set("targetUserName", "demo");
    set("targetPassword", "demo-only-pw");
    document.querySelector("[name='name']").form.requestSubmit();
    return "ok";
  }, connectionName);
  await sleep(2500);
  console.log("connection " + connectionName + ": " + created);
}

// The big file is made inside the container rather than uploaded: 180 MB through the browser
// would take longer than the test it serves.
function seedBigFile() {
  const script = "test -f /files/global/" + BIG_FILE +
    " || dd if=/dev/zero of=/files/global/" + BIG_FILE + " bs=1M count=" + BIG_FILE_MB + " 2>/dev/null";
  try {
    execFileSync("docker", ["exec", CONTAINER, "sh", "-c", "mkdir -p /files/global && " + script]);
    console.log(BIG_FILE + ": present (" + BIG_FILE_MB + " MB)");
  } catch (e) {
    console.log(BIG_FILE + ": could not be created - " + (e.message || "").split("\n")[0]);
    console.log("  (download-large.js needs it; the other scripts do not)");
  }
}

(async () => {
  let browser;
  try { browser = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { browser = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await browser.newContext()).newPage();
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1800);
  for (const name of CONNECTIONS) { await seedConnection(page, name); }
  await browser.close();
  seedBigFile();
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
