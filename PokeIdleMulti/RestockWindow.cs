namespace PokeIdleMulti;

/// <summary>
/// A recompra de uma conta (o carrinho no cabeçalho dela): ligar, quanto manter de pokebola, revive e poção,
/// qual poção e o mínimo de ouro. Vale na hora, sem botão de salvar; o estoque e o registro das compras se
/// atualizam enquanto a janela está aberta. Pokébola e Revives lado a lado, Poções embaixo, o registro recolhido.
/// </summary>
internal sealed class RestockWindow : Form
{
    private const int W = 720, Pad = 14, Gap = 12, Half = (W - 2 * Pad - Gap) / 2, Full = W - 2 * Pad;
    private const int LogHeight = 110;

    private readonly RestockConfig _config;
    private readonly UiSwitch _on = new("Recompra ligada") { Font = Ui.Bold };
    private readonly UiPill _state = new("desligada", UiTone.Neutral);
    private string _stateText = "";
    private readonly UiSwitch _ballsOn = CardSwitch("Pokébola");
    private readonly UiSelect _ball = new();
    private readonly UiStepper _ballKeep = Stock();
    private readonly UiStepper _ballBelow = Stock();
    private readonly Label _ballInfo = Corner();
    private readonly UiSwitch _revivesOn = CardSwitch("Revives");
    private readonly UiStepper _reviveKeep = Stock();
    private readonly UiStepper _reviveBelow = Stock();
    private readonly Label _reviveInfo = Corner();
    private readonly Label _reviveBuy = Ui.Label("sempre o mais barato", muted: true);
    private readonly UiSwitch _potionsOn = CardSwitch("Poções");
    private readonly UiSelect _potion = new();
    private readonly UiStepper _potionKeep = Stock();
    private readonly UiStepper _potionBelow = Stock();
    private readonly UiSwitch _potionFirst = new("Pôr a poção escolhida em 1º na fila da automação \"Usar Poções\"");
    private readonly Label _potionHave = Corner();
    private readonly Label _potionInfo = new() { AutoSize = false, ForeColor = Ui.Muted, Font = Ui.Small, UseMnemonic = false };
    private readonly UiStepper _goldFloor = new() { Maximum = 1_000_000_000_000, Thousands = true, Increment = 100_000, Width = 170 };
    private readonly Label _gold = Ui.Label("", muted: true);
    private readonly Label _logToggle = Ui.Label("▸ Registro", muted: true);
    private readonly UiPill _logCount = new("0", UiTone.Neutral);
    private readonly UiField _log = new(multiline: true) { Visible = false };
    private readonly ToolTip _tips = Ui.Tips(30000);
    private readonly int _collapsedHeight;
    private bool _loading;

    /// <summary>A configuração mudou (o deck salva e manda para a página).</summary>
    public event Action? Changed;

    private sealed record Choice(int Id, string Text)
    {
        public override string ToString() => Text;
    }

    public RestockWindow(string account, RestockConfig config)
    {
        _config = config;
        Text = $"Recompra · {account}";
        Ui.Dialog(this);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        // Topo: o liga/desliga geral, o estado como etiqueta e o ⓘ com a regra das compras.
        Put(this, _on, Pad, Pad + 4);
        Put(this, _state, _on.Right + 12, Pad + 5);
        var about = new UiInfo();
        Put(this, about, W - Pad - about.Width, Pad + 8);
        _tips.SetToolTip(about, "Compra no Market do NPC, até 9.999 por vez, uma compra a cada 4 s, com a conta ligada no deck. Se o " +
                                "ouro não der para tudo: poções, depois revives, depois a pokebola. A Golden Potion fica de fora. " +
                                "Vale na hora, sem botão de salvar.");

        // Pokébola e Revives lado a lado.
        int y = Pad + 42;
        var balls = Card(Pad, y, Half, 158);
        CardHead(balls, _ballsOn, _ballInfo);
        Put(balls, _ball, 12, 46, Half - 24);
        StockRows(balls, Half, "Manter", _ballKeep, "Recomprar abaixo de", _ballBelow);
        var revives = Card(Pad + Half + Gap, y, Half, 158);
        CardHead(revives, _revivesOn, _reviveInfo);
        _reviveBuy.AutoSize = false;
        _reviveBuy.TextAlign = ContentAlignment.MiddleLeft;
        _reviveBuy.AutoEllipsis = true;
        Put(revives, _reviveBuy, 12, 46, Half - 24);
        _reviveBuy.Height = 28;
        StockRows(revives, Half, "Manter", _reviveKeep, "Recomprar abaixo de", _reviveBelow);
        _tips.SetToolTip(_reviveKeep, "Conta todos os revives juntos.");

        // Poções na largura toda.
        y += 158 + Gap;
        var potions = Card(Pad, y, Full, 192);
        CardHead(potions, _potionsOn, _potionHave);
        Put(potions, _potion, 12, 46, 420);
        var keep = Ui.Label("Manter", muted: true);
        Put(potions, keep, 12, 88);
        Put(potions, _potionKeep, 70, 82, 150);
        var below = Ui.Label("Recomprar abaixo de", muted: true);
        Put(potions, below, 240, 88);
        Put(potions, _potionBelow, 370, 82, 150);
        _tips.SetToolTip(_potionKeep, "Conta todas as poções juntas.");
        Put(potions, _potionFirst, 12, 120);
        Put(potions, _potionInfo, 12, 150, Full - 24);
        _potionInfo.Height = 32;

        // Ouro mínimo e o registro (recolhido).
        y += 192 + Gap;
        var floor = Ui.Label("Nunca deixar o ouro abaixo de");
        Put(this, floor, Pad, y + 6);
        Put(this, _goldFloor, floor.Right + 10, y);
        Put(this, _gold, _goldFloor.Right + 12, y + 6);
        y += 28 + 14;
        _logToggle.Cursor = Cursors.Hand;
        Put(this, _logToggle, Pad, y + 2);
        Put(this, _logCount, _logToggle.Right + 6, y);
        _log.Box.ReadOnly = true;
        _log.Box.Font = new Font("Consolas", 8.5f);
        _log.Box.BackColor = Ui.Field;
        Put(this, _log, Pad, y + 28, Full);
        _log.Height = LogHeight;
        _logToggle.Click += (_, _) => ToggleLog();
        _logCount.Click += (_, _) => ToggleLog();

        var close = new UiButton("Fechar", UiLook.Outline) { DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var footer = new UiFooter("Vale na hora, sem salvar.", close);
        Controls.Add(footer);
        _collapsedHeight = y + 20 + Pad + footer.Height;
        ClientSize = new Size(W, _collapsedHeight);

        _loading = true;
        _on.Checked = config.On;
        _ballsOn.Checked = config.Balls.On;
        _ballKeep.Value = config.Balls.Keep;
        _ballBelow.Value = config.Balls.Below;
        _revivesOn.Checked = config.Revives.On;
        _reviveKeep.Value = config.Revives.Keep;
        _reviveBelow.Value = config.Revives.Below;
        _potionsOn.Checked = config.Potions.On;
        _potionKeep.Value = config.Potions.Keep;
        _potionBelow.Value = config.Potions.Below;
        _potionFirst.Checked = config.PotionFirst;
        _goldFloor.Value = Math.Min(config.GoldFloor, _goldFloor.Maximum);
        _loading = false;
        ShowStatus(null);

        foreach (var box in new[] { _on, _ballsOn, _revivesOn, _potionsOn, _potionFirst })
            box.CheckedChanged += (_, _) => Apply();
        foreach (var number in new[] { _ballKeep, _ballBelow, _reviveKeep, _reviveBelow, _potionKeep, _potionBelow, _goldFloor })
            number.ValueChanged += (_, _) => Apply();
        _ball.SelectedIndexChanged += (_, _) => Apply();
        _potion.SelectedIndexChanged += (_, _) => Apply();
        ActiveControl = close;
    }

    // Aberta pelo deck sem ele estar em primeiro plano (o autoteste): não pega o foco de quem está jogando.
    protected override bool ShowWithoutActivation => ActiveForm is null;

    /// <summary>O texto do estado (no alto da janela), para o autoteste.</summary>
    public string StateText => _stateText;

    /// <summary>A poção escolhida na lista, para o autoteste.</summary>
    public string PotionText => _potion.Text;

    /// <summary>O mesmo que clicar em "Recompra ligada".</summary>
    public void SetOn(bool on) => _on.Checked = on;

    /// <summary>Como digitar no "Manter" da pokebola: o valor lido e o texto que fica no campo (para o autoteste).</summary>
    public (decimal Value, string Text) TypeBallKeep(string text)
    {
        _ballKeep.Text = text;
        var value = _ballKeep.Value;
        return (value, _ballKeep.Text);
    }

    /// <summary>O estoque, as listas (com os nomes e preços do jogo) e o registro que a página contou.</summary>
    public void ShowStatus(RestockStatus? status)
    {
        bool connected = status is { Connected: true };
        _stateText = !_config.On ? "Desligada. Ligada, ela confere o estoque a cada segundo e compra o que faltar."
            : !connected ? "Ligada, esperando o jogo conectar (a conta precisa estar no jogo, não na tela de login)."
            : status!.Paused ? "Ligada, em pausa: 3 compras seguidas não deram certo (volta sozinha em 2 min; veja o registro)."
            : $"Ligada{(status.Nick is null ? "" : $" na conta {status.Nick}")}.";
        _state.Text = !_config.On ? "desligada"
            : !connected ? "esperando o jogo conectar"
            : status!.Paused ? "em pausa: 3 compras falharam"
            : status.Nick is null ? "ligada" : $"ligada na conta {status.Nick}";
        _state.Tone = !_config.On ? UiTone.Neutral : !connected ? UiTone.Warn : status!.Paused ? UiTone.Bad : UiTone.Ok;
        _tips.SetToolTip(_state, _stateText);

        var balls = status?.Balls is { Count: > 0 } live ? live : Restock.KnownBalls.ToList();
        Fill(_ball, balls.Select(b => new Choice(b.Id, b.Price > 0 ? $"{b.Name} · {Restock.N(b.Price)} ouro" : b.Name ?? $"bola {b.Id}")),
            _config.Balls.Id);
        var potions = status?.Potions is { Count: > 0 } livePotions ? livePotions : Restock.KnownPotions.ToList();
        Fill(_potion, potions.Select(p => new Choice(p.Id, $"{p.Name} · {Restock.N(p.Price)} ouro · {Restock.N(p.Heal)} HP"))
            .Prepend(new Choice(0, "Melhor custo-benefício (automático)")), _config.Potions.Id);

        if (!connected)
        {
            _ballInfo.Text = _reviveInfo.Text = _potionHave.Text = "na mochila: —";
            _ballInfo.ForeColor = Ui.Muted;
            _reviveBuy.Text = "sempre o mais barato";
            _potionInfo.Text = "Custo-benefício: esperando o jogo conectar.";
            _gold.Text = "";
            _tips.SetToolTip(_reviveInfo, null);
        }
        else
        {
            var ball = status!.Balls?.FirstOrDefault(b => b.Id == _config.Balls.Id);
            _ballInfo.Text = ball is null ? "não está à venda no NPC" : $"na mochila: {Restock.N(ball.Have)}";
            _ballInfo.ForeColor = ball is null ? Color.FromArgb(243, 214, 107) : Ui.Muted;
            var revives = status.Revives ?? new();
            _reviveInfo.Text = $"na mochila: {Restock.N(status.ReviveHave)}";
            _tips.SetToolTip(_reviveInfo, revives.Count > 0 ? string.Join("\n", revives.Select(r => $"{r.Name}: {Restock.N(r.Have)}")) : null);
            _reviveBuy.Text = revives.Count > 0
                ? $"sempre o mais barato · agora: {revives[0].Name}, {Restock.N(revives[0].Price)} ouro"
                : "sempre o mais barato";
            _potionHave.Text = $"na mochila (todas): {Restock.N(status.PotionHave)}";
            _potionInfo.Text = PotionInfo(status);
            _gold.Text = $"ouro agora: {Restock.N(status.Gold)}";
        }

        var lines = status?.Log ?? new();
        var log = string.Join(Environment.NewLine, lines);
        _logCount.Text = lines.Count.ToString();
        if (_log.Text != log)
        {
            _log.Text = log;
            _log.Box.SelectionStart = _log.Box.TextLength;
            _log.Box.ScrollToCaret();
        }
    }

    private static string PotionInfo(RestockStatus status)
    {
        var potions = status.Potions ?? new();
        var lines = new List<string>();
        if (status.MaxHp is double hp && status.Threshold is double threshold)
        {
            var costs = potions.Where(p => p.Cost is not null).OrderBy(p => p.Cost)
                .Select(p => $"{p.Name} {Restock.Cost(p.Cost!.Value)}");
            lines.Add($"Ouro por HP útil no ativo ({Restock.N(hp)} HP, poção abaixo de {threshold * 100:0}%): " + string.Join(" · ", costs));
        }
        else
        {
            lines.Add("Custo-benefício: esperando o jogo mandar o pokémon ativo.");
        }
        var chosen = potions.FirstOrDefault(p => p.Id == status.PotionId);
        if (chosen is not null)
            lines.Add($"Compra: {chosen.Name}" + (status.QueueFirst == chosen.Id ? " (1ª na fila do \"Usar Poções\")" : ""));
        return string.Join("\n", lines);
    }

    private void ToggleLog()
    {
        bool open = !_log.Visible;
        _log.Visible = open;
        _logToggle.Text = open ? "▾ Registro" : "▸ Registro";
        ClientSize = new Size(W, _collapsedHeight + (open ? LogHeight + 8 : 0));
    }

    private void Apply()
    {
        if (_loading)
            return;
        _config.On = _on.Checked;
        _config.Balls.On = _ballsOn.Checked;
        _config.Balls.Id = (_ball.SelectedItem as Choice)?.Id ?? _config.Balls.Id;
        _config.Balls.Keep = (long)_ballKeep.Value;
        _config.Balls.Below = (long)_ballBelow.Value;
        _config.Revives.On = _revivesOn.Checked;
        _config.Revives.Keep = (long)_reviveKeep.Value;
        _config.Revives.Below = (long)_reviveBelow.Value;
        _config.Potions.On = _potionsOn.Checked;
        _config.Potions.Id = (_potion.SelectedItem as Choice)?.Id ?? _config.Potions.Id;
        _config.Potions.Keep = (long)_potionKeep.Value;
        _config.Potions.Below = (long)_potionBelow.Value;
        _config.PotionFirst = _potionFirst.Checked;
        _config.GoldFloor = (long)_goldFloor.Value;
        Changed?.Invoke();
    }

    /// <summary>Troca as opções da lista só quando mudaram, e nunca com ela aberta (a escolha não pula sozinha).</summary>
    private void Fill(ComboBox box, IEnumerable<Choice> choices, int selected)
    {
        var list = choices.ToList();
        if (list.All(c => c.Id != selected))
            list.Add(new Choice(selected, $"item {selected}"));
        if (box.DroppedDown || box.Items.Cast<Choice>().SequenceEqual(list))
            return;
        _loading = true;
        box.Items.Clear();
        box.Items.AddRange(list.ToArray<object>());
        box.SelectedItem = list.First(c => c.Id == selected);
        _loading = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();
        base.Dispose(disposing);
    }

    private UiCard Card(int x, int y, int width, int height)
    {
        var card = new UiCard { Bounds = new Rectangle(x, y, width, height) };
        Controls.Add(card);
        return card;
    }

    /// <summary>O título do cartão: o liga/desliga com o nome à esquerda e a mochila à direita.</summary>
    private static void CardHead(Control card, UiSwitch toggle, Label corner)
    {
        Put(card, toggle, 12, 13);
        Put(card, corner, toggle.Right + 8, 12, card.Width - toggle.Right - 20);
        corner.Height = 24;
    }

    private static void StockRows(Control card, int width, string keepText, UiStepper keep, string belowText, UiStepper below)
    {
        Put(card, Ui.Label(keepText, muted: true), 12, 88);
        Put(card, keep, width - 12 - 150, 82, 150);
        Put(card, Ui.Label(belowText, muted: true), 12, 124);
        Put(card, below, width - 12 - 150, 118, 150);
    }

    private static void Put(Control parent, Control control, int x, int y, int width = 0)
    {
        control.Location = new Point(x, y);
        // O tamanho automático só se aplica no layout: já mede aqui, para posicionar o que vem ao lado.
        if (control.AutoSize)
            control.Size = control.GetPreferredSize(Size.Empty);
        if (width > 0)
            control.Width = width;
        parent.Controls.Add(control);
    }

    private static UiSwitch CardSwitch(string text) => new(text) { Font = Ui.Bold };

    private static Label Corner() => new()
    {
        AutoSize = false, TextAlign = ContentAlignment.MiddleRight, ForeColor = Ui.Muted, Font = Ui.Small, UseMnemonic = false,
        AutoEllipsis = true,
    };

    private static UiStepper Stock() => new() { Maximum = Restock.MaxStock, Thousands = true, Increment = 100 };
}
