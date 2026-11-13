namespace PokeIdleMulti;

/// <summary>Janela para editar os canais da Twitch e da Kick que ficam abertos em segundo plano, e o resgate dos pontos.</summary>
internal sealed class ChannelsDialog : Form
{
    private readonly UiField _twitch = new(multiline: true) { Dock = DockStyle.Fill };
    private readonly UiField _kick = new(multiline: true) { Dock = DockStyle.Fill };
    private readonly UiStepper _twitchLives = new() { Minimum = 0, Maximum = TwitchLive.MaxSlots };
    private readonly UiStepper _kickLives = new() { Minimum = 0, Maximum = KickLive.MaxSlots };
    private readonly List<(UiSelect Kick, UiSelect Twitch, UiSwitch Light, UiSwitch KickLight)> _redeem = new();
    private readonly UiSwitch _claim = new("Pegar o baú de bônus sozinho");
    private readonly ToolTip _tips = Ui.Tips(30000);

    // Na mesma ordem do enum KickReward, depois do "não resgatar".
    private static readonly object[] Rewards = { "Não resgatar", "+15% XP · 300", "+15% Captura · 500", "+15% Shiny · 1000" };

    private const string ListTip =
        "Um canal por linha, como aparece no endereço (twitch.tv/rodil → rodil). Use os streamers oficiais do painel de bônus " +
        "do jogo.\n\nO deck abre o player das primeiras da lista que estiverem no ar, até o número de \"Lives abertas por conta\" " +
        "(a primeira tem prioridade), e não deixa live offline aberta. Os pontos só vêm com a live tocando. Ao salvar, as lives " +
        "abertas reabrem com os canais novos.";

    private const string RedeemTip =
        "Confere o saldo de cada live aberta na conta (Kick a cada 5 min, Twitch a cada 2) e resgata 1 bônus se der (as horas " +
        "acumulam).\n\nSem a {0} vinculada no jogo ({1}) os pontos voltam: o deck para de resgatar nesta conta e avisa no menu Lives.";

    private const string LightTip =
        "Twitch leve: o player da Twitch abre parado, sem vídeo (fica só para o baú e o resgate), e quem assiste é o deck, sem " +
        "navegador, como os mineradores de pontos. Gasta quase nada. Os pontos que entram aparecem no menu Lives (+N/h); sem os " +
        "de assistir em 20 min, o deck avisa.";

    private const string KickLightTip =
        "Kick leve: o player da Kick fica em branco (abre só para resgatar), e quem assiste é o deck, sem navegador, mandando à " +
        "Kick o que o player manda. Rende igual e gasta quase nada. O saldo de cada live vai para o kick-leve.log, na pasta do deck.";

    /// <summary>
    /// <paramref name="accounts"/>, <paramref name="redeemKick"/> e <paramref name="redeemTwitch"/>: o nome de cada conta e o
    /// bônus que ela resgata sozinha com os pontos da Kick e da Twitch (null: não resgata). <paramref name="claimTwitch"/>:
    /// pegar o baú de bônus da Twitch. <paramref name="twitchLight"/> e <paramref name="kickLight"/>: a Twitch leve e a Kick
    /// leve (sem vídeo) de cada conta.
    /// </summary>
    public ChannelsDialog(IEnumerable<string> twitch, IEnumerable<string> kick, int twitchLives, int kickLives,
        IReadOnlyList<string> accounts, IReadOnlyList<KickReward?> redeemKick, IReadOnlyList<KickReward?> redeemTwitch, bool claimTwitch,
        IReadOnlyList<bool>? twitchLight = null, IReadOnlyList<bool>? kickLight = null)
    {
        Text = "Config das lives";
        Ui.Dialog(this);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _twitch.Text = string.Join(Environment.NewLine, twitch);
        _kick.Text = string.Join(Environment.NewLine, kick);
        _twitchLives.Value = Math.Clamp(twitchLives, 0, TwitchLive.MaxSlots);
        _kickLives.Value = Math.Clamp(kickLives, 0, KickLive.MaxSlots);
        _claim.Checked = claimTwitch;

        // Topo: uma linha; o resto fica no ⓘ.
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 28, WrapContents = false };
        var topText = Ui.Label("Um canal por linha, na ordem de prioridade. O deck abre as primeiras que estiverem no ar.", muted: true);
        topText.Margin = new Padding(0, 2, 0, 0);
        var topInfo = new UiInfo { Margin = new Padding(6, 4, 0, 0) };
        _tips.SetToolTip(topInfo, ListTip);
        _tips.SetToolTip(topText, ListTip);
        top.Controls.Add(topText);
        top.Controls.Add(topInfo);

        // As duas listas lado a lado; embaixo de cada uma, quantas lives abrem (e o baú, que é da Twitch).
        var twitchCard = ListCard("Twitch", UiTone.Twitch, _twitch, _twitchLives,
            Ui.Row(_claim, Ui.Label("+50 a cada ~15 min", muted: true, small: true)));
        var kickCard = ListCard("Kick", UiTone.Kick, _kick, _kickLives, null);
        twitchCard.Margin = new Padding(0, 0, 6, 0);
        kickCard.Margin = new Padding(6, 0, 0, 0);
        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.Controls.Add(twitchCard, 0, 0);
        lists.Controls.Add(kickCard, 1, 0);

        // Por conta: o bônus que ela resgata sozinha em cada site e as lives leves.
        var table = new UiTable { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = accounts.Count + 1 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 206));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        table.Controls.Add(Header("Conta", null), 0, 0);
        table.Controls.Add(Header("Resgatar na Kick", string.Format(RedeemTip, "Kick", "Bônus na Kick")), 1, 0);
        table.Controls.Add(Header("Resgatar na Twitch", string.Format(RedeemTip, "Twitch", "Bônus Twitch")), 2, 0);
        table.Controls.Add(Header("Twitch leve", LightTip), 3, 0);
        table.Controls.Add(Header("Kick leve", KickLightTip), 4, 0);
        for (int i = 0; i < accounts.Count; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var name = new Label
            {
                Text = accounts[i], AutoSize = false, Width = 100, Height = 20, AutoEllipsis = true, UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft, Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 0, 0),
            };
            _tips.SetToolTip(name, accounts[i]);
            var kickCombo = NewReward(i < redeemKick.Count ? redeemKick[i] : null);
            var twitchCombo = NewReward(i < redeemTwitch.Count ? redeemTwitch[i] : null);
            var light = Cell(new UiSwitch { Checked = twitchLight is not null && i < twitchLight.Count && twitchLight[i] }, LightTip);
            var kickLightBox = Cell(new UiSwitch { Checked = kickLight is not null && i < kickLight.Count && kickLight[i] }, KickLightTip);
            table.Controls.Add(name, 0, i + 1);
            table.Controls.Add(kickCombo, 1, i + 1);
            table.Controls.Add(twitchCombo, 2, i + 1);
            table.Controls.Add(light, 3, i + 1);
            table.Controls.Add(kickLightBox, 4, i + 1);
            _redeem.Add((kickCombo, twitchCombo, light, kickLightBox));
        }
        int tableHeight = table.Padding.Vertical + 32 + 40 * accounts.Count;

        var accountsCard = new UiCard { Dock = DockStyle.Bottom };
        var accountsHead = new Panel { Dock = DockStyle.Top, Height = 26 };
        var accountsTitle = Ui.Label("Por conta", bold: true);
        accountsTitle.AutoSize = false;
        accountsTitle.Dock = DockStyle.Fill;
        accountsTitle.TextAlign = ContentAlignment.MiddleLeft;
        var accountsNote = Ui.Label("vincule a Kick e a Twitch no jogo em cada conta que resgata", muted: true, small: true);
        accountsNote.AutoSize = false;
        accountsNote.Width = accountsNote.PreferredWidth + 4;
        accountsNote.Dock = DockStyle.Right;
        accountsNote.TextAlign = ContentAlignment.MiddleRight;
        accountsHead.Controls.Add(accountsTitle);
        accountsHead.Controls.Add(accountsNote);
        accountsCard.Controls.Add(table);
        accountsCard.Controls.Add(Ui.Gap(8, DockStyle.Top));
        accountsCard.Controls.Add(accountsHead);
        accountsCard.Height = accountsCard.Padding.Vertical + 26 + 8 + tableHeight;

        var ok = new UiButton("Salvar", UiLook.Primary) { DialogResult = DialogResult.OK };
        var cancel = new UiButton("Cancelar", UiLook.Outline) { DialogResult = DialogResult.Cancel };
        AcceptButton = ok;
        CancelButton = cancel;
        var footer = new UiFooter("As lives abertas reabrem ao salvar.", cancel, ok);

        // Os controles encaixam do último para o primeiro: o rodapé, depois o topo, a tabela embaixo e as listas no meio.
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 14) };
        body.Controls.Add(lists);
        body.Controls.Add(Ui.Gap(12, DockStyle.Bottom));
        body.Controls.Add(accountsCard);
        body.Controls.Add(Ui.Gap(4, DockStyle.Top));
        body.Controls.Add(top);
        Controls.Add(body);
        Controls.Add(footer);
        ClientSize = new Size(820, Math.Max(640, 380 + accountsCard.Height + footer.Height));

        // Sem isso o foco cai na lista da Twitch com tudo selecionado.
        ActiveControl = ok;
    }

    // Aberta com Show (o autoteste mostra um instante para o print): não pega o foco de quem está jogando.
    protected override bool ShowWithoutActivation => !Modal;

    private UiCard ListCard(string site, UiTone tone, UiField list, UiStepper lives, Panel? extra)
    {
        var card = new UiCard { Dock = DockStyle.Fill };
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, WrapContents = false };
        var pill = new UiPill(site, tone) { Margin = new Padding(0, 2, 8, 0) };
        var title = Ui.Label("canais, em ordem de prioridade", bold: true);
        title.Margin = new Padding(0, 3, 0, 0);
        head.Controls.Add(pill);
        head.Controls.Add(title);
        var count = Ui.Label("", muted: true, small: true);
        count.AutoSize = false;
        count.Width = 90;
        count.Dock = DockStyle.Right;
        count.TextAlign = ContentAlignment.MiddleRight;
        void Count() => count.Text = Parse(list.Text).Count switch { 0 => "nenhum", 1 => "1 canal", var n => $"{n} canais" };
        list.Box.TextChanged += (_, _) => Count();
        Count();
        var headRow = new Panel { Dock = DockStyle.Top, Height = 26 };
        headRow.Controls.Add(head);
        headRow.Controls.Add(count);
        head.Dock = DockStyle.Fill;
        _tips.SetToolTip(list.Box, ListTip);

        var livesLabel = Ui.Label("Lives abertas por conta");
        lives.Width = 96;
        _tips.SetToolTip(livesLabel, $"Quantas lives da {site} cada conta deixa abertas (de fábrica 2; 0 = nenhuma).");
        _tips.SetToolTip(lives, $"Quantas lives da {site} cada conta deixa abertas (de fábrica 2; 0 = nenhuma).");

        // Encaixam do último para o primeiro: o cabeçalho em cima, as linhas embaixo (o baú por último) e a lista no meio.
        card.Controls.Add(list);
        card.Controls.Add(Ui.Gap(8, DockStyle.Bottom));
        card.Controls.Add(Ui.Row(livesLabel, lives));
        if (extra is not null)
            card.Controls.Add(extra);
        card.Controls.Add(Ui.Gap(8, DockStyle.Top));
        card.Controls.Add(headRow);
        return card;
    }

    private Control Header(string text, string? tip)
    {
        var cell = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Ui.Head,
            Anchor = AnchorStyles.Left, Margin = new Padding(10, 0, 0, 0),
        };
        var label = Ui.Label(text, muted: true, small: true, bold: true);
        label.Margin = new Padding(0, 0, 0, 0);
        cell.Controls.Add(label);
        if (tip is not null)
        {
            var info = new UiInfo { Margin = new Padding(5, 1, 0, 0) };
            _tips.SetToolTip(info, tip);
            _tips.SetToolTip(label, tip);
            cell.Controls.Add(info);
        }
        return cell;
    }

    private T Cell<T>(T control, string tip) where T : Control
    {
        control.Anchor = AnchorStyles.Left;
        control.Margin = new Padding(12, 0, 0, 0);
        _tips.SetToolTip(control, tip);
        return control;
    }

    public List<string> TwitchChannels => Parse(_twitch.Text);
    public List<string> KickChannels => Parse(_kick.Text);
    public int TwitchLives => (int)_twitchLives.Value;
    public int KickLives => (int)_kickLives.Value;

    /// <summary>O bônus que cada conta resgata sozinha na Kick (null: não resgata).</summary>
    public KickReward?[] RedeemKick => _redeem.Select(r => Chosen(r.Kick)).ToArray();

    /// <summary>O bônus que cada conta resgata sozinha na Twitch (null: não resgata).</summary>
    public KickReward?[] RedeemTwitch => _redeem.Select(r => Chosen(r.Twitch)).ToArray();

    public bool ClaimTwitchBonus => _claim.Checked;

    /// <summary>A Twitch leve (sem vídeo) de cada conta.</summary>
    public bool[] TwitchLight => _redeem.Select(r => r.Light.Checked).ToArray();

    /// <summary>A Kick leve (sem vídeo) de cada conta.</summary>
    public bool[] KickLight => _redeem.Select(r => r.KickLight.Checked).ToArray();


    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();
        base.Dispose(disposing);
    }

    private UiSelect NewReward(KickReward? reward)
    {
        var combo = new UiSelect { Width = 192, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0) };
        combo.Items.AddRange(Rewards);
        combo.SelectedIndex = reward is KickReward r && Enum.IsDefined(r) ? (int)r + 1 : 0;
        return combo;
    }

    private static KickReward? Chosen(ComboBox combo) => combo.SelectedIndex > 0 ? (KickReward)(combo.SelectedIndex - 1) : null;

    // Aceita também o endereço colado inteiro (https://kick.com/yetz → yetz).
    private static List<string> Parse(string text) => text
        .Split(new[] { '\r', '\n', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(c => c.Trim().TrimEnd('/'))
        .Select(c => c[(c.LastIndexOf('/') + 1)..].TrimStart('@').ToLowerInvariant())
        .Where(c => c.Length > 0)
        .Distinct()
        .ToList();
}
