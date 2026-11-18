import http from 'node:http';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { writeFile, mkdir, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { WebSocketServer } from 'ws';
import { JsonProcess } from './rpc.mjs';
import { Deck } from './deck.mjs';
import { createGateway, serveFile, readJSON, sendJSON } from './gateway.mjs';

const directory = path.dirname(fileURLToPath(import.meta.url));
const root = process.env.DECK_DATA_DIR || '/data';
for (const folder of ['', 'imports', 'exports']) await mkdir(path.join(root, folder), { recursive: true });
const bridge = new JsonProcess(process.env.DOTNET_PATH || '/usr/share/dotnet/dotnet', [process.env.BRIDGE_PATH || path.resolve(directory, '../bridge/bin/Release/net10.0/DeckBridge.dll')]);
const x11 = new JsonProcess('/usr/bin/python3', [path.join(directory, 'x11.py')]);
const deck = new Deck(root, bridge, x11);
await deck.init();
const token = randomBytes(32).toString('hex');
const valid = candidate => {
  if (typeof candidate !== 'string') return false;
  const bytes = Buffer.from(candidate), expected = Buffer.from(token);
  return bytes.length === expected.length && timingSafeEqual(bytes, expected);
};
const clients = new Set();
const fixtures = process.env.DECK_TEST_MODE === '1';
const updates = new WebSocketServer({ noServer: true });
const fixtureSockets = new WebSocketServer({ noServer: true });
const local = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://127.0.0.1:8081');
    if (url.pathname === '/api') {
      if (request.method !== 'POST' || !valid(request.headers['x-deck-token']) || request.headers.origin && request.headers.origin !== 'http://127.0.0.1:8081') return sendJSON(response, { error: 'Não autorizado' }, 403);
      const { method, args } = await readJSON(request);
      return sendJSON(response, { result: await deck.command(method, args) });
    }
    if (url.pathname === '/blank') { response.writeHead(200, { 'Content-Type': 'text/html' }); return response.end('<!doctype html><html style="background:#111117;color:#8a8a9e;font:14px sans-serif"><p>Abrindo conta…</p></html>'); }
    if (url.pathname === '/fixture' && fixtures) return await serveFile(response, path.resolve(directory, '../test/fixture.html'));
    if (url.pathname === '/assets/items.json' && fixtures) return sendJSON(response, { items: [{ id: 200, name: 'Small Potion', category: 'heal', healAmount: 60, npcPrice: 50 }, { id: 300, name: 'Revive', category: 'revive', npcPrice: 100 }] });
    const allowed = { '/deck': 'deck.html', '/deck.js': 'deck.js', '/deck.css': 'deck.css' };
    if (!allowed[url.pathname]) return sendJSON(response, { error: 'Não encontrado' }, 404);
    response.setHeader('Content-Security-Policy', "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self' ws://127.0.0.1:8081; img-src 'self' data:; font-src 'self'; base-uri 'none'; frame-ancestors 'none'");
    await serveFile(response, path.resolve(directory, '../ui', allowed[url.pathname]));
  } catch (error) { if (!response.headersSent) sendJSON(response, { error: error.message }, 400); else response.destroy(); }
});
local.on('upgrade', (request, socket, head) => {
  if (fixtures && request.url === '/fixture-ws') {
    fixtureSockets.handleUpgrade(request, socket, head, ws => fixtureSockets.emit('connection', ws)); return;
  }
  const protocols = (request.headers['sec-websocket-protocol'] || '').split(',').map(s => s.trim());
  if (request.url !== '/events' || !protocols.some(valid) || request.headers.origin !== 'http://127.0.0.1:8081') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
  updates.handleUpgrade(request, socket, head, ws => {
    clients.add(ws); ws.on('close', () => clients.delete(ws)); ws.on('error', () => ws.close());
    ws.send(JSON.stringify({ type: 'state', value: deck.snapshot() }));
  });
});
fixtureSockets.on('connection', ws => {
  // Deliberately local, deterministic game state for integration tests. No real
  // account, purchases, live points, or external services are used by tests.
  const estado = { nick: 'Teste', level: 100, gold: 100000, balls: { 1: 20 }, items: { 200: 10, 300: 5 }, automation: {}, activeId: 1, loja: { vip: false },
    twitch: { vinculado: true, bonus: { xp: Date.now() + 3600000 }, oficiais: [{ login: 'teste', ativo: true, aoVivo: true }] } };
  ws.send(JSON.stringify({ t: 'welcome', estado, catalogoBolas: [{ id: 1, nome: 'Poke Ball', compravel: true, priceGold: 10 }], itensNossos: [] }));
  ws.on('message', data => {
    try {
      const m = JSON.parse(data.toString());
      if (m.t === 'shop.buy') {
        const stock = m.kind === 'ball' ? estado.balls : estado.items;
        stock[m.id] = (stock[m.id] || 0) + m.qty; estado.gold -= m.qty * 10;
        ws.send(JSON.stringify({ t: 'estado', estado }));
        ws.send(JSON.stringify({ t: 'batalha', eventos: [{ k: 'compra', qtd: m.qty, gasto: m.qty * 10 }] }));
      } else if (m.t === 'pvp.fila.entrar') ws.send(JSON.stringify({ t: 'pvp', fila: { na: true, desde: Date.now(), tamanho: 1 } }));
      else if (m.t === 'pvp.fila.sair') ws.send(JSON.stringify({ t: 'pvp', fila: { na: false } }));
    } catch { /* Test protocol ignores unknown messages. */ }
  });
});
deck.on('state', value => { const message = JSON.stringify({ type: 'state', value }); for (const ws of clients) if (ws.readyState === ws.OPEN && ws.bufferedAmount < 1024 ** 2) ws.send(message); });
deck.on('fullscreen', () => { for (const ws of clients) if (ws.readyState === ws.OPEN) ws.send(JSON.stringify({ type: 'fullscreen' })); });
await new Promise(resolve => local.listen(8081, '127.0.0.1', resolve));
let ready = false;
const gateway = createGateway({ root, webRoot: path.resolve(directory, '../web'), novncRoot: path.resolve(directory, '../node_modules/@novnc/novnc'),
  user: process.env.WEB_USER || 'pokeidle', password: process.env.WEB_PASSWORD, ready: () => ready });
await new Promise(resolve => gateway.listen(8080, '0.0.0.0', resolve));
let closing = false;
async function close(code = 0) {
  if (closing) return; closing = true; ready = false;
  await deck.close().catch(() => {});
  for (const ws of clients) ws.close();
  gateway.close(); local.close();
  process.exit(code);
}
process.on('SIGTERM', () => close()); process.on('SIGINT', () => close());
deck.on('fatal', error => { deck.problem(error); close(1); });
bridge.on('exit', () => { if (!closing) close(1); });
x11.on('exit', () => { if (!closing) close(1); });
if (fixtures) await writeFile('/tmp/deck-test-token', token, { mode: 0o600 });
try {
  await deck.start(`http://127.0.0.1:8081/deck#${token}`);
  ready = true;
  console.log('PokeIdle Deck pronto na porta 8080.');
} catch (error) { deck.problem(error); await close(1); }
