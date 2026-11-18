import KeyTable from '/novnc/core/input/keysym.js';

export function setupClipboard(getRfb) {
  const area = document.querySelector('#clipboard-text');
  const dialog = document.querySelector('#clipboard-dialog');
  const status = document.querySelector('#clipboard-status');
  const sink = document.createElement('textarea');
  sink.tabIndex = -1;
  sink.setAttribute('aria-label', 'Receber texto colado');
  sink.style.cssText = 'position:fixed;left:-10000px;top:0;width:1px;height:1px;';
  document.body.append(sink);
  let copyTimer, copyPending = false, pasteTimer;
  const remoteFocused = () => document.querySelector('#screen').contains(document.activeElement);
  function show(message) { status.textContent = message; if (!dialog.open) dialog.showModal(); area.focus(); area.select(); }
  function shortcut(letter) {
    const rfb = getRfb();
    rfb.sendKey(KeyTable.XK_Control_L, 'ControlLeft', true);
    rfb.sendKey(letter.charCodeAt(0), 'Key' + letter.toUpperCase());
    rfb.sendKey(KeyTable.XK_Control_L, 'ControlLeft', false);
  }
  async function request(action, text) {
    const session = await fetch('/api/session');
    if (!session.ok) throw new Error('Reconecte a WebUI para usar a área de transferência');
    const { csrf } = await session.json();
    const response = await fetch('/api/clipboard', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Deck-CSRF': csrf }, body: JSON.stringify({ action, text }) });
    const result = await response.json();
    if (!response.ok) throw new Error(result.error);
    return result.text;
  }
  async function copyLocal(text) {
    try { if (navigator.clipboard?.writeText) { await navigator.clipboard.writeText(text); return; } } catch { /* Try an explicit-user-action fallback for HTTP. */ }
    const previous = document.activeElement;
    sink.value = text; sink.focus(); sink.select();
    const copied = document.execCommand('copy');
    previous?.focus({ preventScroll: true });
    if (!copied) throw new Error('Selecione o texto abaixo e use Ctrl+C para copiar para o computador.');
  }
  async function finishCopy() {
    if (!copyPending) return;
    copyPending = false; clearTimeout(copyTimer);
    try { area.value = await request('read'); await copyLocal(area.value); status.textContent = 'Texto copiado para o computador.'; }
    catch (error) { show(error.message); }
  }
  async function pasteRemote(text) {
    copyPending = false; clearTimeout(copyTimer); clearTimeout(pasteTimer);
    area.value = text;
    try {
      await request('write', text);
      if (dialog.open) dialog.close();
      getRfb().focus(); shortcut('v');
      status.textContent = 'Texto colado no aplicativo.';
    } catch (error) { show(error.message); }
  }
  document.addEventListener('keydown', event => {
    if (!remoteFocused() || !event.ctrlKey || event.altKey || event.metaKey || event.shiftKey || event.repeat) return;
    const key = event.key.toLowerCase();
    if (key === 'c') {
      event.preventDefault(); event.stopImmediatePropagation();
      copyPending = true; clearTimeout(copyTimer);
      shortcut('c'); copyTimer = setTimeout(finishCopy, 250);
    } else if (key === 'v') {
      event.stopImmediatePropagation();
      if (navigator.clipboard?.readText) {
        event.preventDefault();
        navigator.clipboard.readText().then(pasteRemote).catch(() => show('O navegador bloqueou a leitura automática. Cole o texto abaixo com Ctrl+V e clique em Colar no app.'));
      } else {
        // A real paste event can provide text on HTTP without Clipboard API
        // access. Keep the default browser action, targeting an editable field.
        sink.value = ''; sink.focus();
        clearTimeout(pasteTimer);
        pasteTimer = setTimeout(() => show('Cole o texto abaixo com Ctrl+V e clique em Colar no app.'), 700);
      }
    }
  }, true);
  sink.addEventListener('paste', event => {
    event.preventDefault();
    clearTimeout(pasteTimer);
    if (!event.clipboardData?.types.includes('text/plain')) return show('A área de transferência aceita texto. Use Arquivos para enviar outros formatos.');
    pasteRemote(event.clipboardData.getData('text/plain'));
  });
  document.querySelector('#clipboard').onclick = () => show('Use Ctrl+C e Ctrl+V com o aplicativo em foco, ou os botões abaixo.');
  document.querySelector('#clipboard-send').onclick = () => pasteRemote(area.value);
  document.querySelector('#clipboard-copy').onclick = () => copyLocal(area.value).then(() => { status.textContent = 'Texto copiado para o computador.'; }).catch(error => show(error.message));
  return { received(text) { area.value = text; if (copyPending) finishCopy(); } };
}
