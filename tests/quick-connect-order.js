// The chips of quick connect are the user's to arrange: Account -> Session has them as a list to drag and
// to tick, and the start page and the quick-connect dialog show them in that order. Before, the order was
// fixed and only the ticks were the user's.
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};
const BUILT_IN = ["rdp", "ssh", "vnc", "sftp", "smb", "website", "ftp", "webdav", "mssql", "mysql", "postgres"];
// The dialog lists them in groups (remote access, files, web, databases), each group where its first member stands.
const GROUP = { rdp: 0, ssh: 0, vnc: 0, sftp: 1, smb: 1, ftp: 1, webdav: 1, website: 2, mssql: 3, mysql: 3, postgres: 3 };
const grouped = keys => { const first = []; keys.forEach(k => { if (!first.includes(GROUP[k])) first.push(GROUP[k]); }); return [...keys].sort((a, b) => first.indexOf(GROUP[a]) - first.indexOf(GROUP[b]) || keys.indexOf(a) - keys.indexOf(b)); };

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

  const settings = async () => {
    await page.goto(BASE + "/account?tab=session", { waitUntil: "networkidle" });
    await sleep(400);
  };
  const listed = () => page.evaluate(() => [...document.querySelectorAll("#quick-order-list .action-order-item")].map(item => item.dataset.orderKey + (item.querySelector("[data-order-visible]").checked ? "" : "-")));
  const save = async () => {
    await Promise.all([
      page.waitForNavigation({ waitUntil: "networkidle", timeout: 20000 }),
      page.evaluate(() => document.querySelector("#quick-order-list").closest("form").requestSubmit()),
    ]);
    await sleep(400);
  };
  // Whatever an earlier run left behind: back to the built-in order, everything shown.
  const reset = async () => {
    await settings();
    await page.evaluate(order => {
      const list = document.querySelector("#quick-order-list");
      order.forEach(key => list.appendChild(list.querySelector("[data-order-key='" + key + "']")));
      list.querySelectorAll("[data-order-visible]").forEach(box => { box.checked = true; });
      list.dispatchEvent(new Event("change", { bubbles: true }));
    }, BUILT_IN);
    await save();
  };
  const onHome = async () => {
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(500);
    return page.evaluate(() => ({
      chips: [...document.querySelectorAll(".home2-quick-row [data-home2-qc]")].map(chip => chip.dataset.home2Qc),
      dialog: [...document.querySelectorAll("[data-home2-qc-proto]")].map(chip => chip.dataset.home2QcProto),
      startsOn: document.querySelector("[data-home2-qc-proto-input]").value,
    }));
  };

  await reset();
  await settings();
  check("the settings list the chips in the built-in order, all shown", await listed(), BUILT_IN);
  check("so does the start page, and the dialog", await onHome(), { chips: BUILT_IN, dialog: grouped(BUILT_IN), startsOn: "rdp" });

  // Dragged: the browser to the front. A real pointer, from the entry's grip to the first entry.
  await settings();
  const centre = async key => {
    const box = await (await page.$("#quick-order-list [data-order-key='" + key + "'] .action-order-grip")).boundingBox();
    return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
  };
  await page.evaluate(() => document.querySelector("#quick-order-list").scrollIntoView({ block: "center" }));
  await sleep(200);
  const from = await centre("website");
  const to = await centre("rdp");
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move(from.x, (from.y + to.y) / 2, { steps: 6 });
  await page.mouse.move(to.x, to.y - 2, { steps: 6 });
  await page.mouse.up();
  const dragged = ["website", "rdp", "ssh", "vnc", "sftp", "smb", "ftp", "webdav", "mssql", "mysql", "postgres"];
  check("dragged to the front, the browser stands first in the list", await listed(), dragged);
  // And one of them left out.
  await page.click("#quick-order-list [data-order-key='ftp'] [data-order-visible]");
  await save();
  check("saved: the list comes back as it was left", await listed(), dragged.map(key => key === "ftp" ? "ftp-" : key));
  const shown = dragged.filter(key => key !== "ftp");
  check("the start page and the dialog follow: that order, without the one left out, starting on the first", await onHome(), { chips: shown, dialog: grouped(shown), startsOn: "website" });
  await page.click("[data-home2-more]");
  await page.waitForSelector("[data-home2-proto-dialog][open]", { timeout: 10000 });
  check("the dialog opens on the first chip", await page.evaluate(() => [document.querySelector("[data-home2-qc-proto].active").dataset.home2QcProto, document.querySelector("[data-home2-qc-hostlabel]").textContent]), ["website", "Start URL"]);

  // What the server takes: only chips it knows, each once.
  check("a made-up order cannot bring in what is not a chip", await page.evaluate(async () => {
    const page = new DOMParser().parseFromString(await (await fetch("/account?tab=session", { credentials: "same-origin" })).text(), "text/html");
    const form = page.querySelector("#quick-order-list").closest("form");
    const body = new FormData(form);
    body.set("quickProtocols", "ssh,nonsense,ssh,local,rdp");
    body.set("hiddenQuickProtocols", "nonsense");
    await fetch(form.getAttribute("action"), { method: "POST", body, credentials: "same-origin" });
    const home = new DOMParser().parseFromString(await (await fetch("/", { credentials: "same-origin" })).text(), "text/html");
    return [...home.querySelectorAll(".home2-quick-row [data-home2-qc]")].map(chip => chip.dataset.home2Qc);
  }), ["ssh", "rdp", "vnc", "sftp", "smb", "website", "ftp", "webdav", "mssql", "mysql", "postgres"]);

  await reset();
  check("and back to the built-in order for whoever comes next", (await onHome()).chips, BUILT_IN);

  await b.close();
  if (failures.length) { console.log("failed: " + JSON.stringify(failures)); }
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
