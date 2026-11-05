using System.Text.Json;

namespace PokeIdleMulti;

/// <summary>
/// O resgate automático do bônus do PokeIdle com os pontos de cada canal de um site (Kick ou Twitch), numa conta.
/// Usa os mesmos pedidos que o site faz ao clicar em Resgatar, com o login da conta, de dentro do player de cada live
/// aberta (que já usa esse login). A cada conferência, no máximo 1 resgate por canal. Sem o site vinculado no jogo o
/// PokeIdle devolve os pontos: o deck vê o saldo voltar e para de resgatar nesta conta. Na Twitch também pega o baú
/// de bônus (+50 a cada ~15 min), que só vem para quem clica.
/// </summary>
internal sealed class PointsRedeemer
{
    /// <summary>De quanto em quanto tempo confere o saldo na Kick (assistindo, ela dá 10 pontos a cada 5 min).</summary>
    public static readonly TimeSpan KickInterval = TimeSpan.FromMinutes(5);

    /// <summary>Na Twitch, mais seguido: o baú de bônus aparece a cada ~15 min e espera o clique.</summary>
    public static readonly TimeSpan TwitchInterval = TimeSpan.FromMinutes(2);

    // Resgate (ou baú) recusado: espera antes de tentar de novo naquele canal.
    private static readonly TimeSpan RetryWait = TimeSpan.FromMinutes(30);

    // Saldo que sobe até esse tempo depois de um resgate, bem mais do que assistir dá, é o bônus devolvido.
    private static readonly TimeSpan RefundWindow = TimeSpan.FromMinutes(30);

    // O Client-Id do próprio site da Twitch, o mesmo do player.
    internal const string TwitchClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";

    /// <summary>
    /// O começo dos scripts da Twitch (dentro de "async a => {"): gql(query, variables) chama a API do site com o login
    /// da conta (a.token, a.device, a.clientId, a.api); no lugar da query pode ir o pedido inteiro (o das operações
    /// salvas da Twitch, com operationName e o hash). Quando a Twitch desafia o pedido com a checagem anti-robô, pede o
    /// token dela de dentro da página e manda de novo. fail(resposta, resultado) diz o que deu errado.
    /// </summary>
    internal const string TwitchGql =
        " const base = { 'Client-Id': a.clientId, Authorization: 'OAuth ' + a.token, 'Content-Type': 'text/plain;charset=UTF-8'," +
        "  ...(a.device ? { 'X-Device-Id': a.device } : {}) };" +
        " let integrity = null;" +
        " const gql = async (query, variables) => {" +
        "  const body = JSON.stringify(typeof query === 'string' ? { query, variables } : query);" +
        "  for (let i = 0; i < 2; i++) {" +
        "   const r = await fetch(a.api + '/gql', { method: 'POST', cache: 'no-cache'," +
        "    headers: integrity ? { ...base, 'Client-Integrity': integrity } : base, body });" +
        "   if (!r.ok) return { failed: 'erro ' + r.status };" +
        "   const j = await r.json();" +
        "   const challenged = !!j?.extensions?.challenge || (j?.errors || []).some(e => e?.extensions?.code === 'IntegrityCheckFailed');" +
        "   if (!challenged) return j;" +
        "   if (integrity) return { failed: 'checagem anti-robô da Twitch' };" +
        "   const t = await fetch(a.api + '/integrity', { method: 'POST', cache: 'no-cache', headers: base });" +
        "   const tj = t.ok ? await t.json() : null;" +
        "   if (!tj?.token) return { failed: 'checagem anti-robô da Twitch (' + t.status + ')' };" +
        "   integrity = tj.token;" +
        "  }" +
        " };" +
        " const fail = (j, x) => j?.failed || x?.error?.code || j?.errors?.[0]?.message || 'sem resposta';";

    private readonly Dictionary<string, Channel> _channels = new();

    public PointsRedeemer(LiveSite site) => Site = site;

    public LiveSite Site { get; }

    private sealed class Channel
    {
        /// <summary>O saldo na última conferência, já sem o que foi resgatado nela.</summary>
        public int? Points { get; set; }
        public int? Cost { get; set; }
        public DateTime? RedeemedAt { get; set; }
        public DateTime RetryAt { get; set; }

        /// <summary>O baú de bônus da Twitch: quando pegou o último, e o que deu errado no último que tentou.</summary>
        public DateTime? ClaimedAt { get; set; }
        public string? ClaimError { get; set; }
        public DateTime ClaimRetryAt { get; set; }

        public string Text { get; set; } = "";

        /// <summary>O id do canal na Twitch (os avisos vêm por id).</summary>
        public string? Id { get; set; }

        /// <summary>Desde quando a live está aberta (a conferência de 2 em 2 min vê), e a última conferência.</summary>
        public DateTime? OpenSince { get; set; }
        public DateTime? CheckedAt { get; set; }

        /// <summary>Os pontos que entraram na última hora (pelos avisos), e o último de assistir.</summary>
        public List<(DateTime At, int Points)> Gains { get; } = new();
        public DateTime? WatchedAt { get; set; }

        /// <summary>Já avisou que a live aberta não está rendendo os pontos de assistir (volta com o próximo).</summary>
        public bool QuietNoted { get; set; }
    }

    /// <summary>O id da conta na Twitch (a conferência lê; os avisos da conta usam).</summary>
    public string? UserId { get; private set; }

    /// <summary>Desde quando os avisos da Twitch estão chegando (null: desconectados), para saber se a live rende.</summary>
    public DateTime? ListeningSince { get; set; }

    /// <summary>Live aberta esse tempo sem nenhum ponto de assistir (com os avisos chegando) não está rendendo.</summary>
    public TimeSpan QuietLimit { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>O id do canal na Twitch (null: ainda não conferiu).</summary>
    public string? IdOf(string channel) => _channels.TryGetValue(channel, out var c) ? c.Id : null;

    /// <summary>O canal de um id da Twitch (null: não é de uma live conferida).</summary>
    public string? ChannelOf(string id) => _channels.FirstOrDefault(c => c.Value.Id == id).Key;

    // Os nomes dos motivos dos pontos nos avisos da Twitch.
    private static string Reason(string code) => code switch
    {
        "WATCH_STREAK" => "da sequência de lives",
        "RAID" => "da raid",
        "CLAIM" => "do baú",
        "WATCH" => "assistindo",
        _ => $"({code})",
    };

    /// <summary>
    /// Um aviso de pontos que entraram (<paramref name="reason"/>: WATCH, CLAIM, WATCH_STREAK, RAID…): conta para o
    /// rendimento e atualiza o saldo. A sequência de lives, a raid e o que não for assistir nem baú vão para o
    /// registro; devolve o aviso para o cabeçalho da conta nesses.
    /// </summary>
    public string? Earned(string channel, string reason, int points, int? balance)
    {
        if (!_channels.TryGetValue(channel, out var c))
            _channels[channel] = c = new Channel();
        var now = DateTime.Now;
        c.Gains.Add((now, points));
        c.Gains.RemoveAll(g => now - g.At > TimeSpan.FromHours(1));
        if (reason == "WATCH")
        {
            c.WatchedAt = now;
            c.QuietNoted = false;
        }
        if (balance is int b)
            c.Points = b;
        if (reason is "WATCH" or "CLAIM")
            return null;
        Log?.Invoke($"{channel}: ganhou +{points} {Reason(reason)}" + (balance is int saldo ? $" (saldo {saldo})" : ""));
        return $"{Site}: +{points} {Reason(reason)} em {channel}";
    }

    /// <summary>
    /// Desde quando a live está sem ponto de assistir: o último que veio, ou a live aberta com os avisos chegando (o
    /// mais novo dos três). Null: sem os avisos, não dá para saber.
    /// </summary>
    private DateTime? QuietFrom(Channel c)
    {
        if (ListeningSince is not DateTime since || c.OpenSince is not DateTime open)
            return null;
        var from = since > open ? since : open;
        return c.WatchedAt is DateTime watched && watched > from ? watched : from;
    }

    /// <summary>Não está rendendo: <see cref="QuietLimit"/> sem ponto de assistir, com a live aberta e os avisos chegando.</summary>
    private bool Quiet(Channel c, DateTime now) => QuietFrom(c) is DateTime from && now - from >= QuietLimit;

    /// <summary>
    /// As lives abertas que acabaram de parar de render (uma vez por vez que param): vão para o registro, e o deck
    /// avisa no cabeçalho da conta.
    /// </summary>
    public List<string> NewQuiet(IEnumerable<string> open)
    {
        var now = DateTime.Now;
        var quiet = new List<string>();
        foreach (var channel in open)
            if (_channels.TryGetValue(channel, out var c) && !c.QuietNoted && Quiet(c, now))
            {
                c.QuietNoted = true;
                quiet.Add(channel);
                Log?.Invoke($"{channel}: a live está aberta há {QuietLimit.TotalMinutes:0} min sem os pontos de assistir");
            }
        return quiet;
    }

    /// <summary>Por que parou de resgatar nesta conta (null: resgatando).</summary>
    public string? Stopped { get; private set; }

    /// <summary>Sem o login do site no perfil da conta (as lives tocam, mas não dá para resgatar).</summary>
    public bool NoLogin { get; set; }

    /// <summary>Para o registro dos resgates: cada resgate, baú, recusa e pontos que voltaram ("canal: o que houve").</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Volta a resgatar do zero (ao salvar Lives → Config…, depois de vincular o site no jogo).</summary>
    public void Restart()
    {
        Stopped = null;
        NoLogin = false;
        _channels.Clear();
    }

    /// <summary>
    /// O saldo do canal e o último resgate, para o menu Lives ("230/300 pontos, resgatou 14:05"), com o que entrou na
    /// última hora pelos avisos ("+320/h") e se a live parou de render.
    /// </summary>
    public string Text(string channel)
    {
        if (!_channels.TryGetValue(channel, out var c))
            return "";
        var now = DateTime.Now;
        int hour = c.Gains.Where(g => now - g.At <= TimeSpan.FromHours(1)).Sum(g => g.Points);
        var parts = new List<string>();
        if (c.Text.Length > 0)
            parts.Add(c.Text);
        if (hour > 0)
            parts.Add($"+{hour}/h");
        if (QuietFrom(c) is DateTime from && now - from >= QuietLimit)
            parts.Add($"sem pontos de assistir há {(now - from).TotalMinutes:0} min");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// O que deu errado com o baú no que o script de uma live devolveu (null: pegou, ou o erro do baú já foi para o
    /// registro). Na Twitch leve o player abre só na hora, e uma falha sem erro do baú ficava sem rastro.
    /// </summary>
    public static string? ClaimProblem(string result)
    {
        if (result.Length == 0)
            return "o player não respondeu em 30 s";
        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.TryGetProperty("claimed", out _) || root.TryGetProperty("claimError", out _))
                return null;
            return root.TryGetProperty("error", out var e) ? e.GetString() : "a página não viu o baú";
        }
        catch (JsonException)
        {
            return "resposta estranha do player";
        }
    }

    /// <summary>Só o saldo do canal, com o custo do bônus ("230/300"; "" antes da primeira leitura), para o resumo curto.</summary>
    public string Balance(string channel) => _channels.TryGetValue(channel, out var c) && c.Points is int points
        ? c.Cost is int cost ? $"{points}/{cost}" : $"{points}"
        : "";

    // A partir desse saldo os pontos do último resgate voltaram (metade do custo: assistir dá 10 a cada 5 min, e o
    // baú da Twitch, 50 a cada ~15 min).
    private static int? RefundAt(Channel c) =>
        c.Points is int points && c.Cost is int cost && c.RedeemedAt is DateTime at && DateTime.Now - at < RefundWindow
            ? points + cost / 2
            : null;

    /// <summary>Se este canal pode gastar agora, e a partir de que saldo é devolução (não resgata em cima dela).</summary>
    private (bool Spend, int? Refund) Plan(string channel)
    {
        _channels.TryGetValue(channel, out var c);
        return (Stopped is null && (c is null || DateTime.Now >= c.RetryAt), c is null ? null : RefundAt(c));
    }

    // O nome do bônus em cada canal: "Bônus +15% XP PokeIdle", "Bônus de +15% Capture Boost", "… Secret Lure".
    internal static string Word(KickReward reward) => reward switch
    {
        KickReward.Capture => "captur",
        KickReward.Lure => "lure|shiny",
        _ => "\\bxp\\b",
    };

    /// <summary>
    /// O script que roda no player da live de <paramref name="channel"/> da Kick: lê o saldo e o bônus escolhido e, se
    /// der e estiver liberado, resgata 1. Devolve (quando a promessa acaba) o JSON que <see cref="Apply"/> lê.
    /// </summary>
    public string KickScript(string api, string channel, string token, KickReward reward)
    {
        var (spend, refund) = Plan(channel);
        var args = JsonSerializer.Serialize(new { api, channel, token, word = Word(reward), spend, refund });
        return "(async a => {" +
               " const call = (path, init) => fetch(a.api + '/api/v2/channels/' + encodeURIComponent(a.channel) + path," +
               "  { cache: 'no-cache', credentials: 'include', ...init, headers: { Accept: 'application/json', 'x-app-platform': 'web'," +
               "   Authorization: 'Bearer ' + a.token, ...(init && init.body ? { 'Content-Type': 'application/json' } : {}) } });" +
               " try {" +
               "  let r = await call('/points');" +
               "  if (!r.ok) return { error: 'saldo: erro ' + r.status };" +
               "  const points = (await r.json())?.data?.points;" +
               "  if (typeof points !== 'number') return { error: 'saldo: sem os pontos na resposta' };" +
               "  r = await call('/rewards');" +
               "  if (!r.ok) return { points, error: 'bônus: erro ' + r.status };" +
               "  const word = new RegExp(a.word, 'i');" +
               "  const reward = ((await r.json())?.data || []).find(x => x.is_enabled && word.test(x.title || '')" +
               "   && /pokeidle/i.test((x.title || '') + ' ' + (x.description || '')));" +
               "  if (!reward) return { points, error: 'canal sem o bônus' };" +
               "  const out = { points, cost: reward.cost, title: reward.title, paused: !!reward.is_paused };" +
               "  if (!a.spend || reward.is_paused || points < reward.cost || (a.refund !== null && points >= a.refund)) return out;" +
               // O transaction_id é um ULID novo, como o site gera: o tempo em base 32 e mais 16 letras ao acaso.
               "  const abc = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'; let t = Date.now(), id = '';" +
               "  for (let i = 0; i < 10; i++) { id = abc[t % 32] + id; t = Math.floor(t / 32); }" +
               "  for (let i = 0; i < 16; i++) id += abc[Math.floor(Math.random() * 32)];" +
               "  r = await call('/rewards/' + reward.id + '/redeem', { method: 'POST', body: JSON.stringify({ transaction_id: id, message: '' }) });" +
               "  return { ...out, redeemed: r.ok, status: r.status };" +
               " } catch (e) { return { error: 'sem resposta da Kick: ' + (e && e.message || e) }; }" +
               $"}})({args}).then(o => JSON.stringify(o))";
    }

    /// <summary>
    /// O script que roda no player da live de <paramref name="channel"/> da Twitch, pela API do site (gql.twitch.tv),
    /// com o login da conta (<paramref name="token"/>, o cookie auth-token) e o aparelho dela (<paramref name="device"/>,
    /// o cookie unique_id): lê o saldo, pega o baú de bônus se tiver um (<paramref name="claim"/>) e, com
    /// <paramref name="reward"/>, resgata 1 bônus se der. Quando a Twitch desafia o pedido com a checagem anti-robô
    /// (como no baú), pede o token dela de dentro do player, como o próprio site faz (o Kasada do player entra no
    /// pedido), e manda de novo. Devolve também o id da conta e o do canal (os avisos da Twitch vêm por id).
    /// <paramref name="spend"/> falso só lê e pega o baú (o baú que o aviso da Twitch disse que apareceu).
    /// <paramref name="claimId"/> é o id do baú que veio no aviso: a consulta nem sempre mostra o baú (availableClaim
    /// vem vazio), e aí ele vai com o id do aviso.
    /// </summary>
    public string TwitchScript(string api, string channel, string token, string? device, KickReward? reward, bool claim, bool spend = true,
                               string? claimId = null)
    {
        var (canSpend, refund) = Plan(channel);
        spend = spend && canSpend;
        _channels.TryGetValue(channel, out var c);
        claim = claim && (c is null || DateTime.Now >= c.ClaimRetryAt);
        var args = JsonSerializer.Serialize(new
        {
            api, channel, token, device, clientId = TwitchClientId, word = reward is KickReward r ? Word(r) : null, spend, refund, claim, claimId,
        });
        return "(async a => {" + TwitchGql +
               " try {" +
               "  const j = await gql('query($login:String!){currentUser{id} user(login:$login){id channel{self{communityPoints{balance availableClaim{id}}}' +" +
               "   ' communityPointsSettings{customRewards{id title prompt cost isEnabled isPaused isInStock cooldownExpiresAt}}}}}', { login: a.channel });" +
               "  if (j?.failed) return { error: 'saldo: ' + j.failed };" +
               "  const user = j?.data?.user;" +
               "  if (!user?.channel) return { error: 'canal não encontrado' };" +
               "  const ids = { me: j?.data?.currentUser?.id ?? null, channelId: user.id };" +
               "  const cp = user.channel.self?.communityPoints;" +
               "  if (typeof cp?.balance !== 'number') return { ...ids, error: 'saldo: sem os pontos na resposta (login da Twitch?)' };" +
               "  let points = cp.balance; const out = { points, ...ids };" +
               "  const claimId = cp.availableClaim?.id || a.claimId;" +
               "  if (a.claim && claimId) {" +
               "   const k = await gql('mutation($i:ClaimCommunityPointsInput!){claimCommunityPoints(input:$i){claim{id} currentPoints error{code}}}'," +
               "    { i: { channelID: user.id, claimID: claimId } });" +
               "   const p = k?.data?.claimCommunityPoints;" +
               "   if (p && !p.error && !k.errors) { out.claimed = true; points = typeof p.currentPoints === 'number' ? p.currentPoints : points + 50; }" +
               "   else out.claimError = fail(k, p);" +
               "   out.points = points;" +
               "  }" +
               "  if (!a.word) return out;" +
               "  const word = new RegExp(a.word, 'i');" +
               "  const reward = (user.channel.communityPointsSettings?.customRewards || []).find(x => x.isEnabled && word.test(x.title || '')" +
               "   && /pokeidle/i.test((x.title || '') + ' ' + (x.prompt || '')));" +
               "  if (!reward) return { ...out, error: 'canal sem o bônus' };" +
               "  Object.assign(out, { cost: reward.cost, title: reward.title, paused: reward.isPaused || !reward.isInStock || !!reward.cooldownExpiresAt });" +
               "  if (!a.spend || out.paused || points < reward.cost || (a.refund !== null && points >= a.refund)) return out;" +
               // O transactionID é um id novo a cada resgate (32 letras hexadecimais). O custo, o título e a descrição
               // (prompt) vão iguais aos do bônus no canal: a Twitch confere e, se não baterem, recusa (PROPERTIES_MISMATCH).
               "  const id = [...crypto.getRandomValues(new Uint8Array(16))].map(b => b.toString(16).padStart(2, '0')).join('');" +
               "  const r = await gql('mutation($i:RedeemCommunityPointsCustomRewardInput!){redeemCommunityPointsCustomReward(input:$i){redemption{id} error{code}}}'," +
               "   { i: { channelID: user.id, cost: reward.cost, prompt: reward.prompt ?? null, rewardID: reward.id, title: reward.title, transactionID: id } });" +
               "  const p = r?.data?.redeemCommunityPointsCustomReward;" +
               "  if (p && !p.error && !r.errors) return { ...out, redeemed: true };" +
               "  return { ...out, refused: fail(r, p) };" +
               " } catch (e) { return { error: 'sem resposta da Twitch: ' + (e && e.message || e) }; }" +
               $"}})({args}).then(o => JSON.stringify(o))";
    }

    // A mesma consulta do começo do TwitchScript: a conta, o canal, o saldo, o baú e os bônus.
    private const string TwitchPointsQuery =
        "query($login:String!){currentUser{id} user(login:$login){id channel{self{communityPoints{balance availableClaim{id}}}" +
        " communityPointsSettings{customRewards{id title prompt cost isEnabled isPaused isInStock cooldownExpiresAt}}}}}";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // O navegador que o deck diz ser fora da página (o mesmo da Twitch leve).
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    /// <summary>
    /// Na Twitch leve, a leitura de 2 em 2 min sem a página: a mesma consulta do <see cref="TwitchScript"/> (consultas
    /// não passam pela checagem anti-robô), pela API em <paramref name="api"/>. Devolve o mesmo JSON que o script
    /// devolveria sem agir (que <see cref="Apply"/> lê), com "claimId" quando o baú está esperando; ou { failed } quando o
    /// pedido não passou (aí o deck usa a página, como antes).
    /// </summary>
    public static async Task<string> TwitchReadAsync(string api, string channel, string token, string? device, KickReward? reward)
    {
        static string Failed(string why) => JsonSerializer.Serialize(new { failed = why, error = "saldo: " + why });
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, api + "/gql");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Client-Id", TwitchClientId);
            request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
            if (device is not null)
                request.Headers.TryAddWithoutValidation("X-Device-Id", device);
            request.Content = new StringContent(JsonSerializer.Serialize(new { query = TwitchPointsQuery, variables = new { login = channel } }),
                System.Text.Encoding.UTF8, "text/plain");
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return Failed($"erro {(int)response.StatusCode}");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("challenge", out _))
                return Failed("checagem anti-robô da Twitch");
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Failed("sem resposta");
            string? me = data.TryGetProperty("currentUser", out var cu) && cu.ValueKind == JsonValueKind.Object
                         && cu.TryGetProperty("id", out var meId) ? meId.GetString() : null;
            if (!data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object
                || !user.TryGetProperty("channel", out var ch) || ch.ValueKind != JsonValueKind.Object)
                return JsonSerializer.Serialize(new { error = "canal não encontrado" });
            string? channelId = user.GetProperty("id").GetString();
            JsonElement cp = default;
            bool hasPoints = ch.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.Object
                             && self.TryGetProperty("communityPoints", out cp) && cp.ValueKind == JsonValueKind.Object
                             && cp.TryGetProperty("balance", out var bal) && bal.ValueKind == JsonValueKind.Number;
            if (!hasPoints)
                return JsonSerializer.Serialize(new { me, channelId, error = "saldo: sem os pontos na resposta (login da Twitch?)" });
            int points = cp.GetProperty("balance").GetInt32();
            string? claimId = cp.TryGetProperty("availableClaim", out var claim) && claim.ValueKind == JsonValueKind.Object
                              && claim.TryGetProperty("id", out var cid) ? cid.GetString() : null;
            if (reward is not KickReward chosen)
                return JsonSerializer.Serialize(new { points, me, channelId, claimId });
            var word = new System.Text.RegularExpressions.Regex(Word(chosen), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            JsonElement? found = null;
            if (ch.TryGetProperty("communityPointsSettings", out var settings) && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("customRewards", out var rewards) && rewards.ValueKind == JsonValueKind.Array)
                foreach (var r in rewards.EnumerateArray())
                {
                    string title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    string prompt = r.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
                    if (r.TryGetProperty("isEnabled", out var on) && on.ValueKind == JsonValueKind.True && word.IsMatch(title)
                        && (title + " " + prompt).Contains("pokeidle", StringComparison.OrdinalIgnoreCase))
                    {
                        found = r;
                        break;
                    }
                }
            if (found is not JsonElement bonus)
                return JsonSerializer.Serialize(new { points, me, channelId, claimId, error = "canal sem o bônus" });
            bool paused = (bonus.TryGetProperty("isPaused", out var ip) && ip.ValueKind == JsonValueKind.True)
                          || (bonus.TryGetProperty("isInStock", out var stock) && stock.ValueKind == JsonValueKind.False)
                          || (bonus.TryGetProperty("cooldownExpiresAt", out var cool) && cool.ValueKind == JsonValueKind.String);
            return JsonSerializer.Serialize(new
            {
                points, me, channelId, claimId, cost = bonus.GetProperty("cost").GetInt32(), title = bonus.GetProperty("title").GetString(), paused,
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
                                      or KeyNotFoundException or FormatException)
        {
            return Failed("sem resposta da Twitch");
        }
    }

    /// <summary>
    /// Na Twitch leve, se a leitura de fora da página (<see cref="TwitchReadAsync"/>) pede a página: o baú esperando
    /// (com o baú ligado e sem espera de 30 min no canal), um resgate que dá agora (as mesmas regras do script), ou a
    /// leitura que não passou.
    /// </summary>
    public bool NeedsPage(string channel, string json, bool claim)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("failed", out _))
                return true;
            _channels.TryGetValue(channel, out var c);
            if (claim && root.TryGetProperty("claimId", out var id) && id.ValueKind == JsonValueKind.String
                && (c is null || DateTime.Now >= c.ClaimRetryAt))
                return true;
            if (root.TryGetProperty("points", out var p) && p.TryGetInt32(out int points)
                && root.TryGetProperty("cost", out var k) && k.TryGetInt32(out int cost)
                && !(root.TryGetProperty("paused", out var z) && z.ValueKind == JsonValueKind.True))
            {
                var (spend, refund) = Plan(channel);
                return spend && points >= cost && !(refund is int limit && points >= limit);
            }
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    // A operação salva da Twitch para entrar numa raid (a mesma que o site manda ao clicar em "Entrar na raid").
    private const string JoinRaidHash = "c6a332a86d1087fbbb1a8623aa01bd1313d2386e7c63be60fdb2d1901f01a4ae";

    /// <summary>
    /// O script que entra na raid <paramref name="raidId"/> (avisada pela Twitch no canal da live aberta), com o login
    /// da conta: quem acompanha a raid ganha pontos no canal. Devolve { joined } ou { error }.
    /// </summary>
    public static string TwitchRaidScript(string api, string token, string? device, string raidId) =>
        TwitchAction(api, token, device, new { raid = raidId },
            "  const r = await gql({ operationName: 'JoinRaid', variables: { input: { raidID: a.raid } }," +
            $"   extensions: {{ persistedQuery: {{ version: 1, sha256Hash: '{JoinRaidHash}' }} }} }});" +
            "  if (r && !r.failed && !r.errors) return { joined: true };" +
            "  return { error: fail(r, null) };");

    /// <summary>O script que pega o "momento" <paramref name="momentId"/> do canal (avisado pela Twitch). Devolve { claimed } ou { error }.</summary>
    public static string TwitchMomentScript(string api, string token, string? device, string momentId) =>
        TwitchAction(api, token, device, new { moment = momentId },
            "  const r = await gql('mutation($i:ClaimCommunityMomentInput!){claimCommunityMoment(input:$i){moment{id}}}', { i: { momentID: a.moment } });" +
            "  const p = r?.data?.claimCommunityMoment;" +
            "  if (p && !r.errors) return { claimed: true };" +
            "  return { error: fail(r, p) };");

    private static string TwitchAction(string api, string token, string? device, object extra, string body)
    {
        var args = JsonSerializer.SerializeToNode(new { api, token, device, clientId = TwitchClientId })!.AsObject();
        foreach (var (name, value) in JsonSerializer.SerializeToNode(extra)!.AsObject())
            args[name] = value?.DeepClone();
        return "(async a => {" + TwitchGql +
               " try {" + body +
               " } catch (e) { return { error: 'sem resposta da Twitch: ' + (e && e.message || e) }; }" +
               "})(" + args.ToJsonString() + ").then(o => JSON.stringify(o))";
    }

    /// <summary>
    /// Anota o que o script de um canal devolveu. Devolve o aviso para o cabeçalho da conta quando resgatou, quando
    /// o site recusou ou quando parou por causa dos pontos que voltaram.
    /// </summary>
    public string? Apply(string channel, string json)
    {
        if (!_channels.TryGetValue(channel, out var c))
            _channels[channel] = c = new Channel();
        // Conferida de novo depois de um tempo sem: a live abriu (de novo) agora.
        var checkedNow = DateTime.Now;
        if (c.CheckedAt is not DateTime last || checkedNow - last > TimeSpan.FromMinutes(5))
            c.OpenSince = checkedNow;
        c.CheckedAt = checkedNow;
        int? points = null, cost = null, status = null;
        string? error = null, title = null, refused = null, claimError = null;
        bool redeemed = false, paused = false, claimed = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            points = root.TryGetProperty("points", out var p) && p.TryGetInt32(out int pv) ? pv : null;
            cost = root.TryGetProperty("cost", out var k) && k.TryGetInt32(out int kv) ? kv : null;
            status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out int sv) ? sv : null;
            error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            refused = root.TryGetProperty("refused", out var f) ? f.GetString() : null;
            claimError = root.TryGetProperty("claimError", out var ce) ? ce.GetString() : null;
            redeemed = root.TryGetProperty("redeemed", out var r) && r.ValueKind == JsonValueKind.True;
            paused = root.TryGetProperty("paused", out var z) && z.ValueKind == JsonValueKind.True;
            claimed = root.TryGetProperty("claimed", out var b) && b.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("me", out var me) && me.ValueKind == JsonValueKind.String)
                UserId = me.GetString();
            if (root.TryGetProperty("channelId", out var id) && id.ValueKind == JsonValueKind.String)
                c.Id = id.GetString();
        }
        catch (JsonException)
        {
            error = "a live não respondeu";
        }

        // O baú (só na Twitch): pegou, ou deu errado e espera 30 min para tentar de novo naquele canal.
        if (claimed)
        {
            c.ClaimedAt = DateTime.Now;
            c.ClaimError = null;
            Log?.Invoke($"{channel}: pegou o baú (+50" + (points is int now ? $", saldo {now})" : ")"));
        }
        else if (claimError is not null)
        {
            c.ClaimError = claimError;
            c.ClaimRetryAt = DateTime.Now + RetryWait;
            Log?.Invoke($"{channel}: o baú não passou ({claimError}); tento de novo em 30 min");
        }
        string bonus = c.ClaimError is string why ? $", baú: {why}" : c.ClaimedAt is DateTime got ? $", baú {got:HH:mm}" : "";

        if (points is not int balance)
        {
            c.Text = error ?? "a live não respondeu";
            return null;
        }
        if (cost is not int price)
        {
            // Sem bônus escolhido (só o baú) ou o canal sem o bônus: só o saldo.
            c.Points = balance;
            c.Text = $"{balance} pontos{bonus}" + (error is not null ? $", {error}" : "");
            return null;
        }

        string? note = null;
        if (RefundAt(c) is int limit && balance >= limit && Stopped is null)
        {
            Stopped = Site == LiveSite.Kick
                ? "os pontos voltaram (vincule a Kick no jogo, em Bônus na Kick, e salve Lives → Config… de novo)"
                : "os pontos voltaram (vincule a Twitch no jogo, em Bônus Twitch, e salve Lives → Config… de novo)";
            note = $"{Site}: os pontos do resgate voltaram ({Site} não vinculada no jogo?); parei de resgatar nesta conta";
            Log?.Invoke($"{channel}: os pontos do último resgate voltaram (saldo {balance}; {Site} não vinculada no jogo?); " +
                        "parei de resgatar nesta conta");
        }
        c.Cost = price;
        c.Points = balance;
        string? refusal = status?.ToString() ?? refused;
        if (redeemed)
        {
            c.Points = balance - price;
            c.RedeemedAt = DateTime.Now;
            note = $"{Site}: resgatei {title} em {channel}";
            Log?.Invoke($"{channel}: resgatou {title} ({price} pontos; sobraram {balance - price})");
        }
        else if (refusal is not null)
        {
            c.RetryAt = DateTime.Now + RetryWait;
            note = $"{Site}: o resgate em {channel} foi recusado (erro {refusal}); tento de novo em 30 min";
            Log?.Invoke($"{channel}: o resgate de {title} foi recusado (erro {refusal}; saldo {balance}); tento de novo em 30 min");
        }
        c.Text = $"{c.Points}/{price} pontos"
                 + (c.RedeemedAt is DateTime at ? $", resgatou {at:HH:mm}" : "")
                 + (refusal is not null && !redeemed ? $", resgate recusado ({refusal})" : "")
                 + (paused ? ", bônus pausado no canal" : "")
                 + bonus;
        return note;
    }
}
