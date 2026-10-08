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

// What the tests create, by exact name. A run that is aborted halfway leaves these behind, and the
// next run then fails on "name already taken" - a failure that looks real and says nothing about
// the thing it was meant to test. Removed before a run starts; nothing else is touched.
const LEFTOVER_USERS = ["UmbauTester", "FreigabeTester", "VorgabeTester", "TestGrossKlein", "DialogTest"];
const LEFTOVER_SHARES = ["Dialogablage", "Dialogablage neu", "DissolvedShare", "Frisch angelegt",
  "Pruefablage", "Zeigerablage", "My place shared", "Shared place shared", "Testablage", "Ansichtssache"];

async function purgeLeftovers(page) {
  const removed = await page.evaluate(async ([users, shares]) => {
    const parse = html => new DOMParser().parseFromString(html, "text/html");
    const text = async url => (await fetch(url, { credentials: "same-origin" })).text();
    // The delete forms carry their own anti-forgery field, so posting the form as it is works.
    const send = form => fetch(form.getAttribute("action"), {
      method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual",
    });
    const done = [];

    const userNames = users.map(name => name.toLowerCase());
    const userPage = parse(await text("/admin/users"));
    for (const row of userPage.querySelectorAll("tr")) {
      const name = ((row.querySelector("td") || {}).textContent || "").trim().toLowerCase();
      const form = row.querySelector("form[action*='/delete']");
      if (form && userNames.includes(name)) { await send(form); done.push("user " + name); }
    }

    const shareList = parse(await text("/workspaces"));
    const seen = new Set();
    for (const link of shareList.querySelectorAll("tr td:first-child a[href^='/workspaces/']")) {
      const id = (link.getAttribute("href") || "").split("/").pop();
      const name = (link.textContent || "").trim();
      if (!shares.includes(name) || seen.has(id)) { continue; }
      seen.add(id);
      const form = parse(await text("/workspaces/" + id)).querySelector("form[action$='/delete']");
      if (form) { await send(form); done.push("share " + name); }
    }
    return done;
  }, [LEFTOVER_USERS, LEFTOVER_SHARES]);
  console.log("leftovers: " + (removed.length ? "removed " + removed.join(", ") : "none"));
}

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
  await purgeLeftovers(page);
  for (const name of CONNECTIONS) { await seedConnection(page, name); }
  await browser.close();
  seedBigFile();
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
