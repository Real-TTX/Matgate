// A VNC server that is just enough for guacd to connect to, with a screen that can be checked:
// a grid of coloured tiles, and a marker that follows the pointer. Everything the client sends -
// pointer positions and keys - is kept and can be read over HTTP, so a test can ask what the
// "remote" really received instead of guessing from what the browser drew.
//
//   node tests/toy-vnc.js            serves VNC on :5901 and the log on :5902
//   GET  http://127.0.0.1:5902/log   { width, height, clients, log: [{ t, type, x, y, buttons }] }
//   POST http://127.0.0.1:5902/clear forgets the log
//
// guacd runs in a container, so the address to connect to is host.docker.internal:5901.
const net = require("net");
const http = require("http");

const WIDTH = Number(process.env.TOY_VNC_WIDTH || 1600);
const HEIGHT = Number(process.env.TOY_VNC_HEIGHT || 900);
const VNC_PORT = Number(process.env.TOY_VNC_PORT || 5901);
const LOG_PORT = Number(process.env.TOY_VNC_LOG_PORT || 5902);
const MARKER = 16;

// The screen: 100 pixel tiles whose colour says where they are, so the top left corner of the
// picture is recognisable at any scale.
const screen = Buffer.alloc(WIDTH * HEIGHT * 3);
for (let y = 0; y < HEIGHT; y++) {
  for (let x = 0; x < WIDTH; x++) {
    const tx = Math.floor(x / 100), ty = Math.floor(y / 100);
    const edge = x % 100 === 0 || y % 100 === 0;
    const o = (y * WIDTH + x) * 3;
    screen[o] = edge ? 20 : 60 + (tx * 37) % 160;
    screen[o + 1] = edge ? 20 : 60 + (ty * 53) % 160;
    screen[o + 2] = edge ? 20 : 120;
  }
}

const log = [];
const clients = new Set();

function packPixel(format, r, g, b) {
  const bytes = format.bpp / 8;
  const value = ((Math.round(r / 255 * format.redMax) << format.redShift)
    | (Math.round(g / 255 * format.greenMax) << format.greenShift)
    | (Math.round(b / 255 * format.blueMax) << format.blueShift)) >>> 0;
  const out = Buffer.alloc(bytes);
  if (bytes === 4) { format.bigEndian ? out.writeUInt32BE(value) : out.writeUInt32LE(value); }
  else if (bytes === 2) { format.bigEndian ? out.writeUInt16BE(value & 0xffff) : out.writeUInt16LE(value & 0xffff); }
  else { out[0] = value & 0xff; }
  return out;
}

// The picture as the client wants it, with the marker drawn in at the last pointer position.
function rectangle(client, x, y, w, h) {
  const bytes = client.format.bpp / 8;
  const out = Buffer.alloc(w * h * bytes);
  const p = client.pointer;
  let o = 0;
  for (let yy = y; yy < y + h; yy++) {
    for (let xx = x; xx < x + w; xx++) {
      const inMarker = p && xx >= p.x && xx < p.x + MARKER && yy >= p.y && yy < p.y + MARKER;
      const s = (yy * WIDTH + xx) * 3;
      const rgb = inMarker ? [255, 255, 255] : [screen[s], screen[s + 1], screen[s + 2]];
      packPixel(client.format, rgb[0], rgb[1], rgb[2]).copy(out, o);
      o += bytes;
    }
  }
  return out;
}

function sendUpdate(client, x, y, w, h) {
  x = Math.max(0, Math.min(WIDTH - 1, x)); y = Math.max(0, Math.min(HEIGHT - 1, y));
  w = Math.max(1, Math.min(WIDTH - x, w)); h = Math.max(1, Math.min(HEIGHT - y, h));
  const head = Buffer.alloc(16);
  head[0] = 0; head.writeUInt16BE(1, 2);
  head.writeUInt16BE(x, 4); head.writeUInt16BE(y, 6); head.writeUInt16BE(w, 8); head.writeUInt16BE(h, 10);
  head.writeInt32BE(0, 12);
  client.socket.write(Buffer.concat([head, rectangle(client, x, y, w, h)]));
}

function initMessage() {
  const name = Buffer.from("toy");
  const out = Buffer.alloc(24 + name.length);
  out.writeUInt16BE(WIDTH, 0); out.writeUInt16BE(HEIGHT, 2);
  out[4] = 32; out[5] = 24; out[6] = 0; out[7] = 1;
  out.writeUInt16BE(255, 8); out.writeUInt16BE(255, 10); out.writeUInt16BE(255, 12);
  out[14] = 16; out[15] = 8; out[16] = 0;
  out.writeUInt32BE(name.length, 20);
  name.copy(out, 24);
  return out;
}

function flush(client) {
  if (client.wants && client.dirty) {
    const d = client.dirty;
    client.dirty = null;
    client.wants = false;
    sendUpdate(client, d.x, d.y, d.w, d.h);
  }
}

// One message of the protocol after the handshake. Returns how many bytes it took, or 0 when the
// message is not complete yet.
function handleMessage(client, b) {
  const type = b[0];
  let need = 0;
  if (type === 0) { need = 20; }
  else if (type === 2) { if (b.length < 4) { return 0; } need = 4 + 4 * b.readUInt16BE(2); }
  else if (type === 3) { need = 10; }
  else if (type === 4) { need = 8; }
  else if (type === 5) { need = 6; }
  else if (type === 6) { if (b.length < 8) { return 0; } need = 8 + b.readUInt32BE(4); }
  else { client.socket.destroy(); return 0; }
  if (b.length < need) { return 0; }
  const m = b.subarray(0, need);
  if (type === 0) {
    client.format = {
      bpp: m[4], depth: m[5], bigEndian: !!m[6],
      redMax: m.readUInt16BE(8), greenMax: m.readUInt16BE(10), blueMax: m.readUInt16BE(12),
      redShift: m[14], greenShift: m[15], blueShift: m[16],
    };
  }
  else if (type === 3) {
    client.wants = true;
    if (m[1] === 0) { client.dirty = { x: m.readUInt16BE(2), y: m.readUInt16BE(4), w: m.readUInt16BE(6), h: m.readUInt16BE(8) }; }
    flush(client);
  }
  else if (type === 4) {
    log.push({ t: Date.now(), type: "key", keysym: m.readUInt32BE(4), down: m[1] === 1 });
  }
  else if (type === 5) {
    const x = m.readUInt16BE(2), y = m.readUInt16BE(4);
    log.push({ t: Date.now(), type: "pointer", x, y, buttons: m[1] });
    const old = client.pointer || { x, y };
    client.pointer = { x, y };
    const x0 = Math.min(old.x, x), y0 = Math.min(old.y, y);
    const x1 = Math.max(old.x, x) + MARKER, y1 = Math.max(old.y, y) + MARKER;
    const d = client.dirty;
    client.dirty = d
      ? { x: Math.min(d.x, x0), y: Math.min(d.y, y0), w: Math.max(d.x + d.w, x1) - Math.min(d.x, x0), h: Math.max(d.y + d.h, y1) - Math.min(d.y, y0) }
      : { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
    flush(client);
  }
  return need;
}

function serve(socket) {
  const client = {
    socket, wants: false, pointer: null, dirty: null, stage: "version", buffer: Buffer.alloc(0),
    format: { bpp: 32, depth: 24, bigEndian: false, redMax: 255, greenMax: 255, blueMax: 255, redShift: 16, greenShift: 8, blueShift: 0 },
  };
  clients.add(client);
  socket.on("close", () => clients.delete(client));
  socket.on("error", () => clients.delete(client));
  socket.write("RFB 003.008\n");

  socket.on("data", data => {
    client.buffer = Buffer.concat([client.buffer, data]);
    for (;;) {
      const b = client.buffer;
      if (client.stage === "version") {
        if (b.length < 12) { return; }
        client.buffer = b.subarray(12);
        socket.write(Buffer.from([1, 1]));          // one security type: none
        client.stage = "security";
      }
      else if (client.stage === "security") {
        if (b.length < 1) { return; }
        client.buffer = b.subarray(1);
        socket.write(Buffer.from([0, 0, 0, 0]));    // security result: ok
        client.stage = "init";
      }
      else if (client.stage === "init") {
        if (b.length < 1) { return; }
        client.buffer = b.subarray(1);
        socket.write(initMessage());
        client.stage = "messages";
      }
      else {
        if (b.length < 1) { return; }
        const used = handleMessage(client, b);
        if (!used) { return; }
        client.buffer = client.buffer.subarray(used);
      }
    }
  });
}

net.createServer(serve).listen(VNC_PORT, "0.0.0.0", () => console.log("toy vnc on " + VNC_PORT + " (" + WIDTH + "x" + HEIGHT + ")"));
http.createServer((req, res) => {
  res.setHeader("Content-Type", "application/json");
  if (req.method === "POST" && req.url === "/clear") { log.length = 0; res.end("{}"); return; }
  res.end(JSON.stringify({ width: WIDTH, height: HEIGHT, clients: clients.size, log }));
}).listen(LOG_PORT, "127.0.0.1");
