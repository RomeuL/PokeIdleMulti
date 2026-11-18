import { EventEmitter } from 'node:events';
import { readFile, writeFile, mkdir, readdir, cp, rename } from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { launchBrowser, waitForWindow, setupGame, applyGameConfig, evaluate, browserMemory, profilePath } from './chromium.mjs';
import { Extensions } from './extensions.mjs';
import { validateConfig } from './config.mjs';

const safeChannel = value => /^[a-zA-Z0-9_-]{1,64}$/.test(value);
const sites = ['Twitch', 'Kick'];
const rewardKinds = ['xp', 'captura', 'shiny'];
const gameURL = process.env.DECK_TEST_MODE === '1' ? 'http://127.0.0.1:8081/fixture' : 'https://pokeidle.io/app';

export class Deck extends EventEmitter {
  accounts = Array(4).fill(null);
  panes = Array.from({ length: 4 }, () => ({ status: 'abrindo…', restock: null, pvp: null, note: '' }));
  rectangles = [];
  overlay = false;
  echo = false;
  shuttingDown = false;
  liveStatus = { Twitch: {}, Kick: {} };
  bulk = { running: false, cancelled: false, progress: [] };
  constructor(root, bridge, x11) {
    super(); Object.assign(this, { root, bridge, x11 });
    this.extensions = new Extensions(root, index => this.accounts[index], (index, url, key) => this.newWindow(index, url, { key }));
    bridge.on('twitch.notice', event => this.twitchNotice(event).catch(error => this.problem(error)));
  }
  problem(error) { console.error('[deck]', error.message); }
  async init() {
    const initial = await this.bridge.call('init');
    Object.assign(this, { config: initial.config, scripts: initial.scripts, balls: initial.balls, potions: initial.potions });
    await this.extensions.init();
  }
  async start(shellURL) {
    this.shell = await launchBrowser(path.join(this.root, 'ui'), 'PokeIdleDeckShell', shellURL, { kiosk: true });
    await waitForWindow(this.x11, 'PokeIdleDeckShell');
    this.shell.browser.on('disconnected', () => { if (!this.shuttingDown) this.emit('fatal', new Error('A interface foi encerrada')); });
    for (let index = 0; index < 4; index++) if (!this.config.Off[index]) await this.startAccount(index);
    this.timer = setInterval(() => this.tick().catch(error => this.problem(error)), 1000);
    this.emitState();
  }
  snapshot() {
    return { config: this.config, panes: this.panes, echo: this.echo, extensions: this.accounts.map((_, i) => this.extensions.list(i)),
      liveStatus: this.liveStatus, bulk: this.bulk, memory: this.memory || 0, now: Date.now(), balls: this.balls, potions: this.potions };
  }
  emitState() { this.emit('state', this.snapshot()); }
  async save(config) {
    this.config = await this.bridge.call('config.save', { config: validateConfig(config, this.config) });
    this.emitState(); return this.config;
  }
  async startAccount(index) {
    if (this.accounts[index]) return;
    this.panes[index].status = 'abrindo…'; this.emitState();
    const account = await launchBrowser(profilePath(this.root, index), `PokeIdleAccount${index + 1}`, 'http://127.0.0.1:8081/blank');
    Object.assign(account, { index, loadedAt: Date.now(), startedAt: Date.now(), retries: 0, pages: new Map(), liveChannels: { Twitch: [], Kick: [] }, points: {}, channelIds: {}, guardHold: {}, busy: false });
    this.accounts[index] = account;
    account.bridgeExtension = (await account.cdp.send('Extensions.loadUnpacked', { path: path.resolve(import.meta.dirname, '../chrome-bridge') })).id;
    await account.cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: path.join(this.root, 'exports') });
    await this.extensions.load(index);
    await setupGame(account, this.scripts, this.config, message => this.gameMessage(index, message));
    const page = account.page;
    page.on('domcontentloaded', () => applyGameConfig(account, this.scripts, this.config).catch(error => this.problem(error)));
    page.on('load', () => { account.loadedAt = Date.now(); applyGameConfig(account, this.scripts, this.config).catch(error => this.problem(error)); });
    page.on('error', () => { this.panes[index].status = 'navegador caiu; recarregando…'; this.reload(index).catch(error => this.problem(error)); });
    account.browser.on('targetcreated', async target => {
      const popup = await target.page().catch(() => null);
      if (!popup || popup === page) return;
      popup.on('response', response => {
        if (response.request().method() === 'POST' && /^https:\/\/(www\.)?topidle\.com\/api\/games\/[^/]+\/vote/.test(response.url()) && response.ok()) {
          this.panes[index].note = 'TopIdle: voto registrado'; this.refreshVote(index).catch(() => {});
        }
      });
    });
    account.browser.on('disconnected', () => {
      if (this.accounts[index] !== account || this.shuttingDown) return;
      this.accounts[index] = null;
      this.bridge.call('watchers.stop', { account: index }).catch(() => {});
      this.panes[index].status = 'navegador caiu; reiniciando…'; this.emitState();
      setTimeout(() => { if (!this.config.Off[index] && !this.shuttingDown) this.startAccount(index).catch(error => this.problem(error)); }, 3000);
    });
    await waitForWindow(this.x11, account.wmClass);
    if (!await this.x11.call('embed', { class: account.wmClass, parent: 'PokeIdleDeckShell' })) throw new Error('Não foi possível incorporar o navegador');
    await this.arrange();
    await page.goto(gameURL, { waitUntil: 'domcontentloaded', timeout: 60_000 }).catch(() => { this.panes[index].status = 'não carregou: sem internet ou o site caiu'; });
    await applyGameConfig(account, this.scripts, this.config).catch(() => {});
    this.panes[index].startedAt = account.startedAt;
    this.emitState();
  }
  async stopAccount(index) {
    const account = this.accounts[index]; this.accounts[index] = null;
    await this.bridge.call('watchers.stop', { account: index });
    if (account) await account.browser.close();
    this.panes[index] = { status: 'desligada', restock: null, pvp: null, note: '' };
    this.emitState();
  }
  async arrange(rectangles = this.rectangles) {
    this.rectangles = rectangles;
    for (let i = 0; i < 4; i++) {
      const account = this.accounts[i], r = rectangles[i];
      if (!account || !r) continue;
      await this.x11.call('bounds', { class: account.wmClass, ...r, visible: r.visible && !this.overlay });
    }
  }
  async reload(index, clean = false) {
    const account = this.accounts[index];
    if (!account) return;
    account.loadedAt = Date.now();
    if (clean) { await account.pageCdp.send('Network.clearBrowserCache'); await account.pageCdp.send('Storage.clearDataForOrigin', { origin: new URL(gameURL).origin, storageTypes: 'service_workers,cache_storage' }); }
    await account.page.goto(gameURL, { waitUntil: 'domcontentloaded', timeout: 60_000 }).catch(() => {});
  }
  async gameMessage(index, message) {
    if (message === 'F11') { this.emit('fullscreen'); return; }
    if (!this.echo || typeof message !== 'string' || message.length > 4096) return;
    const script = await this.bridge.call('echo.replay', { message });
    if (!script) return;
    for (let i = 0; i < 4; i++) if (i !== index && this.accounts[i]) {
      const found = await evaluate(this.accounts[i].page, script).catch(() => false);
      if (!found) this.panes[i].note = 'Eco: este botão não está nesta tela';
    }
    this.emitState();
  }
  async tick() {
    if (this.ticking || this.shuttingDown) return;
    this.ticking = true;
    try {
      for (let i = 0; i < 4; i++) {
        const account = this.accounts[i];
        if (!account) continue;
        try {
          const status = await evaluate(account.page, `({ restock: window.__deckRecompra?.summary() ?? null, pvp: window.__deckPvp?.status() ?? null, bonus: window.__deckBonus?.summary() ?? null, open: ${this.scripts.open}, nick: localStorage.getItem('nick') })`, 4000);
          Object.assign(this.panes[i], status);
          this.panes[i].status = status.open === 'login' ? 'na tela de login' : status.open || 'carregando…';
          const good = ['jogo', 'app', 'login', 'confirmar-email', 'escolher-nick', 'aberta em outro lugar', 'cloudflare'].includes(status.open) || status.open?.startsWith('aviso do jogo');
          if (!good && Date.now() - account.loadedAt > Math.min(5, account.retries + 1) * 60_000) {
            account.retries++; account.loadedAt = Date.now();
            await writeFile(path.join(this.root, 'aberturas.log'), `${new Date().toISOString()} conta${i + 1}: ${status.open}\n`, { flag: 'a' });
            await this.reload(i);
          } else if (good) account.retries = 0;
        } catch { this.panes[i].status = 'carregando…'; }
      }
      const now = Date.now();
      if (!this.memoryAt || now - this.memoryAt > 30_000) {
        this.memoryAt = now;
        this.memory = (await Promise.all(this.accounts.filter(Boolean).map(a => browserMemory(a.browser)))).reduce((a, b) => a + b, 0);
      }
      if (process.env.DECK_TEST_MODE !== '1' && (!this.livesAt || now - this.livesAt > 120_000)) {
        this.livesAt = now; this.refreshLives().catch(error => this.problem(error));
      }
      if (process.env.DECK_TEST_MODE !== '1') for (let i = 0; i < 4; i++) if (this.accounts[i] && (!this.accounts[i].voteAt || now - this.accounts[i].voteAt > 60_000)) this.refreshVote(i).catch(() => {});
      this.emitState();
    } finally { this.ticking = false; }
  }
  async login(index, site) {
    const account = this.accounts[index];
    const cookies = await account.browser.defaultBrowserContext().cookies();
    const domain = site === 'Twitch' ? 'twitch.tv' : 'kick.com';
    const relevant = cookies.filter(c => c.domain.replace(/^\./, '') === domain || c.domain.endsWith('.' + domain));
    const get = name => relevant.find(c => c.name === name)?.value || '';
    return site === 'Twitch'
      ? { token: get('auth-token'), device: get('unique_id') }
      : { token: decodeURIComponent(get('session_token')), device: '' };
  }
  async newWindow(index, url, { hidden = false, key = null, init = null } = {}) {
    const account = this.accounts[index];
    if (!account) throw new Error('Ligue esta conta primeiro');
    if (key && account.pages.has(key)) {
      const p = account.pages.get(key);
      if (!p.isClosed()) { if (!hidden) await p.bringToFront(); return p; }
    }
    const { targetId } = await account.cdp.send('Target.createTarget', { url: 'about:blank', newWindow: true, background: hidden });
    const target = await account.browser.waitForTarget(t => t._targetId === targetId);
    const page = await target.page();
    if (key) account.pages.set(key, page);
    if (init) await page.evaluateOnNewDocument(init);
    const client = await page.createCDPSession();
    const { windowId } = await client.send('Browser.getWindowForTarget');
    await client.send('Browser.setWindowBounds', { windowId, bounds: hidden ? { left: -20000, top: -20000, width: 360, height: 240 } : { left: 60, top: 70, width: 1100, height: 760 } });
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45_000 }).catch(() => {});
    return page;
  }
  async host(index, site, channel) {
    const account = this.accounts[index], key = `host:${site}:${channel}`;
    const page = await this.newWindow(index, site === 'Twitch' ? `https://player.twitch.tv/?channel=${channel}&parent=twitch.tv&player=popout&autoplay=false` : `https://player.kick.com/${channel}?autoplay=false`, { hidden: true, key, init: this.scripts.noDrm });
    // Redeem pages are short lived. Watching continues in the upstream C# worker.
    clearTimeout(account.hostTimers?.get(key)); account.hostTimers ||= new Map();
    account.hostTimers.set(key, setTimeout(() => { page.close().catch(() => {}); account.pages.delete(key); account.hostTimers.delete(key); }, 60_000));
    return page;
  }
  async refreshLives() {
    if (this.refreshingLives) return;
    this.refreshingLives = true;
    try {
      const live = await this.bridge.call('lives.check', { twitch: this.config.TwitchChannels, kick: this.config.KickChannels });
      this.liveStatus = { Twitch: live.twitch, Kick: live.kick };
      for (let index = 0; index < 4; index++) {
        const account = this.accounts[index];
        if (!account || !this.config.LivesAccounts.includes(index + 1)) continue;
        for (const site of sites) {
          if (this.accounts[index] !== account || !this.config.LivesAccounts.includes(index + 1)) break;
          if (this.bulk.running && this.bulk.index === index && this.bulk.site === site) continue;
          const limit = this.config[site][index];
          const channels = this.config[`${site}Channels`].filter(c => this.liveStatus[site][c] !== false && !limit.Closed.includes(c)).slice(0, limit.Slots ?? this.config[`${site}Lives`]);
          account.liveChannels[site] = channels;
          const credentials = await this.login(index, site);
          if (!credentials.token || channels.length === 0) { this.panes[index][site] = channels.length ? 'entre na conta pelas lives' : 'sem lives abertas'; await this.bridge.call('watchers.stop-site', { account: index, site }); continue; }
          const reward = this.config[`Redeem${site}`][index];
          let userId = account.twitchUserId;
          account.redeemAt ||= {};
          const readDue = Date.now() - (account.redeemAt[site] || 0) >= (site === 'Kick' ? 300000 : 120000);
          if (readDue) account.redeemAt[site] = Date.now();
          for (const channel of readDue ? channels : []) {
            if (this.accounts[index] !== account || !this.config.LivesAccounts.includes(index + 1)) break;
            const args = { account: index, site, channel, reward, ...credentials };
            let raw = await this.bridge.call('redeem.read', args);
            if (this.accounts[index] !== account || !this.config.LivesAccounts.includes(index + 1)) break;
            if (this.bulk.running && this.bulk.index === index && this.bulk.site === site) break;
            const parsed = JSON.parse(raw);
            if (parsed.me) userId = account.twitchUserId = parsed.me;
            if (parsed.channelId) account.channelIds[channel] = parsed.channelId;
            const claim = site === 'Twitch' && this.config.ClaimTwitchBonus;
            if ((reward || claim) && await this.bridge.call('redeem.needsPage', { ...args, json: raw, claim })) {
              const script = await this.bridge.call('redeem.script', { ...args, claim });
              const page = await this.host(index, site, channel);
              raw = await evaluate(page, script, 20_000).catch(() => '{}') || '{}';
            }
            const applied = await this.bridge.call('redeem.apply', { ...args, json: raw });
            account.points[`${site}:${channel}`] = applied;
            if (applied.note) this.panes[index].note = applied.note;
          }
          if (this.accounts[index] !== account || !this.config.LivesAccounts.includes(index + 1)) break;
          if (site === 'Kick' || userId) await this.bridge.call('watchers.sync', { account: index, site, ...credentials, channels, userId, channelIds: channels.map(c => account.channelIds[c]).filter(Boolean) });
          this.panes[index].watchers = await this.bridge.call('watchers.status', { account: index, twitch: account.liveChannels.Twitch, kick: account.liveChannels.Kick });
          this.panes[index].points = account.points;
          await this.bonusGuard(index, site, credentials);
        }
      }
    } finally { this.refreshingLives = false; this.emitState(); }
  }
  async bonusGuard(index, site, credentials) {
    const account = this.accounts[index], reward = this.config[`Redeem${site}`][index];
    const bonus = this.panes[index].bonus?.[site.toLowerCase()];
    const kind = rewardKinds[['Xp', 'Capture', 'Lure'].indexOf(reward)];
    if (!this.config.BonusGuard[index] || !reward || !bonus?.linked || !kind || (bonus.left?.[kind] || 0) >= 1800 || Date.now() < (account.guardHold[site] || 0)) return;
    account.guardHold[site] = Date.now() + 5 * 60_000;
    const candidates = [...new Set([...this.config[`${site}Channels`], ...bonus.channels.map(c => c.login)])].filter(c => safeChannel(c) && !account.liveChannels[site].includes(c));
    const reads = {};
    for (const channel of candidates) reads[channel] = JSON.parse(await this.bridge.call('redeem.read', { account: index, site, channel, reward, ...credentials }));
    const pick = await this.bridge.call('guard.pick', { account: index, site, bonus, candidates, reads });
    if (!pick) return;
    const script = await this.bridge.call('redeem.script', { account: index, site, channel: pick.channel, reward, ...credentials });
    const raw = await evaluate(await this.host(index, site, pick.channel), script, 20_000).catch(() => '{}') || '{}';
    const applied = await this.bridge.call('redeem.apply', { account: index, site, channel: pick.channel, json: raw });
    if (applied.note) this.panes[index].note = 'Guarda: ' + applied.note;
    const before = bonus.left?.[kind] || 0;
    if (JSON.parse(raw).redeemed) {
      setTimeout(() => {
        const after = this.panes[index].bonus?.[site.toLowerCase()]?.left?.[kind] || 0;
        if (after <= before) {
          account.guardHold[site] = Date.now() + 30 * 60_000;
          this.bridge.call('log.add', { account: index, site, text: `${pick.channel}: a guarda resgatou, mas o jogo não confirmou o bônus; espera de 30 min` }).catch(() => {});
        }
      }, 5 * 60_000).unref();
    }
  }
  async twitchNotice({ account: index, notice }) {
    const account = this.accounts[index];
    if (!account || !this.config.LivesAccounts.includes(index + 1)) return;
    const m = notice.message;
    const channelId = notice.channelId || m.data?.channel_id;
    const channel = Object.keys(account.channelIds).find(c => account.channelIds[c] === channelId);
    if (!channel || !account.liveChannels.Twitch.includes(channel)) return;
    const credentials = await this.login(index, 'Twitch');
    if (!credentials.token) return;
    const args = { account: index, site: 'Twitch', channel, reward: this.config.RedeemTwitch[index], ...credentials };
    if (notice.type === 'points-earned') {
      const gain = m.data?.point_gain;
      if (gain) await this.bridge.call('redeem.earned', { ...args, reason: gain.reason_code, points: gain.total_points, balance: m.data?.balance?.balance });
    } else if (notice.type === 'claim-available' && this.config.ClaimTwitchBonus) {
      const script = await this.bridge.call('redeem.script', { ...args, claim: true, spend: false, claimId: m.data?.claim?.id });
      const raw = await evaluate(await this.host(index, 'Twitch', channel), script, 20_000).catch(() => '{}') || '{}';
      const applied = await this.bridge.call('redeem.apply', { ...args, json: raw });
      if (applied.note) this.panes[index].note = applied.note;
    } else if (notice.type === 'raid_update_v2' || notice.type === 'active') {
      const id = notice.type === 'active' ? m.data?.moment_id : m.raid?.id;
      account.notices ||= new Set();
      if (!id || account.notices.has(id)) return;
      account.notices.add(id);
      const method = notice.type === 'active' ? 'twitch.moment' : 'twitch.raid';
      const script = await this.bridge.call(method, { ...args, id });
      const result = await evaluate(await this.host(index, 'Twitch', channel), script, 20_000).catch(() => '{}') || '{}';
      const json = JSON.parse(result);
      await this.bridge.call('log.add', { ...args, text: `${channel}: ${method} ${json.error || (json.claimed || json.joined ? 'confirmado' : 'sem confirmação')}` });
    }
    this.emitState();
  }
  async refreshVote(index) {
    const account = this.accounts[index]; if (!account || account.voteBusy) return;
    account.voteAt = Date.now(); account.voteBusy = true;
    try {
      const response = await account.browser.defaultBrowserContext().cookies();
      const cookies = response.filter(c => /(^|\.)topidle\.com$/.test(c.domain.replace(/^\./, ''))).map(c => `${c.name}=${c.value}`).join('; ');
      const headers = { Accept: 'application/json', Cookie: cookies };
      const r = await fetch('https://topidle.com/api/games', { headers, signal: AbortSignal.timeout(10_000) });
      if (!r.ok) return;
      const json = await r.json(), games = Array.isArray(json) ? json : json.games || [];
      const game = games.find(g => (g.domain || '').replace(/^www\./, '') === 'pokeidle.io' || /^https:\/\/(www\.)?pokeidle\.io/.test(g.url || ''));
      if (!game) return;
      this.topIdleGameId = game.id;
      this.panes[index].vote = { canVote: game.canVote !== false || game.nextVoteAt * 1000 < Date.now(), nextVoteAt: game.nextVoteAt ? game.nextVoteAt * 1000 : null };
    } finally { account.voteBusy = false; }
  }
  async bulkRead(index, site, channel) {
    if (!safeChannel(channel) || !sites.includes(site)) throw new Error('Canal inválido');
    const credentials = await this.login(index, site);
    if (!credentials.token) throw new Error(`Entre na ${site} pelas lives desta conta`);
    const args = { ...credentials, channel, api: site === 'Twitch' ? 'https://gql.twitch.tv' : 'https://kick.com' };
    const script = await this.bridge.call('bulk.script', { site, operation: 'read', args });
    const raw = await evaluate(await this.host(index, site, channel), script, 20_000);
    const result = JSON.parse(raw || '{}');
    if (result.error) throw new Error(result.error);
    return { ...result, args };
  }
  async bulkRun(index, site, channel, counts) {
    if (this.bulk.running) throw new Error('Já existe um resgate em lote em andamento');
    if (!Array.isArray(counts) || counts.length !== 3 || counts.some(n => !Number.isInteger(n) || n < 0 || n > 999)) throw new Error('Quantidade inválida');
    this.bulk = { running: true, cancelled: false, progress: [], index, site }; this.emitState();
    try {
      const context = await this.bulkRead(index, site, channel);
      const total = counts.reduce((sum, n, k) => sum + n * (context.rewards[k]?.cost || 0), 0);
      for (let k = 0; k < 3; k++) if (counts[k] && (!context.rewards[k] || context.rewards[k].blocked)) throw new Error(`Bônus indisponível: ${rewardKinds[k]}`);
      if (!context.owner && (!Number.isFinite(context.points) || total > context.points)) throw new Error('Saldo insuficiente');
      for (let k = 0; k < 3; k++) for (let n = 0; n < counts[k]; n++) {
        if (this.bulk.cancelled || !this.accounts[index]) return;
        const r = context.rewards[k];
        const script = await this.bridge.call('bulk.script', { site, operation: 'redeem', args: { ...context.args, channelId: context.channelId, rewardId: r.id, title: r.title, cost: r.cost, prompt: r.prompt } });
        const raw = await evaluate(await this.host(index, site, channel), script, 20_000);
        const result = JSON.parse(raw || '{}');
        if (!result.redeemed) throw new Error(result.error || 'O site não confirmou o resgate');
        const text = `${channel}: resgate em lote ${r.title} (${n + 1}/${counts[k]}; ${r.cost} pontos)`;
        this.bulk.progress.unshift(text);
        await this.bridge.call('log.add', { account: index, site, text });
        await this.bridge.call('redeem.forget', { account: index, site, channel });
        this.emitState(); await delay(1500);
      }
    } catch (error) { this.bulk.progress.unshift(error.message); }
    finally { this.bulk.running = false; this.emitState(); }
  }
  async command(method, a = {}) {
    const index = a.index ?? 0;
    if (!Number.isInteger(index) || index < 0 || index > 3) throw new Error('Conta inválida');
    switch (method) {
      case 'state': return this.snapshot();
      case 'bounds':
        if (!Array.isArray(a.rectangles) || a.rectangles.length !== 4 || a.rectangles.some(r => ['x', 'y', 'width', 'height'].some(k => !Number.isFinite(r[k]) || Math.abs(r[k]) > 10000))) throw new Error('Layout inválido');
        await this.arrange(a.rectangles); return true;
      case 'overlay': {
        let frames = [];
        if (a.open && !this.overlay) frames = await Promise.all(this.accounts.map(async account => account ? (await account.page.screenshot({ type: 'jpeg', quality: 70, encoding: 'base64' }).catch(() => null)) : null));
        this.overlay = !!a.open; await this.arrange(); return frames;
      }
      case 'echo': this.echo = !!a.on; this.emitState(); return true;
      case 'config': {
        const previous = this.config;
        await this.save(a.config);
        for (let i = 0; i < 4; i++) {
          if (this.config.Off[i]) { if (this.accounts[i]) await this.stopAccount(i); }
          else { if (!this.accounts[i]) await this.startAccount(i); await applyGameConfig(this.accounts[i], this.scripts, this.config); }
          if (!this.config.LivesAccounts.includes(i + 1)) await this.bridge.call('watchers.stop', { account: i });
        }
        if (previous.Ultra && !this.config.Ultra) for (const account of this.accounts.filter(Boolean)) await evaluate(account.page, `${this.scripts.ultra}(false)`);
        if (a.restartLives) { await this.bridge.call('redeem.restart'); this.livesAt = 0; }
        this.emitState(); return this.config;
      }
      case 'reload': await this.reload(index, !!a.clean); return true;
      case 'wipe': {
        const account = this.accounts[index]; if (!account) return false;
        const origin = new URL(gameURL).origin;
        await account.page.goto('http://127.0.0.1:8081/blank');
        if (a.everything) { for (const cookie of await account.browser.defaultBrowserContext().cookies()) await account.browser.defaultBrowserContext().deleteCookie(cookie); }
        await account.pageCdp.send('Storage.clearDataForOrigin', { origin, storageTypes: 'all' });
        await this.reload(index, true); return true;
      }
      case 'vote': {
        if (!this.topIdleGameId) await this.refreshVote(index);
        if (!this.topIdleGameId) throw new Error('O TopIdle não respondeu');
        let nick = this.panes[index].nick || '';
        try { nick = JSON.parse(nick); } catch { /* plain string */ }
        await this.newWindow(index, `https://topidle.com/jogo/${encodeURIComponent(this.topIdleGameId)}?votar=1${safeChannel(nick) ? '&playerIdentifier=' + encodeURIComponent(nick) : ''}`, { key: 'vote' }); return true;
      }
      case 'live.login':
        if (!sites.includes(a.site)) throw new Error('Site inválido');
        await this.newWindow(index, a.site === 'Twitch' ? 'https://www.twitch.tv/login' : 'https://kick.com/login', { key: `login:${a.site}` }); return true;
      case 'live.player':
        if (!sites.includes(a.site) || !safeChannel(a.channel)) throw new Error('Canal inválido');
        await this.newWindow(index, a.site === 'Twitch' ? `https://player.twitch.tv/?channel=${a.channel}&parent=twitch.tv&player=popout` : `https://player.kick.com/${a.channel}`, { key: `player:${a.site}:${a.channel}`, init: this.scripts.noDrm });
        return true;
      case 'live.points':
        if (!sites.includes(a.site) || !safeChannel(a.channel)) throw new Error('Canal inválido');
        await this.newWindow(index, a.site === 'Twitch' ? `https://www.twitch.tv/popout/${a.channel}/chat?popout=` : `https://kick.com/${a.channel}`, { key: `points:${a.site}:${a.channel}`,
          init: a.full ? null : `addEventListener('DOMContentLoaded', () => { const s=document.createElement('style'); s.textContent=${JSON.stringify(a.site === 'Twitch' ? '.chat-scrollable-area__message-container,.pinned-chat__message{display:none!important}' : '#chatroom-messages,[data-testid="pinned-message-modal"]{display:none!important}')}; document.head.append(s); });` }); return true;
      case 'extensions.install': return this.extensions.install(a.all ? [0, 1, 2, 3].filter(i => this.accounts[i]) : [index], a.source, !!a.store);
      case 'extensions.scan': return this.extensions.scan(a.source);
      case 'extensions.import-storage': {
        if (!/^[a-p]{32}$/.test(a.id) || !this.extensions.list(index).some(e => e.id === a.id)) throw new Error('Extensão inválida');
        let source = await this.extensions.importedPath(a.source);
        const candidates = [source, path.join(source, a.id), path.join(source, 'Local Extension Settings', a.id), path.join(source, 'Default', 'Local Extension Settings', a.id)];
        source = '';
        for (const candidate of candidates) {
          const files = await readdir(candidate, { withFileTypes: true }).catch(() => []);
          if (files.some(f => f.isFile() && /\.(ldb|sst|log)$/.test(f.name))) { source = candidate; break; }
        }
        if (!source) throw new Error('Não encontrei o banco de configurações da extensão neste arquivo/pasta');
        const wasOn = !!this.accounts[index];
        if (wasOn) await this.stopAccount(index);
        try {
          const destination = path.join(profilePath(this.root, index), 'Default', 'Local Extension Settings', a.id);
          const backup = path.join(this.root, 'backups', `extensao-${index + 1}-${a.id}-${Date.now()}`);
          await mkdir(path.dirname(backup), { recursive: true });
          await rename(destination, backup).catch(error => { if (error.code !== 'ENOENT') throw error; });
          await mkdir(destination, { recursive: true });
          for (const file of await readdir(source, { withFileTypes: true })) if (file.isFile() && /^(CURRENT|MANIFEST-\d+|OPTIONS-\d+|\d+\.(ldb|sst|log))$/.test(file.name)) await cp(path.join(source, file.name), path.join(destination, file.name));
        } finally { if (wasOn) await this.startAccount(index); }
        return true;
      }
      case 'extensions.remove': await this.extensions.remove(index, a.id); this.emitState(); return true;
      case 'extensions.trigger': await this.extensions.trigger(index, a.id); return true;
      case 'extensions.copy': {
        if (!Number.isInteger(a.from) || a.from < 0 || a.from > 3) throw new Error('Conta inválida');
        for (const area of ['local', 'sync']) await this.extensions.storage(index, a.id, area, await this.extensions.storage(a.from, a.id, area));
        return true;
      }
      case 'lives.restart':
        await this.bridge.call('redeem.restart');
        for (const account of this.accounts.filter(Boolean)) { account.guardHold = {}; account.redeemAt = {}; }
        this.livesAt = 0;
        return true;
      case 'lives.refresh': this.livesAt = 0; return true;
      case 'sniper':
        for (const i of a.all ? [0, 1, 2, 3].filter(i => this.accounts[i]) : [index]) {
          const extension = this.extensions.list(i).find(e => /rotom|sniper/i.test(e.name) || e.id === 'olccbpdjdkbfgondiiaicmlifhbcggmk');
          if (extension) await this.extensions.storage(i, extension.id, 'local', { rotom_sniper_enabled: !!a.on });
        }
        return true;
      case 'log.read': return this.bridge.call('log.read', { account: index, all: !!a.all });
      case 'log.export': {
        let lines = await this.bridge.call('log.read', { account: index, all: !!a.all });
        if (a.site && sites.includes(a.site)) lines = lines.filter(line => line.includes(` · ${a.site} · `));
        if (a.search) lines = lines.filter(line => line.toLowerCase().includes(String(a.search).toLowerCase()));
        const filename = `resgates-${Date.now()}.txt`;
        await writeFile(path.join(this.root, 'exports', filename), lines.join('\n') + '\n');
        return filename;
      }
      case 'log.clear': return this.bridge.call('log.clear', { account: index, all: !!a.all });
      case 'bulk.read': { const { args, ...publicResult } = await this.bulkRead(index, a.site, a.channel); return publicResult; }
      case 'bulk.run':
        if (this.bulk.running) throw new Error('Já existe um resgate em lote em andamento');
        this.bulkRun(index, a.site, a.channel, a.counts).catch(error => this.problem(error)); return true;
      case 'bulk.cancel': this.bulk.cancelled = true; return true;
      case 'diagnostic': {
        const filename = `diagnostico-${new Date().toISOString().replaceAll(':', '-')}.json`;
        const report = { date: new Date().toISOString(), platform: process.platform, node: process.version,
          browsers: await Promise.all(this.accounts.filter(Boolean).map(async account => ({ account: account.index + 1, version: await account.browser.version(), gameStatus: this.panes[account.index].status, retries: account.retries, extensions: this.extensions.list(account.index).map(({ id, version }) => ({ id, version })) }))) };
        await writeFile(path.join(this.root, 'exports', filename), JSON.stringify(report, null, 2)); return filename;
      }
      case 'test.run':
        if (process.env.DECK_TEST_MODE !== '1') throw new Error('Ação desconhecida');
        return (await import('../test/integration.mjs')).run(this, a.persistenceOnly);
      case 'test.targets': {
        if (process.env.DECK_TEST_MODE !== '1') throw new Error('Ação desconhecida');
        const windows = await this.x11.call('inspect');
        return {
          columns: await this.shell.page.$eval('[data-action="layout"][data-value="Columns"]', e => e.getBoundingClientRect().toJSON()),
          grid: await this.shell.page.$eval('[data-action="layout"][data-value="Grid"]', e => e.getBoundingClientRect().toJSON()),
          button: await this.accounts[0].page.$eval('#test-click', e => e.getBoundingClientRect().toJSON()),
          window: windows.PokeIdleAccount1,
          counter: await this.accounts[0].page.$eval('#counter', e => e.textContent),
        };
      }
      default: throw new Error('Ação desconhecida');
    }
  }
  async close() {
    this.shuttingDown = true; clearInterval(this.timer);
    await Promise.allSettled(this.accounts.filter(Boolean).map(async a => { for (const t of a.hostTimers?.values() || []) clearTimeout(t); await a.browser.close(); }));
    await this.shell?.browser.close();
    this.bridge.close(); this.x11.close();
  }
}
