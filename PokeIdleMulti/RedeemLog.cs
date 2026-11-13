using System.Diagnostics;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace PokeIdleMulti;

/// <summary>
/// O registro dos resgates (Lives → Registro dos resgates…): cada resgate, cada baú da Twitch, cada recusa e cada vez
/// que os pontos voltaram, com a hora, a conta, o site e o canal. Um arquivo por conta na pasta do deck
/// (resgates-1.log … resgates-4.log, pelo número: o nome da conta muda), de uma execução para outra. Ao abrir, cada
/// um fica só com os últimos <see cref="KeepDays"/> dias (e no máximo <see cref="Keep"/> linhas).
/// </summary>
internal sealed class RedeemLog
{
    private const int Keep = 2000;
    public const int KeepDays = 7;
    private const string Stamp = "dd/MM/yy HH:mm";

    private readonly string _folder;
    private readonly List<string>[] _byAccount;
    private readonly List<string> _all = new();

    /// <summary>
    /// <paramref name="names"/> são os nomes das contas de hoje: separam o resgates.log antigo (um só para todas) por
    /// conta, na primeira vez.
    /// </summary>
    public RedeemLog(string folder, IReadOnlyList<string> names)
    {
        _folder = folder;
        _byAccount = names.Select(_ => new List<string>()).ToArray();
        try
        {
            Directory.CreateDirectory(folder);
            SplitOld(names);
            var cutoff = DateTime.Now.Date.AddDays(-KeepDays);
            for (int i = 0; i < _byAccount.Length; i++)
            {
                var path = FileOf(i);
                if (!File.Exists(path))
                    continue;
                var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
                var kept = lines.Where(l => TimeOf(l) is not DateTime at || at >= cutoff).TakeLast(Keep).ToList();
                if (kept.Count < lines.Count)
                    File.WriteAllLines(path, kept);
                _byAccount[i].AddRange(kept);
            }
        }
        catch
        {
            // Arquivo preso ou estragado: segue com o que leu, sem derrubar o deck.
        }
        // Todas as contas juntas, na ordem da hora (a mesma hora fica na ordem das contas).
        _all.AddRange(_byAccount.SelectMany(l => l).Select((line, order) => (line, order))
            .OrderBy(x => TimeOf(x.line) ?? DateTime.MinValue).ThenBy(x => x.order).Select(x => x.line));
    }

    /// <summary>O arquivo da conta (<paramref name="account"/> a partir de 0).</summary>
    public string FileOf(int account) => Path.Combine(_folder, $"resgates-{account + 1}.log");

    /// <summary>Todas as contas, do mais velho para o mais novo.</summary>
    public IReadOnlyList<string> Lines => _all;

    public IReadOnlyList<string> LinesOf(int account) => _byAccount[account];

    public int Accounts => _byAccount.Length;

    /// <summary>Entrou ou saiu alguma linha (na thread da interface).</summary>
    public event Action? Changed;

    public void Add(int account, string accountName, LiveSite site, string text)
    {
        var line = $"{DateTime.Now.ToString(Stamp)}  {accountName} · {site} · {text}";
        var lines = _byAccount[account];
        lines.Add(line);
        _all.Add(line);
        try
        {
            if (lines.Count > Keep * 2)
            {
                lines.RemoveRange(0, lines.Count - Keep);
                File.WriteAllLines(FileOf(account), lines);
            }
            else
            {
                File.AppendAllText(FileOf(account), line + Environment.NewLine);
            }
        }
        catch
        {
            // Não conseguir gravar o registro não pode parar o resgate.
        }
        Changed?.Invoke();
    }

    /// <summary>Limpa o registro de uma conta (null: de todas): o arquivo vai para a Lixeira.</summary>
    public void Clear(int? account)
    {
        foreach (int i in account is int one ? new[] { one } : Enumerable.Range(0, _byAccount.Length))
        {
            var gone = _byAccount[i].ToHashSet();
            _all.RemoveAll(gone.Contains);
            _byAccount[i].Clear();
            ToRecycleBin(FileOf(i));
        }
        Changed?.Invoke();
    }

    private static DateTime? TimeOf(string line) =>
        line.Length >= Stamp.Length && DateTime.TryParseExact(line[..Stamp.Length], Stamp, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at) ? at : null;

    /// <summary>
    /// O resgates.log de antes (todas as contas num arquivo): cada linha vai para o arquivo da conta pelo nome dela, e o
    /// antigo vai para a Lixeira. Linha de uma conta que mudou de nome fica na conta 1.
    /// </summary>
    private void SplitOld(IReadOnlyList<string> names)
    {
        var old = Path.Combine(_folder, "resgates.log");
        if (!File.Exists(old))
            return;
        var split = names.Select(_ => new List<string>()).ToArray();
        foreach (var line in File.ReadAllLines(old).Where(l => l.Length > Stamp.Length + 2))
        {
            string rest = line[(Stamp.Length + 2)..];
            int account = Enumerable.Range(0, names.Count).FirstOrDefault(i => rest.StartsWith(names[i] + " · "), 0);
            split[account].Add(line);
        }
        for (int i = 0; i < split.Length; i++)
            if (split[i].Count > 0)
                File.AppendAllLines(FileOf(i), split[i]);
        ToRecycleBin(old);
    }

    private static void ToRecycleBin(string path)
    {
        try
        {
            if (File.Exists(path))
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch
        {
            // Sem Lixeira (ou o arquivo preso): fica, e a limpeza dos 7 dias cuida dele.
        }
    }
}

/// <summary>
/// A janela do registro dos resgates: uma tabela (hora, conta, site, canal, o que houve), o mais novo em cima, atualizando
/// sozinha, com filtro de conta, de site e de texto. Cor pelo tipo: resgate verde, problema amarelo, pontos gastos fora do
/// deck vermelho. A linha escolhida aparece inteira embaixo.
/// </summary>
internal sealed class RedeemLogWindow : Form
{
    private sealed record Row(string Line, string When, string Account, string Site, string Channel, string What, Color Color);

    private static readonly Color OkInk = Color.FromArgb(126, 226, 168);
    private static readonly Color WarnInk = Color.FromArgb(243, 214, 107);
    private static readonly Color BadInk = Color.FromArgb(255, 143, 130);
    private static readonly string[] Problems =
    {
        "recusad", "voltaram", "não passou", "nenhum canal", "não viu", "pausad", "erro", "venceu", "vence em", "parou", "não deu",
        "não consegui", "sem login", "entre na",
    };

    private readonly RedeemLog _log;
    private readonly UiSelect _account = new() { Width = 180 };
    private readonly UiSegment _site = new("Todos", "Kick", "Twitch");
    private readonly UiField _search = new() { Width = 240 };
    private readonly Label _count = Ui.Label("", muted: true, small: true);
    private readonly UiList _table = new() { VirtualMode = true, MultiSelect = false, FillLast = true };
    private readonly Label _detail = new() { Dock = DockStyle.Bottom, Height = 40, ForeColor = Theme.Text, UseMnemonic = false, Padding = new Padding(2, 6, 2, 0) };
    private readonly UiButton _open = new("Abrir o arquivo", UiLook.Outline);
    private List<Row> _rows = new();
    private List<string> _shown = new();

    public RedeemLogWindow(RedeemLog log, IReadOnlyList<string> names)
    {
        _log = log;
        Text = "Registro dos resgates";
        Ui.Window(this);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 560);
        MinimumSize = new Size(760, 420);

        _account.Items.Add("Todas as contas");
        foreach (var name in names)
            _account.Items.Add(name);
        _account.SelectedIndex = 0;
        _account.SelectedIndexChanged += (_, _) => Fill();
        _site.SelectedIndexChanged += (_, _) => Fill();
        _search.Box.PlaceholderText = "Buscar canal ou texto…";
        _search.Box.TextChanged += (_, _) => Fill();

        // Em cima: conta, site e busca; à direita, quantas linhas.
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false };
        _account.Margin = new Padding(0, 0, 10, 0);
        _site.Margin = new Padding(0, 0, 10, 0);
        _search.Margin = Padding.Empty;
        top.Controls.AddRange(new Control[] { _account, _site, _search });
        _count.AutoSize = false;
        _count.Dock = DockStyle.Right;
        _count.Width = 220;
        _count.TextAlign = ContentAlignment.MiddleRight;
        var head = new Panel { Dock = DockStyle.Top, Height = 30 };
        head.Controls.Add(top);
        head.Controls.Add(_count);
        top.Dock = DockStyle.Fill;

        foreach (var (title, width) in new[] { ("Hora", 92), ("Conta", 104), ("Site", 74), ("Canal", 128), ("O que houve", 0) })
            _table.Columns.Add(title, width);
        _table.RetrieveVirtualItem += (_, e) => e.Item = Item(_rows[e.ItemIndex]);
        _table.SelectedIndexChanged += (_, _) => ShowDetail();

        _open.Click += (_, _) =>
        {
            try
            {
                if (Selected is int account && File.Exists(_log.FileOf(account)))
                    Process.Start(new ProcessStartInfo(_log.FileOf(account)) { UseShellExecute = true });
            }
            catch
            {
                // Sem programa para abrir .log: fica só a janela.
            }
        };
        var clear = new UiButton("Limpar", UiLook.Danger);
        clear.Click += (_, _) =>
        {
            string who = Selected is int account ? _account.Items[account + 1]!.ToString()! : "todas as contas";
            if (DeckBox.Confirm(this, $"Limpar o registro de {who}?\n\nO arquivo vai para a Lixeira.", Text, "Limpar", danger: true))
                _log.Clear(Selected);
        };
        var close = new UiButton("Fechar", UiLook.Outline) { DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var footer = new UiFooter($"O mais novo em cima. Cada conta guarda os últimos {RedeemLog.KeepDays} dias.", _open, clear, close);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 6) };
        body.Controls.Add(new UiFrame(_table) { Dock = DockStyle.Fill });
        body.Controls.Add(_detail);
        body.Controls.Add(Ui.Gap(10, DockStyle.Top));
        body.Controls.Add(head);
        Controls.Add(body);
        Controls.Add(footer);
        _log.Changed += Fill;
        Fill();
        ActiveControl = _table;
    }

    // Aberta pelo deck sem ele estar em primeiro plano (o autoteste): não pega o foco de quem está jogando.
    protected override bool ShowWithoutActivation => ActiveForm is null;

    /// <summary>A conta escolhida (null: todas).</summary>
    private int? Selected => _account.SelectedIndex > 0 ? _account.SelectedIndex - 1 : null;

    /// <summary>Mostra só a conta <paramref name="account"/> (null: todas) (autoteste).</summary>
    public void ShowAccount(int? account) => _account.SelectedIndex = account is int one ? one + 1 : 0;

    /// <summary>As linhas que a tabela mostra agora, o mais novo em cima (autoteste).</summary>
    public string ShownText => string.Join(Environment.NewLine, _shown);

    /// <summary>"05/11/26 13:19  Conta 1 · Twitch · techzz: pegou o baú (+50, saldo 80)" em colunas.</summary>
    private static Row Parse(string line)
    {
        string when = line.Length >= 14 ? $"{line[..5]} {line[9..14]}" : "";
        string rest = line.Length > 16 ? line[16..] : line;
        var parts = rest.Split(" · ", 3);
        string account = parts.Length > 0 ? parts[0] : "", site = parts.Length > 1 ? parts[1] : "", text = parts.Length > 2 ? parts[2] : rest;
        string channel = "—", what = text;
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && !text[..colon].Contains(' '))
        {
            channel = text[..colon];
            what = text[(colon + 2)..];
        }
        var color = what.Contains("sem resgate do deck") || what.Contains("gastos em outro lugar") || what.Contains("gastos fora") ? BadInk
            : what.Contains("resgatou") ? OkInk
            : Problems.Any(what.Contains) ? WarnInk
            : Theme.Text;
        return new Row(line, when, account, site, channel, what, color);
    }

    private static ListViewItem Item(Row row)
    {
        var item = new ListViewItem(row.When) { UseItemStyleForSubItems = false, ForeColor = Theme.Muted };
        item.SubItems.Add(row.Account, Theme.Text, Color.Empty, Ui.Font);
        var site = item.SubItems.Add(row.Site);
        site.Tag = row.Site == "Kick" ? UiTone.Kick : row.Site == "Twitch" ? UiTone.Twitch : UiTone.Neutral;
        item.SubItems.Add(row.Channel, row.Channel == "—" ? Theme.Muted : Theme.Text, Color.Empty, Ui.Font);
        item.SubItems.Add(row.What, row.Color, Color.Empty, Ui.Font);
        return item;
    }

    private void Fill()
    {
        if (IsDisposed)
            return;
        var lines = Selected is int account ? _log.LinesOf(account) : _log.Lines;
        string site = _site.SelectedIndex switch { 1 => " · Kick · ", 2 => " · Twitch · ", _ => "" };
        string search = _search.Text.Trim();
        _shown = lines.Reverse()
            .Where(l => site.Length == 0 || l.Contains(site))
            .Where(l => search.Length == 0 || l.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        _rows = _shown.Select(Parse).ToList();
        _open.Enabled = Selected is not null;
        _table.VirtualListSize = _rows.Count;
        _table.FitLastColumn();
        _table.Invalidate();
        _count.Text = _rows.Count == lines.Count ? $"{lines.Count} linhas" : $"{_rows.Count} de {lines.Count} linhas";
        if (lines.Count == 0)
            _detail.Text = "Nenhum resgate ainda. Escolha o bônus de cada conta em Lives → Config…; o baú da Twitch vem ligado de fábrica.";
        else
            ShowDetail();
    }

    /// <summary>A linha escolhida inteira (a coluna "O que houve" corta as compridas).</summary>
    private void ShowDetail()
    {
        int index = _table.SelectedIndices.Count > 0 ? _table.SelectedIndices[0] : -1;
        if (index >= 0 && index < _rows.Count)
        {
            _detail.ForeColor = _rows[index].Color;
            _detail.Text = _rows[index].Line;
        }
        else
        {
            _detail.ForeColor = Theme.Muted;
            _detail.Text = _rows.Count == 0 ? "Nada com esse filtro." : "Clique numa linha para ver o texto inteiro.";
        }
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _log.Changed -= Fill;
        base.OnFormClosed(e);
    }
}
