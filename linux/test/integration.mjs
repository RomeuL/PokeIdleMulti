import assert from 'node:assert/strict';
import path from 'node:path';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import { setTimeout as delay } from 'node:timers/promises';

export async function run(deck, persistenceOnly = false) {
  const checks = [];
  async function check(name, work) { try { await work(); checks.push(name); } catch (error) { error.message = name + ': ' + error.message; throw error; } }
  if (persistenceOnly) {
    for (let i = 0; i < 4; i++) await check(`profile ${i + 1} persists across container restart`, async () => {
      assert.equal(await deck.accounts[i].page.evaluate(() => localStorage.getItem('isolation')), `profile-${i + 1}`);
      const cookies = await deck.accounts[i].browser.defaultBrowserContext().cookies();
      assert.equal(cookies.find(c => c.name === 'deck_test_cookie')?.value, `cookie-${i + 1}`);
    });
    return { passed: checks.length, checks };
  }
  await check('upstream C# business rules', async () => { const result = await deck.bridge.call('selftest'); assert.equal(result.passed, 6); });
  for (let i = 0; i < 4; i++) await deck.reload(i);
  for (let i = 0; i < 4; i++) await check(`profile ${i + 1} has isolated localStorage and cookies`, async () => {
    await deck.accounts[i].page.evaluate(value => localStorage.setItem('isolation', value), `profile-${i + 1}`);
    await deck.accounts[i].browser.defaultBrowserContext().setCookie({ name: 'deck_test_cookie', value: `cookie-${i + 1}`, domain: '127.0.0.1', path: '/', expires: Date.now() / 1000 + 86400 });
    for (let j = 0; j < i; j++) assert.equal(await deck.accounts[j].page.evaluate(() => localStorage.getItem('isolation')), `profile-${j + 1}`);
  });
  await check('four full Chromium windows embedded in the deck', async () => {
    await delay(1000);
    const windows = await deck.x11.call('inspect');
    const parent = windows.PokeIdleDeckShell.id;
    for (let i = 0; i < 4; i++) { assert.equal(windows[`PokeIdleAccount${i + 1}`].parent, parent); assert.ok(windows[`PokeIdleAccount${i + 1}`].width > 100); }
  });
  await check('echo replays a real click on all other accounts', async () => {
    deck.echo = true;
    await deck.accounts[0].page.click('#test-click');
    for (const account of deck.accounts) await account.page.waitForFunction(() => document.querySelector('#counter').textContent === '1', { timeout: 10000 });
    deck.echo = false;
  });
  await check('game confirmation dialogs remain under user control', async () => {
    const page = deck.accounts[0].page;
    const opening = new Promise(resolve => page.once('dialog', resolve));
    const answer = page.evaluate(() => confirm('Confirmação do jogo de teste'));
    const dialog = await opening; await delay(100); await dialog.accept();
    assert.equal(await answer, true);
  });
  await check('typing is never echoed', async () => {
    deck.echo = true;
    await deck.accounts[0].page.type('#typing', 'only account one');
    for (let i = 1; i < 4; i++) assert.equal(await deck.accounts[i].page.$eval('#typing', e => e.value), '');
    deck.echo = false;
  });
  await check('original WebSocket hooks initialize before the game', async () => {
    for (const account of deck.accounts) assert.deepEqual(await account.page.evaluate(() => [typeof __deckRecompra.summary, typeof __deckPvp.status, typeof __deckBonus.summary, window.__deckFrameLimit, window.__deckSpriteLimit]), ['function', 'function', 'function', true, true]);
  });
  await check('recompra buys through the original game socket and preserves minimum gold', async () => {
    const page = deck.accounts[0].page;
    await page.waitForFunction(() => window.__deckRecompra.summary().connected);
    await page.evaluate(() => window.__deckRecompra.configure({ on: true, balls: { on: true, id: 1, buy: 10, below: 100 }, revives: { on: false }, potions: { on: false }, goldFloor: 99900 }));
    await page.waitForFunction(() => window.__deckRecompra.summary().gold === 99900);
    await delay(4500);
    assert.equal(await page.evaluate(() => window.__deckRecompra.summary().gold), 99900);
    assert.equal(await page.evaluate(() => window.__deckRecompra.summary().balls[0].have), 30);
    assert.equal(await deck.accounts[1].page.evaluate(() => window.__deckRecompra.summary().gold), 100000);
    await deck.reload(0);
  });
  await check('PvP joins and leaves the queue using the upstream script', async () => {
    const page = deck.accounts[1].page;
    await page.evaluate(() => { window.__deckPvp.config({ tela: 'mini', vel: 2 }); window.__deckPvp.set(true); });
    await page.waitForFunction(() => window.__deckPvp.status().na, { timeout: 25000 });
    assert.equal(await page.evaluate(() => localStorage.getItem('pvpTelaBatalha')), 'mini');
    await page.evaluate(() => window.__deckPvp.set(false));
    assert.equal(await page.evaluate(() => window.__deckPvp.status().na), false);
  });
  await check('bonus monitor reads the game state and official live channels', async () => {
    const bonus = await deck.accounts[0].page.evaluate(() => window.__deckBonus.summary());
    assert.equal(bonus.twitch.linked, true);
    assert.ok(bonus.twitch.left.xp > 3500);
    assert.deepEqual(bonus.twitch.channels, [{ login: 'teste', live: true }]);
  });
  await check('column layout, focused account and grid layout', async () => {
    await deck.shell.page.click('[data-action=layout][data-value=Columns]');
    await deck.shell.page.waitForFunction(() => document.querySelector('#grid').dataset.layout === 'Columns');
    await delay(500);
    let windows = await deck.x11.call('inspect');
    assert.ok(windows.PokeIdleAccount1.width < 700);
    await deck.shell.page.click('#account-buttons [data-index="2"]');
    await deck.shell.page.waitForFunction(() => document.querySelector('#grid').dataset.layout === 'Focus');
    await delay(500);
    windows = await deck.x11.call('inspect');
    assert.equal(windows.PokeIdleAccount1.x, -32000);
    assert.ok(windows.PokeIdleAccount3.width > 1500);
    await deck.shell.page.click('[data-action=layout][data-value=Grid]');
    await deck.shell.page.waitForFunction(() => document.querySelector('#grid').dataset.layout === 'Grid');
    await delay(500);
  });
  await check('power off closes the browser and power on restores its profile', async () => {
    deck.config.Off[3] = true;
    await deck.stopAccount(3);
    assert.equal(deck.accounts[3], null);
    deck.config.Off[3] = false;
    await deck.startAccount(3);
    assert.equal(await deck.accounts[3].page.evaluate(() => localStorage.getItem('isolation')), 'profile-4');
  });
  await check('real Chromium extension content script, popup action and isolated storage', async () => {
    const destination = path.join(deck.root, 'imports', 'test-extension');
    await mkdir(destination, { recursive: true });
    await writeFile(path.join(destination, 'manifest.json'), JSON.stringify({ manifest_version: 3, name: 'Deck Integration Test', version: '1.0.0', permissions: ['storage'], host_permissions: ['http://127.0.0.1/*'], action: { default_popup: 'popup.html' }, content_scripts: [{ matches: ['http://127.0.0.1/*'], js: ['content.js'] }] }));
    await writeFile(path.join(destination, 'content.js'), 'document.documentElement.dataset.extensionTest="loaded";');
    await writeFile(path.join(destination, 'popup.html'), '<!doctype html><title>Deck test popup</title><p>Extension popup</p>');
    const installed = await deck.extensions.install([0, 1], destination);
    const id = installed[0].id;
    assert.equal(installed[1].id, id);
    await deck.reload(0); await deck.reload(1);
    await deck.accounts[0].page.waitForFunction(() => document.documentElement.dataset.extensionTest === 'loaded');
    await deck.extensions.storage(0, id, 'local', { isolation: 'first' });
    assert.equal((await deck.extensions.storage(0, id, 'local')).isolation, 'first');
    assert.equal((await deck.extensions.storage(1, id, 'local')).isolation, undefined);
    await deck.extensions.trigger(0, id);
    await delay(500);
    await deck.extensions.remove(0, id); await deck.extensions.remove(1, id);
  });
  await check('recompra dialog renders and closes without covering game windows', async () => {
    await deck.shell.page.click('#pane-0 [data-action=restock]');
    await deck.shell.page.waitForSelector('dialog[open] #restock-state');
    assert.equal(await deck.shell.page.$eval('dialog h1', e => e.textContent), 'Recompra · ' + deck.config.AccountNames[0]);
    await deck.shell.page.click('#close-dialog');
    await deck.shell.page.waitForFunction(() => !document.querySelector('dialog').open);
    await delay(500);
  });
  await check('diagnostic excludes cookies, tokens and account secrets', async () => {
    const filename = await deck.command('diagnostic');
    const text = await readFile(path.join(deck.root, 'exports', filename), 'utf8');
    assert.ok(!text.includes('cookie-1')); assert.ok(!text.includes('deck_test_cookie'));
  });
  await deck.save(deck.config);
  await deck.x11.call('screenshot', { path: '/data/exports/integration-desktop.png' });
  return { passed: checks.length, checks };
}
