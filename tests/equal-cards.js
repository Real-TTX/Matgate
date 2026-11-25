// Every card on the start page has the same size (0.54.0): connections, recently used, places, in every section -
// also one with a long name, a long note and "last used" on it; "Connect" stays inside the card.
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
  const b = await chromium.launch({ headless: true });
  try {
    const page = await (await b.newContext({ viewport: { width: 1500, height: 1000 } })).newPage();
    page.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
    await page.goto(BASE + "/login", { waitUntil: "networkidle" });
    await page.fill("input[name='username']", "admin");
    await page.fill("input[name='password']", "test-only-pw");
    await Promise.all([page.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), page.click(".login-submit")]);
    await page.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(1200);
    // With the side panel the sections of the start page step back; here they are all shown, as without it.
    await page.evaluate(() => document.querySelectorAll("[data-home2] > .home2-section").forEach(s => s.style.setProperty("display", "block", "important")));
    // One card with everything at once, longer than a card has room for.
    await page.evaluate(() => {
      const card = document.querySelector("[data-home2-all] .connection-choice");
      card.querySelector("h3").textContent = "Ein sehr langer Name einer Verbindung, der nie in eine Zeile passt";
      const copy = card.querySelector(".connection-choice-copy");
      const note = document.createElement("p");
      note.className = "muted connection-choice-notes";
      note.textContent = "Eine lange Notiz ".repeat(20);
      card.querySelector(".connection-choice-body").appendChild(note);
      const used = document.createElement("p");
      used.textContent = "vor 4 h";
      copy.appendChild(used);
      card.setAttribute("data-test-full", "1");
    });
    await sleep(200);
    const sizes = await page.evaluate(() => [...document.querySelectorAll("[data-home2] .home2-card-grid > .connection-choice")]
      .filter(card => card.offsetParent).map(card => { const r = card.getBoundingClientRect(); return Math.round(r.width) + "x" + Math.round(r.height); }));
    const sections = await page.evaluate(() => [...new Set([...document.querySelectorAll("[data-home2] .home2-card-grid > .connection-choice")]
      .filter(card => card.offsetParent).map(card => card.closest(".home2-section").className.replace("home2-section", "").trim() || "recent"))]);
    console.log("     sections: " + sections.join(", ") + " - " + sizes.length + " cards");
    check("all cards have one size", [...new Set(sizes)].length, 1);
    check("in several sections", sections.length >= 3, true);
    check("the full card keeps its Connect inside and its name on one line", await page.evaluate(() => {
      const card = document.querySelector("[data-test-full]");
      const box = card.getBoundingClientRect();
      const open = card.querySelector(".connection-choice-actions").getBoundingClientRect();
      const name = card.querySelector("h3");
      return [open.bottom <= box.bottom + 0.5 && open.height > 20, name.scrollWidth > name.clientWidth, getComputedStyle(name).whiteSpace];
    }), [true, true, "nowrap"]);
    // The phone keeps its list of rows.
    await page.setViewportSize({ width: 390, height: 844 });
    await sleep(400);
    check("on a phone it is still the list", await page.evaluate(() => getComputedStyle(document.querySelector("[data-home2-all] .home2-card-grid")).gap), "0px");
  }
  finally {
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
