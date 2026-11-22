// The tunnel: an agent in another network (agent-tunnel, standing in a network of its own with hidden-ssh in it,
// which Matgate cannot reach) connects to Matgate itself - its connection in Matgate has a host nobody can reach -
// and Matgate gets to hidden-ssh through it. Only where the agent's TunnelAllow lets it.
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
  let b = await chromium.launch({ headless: true });
  const page = await (await b.newContext({ viewport: { width: 1300, height: 900 } })).newPage();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await page.goto(BASE + "/login", { waitUntil: "networkidle" });
  await page.fill("input[name=\"username\"]", "admin");
  await page.fill("input[name=\"password\"]", "test-only-pw");
  await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
  await sleep(1000);

  const servers = () => page.evaluate(async () => {
    const text = await (await fetch("/", { credentials: "same-origin" })).text();
    return JSON.parse(text.match(/const availableServers = (\[.*?\]);/s)[1]);
  });
  const connection = async (name, protocol, fields) => {
    const existing = (await servers()).find(s => s.name === name);
    if (existing) { return existing; }
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
    return (await servers()).find(s => s.name === name);
  };
  const canList = id => page.evaluate(async i => {
    const response = await fetch("/api/files/" + i + "/list?path=/", { credentials: "same-origin" });
    return response.ok ? "ok" : ((await response.json().catch(() => ({}))).error || String(response.status));
  }, id);

  // ---- the agent: a host nobody can reach - it is there only because it connected itself
  const agent = await connection("Tunnel-Kiste", "Agent", { host: "nowhere.invalid", port: "7488", targetPassword: "test-only-tunnel-agent-token-1234", ignoreCertificate: true });
  check("the agent connection exists", !!agent, true);
  let status = null;
  // The agent tries again and again, waiting longer each time (up to a minute): it may take that long.
  for (let i = 0; i < 45 && !(status && status.ok && /tunnel/i.test(JSON.stringify(status.body))); i++) {
    status = await page.evaluate(async id => { const r = await fetch("/api/agent/" + id + "/status", { credentials: "same-origin" }); return r.ok ? { ok: true, body: await r.json() } : { ok: false }; }, agent.id);
    if (!(status && status.ok && /tunnel/i.test(JSON.stringify(status.body)))) { await sleep(3000); }
  }
  check("an agent that connects to Matgate itself is reached, though its host is not", !!(status && status.ok), true);
  const plugins = status && status.body ? JSON.stringify(status.body) : "";
  check("and it offers its tunnel", /tunnel/i.test(plugins), true);

  // ---- a machine behind it
  const behind = await connection("Hinter-dem-Tunnel", "Sftp", { host: "hidden-ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", tunnelId: agent.id });
  const direct = await connection("Hinter-ohne-Tunnel", "Sftp", { host: "hidden-ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw" });
  check("directly the machine behind it cannot be reached", (await canList(direct.id)) === "ok", false);
  check("through the tunnel it can", await canList(behind.id), "ok");

  // ---- only where the agent lets it
  const elsewhere = await connection("Tunnel-woanders", "Sftp", { host: "ssh", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", tunnelId: agent.id });
  check("the tunnel leads only where the agent's TunnelAllow lets it", (await canList(elsewhere.id)) === "ok", false);

  // ---- WireGuard: the peer configuration the server made, pasted into a connection of its own
  let peer = "";
  for (let i = 0; i < 30 && !/PrivateKey/.test(peer); i++) {
    try { peer = require("child_process").execFileSync("docker", ["exec", "matgate-tests-wg-server-1", "cat", "/config/peer1/peer1.conf"], { encoding: "utf8" }); }
    catch { await sleep(2000); }
  }
  check("the WireGuard server made a peer configuration", /PrivateKey/.test(peer), true);
  const wg = await connection("WG-Test", "WireGuard", { name: "WG-Test", wgConfig: peer });
  check("a WireGuard tunnel is a connection with its peer as target", !!wg && /wg-server/.test(wg.target || ""), true);
  check("but not one to open: it is not among the connections of the side panel and start page", await page.evaluate(() => !!document.querySelector("[data-home2-card] h3") && [...document.querySelectorAll("[data-home2-card] h3")].some(h => h.textContent.trim() === "WG-Test")), false);
  const throughWg = await connection("Hinter-WireGuard", "Sftp", { host: "10.253.82.10", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw", tunnelId: wg.id });
  const wgDirect = await connection("Ohne-WireGuard", "Sftp", { host: "10.253.82.10", port: "2222", targetUserName: "demo", targetPassword: "demo-only-pw" });
  check("directly the address behind WireGuard cannot be reached", (await canList(wgDirect.id)) === "ok", false);
  check("through the WireGuard tunnel it can", await canList(throughWg.id), "ok");

  // ---- the form offers it, and a tunnelled connection opens with Matgate's own engine
  await page.goto(BASE + "/admin/servers/" + behind.id, { waitUntil: "networkidle" });
  check("the connection's page has the tunnel chosen", await page.evaluate(() => document.querySelector("[name='tunnelId']").value), agent.id);

  await b.close();
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
