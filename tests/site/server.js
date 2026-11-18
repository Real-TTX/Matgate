// A website to show through Matgate (tests/website.js). It behaves the way the applications do that a
// proxy under a path cannot show: it reads its own address, sends the browser to paths at its root, sets
// cookies, opens a web socket, forbids framing. No dependencies - plain node.
//
//   :8080  the website itself (reached as "site", and as "other" - a second host name of the same server)
//   :8081  the same machine under another port (what an http-to-https redirect looks like)
//   :8082  something that signs in the way Proxmox does
const http = require("http");
const crypto = require("crypto");
const zlib = require("zlib");

const page = (title, body, head = "") =>
  `<!doctype html><html><head><meta charset="utf-8"><title>${title}</title>${head}</head><body>${body}</body></html>`;
const send = (res, status, type, body, headers = {}) => {
  res.writeHead(status, { "Content-Type": type, ...headers });
  res.end(body);
};
const html = (res, title, body, headers = {}, head = "") => send(res, 200, "text/html; charset=utf-8", page(title, body, head), headers);
const cookiesOf = req => Object.fromEntries((req.headers.cookie || "").split(";").map(p => p.trim()).filter(Boolean).map(p => {
  const i = p.indexOf("=");
  return i < 0 ? [p, ""] : [p.slice(0, i), p.slice(i + 1)];
}));
const bodyOf = req => new Promise(resolve => {
  const chunks = [];
  req.on("data", c => chunks.push(c));
  req.on("end", () => resolve(Buffer.concat(chunks)));
});
const big = crypto.createHash("sha256").update("matgate").digest();
const download = Buffer.alloc(1024 * 1024);
for (let i = 0; i < download.length; i++) { download[i] = big[i % big.length] ^ (i & 255); }

const site = async (req, res) => {
  const url = new URL(req.url, "http://" + req.headers.host);
  const path = url.pathname;
  const host = req.headers.host || "";

  if (host.startsWith("other")) {
    return html(res, "Other", `<h1 id="what">hello from other</h1><p id="host">${host}</p><a id="back" href="http://site:8080/page2">back to the first</a>`);
  }

  if (path === "/") {
    return html(res, "Fixture Home", `
      <h1 id="what">Fixture Home</h1>
      <p id="where"></p>
      <a id="relative" href="page2">relative</a>
      <a id="rooted" href="/page2">rooted</a>
      <a id="absolute" href="http://site:8080/page2?from=absolute">absolute, with the real host</a>
      <a id="external" href="http://other:8080/hello">another website</a>
      <a id="top" href="/page2?from=top" target="_top">replaces the top window</a>
      <form id="login" method="post" action="/login"><input name="user" value="demo"><button id="signin">sign in</button></form>
      <script>document.getElementById('where').textContent = location.pathname + '|' + location.host;</script>`);
  }

  if (path === "/page2") {
    return html(res, "Page Two", `<h1 id="what">page two</h1><p id="query">${url.search}</p><a id="home" href="/">home</a>`);
  }

  // An application that takes its route from its own address - and sends the browser to "/login" at the
  // root when it does not like what it sees. Both are what a path prefix breaks.
  if (path === "/app" || path.startsWith("/app/")) {
    return html(res, "App", `
      <h1 id="what">app</h1>
      <p id="route"></p>
      <button id="settings">settings</button>
      <button id="kick">sign me out</button>
      <script>
        const routes = { '/app/dashboard': 'the dashboard', '/app/settings': 'the settings' };
        function render() {
          document.getElementById('route').textContent = routes[location.pathname] || 'no such page: ' + location.pathname;
          document.title = 'App - ' + (routes[location.pathname] || 'unknown');
        }
        document.getElementById('settings').onclick = () => { history.pushState({}, '', '/app/settings'); render(); };
        document.getElementById('kick').onclick = () => { location.href = '/login?redirect=' + location.pathname; };
        window.addEventListener('popstate', render);
        render();
      </script>`);
  }

  if (path === "/login" && req.method === "GET") {
    return html(res, "Sign in", `<h1 id="what">the website's own sign-in page</h1><p id="query">${url.search}</p>`);
  }

  if (path === "/login" && req.method === "POST") {
    const body = (await bodyOf(req)).toString();
    res.writeHead(302, {
      Location: "http://site:8080/whoami",
      "Set-Cookie": [
        "sid=" + crypto.createHash("md5").update(body).digest("hex") + "; Path=/; HttpOnly; SameSite=Lax; Domain=site",
        "theme=dark; Path=/",
        "Matgate-Site=stolen; Path=/",
      ],
      "X-Posted-Origin": req.headers.origin || "",
    });
    return res.end();
  }

  if (path === "/whoami" || path === "/api/echo") {
    const body = (await bodyOf(req)).toString();
    const seen = {
      method: req.method,
      host,
      origin: req.headers.origin || "",
      referer: req.headers.referer || "",
      forwarded: req.headers["x-forwarded-for"] || req.headers["forwarded"] || req.headers["x-forwarded-host"] || "",
      authorization: req.headers.authorization || "",
      cookies: cookiesOf(req),
      custom: req.headers["x-custom"] || "",
      body,
    };
    if (path === "/api/echo") {
      return send(res, 200, "application/json", JSON.stringify(seen));
    }

    return html(res, "Who am I", `<h1 id="what">who am i</h1><pre id="seen">${JSON.stringify(seen).replace(/</g, "&lt;")}</pre>`);
  }

  if (path === "/calls") {
    return html(res, "Calls", `
      <h1 id="what">calls</h1><pre id="fetch"></pre><pre id="xhr"></pre>
      <script>
        fetch('http://site:8080/api/echo', { method: 'POST', headers: { 'X-Custom': 'by fetch' }, body: 'posted' })
          .then(r => r.json()).then(j => { document.getElementById('fetch').textContent = JSON.stringify(j); })
          .catch(e => { document.getElementById('fetch').textContent = 'failed: ' + e; });
        const x = new XMLHttpRequest();
        x.open('GET', 'http://site:8080/api/echo');
        x.setRequestHeader('X-Custom', 'by xhr');
        x.onload = () => { document.getElementById('xhr').textContent = x.responseText; };
        x.onerror = () => { document.getElementById('xhr').textContent = 'failed'; };
        x.send();
      </script>`);
  }

  if (path === "/socket") {
    return html(res, "Socket", `
      <h1 id="what">socket</h1><p id="reply"></p>
      <script>
        const socket = new WebSocket((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws', 'echo');
        socket.onopen = () => socket.send('ping through matgate');
        socket.onmessage = e => { document.getElementById('reply').textContent = e.data + ' [' + socket.protocol + ']'; };
        socket.onerror = () => { document.getElementById('reply').textContent = 'failed'; };
      </script>`);
  }

  if (path === "/events") {
    res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" });
    let n = 0;
    const timer = setInterval(() => {
      res.write(`data: event ${++n}\n\n`);
      if (n === 4) { clearInterval(timer); res.end(); }
    }, 400);
    req.on("close", () => clearInterval(timer));
    return;
  }

  if (path === "/stream") {
    return html(res, "Stream", `
      <h1 id="what">stream</h1><p id="got"></p>
      <script>
        const started = performance.now();
        const arrived = [];
        const source = new EventSource('/events');
        source.onmessage = e => {
          arrived.push(Math.round(performance.now() - started));
          document.getElementById('got').textContent = JSON.stringify(arrived);
          if (arrived.length === 4) { source.close(); }
        };
      </script>`);
  }

  if (path === "/redirect-absolute") { res.writeHead(302, { Location: "http://site:8080/page2?from=redirect" }); return res.end(); }
  if (path === "/redirect-port") { res.writeHead(302, { Location: "http://site:8081/second?from=redirect" }); return res.end(); }
  if (path === "/redirect-out") { res.writeHead(302, { Location: "http://other:8080/hello" }); return res.end(); }

  if (path === "/framed") {
    return html(res, "Framed", `<h1 id="what">not to be framed</h1><p id="ran"></p><script nonce="abc123">document.getElementById('ran').textContent = 'the page\\'s own script ran';</script>`, {
      "X-Frame-Options": "DENY",
      "Content-Security-Policy": "frame-ancestors 'none'; script-src 'nonce-abc123'",
      "Strict-Transport-Security": "max-age=31536000",
    });
  }

  if (path === "/basic") {
    const expected = "Basic " + Buffer.from("demo:demo-pw").toString("base64");
    if (req.headers.authorization !== expected) {
      return send(res, 401, "text/plain", "who are you?", { "WWW-Authenticate": 'Basic realm="fixture"' });
    }

    return html(res, "Basic", `<h1 id="what">signed in with name and password</h1>`);
  }

  if (path === "/download") {
    return send(res, 200, "application/octet-stream", download, { "Content-Disposition": 'attachment; filename="fixture.bin"', "Content-Length": download.length });
  }

  if (path === "/latin1") {
    // Not UTF-8: the bytes must arrive as they are.
    const text = page("Latin", `<h1 id="what">Größe und Ärger</h1>`).replace('<meta charset="utf-8">', '<meta charset="iso-8859-1">');
    return send(res, 200, "text/html; charset=iso-8859-1", Buffer.from(text, "latin1"));
  }

  if (path === "/packed") {
    const packed = zlib.gzipSync(page("Packed", `<h1 id="what">this page was sent compressed</h1>`));
    return send(res, 200, "text/html; charset=utf-8", packed, { "Content-Encoding": "gzip" });
  }

  if (path === "/fragment") {
    // A piece of a page, as applications fetch them: nothing is to be added to it.
    return send(res, 200, "text/html; charset=utf-8", `<div id="piece">only a piece</div>`);
  }

  send(res, 404, "text/plain", "not here: " + path);
};

// A web socket that says back what it was told, by hand: handshake, then unmasked text frames.
const upgrade = (req, socket) => {
  if (new URL(req.url, "http://x").pathname !== "/ws") { return socket.destroy(); }
  const accept = crypto.createHash("sha1").update(req.headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").digest("base64");
  const protocol = (req.headers["sec-websocket-protocol"] || "").split(",")[0].trim();
  socket.write("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n"
    + (protocol ? "Sec-WebSocket-Protocol: " + protocol + "\r\n" : "") + "\r\n");
  const seen = { origin: req.headers.origin || "", cookies: Object.keys(cookiesOf(req)).sort().join(",") };
  socket.on("data", data => {
    if ((data[0] & 15) !== 1) { return; }
    let length = data[1] & 127;
    let at = 2;
    if (length === 126) { length = data.readUInt16BE(2); at = 4; }
    const mask = data.subarray(at, at + 4);
    const text = Buffer.from(data.subarray(at + 4, at + 4 + length).map((b, i) => b ^ mask[i % 4])).toString();
    const reply = Buffer.from(`${text} | origin ${seen.origin} | cookies ${seen.cookies}`);
    socket.write(Buffer.concat([Buffer.from([129, reply.length]), reply]));
  });
  socket.on("error", () => { });
};

const main = http.createServer((req, res) => site(req, res).catch(e => send(res, 500, "text/plain", String(e))));
main.on("upgrade", upgrade);
main.listen(8080);

http.createServer((req, res) => {
  const url = new URL(req.url, "http://" + req.headers.host);
  html(res, "Second", `<h1 id="what">the same machine, another port</h1><p id="host">${req.headers.host}</p><p id="query">${url.search}</p><a id="first" href="http://site:8080/page2?from=second">to the first</a>`);
}).listen(8081);

// Signs in like Proxmox: a ticket from /api2/json/access/ticket, kept in a cookie the page's scripts read.
const ticket = "PVE:root@pam:65F00000::c2lnbmF0dXJl==";
http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://" + req.headers.host);
  if (url.pathname === "/api2/json/access/ticket" && req.method === "POST") {
    const form = new URLSearchParams((await bodyOf(req)).toString());
    if (form.get("username") === "root@pam" && form.get("password") === "pve-pw") {
      return send(res, 200, "application/json", JSON.stringify({ data: { ticket, CSRFPreventionToken: "token", username: "root@pam" } }));
    }

    return send(res, 401, "application/json", JSON.stringify({ data: null }));
  }

  if (url.pathname.endsWith(".js")) {
    return send(res, 200, "application/javascript", "window.pveLoaded = true;");
  }

  const signedIn = decodeURIComponent(cookiesOf(req).PVEAuthCookie || "") === ticket;
  html(res, "pve - Proxmox Virtual Environment", `<h1 id="what">${signedIn ? "signed in as root@pam" : "the sign-in dialog"}</h1><script src="/pve2/js/pvemanagerlib.js"></script>`);
}).listen(8082);

console.log("fixture website on 8080, 8081, 8082");
