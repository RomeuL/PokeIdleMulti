import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import puppeteer from 'puppeteer-core';
import { Deck } from '../src/deck.mjs';
import { withTimeout } from '../src/chromium.mjs';

test('desktop startup completes while all four external game navigations are still pending', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'deck-startup-'));
  let releaseNavigation;
  const pendingNavigation = new Promise(resolve => { releaseNavigation = resolve; });
  const navigations = [];
  // No browser or container is started. Only the external process boundary is
  // replaced; the actual Deck.start/startAccount/launchBrowser code executes.
  const launcher = mock.method(puppeteer, 'launch', async options => {
    const page = Object.assign(new EventEmitter(), {
      url: () => options.args.at(-1),
      exposeFunction: async () => {},
      evaluateOnNewDocument: async () => {},
      emulateMediaFeatures: async () => {},
      createCDPSession: async () => ({ send: async () => ({}) }),
      goto: async url => { navigations.push(url); await pendingNavigation; },
      evaluate: async () => null,
      isClosed: () => false,
    });
    return Object.assign(new EventEmitter(), {
      pages: async () => [page],
      target: () => ({ createCDPSession: async () => ({ send: async () => ({ id: 'bridge' }) }) }),
      close: async () => {},
    });
  });
  const bridge = Object.assign(new EventEmitter(), { close() {} });
  const deck = new Deck(root, bridge, { call: async () => 1, close() {} });
  deck.extensions = { load: async () => {}, list: () => [] };
  deck.config = {
    Off: [false, false, false, false], Muted: [false, false, false, false],
    Restock: [{}, {}, {}, {}], Zoom: [1, 1, 1, 1], GameFps: 30, SpriteMs: 220,
  };
  deck.scripts = {};
  try {
    await withTimeout(deck.start('http://127.0.0.1:8081/deck'), 1500, 'Desktop blocked on the external website');
    assert.equal(launcher.mock.callCount(), 5);
    assert.equal(navigations.length, 4);
    assert.ok(deck.accounts.every(account => account.navigationPending));
    releaseNavigation();
    await Promise.all(deck.accounts.map(account => account.navigation));
    assert.ok(deck.accounts.every(account => !account.navigationPending));
  } finally {
    releaseNavigation();
    await deck.close();
    launcher.mock.restore();
    await rm(root, { recursive: true, force: true });
  }
});
