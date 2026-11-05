namespace PokeIdleMulti;

/// <summary>Janela para editar os canais da Twitch e da Kick que ficam abertos em segundo plano, e o resgate dos pontos.</summary>
internal sealed class ChannelsDialog : Form
{
    private readonly TextBox _twitch = NewList();
    private readonly TextBox _kick = NewList();
    private readonly NumericUpDown _twitchLives = new() { Minimum = 0, Maximum = TwitchLive.MaxSlots, Width = 60 };
    private readonly NumericUpDown _kickLives = new() { Minimum = 0, Maximum = KickLive.MaxSlots, Width = 60 };
    private readonly List<(ComboBox Kick, ComboBox Twitch, CheckBox Light, CheckBox KickLight)> _redeem = new();
    private readonly CheckBox _claim = new()
    {
        Text = "Pegar sozinho o baú de bônus da Twitch (+50 pontos a cada ~15 min)",
        Dock = DockStyle.Bottom, Height = 28,
    };
    private readonly ToolTip _tips = new();

    // Na mesma ordem do enum KickReward, depois do "não resgatar".
    private static readonly object[] Rewards = { "Não resgatar", "+15% XP (300 pontos)", "+15% Captura (500 pontos)", "+15% Shiny (1000 pontos)" };

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
        Icon = Theme.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 700);
        Padding = new Padding(12);

        _twitch.Text = string.Join(Environment.NewLine, twitch);
        _kick.Text = string.Join(Environment.NewLine, kick);
        _twitchLives.Value = Math.Clamp(twitchLives, 0, TwitchLive.MaxSlots);
        var mode = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        mode.Controls.Add(new Label { Text = "Lives da Twitch abertas em cada conta:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        mode.Controls.Add(_twitchLives);
        mode.Controls.Add(new Label { Text = "(de fábrica 2; 0 = nenhuma)", AutoSize = true, Margin = new Padding(4, 7, 0, 0) });
        _kickLives.Value = Math.Clamp(kickLives, 0, KickLive.MaxSlots);
        var lives = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        lives.Controls.Add(new Label { Text = "Lives da Kick abertas em cada conta:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        lives.Controls.Add(_kickLives);
        lives.Controls.Add(new Label { Text = "(de fábrica 2; 0 = nenhuma)", AutoSize = true, Margin = new Padding(4, 7, 0, 0) });

        // Uma linha por conta: o bônus que ela resgata sozinha em cada site.
        var redeemRows = new TableLayoutPanel { Dock = DockStyle.Bottom, ColumnCount = 5, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        redeemRows.Controls.Add(new Label { Text = "Resgatar sozinho:", AutoSize = true, Margin = new Padding(0, 6, 0, 2) }, 0, 0);
        redeemRows.Controls.Add(new Label { Text = "com os pontos da Kick", AutoSize = true, Margin = new Padding(3, 6, 0, 2) }, 1, 0);
        redeemRows.Controls.Add(new Label { Text = "com os pontos da Twitch", AutoSize = true, Margin = new Padding(3, 6, 0, 2) }, 2, 0);
        var lightHeader = new Label { Text = "Twitch leve", AutoSize = true, Margin = new Padding(3, 6, 0, 2) };
        redeemRows.Controls.Add(lightHeader, 3, 0);
        const string lightTip = "Twitch leve: o player da Twitch abre parado, sem vídeo (fica só para o baú e o resgate), e quem assiste é " +
                                "o deck, sem navegador, como os mineradores de pontos. Gasta quase nada. Os pontos que entram aparecem no " +
                                "menu Lives (+N/h); sem os de assistir em 20 min, o deck avisa";
        _tips.SetToolTip(lightHeader, lightTip);
        var kickLightHeader = new Label { Text = "Kick leve", AutoSize = true, Margin = new Padding(3, 6, 0, 2) };
        redeemRows.Controls.Add(kickLightHeader, 4, 0);
        const string kickLightTip = "Kick leve (em teste): o player da Kick fica em branco (abre só para resgatar), e quem assiste é o " +
                                    "deck, sem navegador, mandando à Kick o que o player manda. Gasta quase nada. Ligue numa conta e " +
                                    "compare os pontos por hora com uma conta com o player (kick-leve.log, na pasta do deck)";
        _tips.SetToolTip(kickLightHeader, kickLightTip);
        for (int i = 0; i < accounts.Count; i++)
        {
            var name = new Label { Text = accounts[i], AutoSize = false, Width = 130, Height = 23, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            var kickCombo = NewReward(i < redeemKick.Count ? redeemKick[i] : null);
            var twitchCombo = NewReward(i < redeemTwitch.Count ? redeemTwitch[i] : null);
            var light = new CheckBox { AutoSize = true, Margin = new Padding(24, 5, 0, 0), Checked = twitchLight is not null && i < twitchLight.Count && twitchLight[i] };
            var kickLightBox = new CheckBox { AutoSize = true, Margin = new Padding(20, 5, 0, 0), Checked = kickLight is not null && i < kickLight.Count && kickLight[i] };
            _tips.SetToolTip(kickLightBox, kickLightTip);
            _tips.SetToolTip(kickCombo, "Sem a Kick vinculada no jogo (Bônus na Kick) os pontos voltam: o deck para de resgatar nesta conta e avisa no menu Lives");
            _tips.SetToolTip(twitchCombo, "Sem a Twitch vinculada no jogo (Bônus Twitch) os pontos voltam: o deck para de resgatar nesta conta e avisa no menu Lives");
            _tips.SetToolTip(light, lightTip);
            redeemRows.Controls.Add(name, 0, i + 1);
            redeemRows.Controls.Add(kickCombo, 1, i + 1);
            redeemRows.Controls.Add(twitchCombo, 2, i + 1);
            redeemRows.Controls.Add(light, 3, i + 1);
            redeemRows.Controls.Add(kickLightBox, 4, i + 1);
            _redeem.Add((kickCombo, twitchCombo, light, kickLightBox));
        }
        var redeemHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            Text = "Confere o saldo de cada live aberta na conta (Kick a cada 5 min, Twitch a cada 2) e resgata 1 bônus se " +
                   "der (as horas acumulam). Vincule a Kick e a Twitch no jogo em cada conta que resgata.",
        };
        _claim.Checked = claimTwitch;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 100,
            Text = "Um canal por linha, como aparece no endereço (twitch.tv/rodil → rodil). Use os streamers " +
                   "oficiais do painel de bônus do jogo. Ao salvar, as lives abertas reabrem com os canais novos.\n" +
                   "O deck abre o player das primeiras da lista que estiverem no ar, até o número escolhido embaixo " +
                   "(a primeira tem prioridade), e não deixa live offline aberta. Os pontos só vêm com a live tocando.",
        };

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        columns.Controls.Add(new Label { Text = "Twitch (em ordem de prioridade)", AutoSize = true }, 0, 0);
        columns.Controls.Add(new Label { Text = "Kick (em ordem de prioridade)", AutoSize = true }, 1, 0);
        columns.Controls.Add(_twitch, 0, 1);
        columns.Controls.Add(_kick, 1, 1);

        var ok = new Button { Text = "Salvar", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 40 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        // Os controles encaixam do último para o primeiro: texto no topo, botões embaixo, a caixa logo acima deles.
        Controls.Add(columns);
        Controls.Add(mode);
        Controls.Add(lives);
        Controls.Add(redeemRows);
        Controls.Add(_claim);
        Controls.Add(redeemHint);
        Controls.Add(buttons);
        Controls.Add(hint);
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

    private static ComboBox NewReward(KickReward? reward)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
        combo.Items.AddRange(Rewards);
        combo.SelectedIndex = reward is KickReward r && Enum.IsDefined(r) ? (int)r + 1 : 0;
        return combo;
    }

    private static KickReward? Chosen(ComboBox combo) => combo.SelectedIndex > 0 ? (KickReward)(combo.SelectedIndex - 1) : null;

    private static TextBox NewList() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
    };

    // Aceita também o endereço colado inteiro (https://kick.com/yetz → yetz).
    private static List<string> Parse(string text) => text
        .Split(new[] { '\r', '\n', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(c => c.Trim().TrimEnd('/'))
        .Select(c => c[(c.LastIndexOf('/') + 1)..].TrimStart('@').ToLowerInvariant())
        .Where(c => c.Length > 0)
        .Distinct()
        .ToList();
}
