import { spawn } from 'node:child_process';
import { EventEmitter, once } from 'node:events';
import { mkdir } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';

// Ordinary Chromium for user-controlled login. No DevTools connection, page
// scripts, UA overrides or deck extensions are installed in this process.
export async function launchNativeBrowser(profile, wmClass, url) {
  await mkdir(profile, { recursive: true });
  const child = spawn(process.env.CHROME_PATH || '/usr/bin/chromium', [
    '--no-sandbox', '--ozone-platform=x11', '--no-first-run', '--no-default-browser-check',
    '--start-maximized', `--class=${wmClass}`, `--user-data-dir=${profile}`, url,
  ], { stdio: ['ignore', 'ignore', 'pipe'] });
  child.stderr.pipe(process.stderr);
  const browser = new EventEmitter();
  child.on('exit', () => browser.emit('disconnected'));
  await once(child, 'spawn');
  let closing;
  browser.process = () => child;
  browser.close = () => closing ||= (async () => {
    if (child.exitCode !== null || child.signalCode !== null) return;
    const exited = once(child, 'exit');
    child.kill('SIGTERM');
    await Promise.race([exited, delay(5000, null, { ref: false })]);
    if (child.exitCode === null && child.signalCode === null) {
      child.kill('SIGKILL');
      await exited;
    }
  })();
  return { browser, wmClass, profile, manualLogin: true };
}
