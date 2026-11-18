import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile, readdir, cp, realpath, rm, open } from 'node:fs/promises';
import path from 'node:path';
import yauzl from 'yauzl';

export function crxPayload(buffer) {
  if (buffer.subarray(0, 4).toString() !== 'Cr24') return { zip: buffer, key: null };
  const version = buffer.readUInt32LE(4);
  if (version === 2) {
    const keySize = buffer.readUInt32LE(8), signatureSize = buffer.readUInt32LE(12);
    const offset = 16 + keySize + signatureSize;
    if (offset > buffer.length) throw new Error('CRX truncado');
    return { zip: buffer.subarray(offset), key: buffer.subarray(16, 16 + keySize) };
  }
  if (version !== 3) throw new Error('Formato CRX não suportado');
  const headerSize = buffer.readUInt32LE(8);
  if (12 + headerSize > buffer.length) throw new Error('CRX truncado');
  const header = buffer.subarray(12, 12 + headerSize);
  // Protobuf CRX3: publisher proofs contain the DER public key in field 1.
  const fields = protobuf(header);
  const keys = fields.filter(f => f.field === 2 || f.field === 3).flatMap(f => protobuf(f.data).filter(v => v.field === 1).map(v => v.data));
  const signed = fields.find(f => f.field === 10000);
  const crxId = signed && protobuf(signed.data).find(f => f.field === 1)?.data;
  const key = crxId && keys.find(k => createHash('sha256').update(k).digest().subarray(0, 16).equals(crxId));
  return { zip: buffer.subarray(12 + headerSize), keys, key: key || null };
}

function protobuf(buffer) {
  let offset = 0;
  function varint() {
    let n = 0, shift = 0;
    while (offset < buffer.length && shift < 49) {
      const b = buffer[offset++]; n += (b & 127) * 2 ** shift;
      if (!(b & 128)) return n;
      shift += 7;
    }
    throw new Error('Cabeçalho CRX inválido');
  }
  const fields = [];
  while (offset < buffer.length) {
    const tag = varint(), type = tag & 7, field = tag >>> 3;
    if (type === 0) { varint(); continue; }
    if (type === 1) { offset += 8; continue; }
    if (type === 5) { offset += 4; continue; }
    if (type !== 2) throw new Error('Cabeçalho CRX inválido');
    const length = varint();
    if (offset + length > buffer.length) throw new Error('Cabeçalho CRX truncado');
    fields.push({ field, data: buffer.subarray(offset, offset + length) }); offset += length;
  }
  return fields;
}

export function extensionId(key) {
  return [...createHash('sha256').update(key).digest().subarray(0, 16)].flatMap(n => [String.fromCharCode(97 + (n >> 4)), String.fromCharCode(97 + (n & 15))]).join('');
}

export async function unpack(zip, destination) {
  await mkdir(destination, { recursive: true });
  const archive = await new Promise((resolve, reject) => yauzl.fromBuffer(zip, { lazyEntries: true, validateEntrySizes: true }, (e, z) => e ? reject(e) : resolve(z)));
  let total = 0, count = 0;
  await new Promise((resolve, reject) => {
    const fail = error => { archive.close(); reject(error); };
    archive.on('error', fail);
    archive.on('end', resolve);
    archive.on('entry', async entry => {
      try {
        if (++count > 10_000 || (total += entry.uncompressedSize) > 200 * 1024 ** 2) throw new Error('Extensão grande demais');
        const name = entry.fileName.replaceAll('\\', '/');
        if (name.startsWith('/') || name.split('/').some(p => p === '..') || ((entry.externalFileAttributes >>> 16) & 0xf000) === 0xa000) throw new Error('Caminho inválido no ZIP');
        const target = path.resolve(destination, name);
        if (!target.startsWith(path.resolve(destination) + path.sep)) throw new Error('Caminho inválido no ZIP');
        if (name.endsWith('/')) await mkdir(target, { recursive: true });
        else {
          await mkdir(path.dirname(target), { recursive: true });
          const stream = await new Promise((r, j) => archive.openReadStream(entry, (e, s) => e ? j(e) : r(s)));
          const file = await open(target, 'wx', 0o600);
          try { for await (const chunk of stream) await file.write(chunk); } finally { await file.close(); }
        }
        archive.readEntry();
      } catch (error) { fail(error); }
    });
    archive.readEntry();
  });
}

export class Extensions {
  constructor(root, getAccount, openPopup) { this.root = root; this.getAccount = getAccount; this.openPopup = openPopup; this.file = path.join(root, 'extensions.json'); this.entries = []; }
  async init() {
    try { this.entries = JSON.parse(await readFile(this.file, 'utf8')); } catch (e) { if (e.code !== 'ENOENT') throw e; }
    await mkdir(path.join(this.root, 'extensions'), { recursive: true });
  }
  async save() { await writeFile(this.file + '.tmp', JSON.stringify(this.entries, null, 2)); await (await import('node:fs/promises')).rename(this.file + '.tmp', this.file); }
  async load(index) {
    const account = this.getAccount(index);
    for (const entry of this.entries.filter(e => e.accounts.includes(index))) {
      const { id } = await account.cdp.send('Extensions.loadUnpacked', { path: entry.path });
      if (entry.id !== id) { entry.id = id; await this.save(); }
    }
    return this.list(index);
  }
  list(index) { return this.entries.filter(e => e.accounts.includes(index)).map(({ id, name, version, popup }) => ({ id, name, version, popup })); }
  async install(indexes, source, fromStore = false) {
    if (!indexes.length || indexes.some(i => !this.getAccount(i))) throw new Error('Ligue as contas antes de instalar extensões');
    const previousEntries = structuredClone(this.entries), loaded = [];
    let payload, expectedId = null;
    if (fromStore) {
      expectedId = source.match(/[a-p]{32}/)?.[0];
      if (!expectedId) throw new Error('Cole o link da extensão na Chrome Web Store');
      const url = `https://clients2.google.com/service/update2/crx?response=redirect&prodversion=142.0.0.0&acceptformat=crx2,crx3&x=id%3D${expectedId}%26installsource%3Dondemand%26uc`;
      const response = await fetch(url, { signal: AbortSignal.timeout(30_000) });
      if (!response.ok) throw new Error(`A loja recusou o download (${response.status})`);
      payload = crxPayload(Buffer.from(await response.arrayBuffer()));
    } else {
      const resolved = await realpath(source);
      const imports = await realpath(path.join(this.root, 'imports'));
      if (!resolved.startsWith(imports + path.sep)) throw new Error('Use um arquivo ou pasta dentro de /data/imports');
      const stat = await (await import('node:fs/promises')).stat(resolved);
      if (stat.isDirectory()) payload = { folder: resolved };
      else payload = crxPayload(await readFile(resolved));
    }
    const destination = path.join(this.root, 'extensions', createHash('sha256').update(source + Date.now()).digest('hex').slice(0, 20));
    try {
      if (payload.folder) await cp(payload.folder, destination, { recursive: true, dereference: false, filter: async p => !(await (await import('node:fs/promises')).lstat(p)).isSymbolicLink() });
      else await unpack(payload.zip, destination);
      let manifestPath = path.join(destination, 'manifest.json');
      try { await readFile(manifestPath); } catch {
        const children = await readdir(destination, { withFileTypes: true });
        const directories = children.filter(c => c.isDirectory() && !c.name.startsWith('_'));
        if (directories.length !== 1) throw new Error('O ZIP precisa conter manifest.json ou uma única pasta com a extensão');
        manifestPath = path.join(destination, directories[0].name, 'manifest.json');
      }
      const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
      const key = payload.key || payload.keys?.find(k => !expectedId || extensionId(k) === expectedId);
      if (expectedId && (!key || extensionId(key) !== expectedId)) throw new Error('A chave da extensão não corresponde ao ID da loja');
      if (key && !manifest.key) manifest.key = key.toString('base64');
      await writeFile(manifestPath, JSON.stringify(manifest, null, 2));
      await rm(path.join(path.dirname(manifestPath), '_metadata'), { recursive: true, force: true });
      const results = [];
      for (const index of indexes) {
        const account = this.getAccount(index);
        if (!account) throw new Error('Ligue a conta antes de instalar extensões');
        const { id } = await account.cdp.send('Extensions.loadUnpacked', { path: path.dirname(manifestPath) });
        loaded.push({ index, id });
        let entry = this.entries.find(e => e.id === id);
        if (!entry) {
          entry = { id, name: manifest.name, version: manifest.version, popup: manifest.action?.default_popup || manifest.browser_action?.default_popup || null, path: path.dirname(manifestPath), accounts: [] };
          this.entries.push(entry);
        }
        Object.assign(entry, { name: manifest.name, version: manifest.version, path: path.dirname(manifestPath), popup: manifest.action?.default_popup || manifest.browser_action?.default_popup || null });
        if (!entry.accounts.includes(index)) entry.accounts.push(index);
        results.push({ index, id });
      }
      await this.save();
      return results;
    } catch (error) {
      for (const { index, id } of loaded) {
        const previous = previousEntries.find(e => e.id === id && e.accounts.includes(index));
        const account = this.getAccount(index);
        if (account) await (previous ? account.cdp.send('Extensions.loadUnpacked', { path: previous.path }) : account.cdp.send('Extensions.uninstall', { id })).catch(() => {});
      }
      this.entries = previousEntries;
      await this.save().catch(() => {});
      await rm(destination, { recursive: true, force: true }); throw error;
    }
  }
  async remove(index, id) {
    const account = this.getAccount(index);
    if (account) await account.cdp.send('Extensions.uninstall', { id });
    const entry = this.entries.find(e => e.id === id);
    if (entry) entry.accounts = entry.accounts.filter(i => i !== index);
    await this.save();
  }
  async trigger(index, id) {
    const entry = this.entries.find(e => e.id === id && e.accounts.includes(index));
    if (!entry) throw new Error('Extensão não instalada nesta conta');
    // Same behavior as the Windows PopupWindow: open the extension's actual
    // popup document in this account's profile. Chromium app windows do not
    // expose a normal browser toolbar for Extensions.triggerAction.
    let popup = entry.popup;
    if (!popup) {
      const manifest = JSON.parse(await readFile(path.join(entry.path, 'manifest.json'), 'utf8'));
      const candidates = (await readdir(entry.path)).filter(f => f.endsWith('.html') && f !== manifest.background?.page);
      if (candidates.length === 1) popup = candidates[0];
    }
    if (!popup) throw new Error('Esta extensão não tem uma janela de configuração');
    return this.openPopup(index, `chrome-extension://${id}/${popup}`, `extension:${id}`);
  }
  async importedPath(source) {
    const resolved = await realpath(source), imports = await realpath(path.join(this.root, 'imports'));
    if (!resolved.startsWith(imports + path.sep)) throw new Error('Use um arquivo ou pasta dentro de /data/imports');
    if ((await (await import('node:fs/promises')).stat(resolved)).isDirectory()) return resolved;
    const destination = path.join(imports, 'extraido-' + createHash('sha256').update(resolved).digest('hex').slice(0, 16));
    try { await realpath(destination); } catch { await unpack((crxPayload(await readFile(resolved))).zip, destination); }
    return destination;
  }
  async scan(source) {
    const root = await this.importedPath(source), found = [];
    let scanned = 0;
    async function walk(directory, depth) {
      if (depth > 8) return;
      for (const entry of await readdir(directory, { withFileTypes: true })) {
        if (++scanned > 20000) throw new Error('A exportação contém arquivos demais');
        if (entry.isSymbolicLink()) continue;
        const file = path.join(directory, entry.name);
        if (entry.isDirectory()) await walk(file, depth + 1);
        else if (entry.name === 'manifest.json') {
          try { const manifest = JSON.parse(await readFile(file, 'utf8')); if (manifest.manifest_version) found.push({ name: manifest.name, version: manifest.version, source: directory }); } catch { /* Other browser metadata is not an extension. */ }
        }
      }
    }
    await walk(root, 0);
    return found;
  }
  async storage(index, id, storageArea, values) {
    const account = this.getAccount(index);
    if (!this.entries.some(e => e.id === id && e.accounts.includes(index))) throw new Error('Extensão não instalada');
    return values === undefined
      ? (await account.pageCdp.send('Extensions.getStorageItems', { id, storageArea })).data
      : account.pageCdp.send('Extensions.setStorageItems', { id, storageArea, values });
  }
}
