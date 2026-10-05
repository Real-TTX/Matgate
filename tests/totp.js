// The whole path of the second factor: set up, confirm, sign in, reuse, recovery code,
// switch off.
const { chromium } = require("playwright-core");
const crypto = require("crypto");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

// --- TOTP per RFC 6238, computed here independently of the server
const BASE32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
function base32Decode(s) {
  let bits = 0, puffer = 0; const out = [];
  for (const c of s.replace(/[^A-Z2-7]/gi, "").toUpperCase()) {
    puffer = (puffer << 5) | BASE32.indexOf(c); bits += 5;
    if (bits >= 8) { out.push((puffer >> (bits - 8)) & 0xff); bits -= 8; }
  }
  return Buffer.from(out);
}
function totp(secret, versatzSchritte = 0) {
  const step = Math.floor(Date.now() / 1000 / 30) + versatzSchritte;
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const h = crypto.createHmac("sha1", base32Decode(secret)).update(counter).digest();
  const v = h[h.length - 1] & 0x0f;
  const number = ((h[v] & 0x7f) << 24) | ((h[v + 1] & 0xff) << 16) | ((h[v + 2] & 0xff) << 8) | (h[v + 3] & 0xff);
  return String(number % 1000000).padStart(6, "0");
}

// Signing in is rate limited: 10 attempts per 5 minutes per IP. This test needs more, so it
// waits the limit out instead of loosening it for itself - what should be measured is the real
// behaviour, not a defused one.
let posts = 0;
const makeRoom = async () => {
  if (posts < 8) return;
  console.log("     ... waiting 5 minutes for the login rate limit to lapse");
  await new Promise(r => setTimeout(r, 305000));
  posts = 0;
};

// The code that switches the second factor on uses up its time step. Signing in within the same
// 30 seconds means typing the same code - and that is rightly refused a second time. In real use
// more than half a minute passes in between; here one has to wait.
const nextStep = async () => {
  const now = Math.floor(Date.now() / 1000 / 30);
  while (Math.floor(Date.now() / 1000 / 30) === now) {
    await new Promise(r => setTimeout(r, 1000));
  }
};

const signIn = async (page, name, pw) => {
  for (let attempt = 0; attempt < 2; attempt++) {
    await makeRoom();
    posts++;
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name=\"username\"]", name);
    await page.fill("input[name=\"password\"]", pw);
    const response = await Promise.all([
      page.waitForResponse(r => r.request().method() === "POST" && r.url().includes("/login")).catch(() => null),
      page.click(".login-submit"),
    ]);
    await sleep(1500);
    // 429 means: the rate limit, not a wrong password. Wait once and try again.
    if (response[0] && response[0].status() === 429) {
      posts = 8;
      continue;
    }

    return;
  }
};

(async () => {
  let b; try { b = await chromium.launch({ channel: "msedge", headless: true }); }
  catch { b = await chromium.launch({ channel: "chrome", headless: true }); }
  const status = { last: 0, posts: 0 };
  const newTab = async () => {
    const p = await (await b.newContext({ viewport: { width: 1280, height: 950 } })).newPage();
    p.on("response", r => {
      if (r.request().method() === "POST" && r.url().includes("/login")) { status.last = r.status(); status.posts++; }
    });
    return p;
  };

  // ================= Setting up
  const page = await newTab();
  page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await signIn(page, "admin", "test-only-pw");
  await page.goto(BASE + "/account?tab=security", { waitUntil: "networkidle" });
  await sleep(600);

  // An earlier aborted run may have left a started setup behind: then the confirmation page is
  // here instead of "Set up". Cancelling clears it away - and checks on the way that the path
  // back exists at all.
  const started = await page.evaluate(() => !document.querySelector("form[action='/account/totp/start']")
    && !!document.querySelector("form[action='/account/totp/confirm']"));
  if (started) {
    console.log("     found a started setup - cancelling it");
    await page.evaluate(() => document.querySelector("form[action='/account/totp/disable']").requestSubmit());
    await sleep(1600);
  }
  check("setting up is available", await page.evaluate(() => !!document.querySelector("form[action='/account/totp/start']")), true);
  await page.evaluate(() => document.querySelector("form[action='/account/totp/start'] button").click());
  await sleep(1500);
  const secret = await page.evaluate(() => {
    const el = document.querySelector(".totp-secret code");
    return el ? el.textContent.trim() : null;
  });
  check("the secret is shown", typeof secret === "string" && secret.length >= 16, true);
  const qr = await page.evaluate(() => {
    const img = document.querySelector(".totp-qr");
    return img ? { present: true, length: img.getAttribute("src").length, px: img.naturalWidth } : { present: false };
  });
  check("the QR code is a real image", qr.present && qr.px > 50, true);
  console.log("     QR: " + JSON.stringify(qr));
  await page.screenshot({ path: "totp-1-einrichten.png" });

  // Not switched on yet: signing in has to work without a code
  const probe = await newTab();
  await signIn(probe, "admin", "test-only-pw");
  check("no code asked for before confirming", /\/login/.test(probe.url()), false);
  await probe.context().close();

  // Wrong code
  await page.goto(BASE + "/account?tab=security", { waitUntil: "networkidle" });
  await page.fill("form[action='/account/totp/confirm'] input[name='code']", "000000");
  await page.evaluate(() => document.querySelector("form[action='/account/totp/confirm']").requestSubmit());
  await sleep(1500);
  check("a wrong code does not switch it on", (await page.content()).includes("totp-setup"), true);

  // Right code
  await page.fill("form[action='/account/totp/confirm'] input[name='code']", totp(secret));
  await page.evaluate(() => document.querySelector("form[action='/account/totp/confirm']").requestSubmit());
  await sleep(1800);
  const codes = await page.evaluate(() => Array.from(document.querySelectorAll(".totp-codes li")).map(li => li.textContent.trim()));
  check("ten recovery codes", codes.length, 10);
  // They also have to be VISIBLE - this page comes without ?tab= and used to show the profile.
  check("the codes are in the visible tab", await page.evaluate(() => {
    const panel = document.querySelector("[data-tab-panel='security']");
    return !!panel && !panel.classList.contains("hidden");
  }), true);
  await page.screenshot({ path: "totp-2-codes.png" });

  // On the next visit they are gone
  await page.goto(BASE + "/account?tab=security", { waitUntil: "networkidle" });
  await sleep(500);
  const onceMore = await page.evaluate(() => document.querySelectorAll(".totp-codes li").length);
  check("the codes do not appear a second time", onceMore, 0);
  await page.screenshot({ path: "totp-3-an.png" });

  // ================= Signing in with the second factor
  const p2 = await newTab();
  await signIn(p2, "admin", "test-only-pw");
  const secondStep = await p2.evaluate(() => !!document.querySelector("form[action='/login/totp']"));
  check("zweiter Schritt erscheint", secondStep, true);
  check("not signed in yet", await p2.evaluate(() => !!document.getElementById("matgate-shell")), false);
  await p2.screenshot({ path: "totp-4-abfrage.png" });

  // Wrong code
  await p2.fill("input[name='code']", "123456");
  posts++; await p2.click(".login-submit");
  await sleep(1200);
  check("a wrong code does not sign in", await p2.evaluate(() => !!document.querySelector("form[action='/login/totp']")), true);

  // Right code - but only in the next time step; the previous one was used up switching it on.
  await nextStep();
  const now = totp(secret);
  await p2.fill("input[name='code']", now);
  posts++; await p2.click(".login-submit");
  await sleep(1800);
  check("richtiger Code meldet an", /\/login/.test(p2.url()), false);
  await p2.context().close();

  // ================= The same code a second time
  const p3 = await newTab();
  await signIn(p3, "admin", "test-only-pw");
  await p3.fill("input[name='code']", now);
  posts++; await p3.click(".login-submit");
  await sleep(1500);
  check("the same code is not valid twice", await p3.evaluate(() => !!document.querySelector("form[action='/login/totp']")), true);
  await p3.context().close();

  // ================= Recovery code
  const p4 = await newTab();
  await signIn(p4, "admin", "test-only-pw");
  await p4.fill("input[name='code']", codes[0]);
  posts++; await p4.click(".login-submit");
  await sleep(1800);
  check("a recovery code signs in", /\/login/.test(p4.url()), false);
  await p4.context().close();

  const p5 = await newTab();
  await signIn(p5, "admin", "test-only-pw");
  await p5.fill("input[name='code']", codes[0]);
  posts++; await p5.click(".login-submit");
  await sleep(1500);
  console.log("     status with a used-up code: " + status.last + " (POSTs before: " + status.posts + ")");
  check("a used-up code is not valid again", await p5.evaluate(() => !!document.querySelector("form[action='/login/totp']")), true);
  await p5.context().close();

  // ================= Switching off
  await page.goto(BASE + "/account?tab=security", { waitUntil: "networkidle" });
  await sleep(500);
  const left = await page.evaluate(() => (document.querySelector(".totp-state")?.textContent || "").trim());
  console.log("     Zustand: " + JSON.stringify(left));
  check("one code is used up", /9 /.test(left), true);

  await page.fill("form[action='/account/totp/disable'] input[name='currentPassword']", "falsches-passwort");
  await page.evaluate(() => document.querySelector("form[action='/account/totp/disable']").requestSubmit());
  await sleep(1500);
  check("a wrong password does not switch it off", await page.evaluate(() => !!document.querySelector("form[action='/account/totp/disable']")), true);

  await page.fill("form[action='/account/totp/disable'] input[name='currentPassword']", "test-only-pw");
  await page.evaluate(() => document.querySelector("form[action='/account/totp/disable']").requestSubmit());
  await sleep(1500);
  check("richtiges Passwort schaltet ab", await page.evaluate(() => !!document.querySelector("form[action='/account/totp/start']")), true);

  const p6 = await newTab();
  await signIn(p6, "admin", "test-only-pw");
  check("afterwards without a code again", await p6.evaluate(() => !!document.querySelector("form[action='/login/totp']")), false);
  await p6.context().close();

  // ================= The administrator's path when phone AND codes are gone
  // Switch it on once more - that runs through /account/..., so it costs no sign-in attempt.
  await page.goto(BASE + "/account?tab=security", { waitUntil: "networkidle" });
  await page.evaluate(() => document.querySelector("form[action='/account/totp/start'] button").click());
  await sleep(1500);
  const secret2 = await page.evaluate(() => document.querySelector(".totp-secret code").textContent.trim());
  await page.fill("form[action='/account/totp/confirm'] input[name='code']", totp(secret2));
  await page.evaluate(() => document.querySelector("form[action='/account/totp/confirm']").requestSubmit());
  await sleep(1800);

  const ownId = await page.evaluate(async () => {
    const r = await fetch("/admin/users", { credentials: "same-origin" });
    const text = await r.text();
    const hits = text.match(/\/admin\/users\/([0-9a-f-]{36})/i);
    return hits ? hits[1] : null;
  });
  check("user id found", typeof ownId === "string", true);
  await page.goto(BASE + "/admin/users/" + ownId, { waitUntil: "networkidle" });
  await sleep(500);
  check("administration shows the second factor", await page.evaluate(() =>
    !!document.querySelector("form[action$='/totp-reset']")), true);
  await page.screenshot({ path: "totp-5-admin.png" });
  await page.evaluate(() => document.querySelector("form[action$='/totp-reset']").requestSubmit());
  await sleep(1500);
  check("administration can switch it off", await page.evaluate(() =>
    !document.querySelector("form[action$='/totp-reset']")), true);

  console.log("failed: " + JSON.stringify(failures));
  await b.close();
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.message); process.exit(1); });
