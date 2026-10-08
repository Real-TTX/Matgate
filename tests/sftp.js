// After the jump from SSH.NET: does SFTP still do what it should - list, upload, download,
// delete? That is the only part of the library Matgate uses.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const NAME = "SFTP check";

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const page = await (await b.newContext({ viewport: { width: 1280, height: 950 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await page.click(".login-submit");
  await sleep(1600);

  // --- Create an SFTP connection to the same test machine
  await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
  await sleep(600);
  const created = await page.evaluate((n) => {
    const set = (name, value) => {
      const el = document.querySelector("[name='" + name + "']");
      if (!el) return false;
      el.value = value;
      el.dispatchEvent(new Event("change", { bubbles: true }));
      return true;
    };
    if (!set("name", n)) return "no name field";
    if (!set("protocol", "Sftp")) return "no protocol field";
    set("host", "ssh");
    set("port", "2222");
    set("targetUserName", "demo");
    set("targetPassword", "demo-only-pw");
    document.querySelector("[name='name']").form.requestSubmit();
    return "ok";
  }, NAME);
  check("SFTP connection created", created, "ok");
  await sleep(2500);

  const token = await page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const t = text.match(/const csrfToken = "([^"]+)"/);
    return t ? t[1] : "";
  });
  const id = await page.evaluate(async (n) => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const hits = list.find(s => s.name === n);
    return hits ? hits.id : null;
  }, NAME);
  check("connection is in the list", typeof id === "string", true);

  // --- Listing
  const list = await page.evaluate(async (id) => {
    const r = await fetch("/api/files/" + id + "/list?path=/", { credentials: "same-origin" });
    return { status: r.status, text: (await r.text()).slice(0, 200) };
  }, id);
  console.log("     Auflisten: " + JSON.stringify(list));
  check("listing works", list.status, 200);

  // --- Uploading
  const up = await page.evaluate(async ([id, token]) => {
    const data = new FormData();
    data.append("path", "/config");
    data.append("file", new Blob(["sftp-check"], { type: "text/plain" }), "sftp-check.txt");
    const r = await fetch("/api/files/" + id + "/upload", {
      method: "POST", body: data, credentials: "same-origin",
      headers: { "X-Matgate-Csrf": token },
    });
    return { status: r.status, text: (await r.text()).slice(0, 160) };
  }, [id, token]);
  console.log("     upload: " + JSON.stringify(up));
  check("upload works", up.status, 200);

  // Waiting for the file to show up instead of asking once. The upload has answered by now, but
  // the listing goes through a session of its own on the other side, and under load one question
  // asked straight away can be a moment too early. The wait is measured and printed: a lag that
  // grows would be a defect of the application and must not hide behind a patient test.
  const arrival = await page.evaluate(async (id) => {
    const started = performance.now();
    for (let attempt = 1; attempt <= 40; attempt++) {
      const r = await fetch("/api/files/" + id + "/list?path=/config", { credentials: "same-origin" });
      if ((await r.text()).includes("sftp-check.txt")) {
        return { seen: true, attempts: attempt, ms: Math.round(performance.now() - started) };
      }
      await new Promise(resolve => setTimeout(resolve, 250));
    }
    return { seen: false, attempts: 40, ms: Math.round(performance.now() - started) };
  }, id);
  console.log("     arrival: " + JSON.stringify(arrival));
  check("the file arrived on the other side", arrival.seen, true);
  // Within a couple of seconds is normal; the first look should usually already find it.
  check("and within five seconds", arrival.ms < 5000, true);

  // --- Downloading
  const down = await page.evaluate(async (id) => {
    const r = await fetch("/api/files/" + id + "/download?path=/config/sftp-check.txt", { credentials: "same-origin" });
    return { status: r.status, content: (await r.text()).slice(0, 40) };
  }, id);
  console.log("     download: " + JSON.stringify(down));
  check("the content comes back unchanged", down.content, "sftp-check");

  // --- Create and delete a folder
  const folder = await page.evaluate(async ([id, token]) => {
    const r = await fetch("/api/files/" + id + "/mkdir", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ path: "/config", name: "pruefordner" }),
    });
    return r.status;
  }, [id, token]);
  check("creating a folder works", folder, 200);

  const gone = await page.evaluate(async ([id, token]) => {
    const r = await fetch("/api/files/" + id + "/delete", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ paths: ["/config/sftp-check.txt", "/config/pruefordner"] }),
    });
    return r.status;
  }, [id, token]);
  console.log("     delete: " + gone);

  // --- A write without permission has to give an understandable answer, not an empty 500.
  const forbidden = await page.evaluate(async ([id, token]) => {
    const r = await fetch("/api/files/" + id + "/mkdir", {
      method: "POST", credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-Matgate-Csrf": token },
      body: JSON.stringify({ path: "/", name: "not-allowed" }),
    });
    return { status: r.status, text: (await r.text()).slice(0, 120) };
  }, [id, token]);
  console.log("     without permission: " + JSON.stringify(forbidden));
  check("fehlende Rechte ergeben 403", forbidden.status, 403);
  // The message follows the caller's language - what is checked is that there is one.
  check("with a reason instead of empty",
    /Berechtigung|permission/i.test(forbidden.text), true);

  // --- A path that does not exist
  const gone2 = await page.evaluate(async (id) => {
    const r = await fetch("/api/files/" + id + "/download?path=/config/does-not-exist.txt", { credentials: "same-origin" });
    return { status: r.status, text: (await r.text()).slice(0, 120) };
  }, id);
  console.log("     unknown path: " + JSON.stringify(gone2));
  check("an unknown path gives 404", gone2.status, 404);

  // --- Clean up
  await page.goto(BASE + "/admin/servers/" + id, { waitUntil: "networkidle" });
  await page.evaluate(() => {
    const form = document.querySelector("form[action$='/delete']");
    if (form) { form.removeAttribute("data-confirm"); form.submit(); }
  });
  await sleep(1200);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
