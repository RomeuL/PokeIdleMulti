import { test } from 'node:test';
import assert from 'node:assert/strict';
import { runInNewContext } from 'node:vm';
import { setupGame } from '../src/chromium.mjs';

test('game hooks initialize on the main game document and leave verification and login frames alone', async () => {
  let registered;
  const page = {
    exposeFunction: async () => {},
    evaluateOnNewDocument: async source => { registered = source; },
    emulateMediaFeatures: async () => {},
    createCDPSession: async () => ({}),
  };
  const scripts = Object.fromEntries(['frames', 'sprites', 'echo', 'recompra', 'pvp', 'bonus'].map(name => [name, `window.hooks.push('${name}');`]));
  await setupGame({ page, index: 0 }, scripts, { GameFps: 30, SpriteMs: 220, Muted: [false], ReduceMotion: false }, () => {}, 'https://pokeidle.io/app');

  function document(origin, child = false) {
    const context = { location: { origin }, hooks: [], addEventListener() {}, document: { addEventListener() {}, querySelectorAll: () => [] } };
    context.window = context;
    context.top = child ? {} : context;
    runInNewContext(registered, context);
    return context;
  }
  const game = document('https://pokeidle.io');
  assert.equal(game.hooks.length, 6);
  assert.equal(typeof game.chrome.webview.postMessage, 'function');
  assert.equal(typeof game.__deckAudio.set, 'function');
  for (const [origin, child] of [
    ['https://challenges.cloudflare.com', true],
    ['https://pokeidle.io', true],
    ['https://login.example', false],
    ['http://pokeidle.io', false],
  ]) {
    const other = document(origin, child);
    assert.equal(other.hooks.length, 0);
    assert.equal(other.chrome, undefined);
    assert.equal(other.__deckAudio, undefined);
    assert.equal(other.__deckFps, undefined);
  }
});
