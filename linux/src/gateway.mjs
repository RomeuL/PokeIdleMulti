import http from 'node:http';
import net from 'node:net';
import { createHash, timingSafeEqual, randomBytes } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createReadStream, createWriteStream } from 'node:fs';
import { stat, readdir, realpath, rename, rm } from 'node:fs/promises';
import { pipeline } from 'node:stream/promises';
import path from 'node:path';
import { WebSocketServer } from 'ws';
import { readClipboard, writeClipboard } from './clipboard.mjs';

export function authorized(header, user, password) {
  if (typeof header !== 'string' || !header.startsWith('Basic ')) return false;
  const actual = Buffer.from(header.slice(6), 'base64').toString('utf8');
  const hash = text => createHash('sha256').update(text).digest();
  return timingSafeEqual(hash(actual), hash(`${user}:${password}`));
}
export function sameOrigin(request) {
  if (!request.headers.origin) return true; // Non-browser clients still need authentication.
  try { return new URL(request.headers.origin).host === request.headers.host; } catch { return false; }
}
export async function safeFile(directory, filename) {
  if (!filename || path.basename(filename) !== filename || filename.includes('\\') || filename.startsWith('.')) throw new Error('Arquivo inválido');
  const root = await realpath(directory);
  const target = await realpath(path.join(root, filename));
  if (!target.startsWith(root + path.sep) || !(await stat(target)).isFile()) throw new Error('Arquivo inválido');
  return target;
}
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml', '.png': 'image/png', '.json': 'application/json' };
export async function serveFile(response, filename) {
  const info = await stat(filename);
  response.writeHead(200, { 'Content-Type': types[path.extname(filename)] || 'application/octet-stream', 'Content-Length': info.size, 'X-Content-Type-Options': 'nosniff', 'Cache-Control': 'no-store' });
  await pipeline(createReadStream(filename), response);
}
export async function readJSON(request, max = 1024 * 1024) {
  let size = 0; const chunks = [];
  for await (const chunk of request) { size += chunk.length; if (size > max) throw new Error('Pedido grande demais'); chunks.push(chunk); }
  return JSON.parse(Buffer.concat(chunks).toString('utf8'));
}
export function sendJSON(response, value, status = 200) { response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }); response.end(JSON.stringify(value)); }

export function createGateway({ root, webRoot, novncRoot, user, password, ready, clipboard = { read: readClipboard, write: writeClipboard } }) {
  if (!password) throw new Error('Defina WEB_PASSWORD com uma senha não vazia');
  const csrf = randomBytes(32).toString('hex');
  const sockets = new WebSocketServer({ noServer: true, maxPayload: 1024 * 1024 });
  const server = http.createServer(async (request, response) => {
    try {
      const url = new URL(request.url, 'http://localhost');
      if (url.pathname === '/healthz') return sendJSON(response, { ready: ready() }, ready() ? 200 : 503);
      if (!authorized(request.headers.authorization, user, password)) {
        response.writeHead(401, { 'WWW-Authenticate': 'Basic realm="PokeIdle Deck", charset="UTF-8"' }); return response.end('Autenticação necessária');
      }
      if (!sameOrigin(request)) return sendJSON(response, { error: 'Origem inválida' }, 403);
      if (request.method === 'GET' && url.pathname === '/api/session') return sendJSON(response, { csrf });
      if (request.method === 'POST' && url.pathname === '/api/clipboard') {
        if (request.headers['x-deck-csrf'] !== csrf) return sendJSON(response, { error: 'Sessão inválida' }, 403);
        const value = await readJSON(request);
        if (value.action === 'read') return sendJSON(response, { text: await clipboard.read() });
        if (value.action !== 'write' || typeof value.text !== 'string' || Buffer.byteLength(value.text, 'utf8') > 256 * 1024) throw new Error('Envie apenas texto de até 256 KB');
        await clipboard.write(value.text);
        return sendJSON(response, { ok: true });
      }
      if (request.method === 'GET' && url.pathname === '/api/files') {
        const files = {};
        for (const folder of ['imports', 'exports']) files[folder] = (await readdir(path.join(root, folder), { withFileTypes: true })).filter(e => e.isFile() && !e.name.startsWith('.')).map(e => e.name);
        return sendJSON(response, files);
      }
      if (request.method === 'POST' && url.pathname === '/api/upload') {
        if (request.headers['x-deck-csrf'] !== csrf) return sendJSON(response, { error: 'Sessão inválida' }, 403);
        const filename = path.basename(url.searchParams.get('name') || '').replace(/[^a-zA-Z0-9_.-]/g, '_').slice(0, 120);
        if (!filename || filename.startsWith('.')) throw new Error('Nome de arquivo inválido');
        const target = path.join(root, 'imports', `${Date.now()}-${randomBytes(4).toString('hex')}-${filename}`);
        const temporary = target + '.part';
        let size = 0;
        const destination = createWriteStream(temporary, { flags: 'wx', mode: 0o600 });
        try {
          async function* limited(source) {
            for await (const chunk of source) { size += chunk.length; if (size > 50 * 1024 ** 2) throw new Error('Limite de 50 MB por arquivo'); yield chunk; }
          }
          await pipeline(request, limited, destination);
          await rename(temporary, target);
        } catch (error) { destination.destroy(); await rm(temporary, { force: true }); throw error; }
        return sendJSON(response, { path: target });
      }
      if (request.method === 'GET' && url.pathname === '/api/download') {
        const filename = await safeFile(path.join(root, 'exports'), url.searchParams.get('name'));
        response.setHeader('Content-Disposition', `attachment; filename="${path.basename(filename).replaceAll('"', '')}"`);
        return await serveFile(response, filename);
      }
      if (request.method !== 'GET') return sendJSON(response, { error: 'Método inválido' }, 405);
      const assetRoot = url.pathname.startsWith('/novnc/') ? novncRoot : webRoot;
      const name = url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname.startsWith('/novnc/') ? url.pathname.slice(7) : url.pathname.slice(1));
      const filename = path.resolve(assetRoot, name);
      const resolvedRoot = await realpath(assetRoot), resolved = await realpath(filename);
      if (!resolved.startsWith(resolvedRoot + path.sep)) return sendJSON(response, { error: 'Não encontrado' }, 404);
      await serveFile(response, resolved);
    } catch (error) { if (!response.headersSent) sendJSON(response, { error: error.code === 'ENOENT' ? 'Não encontrado' : error.message }, error.code === 'ENOENT' ? 404 : 400); else response.destroy(); }
  });
  server.on('upgrade', (request, socket, head) => {
    if (!authorized(request.headers.authorization, user, password) || !sameOrigin(request) || !['/socket', '/audio'].includes(request.url)) { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    sockets.handleUpgrade(request, socket, head, ws => {
      if (request.url === '/socket') {
        const tcp = net.createConnection({ host: '127.0.0.1', port: 5900 });
        tcp.on('data', data => { if (ws.readyState === ws.OPEN) { if (ws.bufferedAmount > 16 * 1024 ** 2) return ws.close(1013); ws.send(data, { binary: true }); } });
        ws.on('message', (data, binary) => { if (!binary) return ws.close(1003); if (!tcp.write(data)) ws.pause(); });
        tcp.on('drain', () => ws.resume());
        tcp.on('error', () => ws.close(1011)); tcp.on('close', () => ws.close());
        ws.on('close', () => tcp.destroy()); ws.on('error', () => tcp.destroy());
      } else {
        // PulseAudio output monitor only. Never records the user's microphone.
        const audio = spawn('parec', ['--device=deck.monitor', '--format=s16le', '--rate=48000', '--channels=2', '--latency-msec=40'], { stdio: ['ignore', 'pipe', 'ignore'] });
        audio.stdout.on('data', data => { if (ws.readyState === ws.OPEN && ws.bufferedAmount < 256 * 1024) ws.send(data, { binary: true }); });
        audio.on('error', () => ws.close(1011)); audio.on('exit', () => ws.close());
        ws.on('close', () => audio.kill()); ws.on('error', () => audio.kill());
      }
    });
  });
  return server;
}
