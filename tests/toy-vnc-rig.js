// What the tests that need a remote desktop share: the toy VNC server, a connection to it in the
// stack under test, and a way to look at what the "remote" received. Cleaned up afterwards - a
// connection left behind would be the first "Connect" button of every other test.
const { spawn } = require("child_process");
const BASE = "http://127.0.0.1:18091";
const TOY = "http://127.0.0.1:5902";
const NAME = "Toy-VNC";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const REMOTE = { width: 1600, height: 900 };

async function toyIsUp() {
  try { return (await fetch(TOY + "/log")).ok; } catch { return false; }
}

// Starts the server unless one is already answering. Returns a function that stops what it started.
async function startServer() {
  if (await toyIsUp()) { return () => {}; }
  const child = spawn(process.execPath, [__dirname + "/toy-vnc.js"], { stdio: "ignore" });
  for (let i = 0; i < 40 && !(await toyIsUp()); i++) { await sleep(150); }
  return () => { try { child.kill(); } catch { /* already gone */ } };
}

// The id of the connection to the toy server; made through the admin form when it is not there.
async function ensureConnection(page) {
  const find = () => page.evaluate(async name => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    const list = JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
    const hit = list.find(s => s.name === name);
    return hit ? hit.id : null;
  }, NAME);
  let id = await find();
  if (id) { return id; }
  await page.goto(BASE + "/admin/servers/new", { waitUntil: "networkidle" });
  await sleep(500);
  await page.evaluate(name => {
    const set = (field, value) => {
      const el = document.querySelector("[name='" + field + "']");
      if (el) { el.value = value; el.dispatchEvent(new Event("change", { bubbles: true })); }
    };
    set("name", name); set("protocol", "Vnc"); set("host", "host.docker.internal"); set("port", "5901");
    document.querySelector("[name='name']").form.requestSubmit();
  }, NAME);
  await sleep(2500);
  id = await find();
  return id;
}

async function removeConnection(page) {
  await page.evaluate(async name => {
    const parse = html => new DOMParser().parseFromString(html, "text/html");
    const doc = parse(await (await fetch("/admin?tab=servers", { credentials: "same-origin" })).text());
    for (const row of doc.querySelectorAll("tr")) {
      const cell = ((row.querySelector("td") || {}).textContent || "").trim();
      const form = row.querySelector("form[action$='/delete']");
      if (form && cell === name) {
        await fetch(form.getAttribute("action"), { method: "POST", body: new FormData(form), credentials: "same-origin", redirect: "manual" });
      }
    }
  }, NAME);
}

const log = async () => (await (await fetch(TOY + "/log")).json()).log;
const pointers = async () => (await log()).filter(e => e.type === "pointer");
const clear = () => fetch(TOY + "/clear", { method: "POST" });
const lastPointer = async () => { const list = await pointers(); return list.length ? list[list.length - 1] : null; };

module.exports = { BASE, TOY, NAME, REMOTE, sleep, startServer, ensureConnection, removeConnection, log, pointers, clear, lastPointer };
