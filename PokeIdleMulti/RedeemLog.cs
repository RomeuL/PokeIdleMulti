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

/// <summary>A janela do registro dos resgates: uma conta ou todas, o mais novo em cima, atualizando sozinha.</summary>
internal sealed class RedeemLogWindow : Form
{
    private readonly RedeemLog _log;
    private readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly Button _open = new() { Text = "Abrir o arquivo", Width = 120 };
    private readonly TextBox _text = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    public RedeemLogWindow(RedeemLog log, IReadOnlyList<string> names)
    {
        _log = log;
        Text = "Registro dos resgates";
        Icon = Theme.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 460);
        Padding = new Padding(8);

        _account.Items.Add("Todas as contas");
        foreach (var name in names)
            _account.Items.Add(name);
        _account.SelectedIndex = 0;
        _account.SelectedIndexChanged += (_, _) => Fill();
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34 };
        top.Controls.Add(_account);
        top.Controls.Add(new Label
        {
            AutoSize = true, Margin = new Padding(8, 7, 0, 0),
            Text = $"O mais novo em cima. Cada conta guarda os últimos {RedeemLog.KeepDays} dias.",
        });

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
        var clear = new Button { Text = "Limpar", Width = 90 };
        clear.Click += (_, _) =>
        {
            string who = Selected is int account ? _account.Items[account + 1]!.ToString()! : "todas as contas";
            if (MessageBox.Show(this, $"Limpar o registro de {who}? O arquivo vai para a Lixeira.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                _log.Clear(Selected);
        };
        var close = new Button { Text = "Fechar", Width = 90, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 38 };
        buttons.Controls.Add(close);
        buttons.Controls.Add(clear);
        buttons.Controls.Add(_open);

        Controls.Add(_text);
        Controls.Add(buttons);
        Controls.Add(top);
        _log.Changed += Fill;
        Fill();
    }

    /// <summary>A conta escolhida (null: todas).</summary>
    private int? Selected => _account.SelectedIndex > 0 ? _account.SelectedIndex - 1 : null;

    /// <summary>Mostra só a conta <paramref name="account"/> (null: todas) (autoteste).</summary>
    public void ShowAccount(int? account) => _account.SelectedIndex = account is int one ? one + 1 : 0;

    /// <summary>O que a janela mostra agora (autoteste).</summary>
    public string ShownText => _text.Text;

    private void Fill()
    {
        if (IsDisposed)
            return;
        var lines = Selected is int account ? _log.LinesOf(account) : _log.Lines;
        _open.Enabled = Selected is not null;
        _text.Text = lines.Count == 0
            ? "Nenhum resgate ainda. Escolha o bônus de cada conta em Lives → Config…; o baú da Twitch vem ligado de fábrica."
            : string.Join(Environment.NewLine, lines.Reverse());
        _text.SelectionStart = 0;
        _text.ScrollToCaret();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _log.Changed -= Fill;
        base.OnFormClosed(e);
    }
}
