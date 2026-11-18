import assert from 'node:assert/strict';
import { readdir, readFile } from 'node:fs/promises';
import puppeteer from 'puppeteer-core';

assert.equal((await fetch('http://127.0.0.1:8080/healthz')).status, 200);
assert.equal((await fetch('http://127.0.0.1:8080/')).status, 401);
const commandLines = await Promise.all((await readdir('/proc')).filter(name => /^\d+$/.test(name)).map(name => readFile(`/proc/${name}/cmdline`, 'utf8').catch(() => '')));
const native = commandLines.find(line => line.includes('--class=PokeIdleDiagnostic') && !line.includes('--type='));
assert.ok(native, 'Standalone diagnostic Chromium process is running');
assert.ok(native.includes('--user-data-dir=') && native.includes('diagnostic-profile-'));
for (const flag of ['--remote-debugging', '--enable-automation', '--load-extension', '--test-type']) assert.ok(!native.includes(flag), `Diagnostic browser must not use ${flag}`);
assert.ok(!commandLines.some(line => line.includes('DeckBridge.dll') || line.includes('--class=PokeIdleAccount')));

// Only the outer noVNC web client is automated for this test. The diagnostic
// Chromium itself has no automation connection. Native clicks arrive via VNC.
const browser = await puppeteer.launch({ executablePath: process.env.CHROME_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox'], defaultViewport: { width: 1280, height: 720 } });
try {
  const page = await browser.newPage();
  await page.authenticate({ username: process.env.WEB_USER || 'pokeidle', password: process.env.WEB_PASSWORD });
  await page.goto('http://127.0.0.1:8080');
  await page.waitForFunction(() => document.querySelector('#connection').hidden, { timeout: 15000 });
  const colorVisible = color => {
    const canvas = document.querySelector('#screen canvas');
    if (!canvas || canvas.width < 100) return false;
    const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    let matches = 0;
    for (let i = 0; i < pixels.length; i += 128) {
      if (Math.abs(pixels[i] - color[0]) < 5 && Math.abs(pixels[i + 1] - color[1]) < 5 && Math.abs(pixels[i + 2] - color[2]) < 5 && ++matches > 100) return true;
    }
    return false;
  };
  await page.waitForFunction(colorVisible, { timeout: 15000 }, [180, 20, 40]);
  const canvas = await page.$eval('#screen canvas', element => element.getBoundingClientRect().toJSON());
  await page.mouse.click(canvas.x + canvas.width / 2, canvas.y + canvas.height / 2);
  await page.waitForFunction(colorVisible, { timeout: 10000 }, [20, 180, 40]);
  await page.screenshot({ path: '/data/exports/diagnostic-webui.png' });
  console.log('Diagnostic mode: authenticated noVNC, visible native browser, isolated temporary profile and real remote mouse click passed. No game accounts or Cloudflare challenges were accessed by this test.');
} finally { await browser.close(); }
