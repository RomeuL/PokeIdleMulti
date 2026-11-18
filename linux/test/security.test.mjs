import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, symlink, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { WebSocket } from 'ws';
import { createGateway, authorized, safeFile, sameOrigin } from '../src/gateway.mjs';
import { crxPayload, extensionId } from '../src/extensions.mjs';
import { validateConfig } from '../src/config.mjs';

test('HTTP Basic authentication rejects missing, wrong and partial credentials', () => {
  const encode = text => 'Basic ' + Buffer.from(text).toString('base64');
  assert.equal(authorized(encode('pokeidle:long-test-password'), 'pokeidle', 'long-test-password'), true);
  for (const header of [undefined, '', 'Bearer x', encode('pokeidle:wrong'), encode('pokeidle:long-test-password:extra')]) assert.equal(authorized(header, 'pokeidle', 'long-test-password'), false);
});
test('WebSocket and upload requests reject another browser origin', () => {
  assert.equal(sameOrigin({ headers: { host: 'localhost:8080', origin: 'http://localhost:8080' } }), true);
  assert.equal(sameOrigin({ headers: { host: 'localhost:8080', origin: 'https://other.example' } }), false);
});
test('downloads reject traversal, absolute paths and symlinks outside exports', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'deck-paths-'));
  try {
    const exports = path.join(root, 'exports'); await mkdir(exports);
    await writeFile(path.join(root, 'secret'), 'not public');
    await writeFile(path.join(exports, 'report.json'), '{}');
    await symlink(path.join(root, 'secret'), path.join(exports, 'link'));
    assert.equal(await safeFile(exports, 'report.json'), path.join(exports, 'report.json'));
    for (const name of ['../secret', '/etc/passwd', 'link', '..\\secret', '.hidden']) await assert.rejects(safeFile(exports, name));
  } finally { await rm(root, { recursive: true, force: true }); }
});
test('gateway requires authentication and CSRF, limits public files, and protects WebSockets', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'deck-http-'));
  for (const folder of ['imports', 'exports']) await mkdir(path.join(root, folder));
  const password = 'long-test-password', auth = 'Basic ' + Buffer.from('pokeidle:' + password).toString('base64');
  const server = createGateway({ root, webRoot: path.resolve(import.meta.dirname, '../web'), novncRoot: path.resolve(import.meta.dirname, '../node_modules/@novnc/novnc'), user: 'pokeidle', password, ready: () => true });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  try {
    assert.equal((await fetch(base + '/healthz')).status, 200);
    assert.equal((await fetch(base + '/')).status, 401);
    assert.equal((await fetch(base + '/', { headers: { Authorization: auth } })).status, 200);
    assert.equal((await fetch(base + '/api/upload?name=test.zip', { method: 'POST', headers: { Authorization: auth }, body: 'file' })).status, 403);
    const { csrf } = await (await fetch(base + '/api/session', { headers: { Authorization: auth } })).json();
    const upload = await fetch(base + '/api/upload?name=test.zip', { method: 'POST', headers: { Authorization: auth, 'X-Deck-CSRF': csrf }, body: 'file' });
    assert.equal(upload.status, 200); assert.ok((await upload.json()).path.startsWith(path.join(root, 'imports')));
    assert.equal((await fetch(base + '/api/download?name=../secret', { headers: { Authorization: auth } })).status, 400);
    assert.equal((await fetch(base + '/api/session', { headers: { Authorization: auth, Origin: 'https://other.example' } })).status, 403);
    for (const headers of [{}, { Authorization: auth, Origin: 'https://other.example' }]) await new Promise((resolve, reject) => {
      const socket = new WebSocket(base.replace('http:', 'ws:') + '/socket', { headers });
      socket.on('open', () => { socket.close(); reject(new Error('Unauthorized WebSocket accepted')); });
      socket.on('unexpected-response', (_, response) => { assert.equal(response.statusCode, 403); response.resume(); socket.terminate(); resolve(); });
      socket.on('error', () => {});
    });
  } finally { await new Promise(resolve => server.close(resolve)); await rm(root, { recursive: true, force: true }); }
});
test('malformed CRX headers are rejected and extension IDs are deterministic', () => {
  assert.throws(() => crxPayload(Buffer.from('Cr24')));
  const invalid = Buffer.alloc(12); invalid.write('Cr24'); invalid.writeUInt32LE(3, 4); invalid.writeUInt32LE(2000, 8);
  assert.throws(() => crxPayload(invalid), /truncado/);
  assert.match(extensionId(Buffer.from('public key')), /^[a-p]{32}$/);
  assert.equal(extensionId(Buffer.from('public key')), extensionId(Buffer.from('public key')));
});
test('malformed configuration is rejected before persistence', () => {
  assert.throws(() => validateConfig(null, {}));
  assert.throws(() => validateConfig({ AccountNames: [] }, {}));
});
