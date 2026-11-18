import puppeteer from 'puppeteer-core';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

export async function launchBrowser(profile, wmClass, url, { kiosk = false } = {}) {
  await mkdir(profile, { recursive: true });
  const browser = await puppeteer.launch({
    executablePath: process.env.CHROME_PATH || '/usr/bin/chromium',
    userDataDir: profile,
    headless: false,
    defaultViewport: null,
    pipe: true,
    enableExtensions: true,
    ignoreDefaultArgs: ['--enable-automation', '--disable-dev-shm-usage', '--disable-component-extensions-with-background-pages'],
    args: [
      '--no-sandbox', '--test-type', '--force-dark-mode', '--ozone-platform=x11', '--no-first-run', '--no-default-browser-check',
      '--disable-session-crashed-bubble', '--hide-crash-restore-bubble',
      '--disable-background-timer-throttling', '--disable-backgrounding-occluded-windows',
      '--disable-renderer-backgrounding', '--autoplay-policy=no-user-gesture-required',
      '--process-per-site', '--force-device-scale-factor=1',
      `--class=${wmClass}`, `--app=${url}`, ...(kiosk ? ['--kiosk'] : []),
      // Prevent Puppeteer from adding an extra about:blank startup tab.
      url,
    ],
    timeout: 60_000,
  });
  const pages = await browser.pages();
  const page = pages.find(p => p.url() === url) || pages.find(p => p.url() !== 'about:blank') || pages[0] || await browser.newPage();
  for (const extra of pages) if (extra !== page) await extra.close();
  const cdp = await browser.target().createCDPSession();
  return { browser, page, cdp, wmClass, profile };
}

export async function waitForWindow(x11, wmClass, timeout = 20_000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    const id = await x11.call('find', { class: wmClass });
    if (id) return id;
    await delay(100);
  }
  throw new Error(`A janela ${wmClass} não abriu`);
}

export function withTimeout(promise, ms = 15_000, message = 'A página não respondeu') {
  let timer;
  return Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); })]).finally(() => clearTimeout(timer));
}

export async function evaluate(page, script, timeout = 15_000) {
  if (!page || page.isClosed()) return null;
  return withTimeout(page.evaluate(script), timeout);
}

export async function browserMemory(browser) {
  try {
    const cdp = await browser.target().createCDPSession();
    try {
      const { processInfo } = await cdp.send('SystemInfo.getProcessInfo');
      const rss = await Promise.all(processInfo.map(async ({ id }) => {
        const text = await readFile(`/proc/${id}/status`, 'utf8').catch(() => '');
        return Number(text.match(/^VmRSS:\s+(\d+)/m)?.[1] || 0) * 1024;
      }));
      return rss.reduce((sum, n) => sum + n, 0);
    } finally { await cdp.detach(); }
  } catch { return 0; }
}

// Muting covers HTML media and WebAudio (the game uses both). Changes remain
// reversible and are reapplied before document scripts after every navigation.
export const mediaScript = `(() => {
  if (window.__deckAudio) return;
  const contexts = new Set();
  window.__deckAudio = { muted: false, set(on) {
    this.muted = !!on;
    for (const media of document.querySelectorAll('video,audio')) media.muted = this.muted;
    for (const context of contexts) (this.muted ? context.suspend() : context.resume()).catch(() => {});
  } };
  for (const name of ['AudioContext', 'webkitAudioContext']) {
    const Original = window[name];
    if (!Original) continue;
    window[name] = new Proxy(Original, { construct(target, args) {
      const context = new target(...args); contexts.add(context);
      const resume = context.resume.bind(context);
      context.resume = () => window.__deckAudio.muted ? Promise.resolve() : resume();
      if (window.__deckAudio.muted) context.suspend().catch(() => {});
      return context;
    } });
  }
  document.addEventListener('play', event => { if (event.target instanceof HTMLMediaElement) event.target.muted = window.__deckAudio.muted; }, true);
})();`;

export async function setupGame(account, scripts, config, onMessage) {
  const { page } = account;
  await page.exposeFunction('__deckHostMessage', onMessage);
  const source = `window.chrome ||= {}; chrome.webview = { postMessage: value => window.__deckHostMessage(String(value)).catch(() => {}) };
    window.__deckFps=${config.GameFps}; window.__deckSpriteMs=${config.SpriteMs};\n`
    + mediaScript + `\nwindow.__deckAudio.set(${config.Muted[account.index]});\n`
    + [scripts.frames, scripts.sprites, scripts.echo, scripts.recompra, scripts.pvp, scripts.bonus].join('\n')
    + `\naddEventListener('keydown', e => { if (e.key === 'F11') { e.preventDefault(); chrome.webview.postMessage('F11'); } }, true);`;
  await page.evaluateOnNewDocument(source);
  await page.emulateMediaFeatures([{ name: 'prefers-reduced-motion', value: config.ReduceMotion ? 'reduce' : 'no-preference' }]);
  const client = await page.createCDPSession();
  account.pageCdp = client;
  return source;
}

export async function applyGameConfig(account, scripts, config) {
  if (!account?.page || account.page.isClosed()) return;
  const i = account.index;
  const restock = config.Restock[i];
  const camel = Object.fromEntries(Object.entries(restock).map(([k, v]) => [k[0].toLowerCase() + k.slice(1),
    v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([a, b]) => [a[0].toLowerCase() + a.slice(1), b])) : v]));
  await evaluate(account.page, `(() => {
    window.__deckFps=${config.GameFps}; window.__deckSpriteMs=${config.SpriteMs};
    window.__deckAudio?.set(${config.Muted[i]});
    window.__deckRecompra?.configure(${JSON.stringify(camel)});
    window.__deckPvp?.config(${JSON.stringify({ tela: config.PvpScreen[i], vel: config.PvpSpeed[i] })});
    window.__deckPvp?.set(${config.Pvp[i]});
  })()`);
  if (config.Ultra) await evaluate(account.page, `${scripts.ultra}(true)`);
  await account.page.emulateMediaFeatures([{ name: 'prefers-reduced-motion', value: config.ReduceMotion ? 'reduce' : 'no-preference' }]);
  if (account.bridgeExtension) await account.pageCdp.send('Extensions.setStorageItems', {
    id: account.bridgeExtension, storageArea: 'local',
    values: { command: { revision: Date.now(), url: account.page.url(), zoom: config.Zoom[i], muted: config.Muted[i] } },
  });
}

export const profilePath = (root, index) => path.join(root, 'profiles', `conta${index + 1}`);
