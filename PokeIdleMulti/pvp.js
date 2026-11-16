// A fila de PvP do deck (⚔ no cabeçalho e no menu da conta). Ligada, mantém a conta na fila do PvP ranqueado: entra,
// e depois de cada partida (e da espera que o jogo pede entre elas) entra de novo. É o mesmo pedido do botão da fila no
// painel de PvP do jogo (`pvp.fila.entrar`; desligar manda `pvp.fila.sair`).
//
// Conta VIP: o ⚔ é só o liga/desliga da auto-fila do próprio jogo (Automação → `pvpAutoFila`, o mesmo `auto.set` do
// jogo, com a automação inteira), que segue mesmo com a página fechada; ao ligar, entra na fila uma vez. Sem VIP, quem
// volta para a fila depois de cada partida é o deck.
//
// Como a recompra, acompanha o socket do jogo (`welcome`, os deltas de `estado` e as respostas `pvp`). O deck liga e
// desliga (`__deckPvp.set`), manda como as partidas aparecem e a velocidade delas (`__deckPvp.config`, botão direito no
// ⚔) e lê o resumo (`__deckPvp.status`).
(() => {
  if (window.top !== window || window.__deckPvp) return;

  const TEST = location.host === 'deck.test'; // o jogo-teste do autoteste não espera
  const TRY = TEST ? 300 : 15000; // entre duas tentativas de entrar na fila
  const AFTER = TEST ? 300 : 5000; // depois de uma partida, antes de entrar de novo (o jogo manda a espera dele)
  const AUTO_GAP = TEST ? 300 : 60000; // entre dois pedidos para ligar a auto-fila do jogo
  const KEYS = ['nick', 'level', 'automation', 'loja'];
  const WANTED = new Set(['welcome', 'estado', 'pvp', 'erro']);

  let socket = null;
  let me = null; // as chaves de KEYS da conta, remontadas dos deltas
  let on = false;
  let asked = false; // o estado da fila foi pedido ao jogo nesta ligada
  let fila = { na: false, desde: 0, tamanho: 0 };
  let waitUntil = 0; // a espera que o jogo pediu (entre partidas, ou a recusa)
  let lastTry = 0;
  let lastAuto = 0;
  let sentAt = 0; // o último pedido do deck (um erro logo depois é dele)
  let enteredOnce = false; // VIP: já entrou na fila nesta ligada (depois, quem põe na fila é a auto-fila do jogo)
  let wins = 0;
  let losses = 0;
  let points = null;
  let lastMatch = null;
  let seen = 0; // mensagens do jogo vistas por este script (para a dica dizer por que não conectou)
  let welcomed = false;
  let cfg = { tela: 'off', vel: 0 }; // a Tela da batalha que a fila ligada mantém e a velocidade das partidas (0 = a do jogo)
  let matchAt = 0; // quando chegou a última partida (o replay que o jogo abre logo depois é dela)
  let speedReady = false; // o replay do jogo já passa pelo deck
  let speedBusy = false;
  let speedError = null;
  let lastSpeed = 0; // a última velocidade posta numa partida
  const log = [];

  const note = (text) => {
    log.push(`${new Date().toLocaleTimeString('pt-BR')} ${text}`);
    if (log.length > 30) log.shift();
  };

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
    asked = false;
    fila = { na: false, desde: 0, tamanho: 0 };
    ws.addEventListener('message', (e) => {
      if (ws === socket) receive(e.data);
    });
  }

  // O socket do jogo aberto antes deste script (a página carregou sem ele, e o deck injetou depois): o Rotom guarda o
  // socket do jogo em window.__activeGameWs. A conta é a mesma; o estado chega nos próximos deltas.
  function late() {
    if (socket?.readyState === 1) return;
    const ws = window.__activeGameWs;
    if (ws && ws.readyState === 1 && new URL(ws.url, location.href).host === location.host) attach(ws);
  }

  function receive(data) {
    if (typeof data !== 'string') return;
    seen++;
    const head = /^\{"t":"([^"]*)"/.exec(data);
    if (head && !WANTED.has(head[1])) return;
    let m;
    try {
      m = JSON.parse(data);
    } catch {
      return;
    }
    if (m.t === 'welcome') {
      welcomed = true;
      merge(m.estado, true);
    }
    else if (m.t === 'estado') merge(m.estado, false);
    else if (m.t === 'pvp') pvp(m);
    else if (m.t === 'erro' && on && Date.now() - sentAt < 3000) note(`o jogo respondeu: ${m.msg ?? m.chave ?? 'erro'}`);
  }

  function merge(state, full) {
    if (!state || typeof state !== 'object') return;
    if (full || state.cheio || !me) me = {};
    for (const key of KEYS) if (key in state) me[key] = state[key];
  }

  function pvp(m) {
    const now = Date.now();
    if (m.fila && typeof m.fila === 'object') {
      const was = fila.na;
      fila = { ...fila, ...m.fila };
      if (m.fila.cooldownMs !== undefined) waitUntil = now + Math.max(0, Number(m.fila.cooldownMs) || 0);
      if (on && fila.na && !was) note(`na fila (${fila.tamanho || 1} na fila agora)`);
    }
    if (m.filaRecusa) {
      fila = { ...fila, na: false, desde: 0 };
      const rest = Number(m.filaRecusa.restaMs) || 0;
      waitUntil = Math.max(waitUntil, now + (rest > 0 ? rest : TRY));
      if (on) note(`o jogo não deixou entrar na fila: ${m.filaRecusa.msg ?? 'recusou'}`);
    }
    if (m.filaAuto && on) note('o jogo pôs a conta na fila (auto-fila VIP)');
    if (m.filaAutoDesligada) {
      if (me?.automation) me.automation.pvpAutoFila = false;
      if (on) note('o jogo desligou a auto-fila VIP');
    }
    if (m.partida && typeof m.partida === 'object') {
      const p = m.partida;
      matchAt = now;
      fila = { ...fila, na: false, desde: 0 };
      if (p.venci) wins++;
      else losses++;
      if (p.pontos != null) points = Number(p.pontos);
      const rival = String((typeof p.oponente === 'object' ? p.oponente?.nick ?? p.oponente?.nome : p.oponente) ?? '?');
      const delta = Number(p.delta) || 0;
      lastMatch = { at: now, won: !!p.venci, rival, delta };
      waitUntil = Math.max(waitUntil, now + AFTER);
      if (on) note(`partida contra ${rival}: ${p.venci ? 'venceu' : 'perdeu'} (${delta >= 0 ? '+' : ''}${delta})`);
    }
  }

  const vip = () => !!me?.loja?.vip;

  const send = (obj) => {
    try {
      socket.send(JSON.stringify(obj));
      sentAt = Date.now();
      return true;
    } catch {
      return false;
    }
  };

  // A "Tela da batalha" do PvP do jogo (Completa, Mini ou Off), que ele guarda no localStorage e lê a cada partida: com a
  // fila ligada, fica sempre na escolhida no deck (de fábrica Off: a partida não abre na tela; o jogo só avisa o resultado).
  const SCREEN_KEY = 'pvpTelaBatalha';
  const SCREENS = { completa: 'as partidas abrem na tela (Tela da batalha: Completa)', mini: 'as partidas abrem minimizadas (Tela da batalha: Mini)', off: 'as partidas não abrem na tela (Tela da batalha: Off)' };
  function screen() {
    try {
      return localStorage.getItem(SCREEN_KEY);
    } catch {
      return null;
    }
  }
  // Com o Modo Economia ligado (ou a tela ULTRA à vista) o jogo não tem cena: a Tela da batalha vale Off, seja qual for a
  // salva (o `semCena()` do jogo), e o painel do PvP mostra Off.
  function noScene() {
    try {
      if (localStorage.getItem('cfg-economia') === '1') return 'economia';
    } catch { }
    return document.documentElement.classList.contains('modo-ultra') ? 'ultra' : null;
  }
  function keepScreen() {
    if (screen() === cfg.tela) return;
    try {
      localStorage.setItem(SCREEN_KEY, cfg.tela);
      note(SCREENS[cfg.tela]);
    } catch { }
  }

  // O Battery Saver (Modo Economia) esconde as partidas. Com a fila ligada e as partidas mostradas ou minimizadas, o deck
  // desliga ele pelo botão do próprio jogo (#eco-sair, o "Sair" do painel da Economia) e anota que foi ele
  // (ECO_KEY, que sobrevive ao recarregar); ao voltar para Escondida ou desligar a fila, religa pelo atalho do jogo
  // (#hunt-eco) — só se foi o deck que desligou. Vale na mudança (ligar a fila, mudar a tela), não a cada segundo: quem
  // religar à mão fica com ele ligado até a próxima mudança.
  const ECO_KEY = 'deckPvpSemEconomia';
  const economy = () => noScene() === 'economia';
  let ecoPending = false; // conferir o Battery Saver assim que o jogo estiver de pé (welcome)
  let ecoTries = 0;
  function ecoFlag(value) {
    try {
      if (value === undefined) return localStorage.getItem(ECO_KEY) === '1';
      if (value) localStorage.setItem(ECO_KEY, '1');
      else localStorage.removeItem(ECO_KEY);
    } catch { }
    return false;
  }
  function fitEconomy() {
    if (!ecoPending || !welcomed) return;
    const show = on && cfg.tela !== 'off';
    if (show && economy()) {
      document.getElementById('eco-sair')?.click();
      if (economy()) {
        // O jogo ainda não pôs o clique no botão: tenta de novo nas próximas voltas (até ~10).
        if (++ecoTries >= 10) {
          ecoPending = false;
          note('não consegui desligar o Battery Saver (o jogo esconde as partidas)');
        }
        return;
      }
      ecoFlag(true);
      note('desliguei o Battery Saver (Modo Economia) para mostrar as partidas');
    }
    else if (!show && ecoFlag() && !economy()) {
      document.getElementById('hunt-eco')?.click();
      if (!economy()) {
        if (++ecoTries < 10) return;
        note('não consegui religar o Battery Saver');
      }
      else note('religuei o Battery Saver (Modo Economia)');
      ecoFlag(false);
    }
    else if (!show && ecoFlag()) ecoFlag(false); // já está ligado de novo (a pessoa religou)
    ecoPending = false;
  }
  // Só marca: quem confere é a volta do relógio, depois do config e do set que o deck manda juntos (conferir no config,
  // com a fila ainda desligada numa página recém-aberta, religaria o Battery Saver só para desligar logo em seguida).
  function checkEconomy() {
    ecoPending = true;
    ecoTries = 0;
  }

  // A velocidade das partidas mostradas. O replay do jogo (o ReplayGuerra de /replay.mjs e o ReplayTabuleiro de
  // /replay-tabuleiro.mjs, que herda dele) não guarda velocidade: cada partida começa em 1× (ou mais rápida, para caber
  // até a próxima). O deck pega as mesmas classes do jogo (o import de um módulo que o jogo já carregou devolve o mesmo, sem
  // baixar de novo) e, no primeiro play do replay que abriu junto com uma partida da fila, põe a velocidade escolhida;
  // os botões de velocidade do replay seguem funcionando.
  // Só importa depois que o jogo baixou os dois: um import antes dele (bloqueado pelo CSP do site) estragaria o do jogo.
  const MATCH_GAP = TEST ? 300 : 3000; // replay aberto até isso da partida é dela
  const SPEED_RETRY = TEST ? 300 : 60000; // entre duas tentativas de pegar o replay do jogo
  let speedTriedAt = 0;
  // O endereço exato com que o jogo baixou o módulo (o import com o mesmo endereço devolve o módulo dele).
  const loaded = (name) => {
    try {
      return performance.getEntriesByType('resource').find((e) => new URL(e.name).pathname === name)?.name ?? null;
    } catch {
      return null;
    }
  };
  function prepareSpeed() {
    if (speedReady || speedBusy || Date.now() - speedTriedAt < SPEED_RETRY) return;
    const baseUrl = loaded('/replay.mjs');
    const boardUrl = loaded('/replay-tabuleiro.mjs');
    if (!baseUrl || !boardUrl) {
      speedError = 'o replay do jogo ainda não carregou';
      return;
    }
    speedBusy = true;
    speedTriedAt = Date.now();
    Promise.all([import(baseUrl), import(boardUrl)])
      .then(([base, board]) => {
        const Replay = base.ReplayGuerra;
        if (typeof Replay?.prototype?.iniciar !== 'function' || typeof Replay.prototype.velocidade !== 'function') throw new Error('o replay do jogo mudou');
        const start = Replay.prototype.iniciar;
        Replay.prototype.iniciar = function (...args) {
          this.__deckAberto = Date.now();
          return start.apply(this, args);
        };
        // O ReplayTabuleiro tem o play dele (não chama o do ReplayGuerra): os dois passam pelo deck.
        for (const Kind of [Replay, board.ReplayTabuleiro]) {
          if (typeof Kind?.prototype?.tocar !== 'function' || !Object.prototype.hasOwnProperty.call(Kind.prototype, 'tocar')) continue;
          const play = Kind.prototype.tocar;
          Kind.prototype.tocar = function (...args) {
            try {
              speedUp(this);
            } catch { }
            return play.apply(this, args);
          };
        }
        speedReady = true;
        speedError = null;
      })
      .catch((e) => {
        speedError = `não deu para pegar o replay do jogo (${String(e?.message || e)})`;
      })
      .finally(() => {
        speedBusy = false;
      });
  }
  function speedUp(player) {
    if (player.__deckVel !== undefined) return;
    player.__deckVel = 0;
    // Só o replay de uma partida da fila (não os replays que a pessoa abre no jogo) e com a fila ligada.
    if (!on || cfg.tela === 'off' || !(cfg.vel > 0) || !player.__deckAberto || Math.abs(player.__deckAberto - matchAt) > MATCH_GAP) return;
    player.__deckVel = lastSpeed = player.velocidade(cfg.vel, { livre: true });
    if (!player.mini) syncSpeedButtons(player.vel);
  }
  // Os botões 1×/2×/4×/8× do replay na tela acendem na velocidade de agora (16× e 32× aparecem como "livre", como no jogo).
  function syncSpeedButtons(speed) {
    const host = document.getElementById('gwr-vels');
    if (!host) return;
    let listed = false;
    for (const b of host.querySelectorAll('.gwr-vel')) {
      const same = Number(b.dataset.vel) === speed;
      listed ||= same;
      b.classList.toggle('on', same);
    }
    host.dataset.livre = listed ? '' : `${Math.round(speed)}×`;
    host.classList.toggle('com-livre', !listed);
  }

  // Entrar na fila só precisa do socket aberto: o estado da conta (VIP, a automação) é só para a auto-fila do jogo.
  function tick() {
    if (!on) return;
    keepScreen();
    if (cfg.tela !== 'off' && cfg.vel > 0) prepareSpeed();
    late();
    if (socket?.readyState !== 1) return;
    const now = Date.now();
    // Ao ligar (e depois de reconectar): pergunta ao jogo se a conta já está na fila.
    if (!asked) {
      asked = true;
      send({ t: 'pvp.info', semLadder: 1, soCaixa: 1 });
      lastTry = now;
      return;
    }
    if (vip() && me?.automation) {
      // VIP: quem põe a conta na fila é a auto-fila do jogo; o deck só liga ela e entra uma vez, ao ligar.
      if (!me.automation.pvpAutoFila && now - lastAuto >= AUTO_GAP) {
        lastAuto = now;
        me.automation = { ...me.automation, pvpAutoFila: true };
        send({ t: 'auto.set', ...me.automation });
        note('liguei a auto-fila do jogo (conta VIP)');
      }
      if (!enteredOnce && !fila.na && now >= waitUntil) {
        enteredOnce = true;
        lastTry = now;
        send({ t: 'pvp.fila.entrar' });
      }
      return;
    }
    if (fila.na || now < waitUntil || now - lastTry < TRY) return;
    lastTry = now;
    send({ t: 'pvp.fila.entrar' });
  }

  setInterval(() => {
    try {
      tick();
    } catch { }
    try {
      fitEconomy();
    } catch { }
  }, TEST ? 100 : 1000);

  window.__deckPvp = {
    /** Como as partidas da fila aparecem (`tela`: completa, mini ou off) e a velocidade delas (`vel`, 0 = a do jogo). */
    config(value) {
      const tela = ['completa', 'mini', 'off'].includes(value?.tela) ? value.tela : 'off';
      const vel = [1, 2, 4, 8, 16, 32].includes(Number(value?.vel)) ? Number(value.vel) : 0;
      if (tela === cfg.tela && vel === cfg.vel) return true;
      if (tela !== cfg.tela) checkEconomy();
      cfg = { tela, vel };
      if (on) tick();
      return true;
    },
    /** Liga ou desliga a fila. */
    set(value) {
      const want = !!value;
      if (want === on) return true;
      on = want;
      asked = false;
      enteredOnce = false;
      checkEconomy();
      if (on) {
        note('fila de PvP ligada');
        tick();
        return true;
      }
      if (socket?.readyState === 1) {
        if (fila.na) send({ t: 'pvp.fila.sair' });
        // Desligar o ⚔ desliga também a auto-fila do jogo (VIP).
        if (me?.automation?.pvpAutoFila) {
          me.automation = { ...me.automation, pvpAutoFila: false };
          send({ t: 'auto.set', ...me.automation });
          note('desliguei a auto-fila do jogo');
        }
      }
      fila = { ...fila, na: false, desde: 0 };
      note('fila de PvP desligada');
      return true;
    },
    /** O que o deck lê a cada conferência. */
    status() {
      const now = Date.now();
      late();
      return {
        on,
        connected: socket?.readyState === 1,
        socket: socket ? socket.readyState : -1,
        seen,
        welcomed,
        state: !!me,
        vip: vip(),
        auto: !!me?.automation?.pvpAutoFila,
        hidden: screen() === 'off',
        tela: screen(),
        semCena: noScene(),
        ecoDoDeck: ecoFlag(),
        querTela: cfg.tela,
        querVel: cfg.vel,
        velPronta: speedReady,
        velErro: speedReady ? null : speedError,
        velUltima: lastSpeed,
        na: !!fila.na,
        naSegundos: fila.na && fila.desde ? Math.max(0, Math.round((now - Number(fila.desde)) / 1000)) : 0,
        tamanho: Number(fila.tamanho) || 0,
        esperaSegundos: Math.max(0, Math.round((waitUntil - now) / 1000)),
        wins,
        losses,
        points: Number.isFinite(points) ? points : null,
        lastMatch,
        log: log.slice(-8).map(String),
      };
    },
  };
})();
