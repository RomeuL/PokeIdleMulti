using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PokeIdleMulti;

/// <summary>
/// O resgate em lote (menu Lives → Resgate em lote… (CUIDADO)): N de cada bônus do PokeIdle num canal da Twitch ou da Kick de
/// uma vez, com os pontos de uma conta do deck (o login do site fica no perfil dela). Mostra o saldo e o custo antes e não
/// começa sem saldo; o dono do canal tem pontos infinitos (∞) e não precisa de saldo. Resgata um por vez, de 1 a 2 s
/// entre um e outro, e para no primeiro erro do site. Os pedidos são os do resgate automático (<see cref="PointsRedeemer"/>),
/// de dentro de um player do site escondido no perfil da conta, aberto só enquanto a janela está aberta: funciona com o
/// canal offline.
/// </summary>
internal sealed class BulkRedeemDialog : Form
{
    private static readonly LiveSite[] Sites = { LiveSite.Twitch, LiveSite.Kick };
    private static readonly KickReward[] Kinds = { KickReward.Xp, KickReward.Capture, KickReward.Lure };
    private static readonly string[] Names = { "+15% XP", "+15% Captura", "+15% Shiny (Secret Lure)" };
    private static readonly string[] Short = { "XP", "Captura", "Shiny" };
    private static readonly int[] DefaultCosts = { 300, 500, 1000 };
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    // Os 3 bônus do PokeIdle entre os do canal (na ordem de Kinds; null: o canal não tem), já no mesmo formato nos 2
    // sites: { id, title, prompt, about (a descrição, só para achar o "PokeIdle"), cost, blocked }.
    private const string Pick =
        " const pick = all => a.words.map(w => { const word = new RegExp(w, 'i');" +
        "  return [...all].sort((x, y) => x.cost - y.cost).find(r => word.test(r.title) && /pokeidle/i.test(r.title + ' ' + (r.prompt || r.about || ''))) ?? null; });";

    // A API da Kick com o login da conta (o cookie session_token vai como Bearer), como o site e o resgate automático.
    // get(url) não falha: devolve o status (0: sem resposta) e o JSON quando deu certo.
    private const string KickApi =
        " const head = { Accept: 'application/json', 'x-app-platform': 'web', Authorization: 'Bearer ' + a.token };" +
        " const ch = a.api + '/api/v2/channels/' + encodeURIComponent(a.channel);" +
        " const get = async url => { try { const r = await fetch(url, { cache: 'no-cache', credentials: 'include', headers: head });" +
        "  return { status: r.status, json: r.ok ? await r.json().catch(() => null) : null }; }" +
        "  catch (e) { return { status: 0, json: null, fail: (e && e.message) || String(e) }; } };";

    // Twitch: o saldo e os bônus do canal, quem é a conta e se ela é o dono do canal (pontos infinitos: o saldo pode
    // nem vir, ou vir qualquer número).
    private const string TwitchReadBody =
        "  const j = await gql('query($login:String!){currentUser{id login} user(login:$login){id channel{self{communityPoints{balance}}' +" +
        "   ' communityPointsSettings{customRewards{id title prompt cost isEnabled isPaused isInStock cooldownExpiresAt}}}}}', { login: a.channel });" +
        "  if (j?.failed) return { error: j.failed };" +
        "  const user = j?.data?.user;" +
        "  if (!user?.channel) return { error: 'canal não encontrado na Twitch' };" +
        "  const me = j?.data?.currentUser;" +
        "  const owner = !!me?.id && me.id === user.id;" +
        "  const points = user.channel.self?.communityPoints?.balance;" +
        "  if (!owner && typeof points !== 'number') return { error: 'sem o saldo na resposta (a conta entrou na Twitch?)' };" +
        "  const all = (user.channel.communityPointsSettings?.customRewards || []).filter(r => r.isEnabled).map(r => ({ id: r.id," +
        "   title: r.title || '', prompt: r.prompt ?? null, cost: r.cost, blocked: r.isPaused ? 'pausado no canal'" +
        "    : !r.isInStock ? 'esgotado no canal' : r.cooldownExpiresAt ? 'em espera no canal' : null }));" +
        "  return { channelId: user.id, points: typeof points === 'number' ? points : null, owner, login: me?.login ?? null, rewards: pick(all) };";

    // Kick: o mesmo. O dono do canal: o /me do canal diz is_broadcaster, ou a conta (/api/v1/user) é o usuário do canal.
    private const string KickReadBody =
        "  const [me, info, mine, pts, rws] = await Promise.all([get(a.api + '/api/v1/user'), get(ch), get(ch + '/me')," +
        "   get(ch + '/points'), get(ch + '/rewards')]);" +
        "  if (rws.fail) return { error: 'sem resposta da Kick: ' + rws.fail };" +
        "  if (info.status === 404 || rws.status === 404) return { error: 'canal não encontrado na Kick' };" +
        "  const user = me.json;" +
        "  const owner = mine.json?.is_broadcaster === true" +
        "   || (user?.id != null && (user.id === info.json?.user_id || user.id === info.json?.user?.id))" +
        "   || (!!user?.streamer_channel?.slug && user.streamer_channel.slug === a.channel);" +
        "  const points = pts.json?.data?.points;" +
        "  if (!owner && typeof points !== 'number') return { error: 'sem o saldo na resposta (erro ' + pts.status + '; a conta entrou na Kick?)' };" +
        "  if (!rws.json) return { error: 'os bônus do canal: erro ' + rws.status };" +
        "  const all = (rws.json.data || []).filter(r => r.is_enabled).map(r => ({ id: String(r.id), title: r.title || '', prompt: null," +
        "   about: r.description || '', cost: r.cost, blocked: r.is_paused ? 'pausado no canal' : null }));" +
        "  return { channelId: a.channel, points: typeof points === 'number' ? points : null, owner, login: user?.username ?? null, rewards: pick(all) };";

    // 1 resgate na Twitch. O transactionID é novo a cada um (32 letras hexadecimais); o custo, o título e a descrição
    // (prompt) vão iguais aos do bônus no canal, senão a Twitch recusa (PROPERTIES_MISMATCH).
    private const string TwitchRedeemBody =
        "  const id = [...crypto.getRandomValues(new Uint8Array(16))].map(b => b.toString(16).padStart(2, '0')).join('');" +
        "  const r = await gql('mutation($i:RedeemCommunityPointsCustomRewardInput!){redeemCommunityPointsCustomReward(input:$i){redemption{id} error{code}}}'," +
        "   { i: { channelID: a.channelId, cost: a.cost, prompt: a.prompt, rewardID: a.rewardId, title: a.title, transactionID: id } });" +
        "  const p = r?.data?.redeemCommunityPointsCustomReward;" +
        "  if (p && !p.error && !r.errors) return { redeemed: true };" +
        "  return { error: fail(r, p) };";

    // 1 resgate na Kick. O transaction_id é um ULID novo, como o site gera: o tempo em base 32 e mais 16 letras ao acaso.
    // Recusado, o erro é o status e a mensagem da Kick.
    private const string KickRedeemBody =
        "  const abc = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'; let t = Date.now(), id = '';" +
        "  for (let i = 0; i < 10; i++) { id = abc[t % 32] + id; t = Math.floor(t / 32); }" +
        "  for (let i = 0; i < 16; i++) id += abc[Math.floor(Math.random() * 32)];" +
        "  const r = await fetch(ch + '/rewards/' + encodeURIComponent(a.rewardId) + '/redeem', { method: 'POST', cache: 'no-cache'," +
        "   credentials: 'include', headers: { ...head, 'Content-Type': 'application/json' }, body: JSON.stringify({ transaction_id: id, message: '' }) });" +
        "  if (r.ok) return { redeemed: true };" +
        "  const j = await r.json().catch(() => null);" +
        "  return { error: r.status + (typeof j?.message === 'string' && j.message ? ' ' + j.message.slice(0, 80) : '') };";

    private readonly CoreWebView2Environment _env;
    private readonly IReadOnlyList<(string Name, string Profile)> _accounts;
    private readonly Func<LiveSite, string> _api;
    private readonly Func<LiveSite, string, string> _hostUrl;
    private readonly Action<CoreWebView2>? _setup;
    private readonly Action<int, LiveSite, string> _log;

    private readonly UiSelect _account = new() { Width = 300 };
    private readonly UiSegment _site = new("Twitch", "Kick");
    private readonly UiField _channel = new() { Width = 200 };
    private readonly UiButton _check = new("Conferir saldo", UiLook.Outline, compact: true);
    private readonly UiStepper[] _counts = Kinds.Select(_ => new UiStepper { Minimum = 0, Maximum = 999, Width = 104 }).ToArray();
    private readonly Label[] _costs = Kinds.Select(_ => new Label { AutoSize = true, ForeColor = Theme.Muted, UseMnemonic = false }).ToArray();
    private readonly Label _balance = new() { AutoSize = false, Height = 22, UseMnemonic = false, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _total = new() { AutoSize = false, Height = 38, UseMnemonic = false, Font = Ui.Bold };
    private readonly UiList _progress = new(rowHeight: 22) { MultiSelect = false, FillLast = true, HeaderStyle = ColumnHeaderStyle.None };
    private readonly UiButton _redeem = new("Resgatar", UiLook.DangerFill);
    private readonly UiButton _cancel = new("Cancelar", UiLook.Outline);
    private readonly ToolTip _tips = Ui.Tips(30000);

    private sealed record Reward(string Id, string Title, string? Prompt, int Cost, string? Blocked);

    /// <summary>
    /// O que foi conferido: o login da conta no site, o canal, o saldo (null no dono do canal, quando o site nem manda),
    /// se a conta é o dono do canal (pontos infinitos) e os bônus.
    /// </summary>
    private sealed record Context(int Account, LiveSite Site, string Channel, string Token, string? Device, string ChannelId,
        int? Points, bool Owner, string? Login, Reward?[] Rewards);

    private WebView2? _host;
    private Task<CoreWebView2?>? _hostTask;
    private (int Account, LiveSite Site) _hostKey = (-1, LiveSite.Twitch);
    private Context? _context;
    private string _note = "";
    private int _checks;
    private Task<bool>? _checkTask;
    private (int Account, LiveSite Site, string Channel) _checkKey;
    private bool _busy;
    private bool _cancelRequested;
    private bool _closeWhenDone;

    /// <summary>
    /// <paramref name="accounts"/>: o nome e o perfil de cada conta do deck; <paramref name="account"/>, a que vem escolhida.
    /// <paramref name="api"/>: a API de cada site; <paramref name="hostUrl"/>: a página do site (o player do canal) onde
    /// os pedidos rodam; <paramref name="setup"/>: o que o autoteste liga nela. <paramref name="log"/>: cada linha para o
    /// registro dos resgates, com o número da conta e o site.
    /// </summary>
    public BulkRedeemDialog(CoreWebView2Environment env, IReadOnlyList<(string Name, string Profile)> accounts, int account,
        Func<LiveSite, string> api, Func<LiveSite, string, string> hostUrl, Action<CoreWebView2>? setup, Action<int, LiveSite, string> log)
    {
        _env = env;
        _accounts = accounts;
        _api = api;
        _hostUrl = hostUrl;
        _setup = setup;
        _log = log;

        Text = "Resgate em lote (pontos da Twitch e da Kick)";
        Ui.Window(this);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(576, 620);
        MinimumSize = new Size(520, 560);

        // O aviso no topo: gasta na hora.
        var warn = new Label
        {
            Dock = DockStyle.Top, Height = 34, ForeColor = Color.FromArgb(243, 214, 107), UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft,
            Text = "⚠  Gasta os pontos na hora: um por vez, de 1 a 2 s entre eles; para no primeiro erro do site.",
        };
        _tips.SetToolTip(warn, "Resgata de uma vez quantos você quiser de cada bônus do PokeIdle no canal, com os pontos da conta escolhida " +
                               "(o login do site nela). Um por vez, de 1 a 2 s entre eles; para no primeiro erro do site. O dono do canal " +
                               "tem pontos infinitos e não precisa de saldo.");

        foreach (var (name, _) in accounts)
            _account.Items.Add(name);
        _account.SelectedIndex = Math.Clamp(account, 0, accounts.Count - 1);
        _channel.Text = "biscoitao";

        // De onde: a conta, o site e o canal (com o Conferir saldo grudado); o saldo embaixo.
        var where = new UiCard { Dock = DockStyle.Top, Height = 158 };
        Put(where, Caption("Conta"), 12, 18);
        Put(where, _account, 84, 12);
        Put(where, Caption("Site"), 12, 54);
        Put(where, _site, 84, 48);
        Put(where, Caption("Canal"), 12, 90);
        Put(where, _channel, 84, 84);
        Put(where, _check, _channel.Right + 8, 84);
        Put(where, _balance, 12, 122);
        _balance.Width = 520;

        // Os bônus: quantos de cada, o custo e o total.
        var rewards = new UiCard { Dock = DockStyle.Top, Height = 12 + 36 * Kinds.Length + 50 };
        for (int k = 0; k < Kinds.Length; k++)
        {
            Put(rewards, Caption(Names[k]), 12, 18 + 36 * k);
            Put(rewards, _counts[k], 190, 12 + 36 * k);
            Put(rewards, _costs[k], 306, 18 + 36 * k);
            _counts[k].ValueChanged += (_, _) => UpdateView();
        }
        Put(rewards, _total, 12, 12 + 36 * Kinds.Length + 4);
        _total.Width = 520;

        var progressTitle = Ui.Label("Andamento", muted: true, small: true);
        progressTitle.AutoSize = false;
        progressTitle.Dock = DockStyle.Top;
        progressTitle.Height = 22;
        _progress.Columns.Add("Andamento", 0);

        var footer = new UiFooter(null, _cancel, _redeem);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 10) };
        body.Controls.Add(new UiFrame(_progress) { Dock = DockStyle.Fill });
        body.Controls.Add(progressTitle);
        body.Controls.Add(Ui.Gap(10, DockStyle.Top));
        body.Controls.Add(rewards);
        body.Controls.Add(Ui.Gap(10, DockStyle.Top));
        body.Controls.Add(where);
        body.Controls.Add(warn);

        // Enter confere o saldo; resgatar é sempre no clique (e pergunta antes).
        AcceptButton = _check;

        _check.Click += async (_, _) => await CheckAsync();
        _account.SelectedIndexChanged += async (_, _) => await CheckAsync();
        _site.SelectedIndexChanged += async (_, _) => await CheckAsync();
        _channel.Box.TextChanged += (_, _) =>
        {
            _note = "Confira o saldo deste canal (Conferir saldo).";
            UpdateView();
        };
        _redeem.Click += async (_, _) => await ConfirmAndRunAsync();
        // Parado, fecha a janela; resgatando, para depois do resgate da vez.
        _cancel.Click += (_, _) =>
        {
            if (_busy)
                Cancel();
            else
                Close();
        };
        Shown += async (_, _) => await CheckAsync();

        Controls.Add(body);
        Controls.Add(footer);
        UpdateView();
    }

    // Aberta pelo deck sem ele estar em primeiro plano (o autoteste): não pega o foco de quem está jogando.
    protected override bool ShowWithoutActivation => ActiveForm is null;

    private static void Put(Control parent, Control control, int x, int y)
    {
        control.Location = new Point(x, y);
        parent.Controls.Add(control);
    }

    /// <summary>A conta escolhida (índice nas contas do deck).</summary>
    public int Account
    {
        get => _account.SelectedIndex;
        set => _account.SelectedIndex = value;
    }

    /// <summary>O site escolhido (Twitch ou Kick).</summary>
    public LiveSite Platform
    {
        get => Sites[Math.Max(0, _site.SelectedIndex)];
        set => _site.SelectedIndex = Array.IndexOf(Sites, value);
    }

    /// <summary>O canal escolhido, como no endereço (aceita o link colado: twitch.tv/biscoitao → biscoitao).</summary>
    public string Channel
    {
        get
        {
            var text = _channel.Text.Trim().TrimEnd('/');
            return text[(text.LastIndexOf('/') + 1)..].TrimStart('@').ToLowerInvariant();
        }
        set => _channel.Text = value;
    }

    /// <summary>Quantos de cada bônus resgatar.</summary>
    public void SetCounts(int xp, int capture, int lure)
    {
        _counts[0].Value = xp;
        _counts[1].Value = capture;
        _counts[2].Value = lure;
    }

    /// <summary>A pausa entre um resgate e o seguinte, em ms (o autoteste encurta).</summary>
    public (int Min, int Max) PauseMs { get; set; } = (1000, 2000);

    /// <summary>Dá para resgatar o escolhido agora (saldo conferido e suficiente, ou o dono do canal, e o canal com os bônus).</summary>
    public bool CanRedeem => !_busy && Refusal() is null;

    /// <summary>O saldo e o total mostrados agora.</summary>
    public string StatusText => _balance.Text + Environment.NewLine + _total.Text;

    /// <summary>As linhas do andamento do último resgate.</summary>
    public IReadOnlyList<string> ProgressLines => _progress.Items.Cast<ListViewItem>().Select(i => i.Text).ToList();

    /// <summary>Quantos o último resgate fez, e por que parou antes do fim (null: fez todos).</summary>
    public int Done { get; private set; }
    public string? Stop { get; private set; }

    /// <summary>Para o resgate depois do da vez.</summary>
    public void Cancel() => _cancelRequested = true;

    // O que foi conferido vale só para a conta, o site e o canal escolhidos agora.
    private Context? Current => _context is Context c && c.Account == Account && c.Site == Platform && c.Channel == Channel ? c : null;

    private int Cost(int k) => Current?.Rewards[k]?.Cost ?? DefaultCosts[k];

    private int[] Counts => _counts.Select(c => (int)c.Value).ToArray();

    private int Total() => Enumerable.Range(0, Kinds.Length).Sum(k => Counts[k] * Cost(k));

    private static string N(int value) => value.ToString("N0", PtBr);

    private static Label Caption(string text) => Ui.Label(text, muted: true);

    // "10 XP + 10 Captura + 10 Shiny"
    private static string Describe(int[] counts) =>
        string.Join(" + ", counts.Select((n, k) => n > 0 ? $"{n} {Short[k]}" : null).OfType<string>());

    // O saldo para o registro: "saldo 700", ou o dono do canal (pontos infinitos).
    private static string LogBalance(int? points) => points is int p ? $"saldo {p}" : "dono do canal, pontos infinitos";

    /// <summary>Por que não dá para resgatar o escolhido agora (null: dá).</summary>
    private string? Refusal()
    {
        if (Current is not Context c)
            return "Confira o saldo antes.";
        if (Total() == 0)
            return "Escolha quantos de cada bônus.";
        for (int k = 0; k < Kinds.Length; k++)
        {
            if (Counts[k] == 0)
                continue;
            if (c.Rewards[k] is not Reward reward)
                return $"O canal {c.Channel} na {c.Site} não tem o bônus {Names[k]} do PokeIdle.";
            if (reward.Blocked is string why)
                return $"{Names[k]}: {why}.";
        }
        // O dono do canal tem pontos infinitos.
        if (c.Owner)
            return null;
        int points = c.Points ?? 0;
        return Total() > points ? $"Saldo insuficiente: precisa de {N(Total())} pontos e a conta tem {N(points)}." : null;
    }

    private void UpdateView()
    {
        if (IsDisposed)
            return;
        var c = Current;
        for (int k = 0; k < Kinds.Length; k++)
        {
            var reward = c?.Rewards[k];
            string state = c is null ? "" : reward is null ? " (o canal não tem)" : reward.Blocked is string why ? $" ({why})" : "";
            _costs[k].Text = $"× {N(Cost(k))} = {N(Counts[k] * Cost(k))} pontos{state}";
        }
        _balance.Text = c is null ? _note
            : c.Owner ? $"{c.Login ?? "A conta"} é o dono do canal {c.Channel} na {c.Site}: pontos infinitos (∞)."
            : $"{c.Login ?? "A conta"} tem {N(c.Points ?? 0)} pontos em {c.Channel} na {c.Site}.";
        string? refusal = _busy || c is null ? null : Refusal();
        _total.Text = $"Total: {N(Total())} pontos"
                      + (c is null ? ""
                          : c.Owner ? " (dono do canal: não precisa de saldo)"
                          : Total() <= (c.Points ?? 0) ? $" (sobram {N((c.Points ?? 0) - Total())})" : "")
                      + (refusal is not null ? Environment.NewLine + refusal : "");
        _total.ForeColor = refusal is not null ? Color.FromArgb(255, 143, 130) : c is null ? Theme.Text : Color.FromArgb(126, 226, 168);
        _balance.ForeColor = c is null ? Theme.Muted : Theme.Text;
        string verb = Total() > 0 ? $"Resgatar {N(Total())} pontos" : "Resgatar";
        if (_redeem.Text != verb)
        {
            _redeem.Text = verb;
            _redeem.Width = Math.Max(84, TextRenderer.MeasureText(verb, Ui.Font, Size.Empty, Ui.Measure).Width + 28);
        }
        _redeem.Enabled = !_busy && Refusal() is null;
        _check.Enabled = _account.Enabled = _site.Enabled = _channel.Enabled = !_busy;
        foreach (var count in _counts)
            count.Enabled = !_busy;
    }

    private void Add(string line)
    {
        if (IsDisposed)
            return;
        var item = _progress.Items.Add(line);
        if (line.Contains("erro", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Parou"))
            item.ForeColor = Color.FromArgb(243, 214, 107);
        _progress.FitLastColumn();
        item.EnsureVisible();
    }

    /// <summary>
    /// Lê o saldo e os bônus do canal escolhido, com o login da conta escolhida no site escolhido. Devolve se deu. Pedir
    /// de novo enquanto a mesma conferência anda (ao abrir a janela, Enter, o botão) espera por ela.
    /// </summary>
    public Task<bool> CheckAsync()
    {
        if (_busy || IsDisposed)
            return Task.FromResult(false);
        (int Account, LiveSite Site, string Channel) key = (Account, Platform, Channel);
        if (_checkTask is { IsCompleted: false } && _checkKey == key)
            return _checkTask;
        _checkKey = key;
        return _checkTask = ReadNowAsync(key.Account, key.Site, key.Channel);
    }

    private async Task<bool> ReadNowAsync(int account, LiveSite site, string channel)
    {
        int check = ++_checks;
        _context = null;
        _note = channel.Length == 0 ? "Escreva o canal." : $"Conferindo o saldo em {channel} na {site}…";
        UpdateView();
        if (channel.Length == 0)
            return false;
        var (context, error) = await ReadAsync(account, site, channel);
        // Mudou a conta, o site ou o canal no meio: vale a conferência mais nova.
        if (check != _checks || IsDisposed)
            return false;
        _context = context;
        _note = error ?? "";
        UpdateView();
        return context is not null;
    }

    private async Task ConfirmAndRunAsync()
    {
        if (_busy || Current is not Context c || Refusal() is not null)
            return;
        bool answer = DeckBox.Confirm(this,
            $"Resgatar {Describe(Counts)} em {c.Channel} na {c.Site} com a conta {_accounts[Account].Name}" +
            (c.Login is null ? "" : $" ({c.Site}: {c.Login})") + "?\n\n" +
            (c.Owner ? $"É o dono do canal (pontos infinitos): {N(Total())} pontos em bônus."
                : $"Gasta {N(Total())} dos {N(c.Points ?? 0)} pontos."),
            Text, "Resgatar", danger: true);
        if (answer)
            await RunAsync();
    }

    /// <summary>
    /// Resgata o escolhido, um por vez, revezando os bônus (XP, Captura, Shiny, XP…): lê o saldo de novo na hora (não
    /// começa sem saldo, a não ser o dono do canal), para no primeiro erro do site ou no Cancelar, e no fim lê o saldo
    /// outra vez. Cada resgate e cada erro vão para o registro dos resgates.
    /// </summary>
    public async Task RunAsync()
    {
        if (_busy || IsDisposed)
            return;
        _busy = true;
        _cancelRequested = false;
        Done = 0;
        Stop = null;
        ++_checks;
        _progress.Items.Clear();
        int account = Account;
        var site = Platform;
        string channel = Channel;
        var counts = Counts;
        UpdateView();
        try
        {
            Add($"Conferindo o saldo em {channel} na {site}…");
            var (context, error) = await ReadAsync(account, site, channel);
            if (IsDisposed)
                return;
            _context = context;
            _note = error ?? "";
            if (context is null || Refusal() is string refusal)
            {
                Stop = error ?? Refusal();
                Add($"Não comecei: {Stop}");
                return;
            }

            var queue = new List<int>();
            for (int round = 0; round < counts.Max(); round++)
                for (int k = 0; k < Kinds.Length; k++)
                    if (round < counts[k])
                        queue.Add(k);
            int total = queue.Sum(k => context.Rewards[k]!.Cost);
            // O saldo que sobra a cada resgate; o dono do canal não tem (pontos infinitos).
            int? points = context.Owner ? null : context.Points ?? 0;
            Add(points is int start
                ? $"Saldo {N(start)}: vou resgatar {Describe(counts)} ({N(total)} pontos)."
                : $"Dono do canal (pontos infinitos): vou resgatar {Describe(counts)} ({N(total)} pontos).");
            _log(account, site, $"{channel}: resgate em lote de {Describe(counts)} ({total} pontos; {LogBalance(points)})");

            for (int n = 0; n < queue.Count; n++)
            {
                if (n > 0)
                    await Task.Delay(Random.Shared.Next(PauseMs.Min, PauseMs.Max + 1));
                if (_cancelRequested || IsDisposed)
                {
                    Stop = "cancelado";
                    break;
                }
                int k = queue[n];
                var reward = context.Rewards[k]!;
                var core = await HostAsync(account, site, channel);
                var result = core is null ? null : await EvalAsync(core, Script(site,
                    site == LiveSite.Kick ? KickRedeemBody : TwitchRedeemBody, new
                    {
                        api = _api(site), clientId = PointsRedeemer.TwitchClientId, token = context.Token, device = context.Device,
                        channel, channelId = context.ChannelId, rewardId = reward.Id, cost = reward.Cost, prompt = reward.Prompt,
                        title = reward.Title,
                    }));
                string? code = result is not JsonElement r ? $"a página da {site} não respondeu"
                    : r.TryGetProperty("redeemed", out var ok) && ok.ValueKind == JsonValueKind.True ? null
                    : r.TryGetProperty("error", out var e) && e.GetString() is string why ? why : "sem resposta";
                string step = $"{n + 1}/{queue.Count} {Names[k]}";
                if (code is null)
                {
                    Done++;
                    if (points is int before)
                    {
                        points = before - reward.Cost;
                        Add($"{step}: resgatado (sobram {N(before - reward.Cost)})");
                        _log(account, site, $"{channel}: resgatou {reward.Title} (em lote, {n + 1} de {queue.Count}; {reward.Cost} pontos; " +
                                            $"sobraram {before - reward.Cost})");
                    }
                    else
                    {
                        Add($"{step}: resgatado");
                        _log(account, site, $"{channel}: resgatou {reward.Title} (em lote, {n + 1} de {queue.Count}; {reward.Cost} pontos; " +
                                            "dono do canal)");
                    }
                }
                else
                {
                    Stop = $"erro {code}";
                    Add($"{step}: recusado, erro {code}. Parei aqui.");
                    _log(account, site, $"{channel}: o resgate em lote de {reward.Title} foi recusado (erro {code}; {LogBalance(points)}); " +
                                        $"parei no {n + 1} de {queue.Count}");
                    break;
                }
            }
            if (Stop == "cancelado")
                Add("Cancelado.");

            // O saldo de verdade no fim (o resgate não devolve o saldo).
            var (after, afterError) = await ReadAsync(account, site, channel);
            if (IsDisposed)
                return;
            _context = after;
            _note = afterError ?? "";
            string end = $"{Done} de {queue.Count}" + (Stop is null ? "" : $" ({Stop})");
            string shown = after is null ? $"não deu para ler ({afterError})" : after.Owner ? "∞ (dono do canal)" : N(after.Points ?? 0);
            Add($"Fim: {end}. Saldo agora: {shown}.");
            _log(account, site, $"{channel}: resgate em lote terminou: {end}; " +
                                $"saldo {(after is null ? "?" : after.Owner ? "∞ (dono do canal)" : (after.Points ?? 0).ToString())}");
        }
        catch (Exception ex)
        {
            Stop ??= ex.Message;
            Add($"Parou: {ex.Message}");
            _log(account, site, $"{channel}: o resgate em lote parou ({ex.Message}; {Done} feitos)");
        }
        finally
        {
            _busy = false;
            if (_closeWhenDone && !IsDisposed)
                BeginInvoke(Close);
            else
                UpdateView();
        }
    }

    /// <summary>O login da conta no site, o saldo, se é o dono do canal e os bônus do canal (ou o que deu errado).</summary>
    private async Task<(Context? Context, string? Error)> ReadAsync(int account, LiveSite site, string channel)
    {
        try
        {
            if (await HostAsync(account, site, channel) is not CoreWebView2 core)
                return (null, $"A página da {site} não abriu (sem internet?).");
            string? token, device = null;
            if (site == LiveSite.Kick)
            {
                // O cookie session_token, que o site manda como "Bearer" (no cookie ele vem com o | escapado).
                var cookies = await core.CookieManager.GetCookiesAsync("https://kick.com");
                var raw = cookies.FirstOrDefault(c => c.Name == "session_token")?.Value;
                token = string.IsNullOrEmpty(raw) ? null : Uri.UnescapeDataString(raw);
            }
            else
            {
                var cookies = await core.CookieManager.GetCookiesAsync("https://www.twitch.tv");
                token = cookies.FirstOrDefault(c => c.Name == "auth-token")?.Value;
                device = cookies.FirstOrDefault(c => c.Name == "unique_id")?.Value;
                if (string.IsNullOrEmpty(device))
                    device = null;
            }
            if (string.IsNullOrEmpty(token))
                return (null, $"Esta conta não entrou na {site} (entre pelas lives dela: Lives → Mostrar as lives).");
            var result = await EvalAsync(core, Script(site, site == LiveSite.Kick ? KickReadBody : TwitchReadBody, new
            {
                api = _api(site), clientId = PointsRedeemer.TwitchClientId, token, device, channel, words = Kinds.Select(PointsRedeemer.Word),
            }));
            if (result is not JsonElement root)
                return (null, $"A página da {site} não respondeu.");
            if (root.TryGetProperty("error", out var error))
                return (null, $"Não deu para ler o saldo: {error.GetString()}.");
            bool owner = root.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.True;
            int? points = root.TryGetProperty("points", out var pt) && pt.ValueKind == JsonValueKind.Number && pt.TryGetInt32(out int pv) ? pv : null;
            if (!owner && points is null)
                return (null, "Não deu para ler o saldo: sem o saldo na resposta.");
            var rewards = root.GetProperty("rewards").EnumerateArray().Select(x => x.ValueKind != JsonValueKind.Object ? null : new Reward(
                x.GetProperty("id").GetString()!, x.GetProperty("title").GetString() ?? "",
                x.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                x.GetProperty("cost").GetInt32(),
                x.TryGetProperty("blocked", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null)).ToArray();
            string? login = root.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            return (new Context(account, site, channel, token, device, root.GetProperty("channelId").GetString()!,
                points, owner, login, rewards), null);
        }
        catch (Exception ex)
        {
            // Resposta fora do esperado, ou a página fechou no meio.
            return (null, $"Não deu para ler o saldo ({ex.Message}).");
        }
    }

    private static string Script(LiveSite site, string body, object args) =>
        "(async a => {" + (site == LiveSite.Kick ? KickApi : PointsRedeemer.TwitchGql) + Pick +
        " try {" + body +
        " } catch (e) { return { error: 'sem resposta da " + site + ": ' + (e && e.message || e) }; }" +
        "})(" + JsonSerializer.Serialize(args) + ").then(o => JSON.stringify(o))";

    /// <summary>Roda o script na página do site, esperando a promessa dele (até 30 s), e devolve o JSON que ele entrega.</summary>
    private static async Task<JsonElement?> EvalAsync(CoreWebView2 core, string script)
    {
        try
        {
            var call = core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }));
            if (await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))) != call)
                return null;
            using var doc = JsonDocument.Parse(await call);
            if (doc.RootElement.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(text.GetString()!);
                return inner.RootElement.Clone();
            }
        }
        catch
        {
            // A página fechou ou recarregou no meio.
        }
        return null;
    }

    /// <summary>
    /// A página do site escondida no perfil da conta, onde os pedidos rodam (o player do canal: abre mesmo offline).
    /// Uma por vez: trocar de conta ou de site fecha a anterior.
    /// </summary>
    private Task<CoreWebView2?> HostAsync(int account, LiveSite site, string channel)
    {
        if (_hostTask is not null && _hostKey == (account, site) && !(_hostTask.IsCompleted && _hostTask.Result is null))
            return _hostTask;
        _host?.Dispose();
        _host = null;
        _hostKey = (account, site);
        return _hostTask = OpenHostAsync(account, site, channel);
    }

    private async Task<CoreWebView2?> OpenHostAsync(int account, LiveSite site, string channel)
    {
        if (IsDisposed)
            return null;
        var view = new WebView2 { Visible = false, DefaultBackgroundColor = Theme.Back };
        _host = view;
        Controls.Add(view);
        try
        {
            var options = _env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = _accounts[account].Profile;
            _ = view.Handle;
            await view.EnsureCoreWebView2Async(_env, options);
            var core = view.CoreWebView2;
            core.IsMuted = true;
            core.Settings.IsStatusBarEnabled = false;
            _setup?.Invoke(core);
            var loaded = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            core.Navigate(_hostUrl(site, channel));
            if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(30))) == loaded.Task && loaded.Task.Result
                && _host == view && !view.IsDisposed)
                return core;
        }
        catch
        {
            // A janela fechou ou trocou de conta no meio.
        }
        if (_host == view)
            _host = null;
        view.Dispose();
        return null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Fechar no meio do resgate: termina o da vez, para, e aí fecha.
        if (_busy)
        {
            e.Cancel = true;
            _closeWhenDone = true;
            Cancel();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _host?.Dispose();
        _host = null;
        base.OnFormClosed(e);
    }
}
