import assert from 'node:assert/strict';
import { readFile, writeFile, rm } from 'node:fs/promises';
import puppeteer from 'puppeteer-core';
import { WebSocket } from 'ws';
import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { networkInterfaces } from 'node:os';
const token = await readFile('/tmp/deck-test-token', 'utf8');
async function api(method) {
  const response = await fetch('http://127.0.0.1:8081/api', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Deck-Token': token }, body: JSON.stringify({ method }) });
  const json = await response.json(); if (json.error) throw new Error(json.error); return json.result;
}
const response = await fetch('http://127.0.0.1:8081/api', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Deck-Token': token }, body: JSON.stringify({ method: 'test.run', args: { persistenceOnly: process.argv.includes('--persistence') } }), signal: AbortSignal.timeout(180000) });
const result = await response.json();
assert.equal(result.error, undefined, result.error);
console.log(JSON.stringify(result.result, null, 2));
if (!process.argv.includes('--persistence')) {
  const browser = await puppeteer.launch({ executablePath: process.env.CHROME_PATH || '/usr/bin/chromium', headless: true, args: ['--no-sandbox', '--test-type'], defaultViewport: { width: 1280, height: 720 } });
  try {
    await browser.defaultBrowserContext().overridePermissions('http://127.0.0.1:8080', ['clipboard-read', 'clipboard-write', 'clipboard-sanitized-write']);
    const page = await browser.newPage();
    await page.authenticate({ username: process.env.WEB_USER || 'pokeidle', password: process.env.WEB_PASSWORD });
    await page.goto('http://127.0.0.1:8080');
    await page.waitForFunction(() => document.querySelector('#connection').hidden, { timeout: 15000 });
    assert.ok(await page.$('#screen canvas'));
    await page.waitForFunction(() => {
      const canvas = document.querySelector('#screen canvas');
      if (!canvas || canvas.width < 100 || canvas.height < 100) return false;
      const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
      for (let i = 0; i < pixels.length; i += 128) if (pixels[i] > 120) return true;
      return false;
    }, { timeout: 15000, polling: 100 });
    const canvas = await page.$eval('#screen canvas', c => ({ ...c.getBoundingClientRect().toJSON(), bufferWidth: c.width, bufferHeight: c.height }));
    const mouse = async (x, y) => page.mouse.click(canvas.x + x * canvas.width / canvas.bufferWidth, canvas.y + y * canvas.height / canvas.bufferHeight);
    const targets = await api('test.targets');
    await mouse(targets.columns.x + targets.columns.width / 2, targets.columns.y + targets.columns.height / 2);
    for (let i = 0; i < 50 && (await api('state')).config.Layout !== 'Columns'; i++) await delay(100);
    assert.equal((await api('state')).config.Layout, 'Columns');
    await mouse(targets.grid.x + targets.grid.width / 2, targets.grid.y + targets.grid.height / 2);
    for (let i = 0; i < 50 && (await api('state')).config.Layout !== 'Grid'; i++) await delay(100);
    assert.equal((await api('state')).config.Layout, 'Grid'); await delay(300);
    const game = await api('test.targets');
    await mouse(game.window.x + game.button.x + game.button.width / 2, game.window.y + game.button.y + game.button.height / 2);
    for (let i = 0; i < 50 && Number((await api('test.targets')).counter) === Number(game.counter); i++) await delay(100);
    assert.equal(Number((await api('test.targets')).counter), Number(game.counter) + 1);
    await verifyClipboard(page, browser);
    await page.click('#controls summary'); await page.click('#audio');
    await page.waitForFunction(() => document.querySelector('#audio').textContent === 'Desligar áudio');
    await page.screenshot({ path: '/data/exports/integration-webui.png' });
    await verifyAudio();
    console.log('Browser WebUI: authenticated noVNC, framebuffer, remote layout controls, embedded game clicks and audio passed.');
  } finally { await browser.close(); }
}

async function verifyClipboard(page, browser) {
  const text = 'Copiar e colar: ação 🎮';
  const shortcut = async (client, key) => { await client.keyboard.down('Control'); await client.keyboard.press(key); await client.keyboard.up('Control'); };
  async function focusInput(client) {
    const targets = await api('test.targets');
    const canvas = await client.$eval('#screen canvas', c => ({ ...c.getBoundingClientRect().toJSON(), bw: c.width, bh: c.height }));
    await client.mouse.click(canvas.x + (targets.window.x + targets.typing.x + targets.typing.width / 2) * canvas.width / canvas.bw,
      canvas.y + (targets.window.y + targets.typing.y + targets.typing.height / 2) * canvas.height / canvas.bh);
    await shortcut(client, 'a');
  }
  async function waitForInput(expected) {
    for (let i = 0; i < 100 && (await api('test.targets')).typingValue !== expected; i++) await delay(100);
    assert.equal((await api('test.targets')).typingValue, expected);
  }
  await page.evaluate(text => navigator.clipboard.writeText(text), text);
  await focusInput(page); await shortcut(page, 'v'); await waitForInput(text);
  await shortcut(page, 'a');
  await page.evaluate(() => navigator.clipboard.writeText('previous local clipboard'));
  await shortcut(page, 'c');
  await page.waitForFunction(async expected => await navigator.clipboard.readText() === expected, { timeout: 10000 }, text);
  console.log('Clipboard: Ctrl+V into the native game and Ctrl+C back to the browser preserve Unicode.');

  const address = Object.values(networkInterfaces()).flat().find(value => value?.family === 'IPv4' && !value.internal)?.address;
  assert.ok(address, 'Container network address available for HTTP fallback test');
  const insecure = await browser.newPage();
  try {
    const fallbackText = 'Texto HTTP: café 🎮';
    await page.evaluate(text => navigator.clipboard.writeText(text), fallbackText);
    await insecure.authenticate({ username: process.env.WEB_USER || 'pokeidle', password: process.env.WEB_PASSWORD });
    await insecure.goto(`http://${address}:8080`);
    await insecure.waitForFunction(() => document.querySelector('#connection').hidden, { timeout: 15000 });
    assert.equal(await insecure.evaluate(() => window.isSecureContext), false);
    assert.equal(await insecure.evaluate(() => !!navigator.clipboard), false);
    await focusInput(insecure); await shortcut(insecure, 'v'); await waitForInput(fallbackText);
    await shortcut(insecure, 'a');
    await page.bringToFront();
    await page.evaluate(() => navigator.clipboard.writeText('previous HTTP clipboard'));
    await insecure.bringToFront(); await shortcut(insecure, 'c');
    await insecure.waitForFunction(() => document.querySelector('#clipboard-status').textContent === 'Texto copiado para o computador.', { timeout: 10000 });
    await page.bringToFront();
    assert.equal(await page.evaluate(() => navigator.clipboard.readText()), fallbackText);
    console.log('Clipboard: HTTP paste-event fallback works without navigator.clipboard.');
  } finally { await insecure.close(); await page.bringToFront(); }
}

async function verifyAudio() {
  const frames = 12000, wav = Buffer.alloc(44 + frames * 4);
  wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16);
  wav.writeUInt16LE(1, 20); wav.writeUInt16LE(2, 22); wav.writeUInt32LE(48000, 24); wav.writeUInt32LE(192000, 28);
  wav.writeUInt16LE(4, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(frames * 4, 40);
  for (let i = 0; i < frames; i++) for (let channel = 0; channel < 2; channel++) wav.writeInt16LE(Math.round(Math.sin(i * 2 * Math.PI * 440 / 48000) * 4000), 44 + i * 4 + channel * 2);
  const filename = '/tmp/pokeidle-audio-test.wav'; await writeFile(filename, wav);
  const authorization = 'Basic ' + Buffer.from(`${process.env.WEB_USER || 'pokeidle'}:${process.env.WEB_PASSWORD}`).toString('base64');
  const socket = new WebSocket('ws://127.0.0.1:8080/audio', { headers: { Authorization: authorization, Origin: 'http://127.0.0.1:8080' } });
  try {
    await new Promise((resolve, reject) => { socket.once('open', resolve); socket.once('error', reject); });
    const received = new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Audio monitor did not deliver sound')), 10000);
      socket.on('message', data => { if (data.some(byte => byte !== 0)) { clearTimeout(timer); resolve(); } });
    });
    const play = spawn('paplay', ['--device=deck', filename]);
    play.on('error', () => {});
    await received;
  } finally { socket.close(); await rm(filename, { force: true }); }
}
