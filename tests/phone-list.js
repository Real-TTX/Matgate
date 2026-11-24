// Signs and the phone's list (0.48.0):
//   - a connection's sign is the mark alone in its colour (no tinted square), RDP the Windows window, SSH the
//     prompt, a database with its letters (PG / My / MS) - on the start page and in the side panel
//   - on a phone the connections are a list: one row each, no star/gear/"Connect" to be seen, a tap on the row
//     lands on "Connect", the "..." opens the card's menu as a sheet
//   - quick connect is chips that wrap, nothing to swipe sideways
//   - on a larger screen the cards stay as they were, without the "..."
const { chromium } = require("playwright-core");
const BASE = "http://127.0.0.1:18091";
const sleep = ms => new Promise(r => setTimeout(r, ms));
const failures = [];
const check = (name, got, want) => {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  console.log((ok ? "ok   " : "FAIL ") + name + "  got=" + JSON.stringify(got) + (ok ? "" : " want=" + JSON.stringify(want)));
  if (!ok) failures.push(name);
};

const login = async context => {
  const p = await context.newPage();
  p.on("pageerror", e => { console.log("JS ERROR: " + e.message); failures.push("JS error"); });
  await p.goto(BASE + "/login", { waitUntil: "networkidle" });
  await p.fill("input[name='username']", "admin");
  await p.fill("input[name='password']", "test-only-pw");
  await Promise.all([p.waitForURL(u => !/login/.test(u.toString()), { timeout: 20000 }), p.click(".login-submit")]);
  await sleep(800);
  return p;
};

(async () => {
  const b = await chromium.launch({ headless: true });
  try {
    // ---- a large screen: the signs, and the cards as before
    const wide = await login(await b.newContext({ viewport: { width: 1400, height: 950 } }));
    await wide.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(500);
    const signs = await wide.evaluate(() => {
      const cardOf = name => [...document.querySelectorAll("[data-home2-card]")].find(card => (card.querySelector("h3") || {}).textContent === name);
      const signOf = card => card && card.querySelector(".server-icon");
      const linux = signOf(cardOf("Linux-Testhost"));
      const pg = signOf(cardOf("Test-Postgres"));
      return {
        noSquare: linux ? getComputedStyle(linux).backgroundColor : null,
        prompt: linux ? linux.querySelectorAll("svg path").length : null,
        tag: pg ? (pg.querySelector(".server-icon-tag") || {}).textContent : null,
        moreHidden: [...document.querySelectorAll("[data-card-more]")].every(button => getComputedStyle(button).display === "none"),
        cards: getComputedStyle(cardOf("Linux-Testhost")).borderTopStyle
      };
    });
    check("a sign has no tinted square behind it", signs.noSquare, "rgba(0, 0, 0, 0)");
    check("SSH is the prompt, two strokes", signs.prompt, 2);
    check("a database carries its letters", signs.tag, "PG");
    check("a large screen has no \"...\" on the cards", signs.moreHidden, true);
    check("and keeps its cards", signs.cards, "solid");
    check("the side panel shows the letters too", await wide.evaluate(() => [...document.querySelectorAll(".server-manager-item-icon .server-icon-tag")].map(tag => tag.textContent).sort()), ["MS", "My", "PG"]);
    check("an RDP sign is the Windows window", await wide.evaluate(() => {
      const chip = document.querySelector(".home2-proto-chip[data-home2-qc='rdp'] svg");
      return chip ? chip.querySelectorAll("path[fill='currentColor']").length : null;
    }), 4);
    await wide.context().close();

    // ---- a phone: the list
    const phone = await login(await b.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true }));
    await phone.goto(BASE + "/", { waitUntil: "networkidle" });
    await sleep(500);
    const list = await phone.evaluate(() => {
      const cards = [...document.querySelectorAll("[data-home2-card]")].filter(card => card.offsetParent);
      const card = cards.find(c => (c.querySelector("h3") || {}).textContent === "Linux-Testhost") || cards[0];
      const box = card.getBoundingClientRect();
      const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2);
      const style = getComputedStyle(card);
      return {
        rowHigh: box.height <= 72,
        noCorner: getComputedStyle(card.querySelector(".connection-choice-corner")).display,
        connectUnseen: getComputedStyle(card.querySelector(".connection-choice-open")).opacity,
        tapConnects: !!(hit && hit.closest(".connection-choice-open")),
        more: getComputedStyle(card.querySelector("[data-card-more]")).display !== "none",
        flat: [style.borderTopLeftRadius, style.boxShadow]
      };
    });
    check("a connection is a row, not a card", list.rowHigh, true);
    check("its star and gear are not on the row", list.noCorner, "none");
    check("\"Connect\" is not to be seen", list.connectUnseen, "0");
    check("but a tap on the row is a tap on it", list.tapConnects, true);
    check("the row has its \"...\"", list.more, true);
    check("rows sit flat in one box", list.flat, ["0px", "none"]);
    await phone.click("[data-home2-card] [data-card-more] >> nth=0");
    await sleep(400);
    check("the \"...\" opens the card's menu as a sheet", await phone.evaluate(() => {
      const sheet = document.querySelector(".toolbar-menu-panel.menu-sheet[data-menu-dynamic]");
      return sheet && !sheet.hidden ? [...sheet.children].filter(entry => !entry.classList.contains("menu-sheet-head")).map(entry => entry.textContent.trim()).slice(0, 1) : null;
    }), ["Connect"]);
    await phone.click(".menu-sheet-close");
    await sleep(300);
    check("quick connect wraps, nothing to swipe", await phone.evaluate(() => {
      const row = document.querySelector(".home2-quick-row");
      return [getComputedStyle(row).flexWrap, row.scrollWidth <= row.clientWidth + 1];
    }), ["wrap", true]);
    check("a chip has its sign beside its name", await phone.evaluate(() => getComputedStyle(document.querySelector(".home2-proto-chip")).flexDirection), "row");
    check("nothing is wider than the phone", await phone.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
  }
  finally {
    await b.close();
  }
  console.log(failures.length ? "\n" + failures.length + " FAILED: " + failures.join(", ") : "\nall passed");
  process.exit(failures.length ? 1 : 0);
})().catch(e => { console.error("ABORTED " + e.stack); process.exit(1); });
