// O bônus das lives no jogo, para a dica do cabeçalho e a guarda do bônus (Lives → Config…): acompanha o socket do jogo
// e guarda os blocos `twitch` e `kick` do estado da conta (o `welcome` e os deltas de `estado`). O deck lê o resumo
// (`__deckBonus.summary()`).
(() => {
  if (window.top !== window || window.__deckBonus) return;

  const KEYS = ['twitch', 'kick'];
  let socket = null;
  let me = null; // os blocos de KEYS da conta, remontados dos deltas
  let skew = 0; // o relógio da página menos o do servidor (os prazos dos bônus são do servidor)

  const Native = window.WebSocket;
  if (typeof Native === 'function') {
    window.WebSocket = new Proxy(Native, {
      construct(target, args, newTarget) {
        const ws = Reflect.construct(target, args, newTarget);
        try {
          if (new URL(String(args[0]), location.href).host === location.host) attach(ws);
        } catch { }
        return ws;
      },
    });
  }

  function attach(ws) {
    if (!ws || ws === socket) return;
    socket = ws;
    ws.addEventListener('message', (e) => {
      if (ws === socket) receive(e.data);
    });
  }

  // O socket do jogo aberto antes deste script (a página carregou sem ele): o Rotom guarda o socket do jogo em
  // window.__activeGameWs. O estado chega nos próximos deltas.
  function late() {
    if (socket?.readyState === 1) return;
    const ws = window.__activeGameWs;
    if (ws && ws.readyState === 1 && new URL(ws.url, location.href).host === location.host) attach(ws);
  }

  function receive(data) {
    if (typeof data !== 'string' || !/^\{"t":"(welcome|estado)"/.test(data)) return;
    let m;
    try {
      m = JSON.parse(data);
    } catch {
      return;
    }
    merge(m.estado, m.t === 'welcome');
  }

  // Chave ausente é "não mudou"; `cheio` (welcome, reconexão) recomeça do zero.
  function merge(state, full) {
    if (!state || typeof state !== 'object') return;
    if (full || state.cheio || !me) me = {};
    for (const key of KEYS) if (key in state) me[key] = state[key];
    if (Number(state.servidorAgora) > 0) skew = Date.now() - Number(state.servidorAgora);
  }

  /**
   * O bônus das lives num site ('twitch', 'kick'): se a conta vinculou o site no jogo, quantos segundos faltam para cada
   * bônus vencer (xp, captura, shiny; 0 = sem bônus) e os canais oficiais com os bônus do PokeIdle (e se estão no ar).
   */
  function site(block) {
    if (!block || typeof block !== 'object') return null;
    const now = Date.now() - skew;
    const left = {};
    for (const [kind, until] of Object.entries(block.bonus ?? {})) left[kind] = Math.max(0, Math.round((Number(until) - now) / 1000)) || 0;
    return {
      linked: !!(block.vinculado ?? block.login),
      left,
      channels: (Array.isArray(block.oficiais) ? block.oficiais : []).filter((o) => o?.login && o.ativo !== false)
        .map((o) => ({ login: String(o.login), live: !!o.aoVivo })),
    };
  }

  window.__deckBonus = {
    summary() {
      late();
      return { connected: socket?.readyState === 1, twitch: site(me?.twitch), kick: site(me?.kick) };
    },
  };
})();
