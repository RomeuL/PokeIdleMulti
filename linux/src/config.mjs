const channel = /^[a-z0-9_-]{1,64}$/i;
export function validateConfig(input, current) {
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('Configuração inválida');
  const config = structuredClone({ ...current, ...input });
  for (const key of ['AccountNames', 'Zoom', 'Muted', 'Off', 'BonusGuard', 'Kick', 'Twitch', 'Restock', 'Pvp', 'PvpScreen', 'PvpSpeed', 'RedeemKick', 'RedeemTwitch']) {
    if (!Array.isArray(config[key]) || config[key].length !== 4) throw new Error(`Configuração inválida: ${key} precisa de 4 contas`);
  }
  if (!['Grid', 'Columns', 'Focus'].includes(config.Layout)) throw new Error('Layout inválido');
  if (!Number.isInteger(config.Focused) || config.Focused < 0 || config.Focused > 3) throw new Error('Conta em foco inválida');
  config.AccountNames = config.AccountNames.map((s, i) => typeof s === 'string' && s.trim() ? s.trim().slice(0, 64) : `Conta ${i + 1}`);
  if (config.Zoom.some(z => !Number.isFinite(z) || z < .5 || z > 1.5)) throw new Error('Zoom inválido');
  if (!Array.isArray(config.LivesAccounts) || config.LivesAccounts.some(i => !Number.isInteger(i) || i < 1 || i > 4)) throw new Error('Contas das lives inválidas');
  config.LivesAccounts = [...new Set(config.LivesAccounts)];
  for (const site of ['Twitch', 'Kick']) {
    if (!Array.isArray(config[site + 'Channels']) || config[site + 'Channels'].length > 100 || config[site + 'Channels'].some(c => typeof c !== 'string' || !channel.test(c))) throw new Error(`Canais da ${site} inválidos`);
    config[site + 'Channels'] = [...new Set(config[site + 'Channels'].map(s => s.toLowerCase()))];
    if (!Number.isInteger(config[site + 'Lives']) || config[site + 'Lives'] < 0 || config[site + 'Lives'] > 10) throw new Error('Quantidade de lives inválida');
    if (config['Redeem' + site].some(r => r !== null && !['Xp', 'Capture', 'Lure'].includes(r))) throw new Error('Bônus inválido');
    if (config[site].some(limit => !limit || !Array.isArray(limit.Closed) || limit.Closed.some(c => typeof c !== 'string' || !channel.test(c)) || limit.Slots !== null && (!Number.isInteger(limit.Slots) || limit.Slots < 0 || limit.Slots > 10))) throw new Error('Limite de lives inválido');
  }
  for (const key of ['GameFps', 'SpriteMs']) if (!Number.isFinite(config[key])) throw new Error('Desempenho inválido');
  for (const key of ['Muted', 'Off', 'BonusGuard', 'Pvp']) if (config[key].some(v => typeof v !== 'boolean')) throw new Error('Opção inválida: ' + key);
  for (const rule of config.Restock) {
    if (!rule || typeof rule.On !== 'boolean' || !Number.isSafeInteger(rule.GoldFloor) || rule.GoldFloor < 0) throw new Error('Recompra inválida');
    for (const kind of ['Balls', 'Revives', 'Potions']) if (!rule[kind] || typeof rule[kind].On !== 'boolean' || !Number.isInteger(rule[kind].Id) || ['Below', 'Buy'].some(k => !Number.isSafeInteger(rule[kind][k]) || rule[kind][k] < 0 || rule[kind][k] > 10000000)) throw new Error('Quantidade de recompra inválida');
  }
  return config;
}
