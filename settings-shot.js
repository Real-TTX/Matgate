const { chromium } = require('playwright-core');
const BASE = 'http://127.0.0.1:18091';
const sleep = ms => new Promise(r => setTimeout(r, ms));
(async () => {
  let b; try { b = await chromium.launch({ channel: 'msedge', headless: true }); }
  catch { b = await chromium.launch({ channel: 'chrome', headless: true }); }
  for (const modus of ['light', 'dark']) {
    const page = await (await b.newContext({ viewport: { width: 1100, height: 950 }, colorScheme: modus })).newPage();
    const fehler = []; page.on('pageerror', e => fehler.push(String(e).slice(0, 120)));
    await page.goto(BASE + '/login', { waitUntil: 'networkidle' });
    await page.fill('input[name="username"]', 'admin');
    await page.fill('input[name="password"]', 'test-only-pw');
    await Promise.all([page.waitForURL(u => !/\/login/.test(u.toString())).catch(() => {}), page.click('.login-submit')]);
    await sleep(1000);
    await page.goto(BASE + '/account?tab=profile', { waitUntil: 'networkidle' }); await sleep(1200);
    const z = await page.evaluate(() => ({
      modi: [...document.querySelectorAll('.mode-option')].map(m => m.textContent.trim() + (m.classList.contains('is-selected') ? '*' : '')),
      karten: [...document.querySelectorAll('.theme-card')].map(c => c.querySelector('.theme-card-name').textContent + (c.classList.contains('is-selected') ? '*' : '')),
      vorschauen: document.querySelectorAll('.theme-preview').length,
    }));
    console.log(modus + ':', JSON.stringify(z), fehler.length ? 'FEHLER ' + fehler[0] : '');
    const box = await page.$('.theme-cards');
    if (box) {
      const b2 = await box.boundingBox();
      await page.screenshot({ path: 'settings-' + modus + '.png', clip: { x: 0, y: Math.max(0, b2.y - 220), width: 1000, height: 520 } });
    }
    await page.context().close();
  }
  await b.close();
})();
