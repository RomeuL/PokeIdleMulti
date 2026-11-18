import RFB from '/novnc/core/rfb.js';
let rfb, audioSocket, audioContext, audioNode;
const $ = selector => document.querySelector(selector);
function connect() {
  if (rfb) rfb.disconnect();
  $('#screen').replaceChildren();
  const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  rfb = new RFB($('#screen'), `${protocol}//${location.host}/socket`);
  rfb.scaleViewport = true; rfb.resizeSession = false; rfb.showDotCursor = true;
  rfb.addEventListener('connect', () => { $('#connection').hidden = true; });
  rfb.addEventListener('disconnect', () => { $('#connection').hidden = false; $('#connection').textContent = 'Conexão encerrada. Use ☰ → Reconectar.'; });
  rfb.addEventListener('clipboard', event => { $('#clipboard-text').value = event.detail.text; });
}
$('#fullscreen').onclick = () => document.fullscreenElement ? document.exitFullscreen() : document.documentElement.requestFullscreen();
document.addEventListener('keydown', event => {
  if (event.key !== 'F11') return;
  event.preventDefault(); event.stopImmediatePropagation();
  $('#fullscreen').click();
}, true);
$('#reconnect').onclick = connect;
$('#clipboard').onclick = () => $('#clipboard-dialog').showModal();
$('#clipboard-send').onclick = () => { rfb.clipboardPasteFrom($('#clipboard-text').value); $('#clipboard-dialog').close(); };
for (const button of document.querySelectorAll('[data-close]')) button.onclick = () => button.closest('dialog').close();
async function listFiles() {
  const files = await (await fetch('/api/files')).json();
  for (const folder of ['imports', 'exports']) {
    const container = $('#' + folder); container.replaceChildren();
    for (const name of files[folder]) {
      const row = document.createElement('div'); row.className = 'file';
      const text = document.createElement('code'); text.textContent = folder === 'imports' ? '/data/imports/' + name : name; row.append(text);
      if (folder === 'imports') { const copy = document.createElement('button'); copy.textContent = 'Copiar caminho'; copy.onclick = () => { rfb.clipboardPasteFrom('/data/imports/' + name); $('#upload-status').textContent = 'Caminho copiado para o deck. Cole com Ctrl+V.'; }; row.append(copy); }
      else { const link = document.createElement('a'); link.textContent = 'Baixar'; link.href = '/api/download?name=' + encodeURIComponent(name); link.download = name; row.append(link); }
      container.append(row);
    }
    if (!files[folder].length) container.textContent = 'Nenhum arquivo.';
  }
}
$('#files').onclick = async () => { $('#file-dialog').showModal(); await listFiles(); };
$('#upload').onchange = async event => {
  const file = event.target.files[0]; if (!file) return;
  $('#upload-status').textContent = 'Enviando…';
  try {
    const { csrf } = await (await fetch('/api/session')).json();
    const response = await fetch('/api/upload?name=' + encodeURIComponent(file.name), { method: 'POST', headers: { 'X-Deck-CSRF': csrf }, body: file });
    const result = await response.json(); if (!response.ok) throw new Error(result.error);
    $('#upload-status').textContent = 'Enviado: ' + result.path; await listFiles();
  } catch (error) { $('#upload-status').textContent = error.message; }
};
async function stopAudio() {
  const socket = audioSocket, context = audioContext;
  audioSocket = null; audioContext = null; audioNode?.disconnect(); audioNode = null;
  if (socket) { socket.onclose = null; socket.close(); }
  $('#audio').textContent = 'Ligar áudio';
  if (context && context.state !== 'closed') await context.close();
}
$('#audio').onclick = async () => {
  if (audioContext) return stopAudio();
  try {
    audioContext = new AudioContext({ sampleRate: 48000, latencyHint: 'interactive' });
    await audioContext.audioWorklet.addModule('/pcm-worklet.js');
    audioNode = new AudioWorkletNode(audioContext, 'deck-pcm', { outputChannelCount: [2] }); audioNode.connect(audioContext.destination);
    await audioContext.resume();
    audioSocket = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/audio`); audioSocket.binaryType = 'arraybuffer';
    audioSocket.onmessage = event => audioNode?.port.postMessage(event.data, [event.data]);
    audioSocket.onclose = () => { if (audioContext) stopAudio(); };
    $('#audio').textContent = 'Desligar áudio';
  } catch (error) { await stopAudio(); alert('O áudio precisa de HTTPS ou localhost: ' + error.message); }
};
connect();
