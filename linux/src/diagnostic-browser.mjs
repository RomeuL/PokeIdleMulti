// Manual compatibility comparison using the same desktop, network and binary.
// No Puppeteer import, CDP connection, extension or page script is used here.
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, rm } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { createGateway } from './gateway.mjs';
import { JsonProcess } from './rpc.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const root = process.env.DECK_DATA_DIR || '/data';
for (const folder of ['', 'imports', 'exports']) await mkdir(path.join(root, folder), { recursive: true });
const runtime = process.env.XDG_RUNTIME_DIR || '/tmp';
await mkdir(runtime, { recursive: true });
const profile = await mkdtemp(path.join(runtime, 'diagnostic-profile-'));
const x11 = new JsonProcess('/usr/bin/python3', [path.join(directory, 'x11.py')]);
let ready = false, closing = false, browser;
const gateway = createGateway({ root, webRoot: path.resolve(directory, '../web'), novncRoot: path.resolve(directory, '../node_modules/@novnc/novnc'),
  user: process.env.WEB_USER || 'pokeidle', password: process.env.WEB_PASSWORD, ready: () => ready });

async function close(code) {
  if (closing) return;
  closing = true; ready = false;
  gateway.close();
  if (browser && browser.exitCode === null && browser.signalCode === null) {
    const exited = new Promise(resolve => browser.once('exit', resolve));
    browser.kill('SIGTERM');
    await Promise.race([exited, delay(3000)]);
    if (browser.exitCode === null && browser.signalCode === null) {
      browser.kill('SIGKILL');
      await Promise.race([exited, delay(1000)]);
    }
  }
  x11.close();
  await rm(profile, { recursive: true, force: true }).catch(() => {});
  process.exit(code);
}
process.on('SIGTERM', () => close(0)); process.on('SIGINT', () => close(0));
x11.on('exit', () => { if (!closing) { console.error('[diagnostic] Serviço X11 encerrou.'); close(1); } });

try {
  await new Promise((resolve, reject) => { gateway.once('error', reject); gateway.listen(8080, '0.0.0.0', resolve); });
  const url = process.env.DECK_TEST_MODE === '1' ? new URL('../test/diagnostic.html', import.meta.url).href : 'https://pokeidle.io/app';
  console.log('[diagnostic] Chromium comum com perfil temporário; sem automações. As quatro contas estão preservadas em /data.');
  browser = spawn(process.env.CHROME_PATH || '/usr/bin/chromium', [
    '--no-sandbox', '--ozone-platform=x11', '--no-first-run', '--no-default-browser-check',
    '--start-maximized', '--class=PokeIdleDiagnostic', `--user-data-dir=${profile}`, url,
  ], { stdio: ['ignore', 'ignore', 'pipe'] });
  browser.stderr.pipe(process.stderr);
  browser.on('error', error => { console.error('[diagnostic]', error.message); close(1); });
  browser.on('exit', code => { if (!closing) { console.error(`[diagnostic] Chromium encerrou (código ${code}).`); close(1); } });
  const deadline = Date.now() + 30_000;
  while (!await x11.call('find', { class: 'PokeIdleDiagnostic' }, 3000)) {
    if (Date.now() >= deadline) throw new Error('A janela Chromium não abriu em 30 segundos');
    await delay(100);
  }
  ready = true;
  console.log('[diagnostic] Navegador pronto na WebUI. Use a barra de endereços para abrir https://debug.challenges.cloudflare.com se o jogo falhar.');
} catch (error) { console.error('[diagnostic]', error.message); await close(1); }
