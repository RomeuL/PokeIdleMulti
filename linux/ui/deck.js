const token = location.hash.slice(1) || sessionStorage.getItem('deckToken') || '';
sessionStorage.setItem('deckToken', token);
history.replaceState(null, '', '/deck');
const $ = selector => document.querySelector(selector);
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const n = value => Number(value || 0).toLocaleString('pt-BR');
let state, index = 0, formIndex = 0, dialogKind = '', notifyTimer, pendingBulk;
const dialog = $('#dialog');
async function call(method, args = {}) {
  const response = await fetch('/api', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Deck-Token': token }, body: JSON.stringify({ method, args }) });
  const json = await response.json();
  if (json.error) throw new Error(json.error);
  return json.result;
}
function notice(text) { $('#notice').textContent = text; clearTimeout(notifyTimer); notifyTimer = setTimeout(() => $('#notice').textContent = '', 15000); }
function cloneConfig() { return structuredClone(state.config); }
async function save(config) { await call('config', { config }); }
function button(action, text, extra = '') { return `<button data-action="${action}" ${extra}>${text}</button>`; }
function select(name, choices, selected) { return `<select name="${name}">${choices.map(([value, label]) => `<option value="${esc(value)}" ${String(value) === String(selected ?? '') ? 'selected' : ''}>${esc(label)}</option>`).join('')}</select>`; }
function check(name, label, on) { return `<label><input type="checkbox" name="${name}" ${on ? 'checked' : ''}> ${label}</label>`; }
function number(name, value, max = 10000000, min = 0) { return `<input type="number" name="${name}" value="${value}" min="${min}" max="${max}" step="1">`; }
const accounts = () => state.config.AccountNames.map((name, i) => [i, name]);
const rewards = [['', 'Não resgatar'], ['Xp', '+15% XP · 300'], ['Capture', '+15% Captura · 500'], ['Lure', '+15% Shiny · 1000']];
function uptime(at) { if (!at) return ''; const mins = Math.floor((Date.now() - at) / 60000); return mins >= 60 ? `${Math.floor(mins / 60)} h ${String(mins % 60).padStart(2, '0')}` : `${mins} min`; }
function render() {
  const c = state.config;
  $('#grid').dataset.layout = c.Layout;
  $('#account-buttons').innerHTML = c.AccountNames.map((name, i) => button('account', esc(name), `data-index="${i}" class="${index === i ? 'active' : ''}" title="Clique para ampliar; botão direito para mais opções"`)).join('');
  $('[data-action=sniper]').hidden = !state.extensions.some(list => list.some(e => /rotom|sniper/i.test(e.name)));
  $('#echo').classList.toggle('active', state.echo);
  $('#ultra').classList.toggle('active', c.Ultra);
  $('#zoom').textContent = `Zoom ${Math.round(c.Zoom[index] * 100)}%`;
  for (let i = 0; i < 4; i++) {
    const pane = $(`#pane-${i}`), p = state.panes[i], off = c.Off[i];
    pane.classList.toggle('focused', c.Focused === i);
    const ext = (state.extensions[i] || []).map(e => button('extension-popup', '🧩', `data-index="${i}" data-id="${esc(e.id)}" title="${esc(e.name)}"`)).join('');
    const connected = p.restock?.connected || p.bonus?.connected;
    const signature = JSON.stringify([c.AccountNames[i], off, p.manualLogin, p.status, p.note, uptime(p.startedAt), c.Restock[i].On, c.Pvp[i], c.Muted[i], p.vote?.canVote, state.extensions[i]]);
    if (pane.dataset.signature !== signature) {
      pane.dataset.signature = signature;
    pane.querySelector('.head').innerHTML = `<span class="dot ${connected ? 'ok' : off ? '' : 'warn'}">●</span><span class="name">${esc(c.AccountNames[i])}</span><span class="info" title="${esc(p.note || p.status)}">${esc(p.note || (p.status === 'jogo' || p.status === 'app' ? '· rodando há ' + uptime(p.startedAt) : p.status))}</span><span class="controls">`
      + (p.manualLogin ? button('login-resume', 'Retomar recursos', `data-index="${i}" class="green" title="Após entrar no jogo, reabrir o mesmo perfil com os recursos do deck"`) : ext + button('live-account', 'lives', `data-index="${i}" title="Lives da Twitch/Kick nesta conta"`)
      + button('restock', '🛒', `data-index="${i}" class="${c.Restock[i].On ? 'green' : ''}" title="Recompra"`)
      + button('pvp', '⚔', `data-index="${i}" class="${c.Pvp[i] ? 'green' : ''}" title="Fila de PvP"`)
      + button('vote', '🗳', `data-index="${i}" class="${p.vote?.canVote ? 'green' : ''}" title="Votar no TopIdle"`)
      + button('mute', c.Muted[i] ? '🔇' : '🔊', `data-index="${i}" title="Ligar/desligar som"`)
      + button('reload', '⟳', `data-index="${i}" title="Recarregar"`))
      + button('account', '⛶', `data-index="${i}" title="Ampliar/voltar"`)
      + button('power', '⏻', `data-index="${i}" title="${off ? 'Ligar' : 'Desligar'} esta conta"`) + '</span>';
    }
    const content = pane.querySelector('.viewport');
    if (off && !content.querySelector('.off')) content.innerHTML = `<div class="off"><span>Conta desligada</span>${button('power', '⏻ Ligar esta conta', `data-index="${i}"`)}</div>`;
    if (!off) content.replaceChildren();
  }
  $('#memory').textContent = `RAM Chromium · ${(state.memory / 1024 ** 2).toFixed(0)} MB`;
  $('#lives-status').textContent = c.LivesAccounts.map(i => c.AccountNames[i - 1]).join(', ') + ' · lives';
  $('#vote-status').textContent = state.panes.map((p, i) => p.vote?.canVote && !c.Off[i] ? c.AccountNames[i] : null).filter(Boolean).join(', ') + ' · voto';
  if (dialog.open && dialogKind === 'restock') {
    const p = state.panes[formIndex].restock;
    $('#restock-state').textContent = p ? `Ouro: ${n(p.gold)} · Revives: ${n(p.revives?.reduce((a, b) => a + b.have, 0))} · Poções: ${n(p.potions?.reduce((a, b) => a + b.have, 0))}` : 'Esperando o jogo conectar…';
    $('#restock-log').textContent = p?.log?.join('\n') || '';
  }
  if (dialog.open && dialogKind === 'bulk' && $('#bulk-progress')) $('#bulk-progress').textContent = state.bulk.progress.join('\n');
  scheduleBounds();
}
for (let i = 0; i < 4; i++) { const pane = document.createElement('section'); pane.className = 'pane'; pane.id = `pane-${i}`; pane.innerHTML = '<header class="head"></header><div class="viewport"></div>'; $('#grid').append(pane); pane.querySelector('.head').ondblclick = () => focus(i).catch(error => notice(error.message)); }
let boundsTimer;
function scheduleBounds() { clearTimeout(boundsTimer); boundsTimer = setTimeout(() => {
  const rectangles = [...document.querySelectorAll('.viewport')].map(e => { const r = e.getBoundingClientRect(); return { x: r.x, y: r.y, width: Math.max(1, r.width), height: Math.max(1, r.height), visible: r.width > 0 && r.height > 0 }; });
  call('bounds', { rectangles }).catch(error => notice(error.message));
}, 50); }
new ResizeObserver(scheduleBounds).observe($('#grid'));
async function focus(i) {
  index = i; const c = cloneConfig();
  if (c.Layout === 'Focus' && c.Focused === i) { c.Layout = sessionStorage.getItem('layoutBeforeFocus') || 'Grid'; c.Zoom = c.Zoom.map(() => c.ZoomOutsideFocus ?? 1); c.ZoomOutsideFocus = null; }
  else { if (c.Layout !== 'Focus') { sessionStorage.setItem('layoutBeforeFocus', c.Layout); c.ZoomOutsideFocus = c.Zoom[i]; } c.Layout = 'Focus'; c.Focused = i; c.Zoom = c.Zoom.map(() => 1); }
  await save(c);
}
async function openDialog(title, body, footer = '', kind = '') {
  const frames = await call('overlay', { open: true });
  if (Array.isArray(frames)) frames.forEach((frame, i) => { if (frame) $(`#pane-${i} .viewport`).style.backgroundImage = `url(data:image/jpeg;base64,${frame})`; });
  $('#dialog-title').textContent = title; $('#dialog-body').innerHTML = body;
  $('#dialog-footer').innerHTML = footer || button('close', 'Fechar');
  dialogKind = kind; if (!dialog.open) dialog.showModal();
}
function closeDialog() { dialog.close(); }
dialog.addEventListener('close', () => { if (dialogKind === 'bulk' && state?.bulk.running) call('bulk.cancel').catch(() => {}); dialogKind = ''; call('overlay', { open: false }).then(() => { for (const e of document.querySelectorAll('.viewport')) e.style.backgroundImage = ''; }).catch(() => {}); });
$('#close-dialog').onclick = closeDialog;
function fields() { const body = $('#dialog-body'); return { value: name => body.querySelector(`[name="${name}"]`)?.value, checked: name => !!body.querySelector(`[name="${name}"]`)?.checked, number: name => Math.max(0, Number(body.querySelector(`[name="${name}"]`)?.value) || 0) }; }
async function menu(title, items) { await openDialog(title, `<div class="menu">${items.join('')}</div>`); }
async function restock(i) {
  formIndex = i; const c = state.config.Restock[i], p = state.panes[i].restock;
  const balls = (p?.balls || state.balls).map(b => [b.id, b.name]);
  const potions = [[0, 'Melhor custo-benefício'], ...(p?.potions || state.potions).map(p => [p.id, p.name])];
  const rows = [['Balls', 'Pokebolas', balls], ['Revives', 'Revives · o mais barato', null], ['Potions', 'Poções', potions]].map(([key, label, options]) => `<tr><td>${check(key + 'On', label, c[key].On)}</td><td>${options ? select(key + 'Id', options, c[key].Id) : 'Mais barato'}</td><td>${number(key + 'Below', c[key].Below)}</td><td>${number(key + 'Buy', c[key].Buy)}</td></tr>`).join('');
  await openDialog(`Recompra · ${state.config.AccountNames[i]}`, `<div class="stack"><div class="row">${check('on', 'Recompra ligada', c.On)}</div><div class="card"><table><thead><tr><th>Comprar</th><th>Item</th><th>Recomprar abaixo de</th><th>Comprar (lote)</th></tr></thead><tbody>${rows}</tbody></table></div><div class="row"><label>Ouro mínimo ${number('gold', c.GoldFloor, Number.MAX_SAFE_INTEGER)}</label>${check('first', 'Pôr a poção escolhida em 1º na fila', c.PotionFirst)}</div><p id="restock-state" class="muted">Esperando o jogo…</p><details><summary>▸ Registro</summary><pre id="restock-log"></pre></details></div>`, '<span class="hint">As quantidades seguem as regras do jogo.</span>' + button('close', 'Cancelar') + button('save-restock', 'Salvar', 'class="primary"'), 'restock'); render();
}
async function channels() {
  const c = state.config;
  await openDialog('Config das lives', `<div class="stack"><p class="muted">Um canal por linha, na ordem de prioridade. O deck abre as primeiras que estiverem no ar.</p><div class="cards">${['Twitch', 'Kick'].map(site => `<div class="card"><h2>${site}</h2><textarea name="${site}Channels">${esc(c[site + 'Channels'].join('\n'))}</textarea><div class="row"><label>Lives abertas por conta ${number(site + 'Lives', c[site + 'Lives'], 10)}</label></div>${site === 'Twitch' ? check('claim', 'Pegar o baú de bônus sozinho', c.ClaimTwitchBonus) : ''}</div>`).join('')}</div><div class="card"><h2>Por conta</h2><table><thead><tr><th>Conta</th><th>Resgatar na Kick</th><th>Resgatar na Twitch</th><th>Guarda</th></tr></thead><tbody>${c.AccountNames.map((name, i) => `<tr><td>${esc(name)}</td><td>${select('kick' + i, rewards, c.RedeemKick[i])}</td><td>${select('twitch' + i, rewards, c.RedeemTwitch[i])}</td><td>${check('guard' + i, '', c.BonusGuard[i])}</td></tr>`).join('')}</tbody></table></div></div>`, '<span class="hint">Vincule a Kick e a Twitch no jogo em cada conta que resgata.</span>' + button('close', 'Cancelar') + button('save-channels', 'Salvar', 'class="primary"'));
}
async function liveAccount(i) {
  formIndex = i;
  const c = state.config, points = state.panes[i].points || {};
  await openDialog(`Lives · ${c.AccountNames[i]}`, `<div class="stack"><div class="row">${check('enabled', 'Lives nesta conta', c.LivesAccounts.includes(i + 1))}</div>${['Twitch', 'Kick'].map(site => `<div class="card"><div class="row"><h2>${site}</h2>${button('live-login', 'Entrar / mostrar login', `data-index="${i}" data-site="${site}"`)}</div><table><thead><tr><th>Canal</th><th>Estado</th><th>Pontos</th><th>Ações</th></tr></thead><tbody>${c[site + 'Channels'].map(channel => `<tr><td>${esc(channel)}</td><td>${state.liveStatus[site]?.[channel] === false ? 'offline' : state.liveStatus[site]?.[channel] ? 'no ar' : 'não conferido'}</td><td>${esc(points[site + ':' + channel]?.text || '—')}</td><td>${button('live-player', '▶ Live', `data-index="${i}" data-site="${site}" data-channel="${esc(channel)}"`)}${button('live-points', '💰 Pontos', `data-index="${i}" data-site="${site}" data-channel="${esc(channel)}"`)}${button('live-limit', c[site][i].Closed.includes(channel) ? 'Reabrir' : 'Fechar', `data-index="${i}" data-site="${site}" data-channel="${esc(channel)}"`)}</td></tr>`).join('')}</tbody></table></div>`).join('')}</div>`, button('live-next', 'Próxima conta ▶') + button('close', 'Cancelar') + button('save-live-account', 'Salvar', 'class="primary"'));
}
async function extensions(i = index) {
  formIndex = i;
  await openDialog('Extensões', `<div class="stack"><div class="row"><label>Conta ${select('account', accounts(), i)}</label>${button('extensions-change', 'Mostrar')}${check('all', 'Instalar em todas as contas ligadas', false)}</div><div class="card"><h2>Instalar uma extensão</h2><div class="row"><input class="wide" name="source" type="text" placeholder="Link da Chrome Web Store ou /data/imports/extensao.zip"></div><div class="row">${button('import-browser', 'Importar do meu Chrome…')}${button('install-store', 'Da Chrome Web Store…')}${button('install-file', 'Arquivo / pasta descompactada…')}</div><p class="muted small">Use o menu Arquivos da WebUI para enviar ZIP/CRX ou uma exportação do Chrome. Os arquivos ficam em /data/imports.</p></div><div class="card"><table><thead><tr><th>Extensão</th><th>Versão</th><th>Ações</th></tr></thead><tbody>${state.extensions[i].map(e => `<tr><td>${esc(e.name)}</td><td>${esc(e.version)}</td><td>${button('extension-popup', 'Abrir', `data-index="${i}" data-id="${esc(e.id)}"`)}${button('extension-copy', 'Trazer configurações…', `data-index="${i}" data-id="${esc(e.id)}"`)}${button('extension-remove', 'Remover', `class="danger" data-index="${i}" data-id="${esc(e.id)}"`)}</td></tr>`).join('') || '<tr><td colspan="3">Nenhuma extensão instalada nesta conta.</td></tr>'}</tbody></table></div></div>`);
}
async function log() {
  const lines = await call('log.read', { all: true });
  await openDialog('Registro dos resgates', `<div class="stack"><div class="row"><label>Conta ${select('log-account', [['all', 'Todas'], ...accounts()], 'all')}</label><label>Site ${select('log-site', [['', 'Todos'], ['Kick', 'Kick'], ['Twitch', 'Twitch']], '')}</label><input name="search" type="text" placeholder="Pesquisar no registro">${button('filter-log', 'Filtrar')}</div><pre id="redeem-log"></pre></div>`, button('export-log', 'Abrir / exportar arquivo') + button('clear-log', 'Limpar registro…', 'class="danger"') + button('close', 'Fechar'));
  $('#redeem-log').textContent = [...lines].reverse().join('\n') || 'Nenhum resgate registrado.';
}
async function bulk() {
  await openDialog('Resgate em lote', `<div class="stack"><p class="warning">O resgate gasta os pontos da conta selecionada. Confira o saldo e os bônus antes de iniciar.</p><div class="row"><label>Conta ${select('account', accounts(), index)}</label><label>Site ${select('site', [['Twitch', 'Twitch'], ['Kick', 'Kick']], 'Twitch')}</label><label>Canal <input name="channel" type="text"></label>${button('bulk-read', 'Conferir saldo')}</div><div class="card"><table><thead><tr><th>Bônus</th><th>Quantidade</th></tr></thead><tbody>${['+15% XP', '+15% Captura', '+15% Shiny (Secret Lure)'].map((s, i) => `<tr><td>${s}</td><td>${number('count' + i, 0, 999)}</td></tr>`).join('')}</tbody></table><p id="bulk-balance" class="muted">Confira o saldo.</p></div><details open><summary>▸ Registro</summary><pre id="bulk-progress"></pre></details></div>`, button('bulk-cancel', 'Parar') + button('close', 'Fechar') + button('bulk-run', 'Resgatar…', 'class="danger"'), 'bulk');
}
async function confirm(title, message, action) {
  await openDialog(title, `<p>${esc(message)}</p>`, button('close', 'Cancelar') + button('confirmed', 'Confirmar', 'class="danger"'));
  $('#dialog-footer [data-action=confirmed]').onclick = async event => { event.stopPropagation(); try { await action(); closeDialog(); } catch (error) { notice(error.message); } };
}
async function act(action, element) {
  const i = Number(element.dataset.index ?? index), value = element.dataset.value;
  let c;
  switch (action) {
    case 'close': closeDialog(); break;
    case 'layout': c = cloneConfig(); c.Layout = value; if (c.ZoomOutsideFocus != null) { c.Zoom = c.Zoom.map(() => c.ZoomOutsideFocus); c.ZoomOutsideFocus = null; } await save(c); break;
    case 'focus': await focus(index); break;
    case 'account': await focus(i); break;
    case 'echo': await call('echo', { on: !state.echo }); break;
    case 'zoom': c = cloneConfig(); c.Zoom = c.Zoom.map(z => Math.max(.5, Math.min(1.5, Math.round((z + (value === '+' ? .1 : -.1)) * 100) / 100))); await save(c); break;
    case 'zoom-menu': await menu('Zoom', [50, 67, 75, 80, 90, 100, 110, 125, 150].map(z => button('zoom-to', z + '%', `data-value="${z}"`))); break;
    case 'zoom-to': c = cloneConfig(); c.Zoom = c.Zoom.map(() => Number(value) / 100); await save(c); closeDialog(); break;
    case 'ultra': c = cloneConfig(); c.Ultra = !c.Ultra; await save(c); break;
    case 'power': c = cloneConfig(); c.Off[i] = !c.Off[i]; await save(c); break;
    case 'mute': c = cloneConfig(); c.Muted[i] = !c.Muted[i]; await save(c); break;
    case 'reload': await call('reload', { index: i }); break;
    case 'login-manual': closeDialog(); await call('login.manual', { index: i }); notice('Entre no jogo nesta conta e depois clique em Retomar recursos.'); break;
    case 'login-resume': closeDialog(); await call('login.resume', { index: i }); notice('Reabrindo a conta com os recursos do deck…'); break;
    case 'vote': await call('vote', { index: i }); break;
    case 'fullscreen': document.fullscreenElement ? await document.exitFullscreen() : await document.documentElement.requestFullscreen(); break;
    case 'performance': c = state.config; await openDialog('ULTRA e desempenho', `<div class="stack">${check('reduce', '🐢 Menos animações', c.ReduceMotion)}<label>Quadros por segundo do mapa ${select('fps', [5, 10, 15, 20, 30, 60].map(x => [x, x === 60 ? '60 · sem limite' : x]), c.GameFps)}</label><label>Ritmo dos sprites (ms) ${number('sprites', c.SpriteMs, 10000, 220)}</label></div>`, button('close', 'Cancelar') + button('save-performance', 'Salvar', 'class="primary"')); break;
    case 'save-performance': { const f = fields(); c = cloneConfig(); c.ReduceMotion = f.checked('reduce'); c.GameFps = f.number('fps'); c.SpriteMs = f.number('sprites'); await save(c); closeDialog(); break; }
    case 'restock': await restock(i); break;
    case 'save-restock': { const f = fields(); c = cloneConfig(); const r = c.Restock[formIndex]; r.On = f.checked('on'); r.GoldFloor = f.number('gold'); r.PotionFirst = f.checked('first'); for (const key of ['Balls', 'Revives', 'Potions']) { r[key].On = f.checked(key + 'On'); r[key].Below = f.number(key + 'Below'); r[key].Buy = f.number(key + 'Buy'); if (key !== 'Revives') r[key].Id = f.number(key + 'Id'); } await save(c); closeDialog(); break; }
    case 'pvp': c = cloneConfig(); c.Pvp[i] = !c.Pvp[i]; await save(c); break;
    case 'pvp-settings': formIndex = i; await openDialog(`Fila de PvP · ${state.config.AccountNames[i]}`, `<div class="stack">${check('pvp-on', 'Fila ligada', state.config.Pvp[i])}<label>Tela da batalha ${select('screen', [['completa', 'Completa'], ['mini', 'Mini'], ['off', 'Off · só o resultado']], state.config.PvpScreen[i])}</label><label>Velocidade ${select('speed', [0, 1, 2, 4, 8, 16, 32].map(v => [v, v ? v + '×' : 'A do jogo']), state.config.PvpSpeed[i])}</label><pre>${esc(JSON.stringify(state.panes[i].pvp, null, 2) || 'Esperando o jogo…')}</pre></div>`, button('close', 'Cancelar') + button('save-pvp', 'Salvar', 'class="primary"')); break;
    case 'save-pvp': { const f = fields(); c = cloneConfig(); c.Pvp[formIndex] = f.checked('pvp-on'); c.PvpScreen[formIndex] = f.value('screen'); c.PvpSpeed[formIndex] = f.number('speed'); await save(c); closeDialog(); break; }
    case 'lives': await menu('Lives', [button('live-account', 'Mostrar as lives desta conta', `data-index="${index}"`), button('channels', 'Config…'), button('log', 'Registro dos resgates…'), button('bulk', 'Resgate em lote…')]); break;
    case 'channels': await channels(); break;
    case 'save-channels': { const f = fields(); c = cloneConfig(); for (const site of ['Twitch', 'Kick']) { c[site + 'Channels'] = [...new Set(f.value(site + 'Channels').split(/[\s,;]+/).filter(Boolean).map(s => s.toLowerCase()))]; c[site + 'Lives'] = Math.min(10, f.number(site + 'Lives')); } for (let j = 0; j < 4; j++) { c.RedeemKick[j] = f.value('kick' + j) || null; c.RedeemTwitch[j] = f.value('twitch' + j) || null; c.BonusGuard[j] = f.checked('guard' + j); } c.ClaimTwitchBonus = f.checked('claim'); await save(c); await call('lives.restart'); closeDialog(); break; }
    case 'live-account': await liveAccount(i); break;
    case 'live-next': await liveAccount((formIndex + 1) % 4); break;
    case 'save-live-account': c = cloneConfig(); c.LivesAccounts = c.LivesAccounts.filter(n => n !== formIndex + 1); if (fields().checked('enabled')) c.LivesAccounts.push(formIndex + 1); await save(c); closeDialog(); break;
    case 'live-player': await call('live.player', { index: i, site: element.dataset.site, channel: element.dataset.channel }); break;
    case 'live-login': await call('live.login', { index: i, site: element.dataset.site }); break;
    case 'live-points': await call('live.points', { index: i, site: element.dataset.site, channel: element.dataset.channel, full: false }); break;
    case 'live-limit': c = cloneConfig(); { const site = element.dataset.site, limit = c[site][i], channel = element.dataset.channel, reopening = limit.Closed.includes(channel); limit.Closed = reopening ? limit.Closed.filter(v => v !== channel) : [...limit.Closed, channel]; const slots = Math.max(0, Math.min(c[site + 'Lives'], (limit.Slots ?? c[site + 'Lives']) + (reopening ? 1 : -1))); limit.Slots = slots === c[site + 'Lives'] ? null : slots; } await save(c); await call('lives.refresh'); await liveAccount(i); break;
    case 'extensions': await extensions(); break;
    case 'extensions-change': await extensions(Number(fields().value('account'))); break;
    case 'install-store': case 'install-file': { const f = fields(); await call('extensions.install', { index: Number(f.value('account')), all: f.checked('all'), source: f.value('source'), store: action === 'install-store' }); notice('Extensão instalada. Recarregue a conta para ativar o script.'); state = await call('state'); await extensions(Number(f.value('account'))); break; }
    case 'extension-popup': await call('extensions.trigger', { index: i, id: element.dataset.id }); break;
    case 'extension-remove': await confirm('Remover extensão', 'Remover esta extensão da conta?', () => call('extensions.remove', { index: i, id: element.dataset.id })); break;
    case 'extension-copy': await openDialog('Trazer configurações', `<div class="stack"><div class="row"><label>Da conta ${select('from', accounts().filter(([j]) => j !== i), (i + 1) % 4)}</label>${button('copy-extension-now', 'Copiar desta conta', `data-index="${i}" data-id="${esc(element.dataset.id)}"`)}</div><div class="card"><h2>Do Chrome, IdleDeck ou deck do Windows</h2><input class="wide" name="storage-source" type="text" placeholder="/data/imports/configuracoes.zip"><p class="muted small">Envie o ZIP com Local Extension Settings/${esc(element.dataset.id)} pelo menu Arquivos. A conta será reiniciada e as configurações atuais serão guardadas em backup.</p>${button('import-extension-storage', 'Trazer do arquivo…', `data-index="${i}" data-id="${esc(element.dataset.id)}"`)}</div></div>`, button('close', 'Fechar')); break;
    case 'import-extension-storage': await call('extensions.import-storage', { index: i, id: element.dataset.id, source: fields().value('storage-source') }); closeDialog(); notice('Configurações importadas; a conta foi reiniciada.'); break;
    case 'import-browser': formIndex = Number(fields().value('account')); await openDialog('Importar do meu Chrome', `<div class="stack"><p class="muted">Envie pelo menu Arquivos um ZIP da pasta Extensions do seu perfil do Chrome. Feche o Chrome antes de exportar.</p><input class="wide" name="browser-source" type="text" placeholder="/data/imports/chrome.zip">${button('scan-browser', 'Localizar extensões')}<div id="browser-results"></div></div>`); break;
    case 'scan-browser': { const found = await call('extensions.scan', { source: fields().value('browser-source') }); $('#browser-results').innerHTML = found.map(e => `<div class="row"><span class="spacer">${esc(e.name)} · ${esc(e.version)}</span>${button('import-browser-extension', 'Importar nesta conta', `data-index="${formIndex}" data-source="${esc(e.source)}"`)}</div>`).join('') || '<p>Nenhuma extensão encontrada.</p>'; break; }
    case 'import-browser-extension': await call('extensions.install', { index: i, source: element.dataset.source }); notice('Extensão importada. Recarregue a conta.'); state = await call('state'); await extensions(i); break;
    case 'copy-extension-now': await call('extensions.copy', { index: i, from: Number(fields().value('from')), id: element.dataset.id }); closeDialog(); notice('Configurações copiadas.'); break;
    case 'sniper': await menu('Rotom Sniper', [button('sniper-on', 'Ligar em todas as contas'), button('sniper-off', 'Desligar em todas as contas'), ...state.config.AccountNames.flatMap((name, j) => [button('sniper-one-on', 'Ligar · ' + esc(name), `data-index="${j}"`), button('sniper-one-off', 'Desligar · ' + esc(name), `data-index="${j}"`)])]); break;
    case 'sniper-one-on': case 'sniper-one-off': await call('sniper', { index: i, on: action === 'sniper-one-on' }); closeDialog(); break;
    case 'sniper-on': case 'sniper-off': await call('sniper', { all: true, on: action === 'sniper-on' }); closeDialog(); break;
    case 'log': await log(); break;
    case 'filter-log': { const f = fields(), selected = f.value('log-account'); const lines = await call('log.read', { all: selected === 'all', index: selected === 'all' ? 0 : Number(selected) }); $('#redeem-log').textContent = lines.filter(line => (!f.value('log-site') || line.includes(' · ' + f.value('log-site') + ' · ')) && line.toLowerCase().includes(f.value('search').toLowerCase())).reverse().join('\n'); break; }
    case 'clear-log': { const selected = fields().value('log-account'); await confirm('Limpar registro', `Limpar o registro de ${selected === 'all' ? 'todas as contas' : state.config.AccountNames[Number(selected)]}? Uma cópia será guardada no volume.`, () => call('log.clear', { all: selected === 'all', index: selected === 'all' ? 0 : Number(selected) })); break; }
    case 'export-log': { const f = fields(), selected = f.value('log-account'); const file = await call('log.export', { all: selected === 'all', index: selected === 'all' ? 0 : Number(selected), site: f.value('log-site'), search: f.value('search') }); notice('Registro exportado: ' + file + '. Baixe em Arquivos na WebUI.'); break; }
    case 'bulk': await bulk(); break;
    case 'bulk-read': { const f = fields(), result = await call('bulk.read', { index: Number(f.value('account')), site: f.value('site'), channel: f.value('channel') }); $('#bulk-balance').textContent = `${result.owner ? 'Dono do canal · pontos infinitos' : 'Saldo: ' + n(result.points)} · ${result.rewards.map(r => r ? `${r.title}: ${n(r.cost)}` : 'Bônus indisponível').join(' · ')}`; break; }
    case 'bulk-run': { const f = fields(), args = { index: Number(f.value('account')), site: f.value('site'), channel: f.value('channel'), counts: [0, 1, 2].map(j => Math.min(999, f.number('count' + j))) }; if (!args.counts.some(Boolean)) throw new Error('Escolha a quantidade dos bônus.'); const balance = await call('bulk.read', args); const cost = args.counts.reduce((sum, count, k) => sum + count * (balance.rewards[k]?.cost || 0), 0); if (!balance.owner && cost > balance.points) throw new Error('Saldo insuficiente.'); pendingBulk = args; await openDialog('Confirmar resgate em lote', `<p>Resgatar ${args.counts.join(' / ')} bônus (XP / Captura / Shiny) em ${esc(args.channel)}, com ${esc(state.config.AccountNames[args.index])}?</p><p class="warning">Total: ${n(cost)} pontos. Saldo: ${balance.owner ? 'infinito (dono do canal)' : n(balance.points)}.</p>`, button('close', 'Cancelar') + button('bulk-start-confirm', 'Resgatar', 'class="danger"')); break; }
    case 'bulk-start-confirm': await call('bulk.run', pendingBulk); await bulk(); break;
    case 'bulk-cancel': await call('bulk.cancel'); break;
    case 'rename-save': c = cloneConfig(); c.AccountNames[formIndex] = fields().value('name').trim().slice(0, 64) || `Conta ${formIndex + 1}`; await save(c); closeDialog(); break;
    case 'clean': await call('reload', { index: i, clean: true }); closeDialog(); break;
    case 'wipe': await confirm('Apagar dados da conta', 'Apagar os dados do jogo desta conta? Será necessário entrar no jogo de novo.', () => call('wipe', { index: i })); break;
    case 'diagnostic': { const file = await call('diagnostic'); notice(`Diagnóstico gerado: ${file}. Baixe em Arquivos na WebUI.`); closeDialog(); break; }
  }
}
document.addEventListener('click', event => { const target = event.target.closest('[data-action]'); if (!target || target.dataset.action === 'confirmed') return; act(target.dataset.action, target).catch(error => notice(error.message)); });
$('#account-buttons').addEventListener('contextmenu', async event => {
  const target = event.target.closest('[data-index]'); if (!target) return;
  event.preventDefault(); const i = Number(target.dataset.index); formIndex = i;
  await menu(state.config.AccountNames[i], [button('account', 'Ampliar no deck / voltar', `data-index="${i}"`), button(state.panes[i].manualLogin ? 'login-resume' : 'login-manual', state.panes[i].manualLogin ? 'Retomar recursos do deck' : 'Entrar pelo navegador comum…', `data-index="${i}"`), button('clean', 'Recarregar limpo (sem o cache do jogo)', `data-index="${i}"`), button('rename', 'Renomear…', `data-index="${i}"`), button('wipe', 'Apagar os dados da conta (entrar de novo)…', `class="danger" data-index="${i}"`), button('live-account', 'Lives da Twitch/Kick nesta conta', `data-index="${i}"`), button('restock', 'Recompra…', `data-index="${i}"`), button('pvp-settings', '⚔ Fila de PvP…', `data-index="${i}"`), button('diagnostic', '🩺 Gerar diagnóstico…')]);
});
document.addEventListener('click', event => { const target = event.target.closest('[data-action=rename]'); if (target) { formIndex = Number(target.dataset.index); openDialog('Renomear conta', `<input class="wide" type="text" name="name" maxlength="64" value="${esc(state.config.AccountNames[formIndex])}">`, button('close', 'Cancelar') + button('rename-save', 'Salvar', 'class="primary"')).catch(error => notice(error.message)); } });
document.addEventListener('keydown', event => { if (event.key === 'F11') { event.preventDefault(); act('fullscreen', {}).catch(() => {}); } });
function connect() {
  const ws = new WebSocket('ws://127.0.0.1:8081/events', ['deck', token]);
  ws.onmessage = event => { const message = JSON.parse(event.data); if (message.type === 'state') { state = message.value; render(); } else if (message.type === 'fullscreen') act('fullscreen', {}).catch(() => {}); };
  ws.onclose = () => { notice('Reconectando ao deck…'); setTimeout(connect, 2000); };
}
connect();

document.querySelector('#grid').addEventListener('contextmenu', event => {
  const target = event.target.closest('[data-action=pvp]');
  if (!target) return;
  event.preventDefault();
  act('pvp-settings', target).catch(error => notice(error.message));
});
