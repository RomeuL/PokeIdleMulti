using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace PokeIdleMulti;

/// <summary>Instalar, ligar/desligar e remover as extensões de cada conta.</summary>
internal sealed class ExtensionsDialog : Form
{
    private readonly IReadOnlyList<AccountPane> _panes;
    private readonly ExtensionStore _store;
    private readonly CoreWebView2Environment _env;
    private readonly string _root;
    private readonly Action _changed;
    private readonly UiSelect _account = new() { Width = 220 };
    private readonly UiList _list = new() { CheckBoxes = true, MultiSelect = false, FillLast = true };
    private readonly UiSwitch _allAccounts = new("Instalar em todas as contas");
    private readonly ToolTip _tips = Ui.Tips();
    private bool _filling;

    public ExtensionsDialog(IReadOnlyList<AccountPane> panes, ExtensionStore store, CoreWebView2Environment env, string root, Action changed)
    {
        _panes = panes;
        _store = store;
        _env = env;
        _root = root;
        _changed = changed;

        Text = "Extensões das contas";
        Ui.Window(this);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(736, 460);
        MinimumSize = new Size(620, 340);
        ShowInTaskbar = false;
        MaximizeBox = MinimizeBox = false;

        _list.Columns.Add("Extensão (marcada = ligada)", 330);
        _list.Columns.Add("Versão", 84);
        _list.Columns.Add("ID", 0);
        // Os ícones das extensões (24 px de linha, o desenho de 18 no meio).
        _list.SmallImageList?.Dispose();
        _list.SmallImageList = new ImageList { ImageSize = new Size(24, 24), ColorDepth = ColorDepth.Depth32Bit };
        _list.ItemChecked += async (_, e) => await SetEnabledAsync(e.Item);

        foreach (var pane in panes)
            _account.Items.Add(pane.AccountName);
        _account.SelectedIndexChanged += async (_, _) => await RefreshListAsync();

        // Em cima: a conta; o aviso do recarregar fica numa linha amarela.
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false };
        var account = Ui.Label("Conta", muted: true);
        account.Margin = new Padding(0, 6, 8, 0);
        top.Controls.Add(account);
        _account.Margin = Padding.Empty;
        top.Controls.Add(_account);
        var warn = new Label
        {
            Dock = DockStyle.Top, Height = 30, ForeColor = Color.FromArgb(243, 214, 107), Font = Ui.Small, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "⚠  Extensão nova (ou ligada de novo) só entra na página depois de recarregar a conta: ⟳ no cabeçalho dela.",
        };

        // Embaixo da lista: + Adicionar ▾ (Chrome, Web Store, pasta) e o "em todas as contas".
        var addMenu = new ContextMenuStrip();
        addMenu.Items.Add("Importar do meu Chrome…", null, async (_, _) => await RunAsync(ImportFromChromeAsync));
        addMenu.Items.Add("Da Chrome Web Store…", null, async (_, _) => await RunAsync(FromWebStoreAsync));
        addMenu.Items.Add("Pasta descompactada…", null, async (_, _) => await RunAsync(FromFolderAsync));
        var add = new UiButton("+ Adicionar ▾", UiLook.Primary);
        add.Click += (_, _) => addMenu.Show(add, new Point(0, add.Height + 2));
        add.Location = new Point(0, 4);
        _allAccounts.Location = new Point(add.Right + 16, 7);
        var addRow = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        addRow.Controls.Add(add);
        addRow.Controls.Add(_allAccounts);
        _tips.SetToolTip(_allAccounts, "Ligado: a extensão nova entra em todas as contas do deck de uma vez.");

        var close = new UiButton("Fechar", UiLook.Outline);
        close.Click += (_, _) => Close();
        CancelButton = close;
        var footer = new UiFooter(null,
            NewButton("Trazer configurações…", UiLook.Outline, () => { ImportSettings(); return Task.CompletedTask; }),
            NewButton("Opções", UiLook.Outline, async () => await OpenOptionsAsync()),
            NewButton("Remover", UiLook.Danger, async () => await RemoveAsync()),
            close);
        _tips.SetToolTip(footer.Controls[0].Controls[0], "Copia as configurações da extensão escolhida de outro lugar (Chrome, IdleDeck ou outra conta) para esta conta.");

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 8) };
        body.Controls.Add(new UiFrame(_list) { Dock = DockStyle.Fill });
        body.Controls.Add(Ui.Gap(4, DockStyle.Bottom));
        body.Controls.Add(addRow);
        body.Controls.Add(warn);
        body.Controls.Add(top);
        Controls.Add(body);
        Controls.Add(footer);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tips.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_account.Items.Count > 0)
            _account.SelectedIndex = 0;
    }

    private AccountPane? Selected => _account.SelectedIndex >= 0 ? _panes[_account.SelectedIndex] : null;

    private UiButton NewButton(string text, UiLook look, Func<Task> action)
    {
        var button = new UiButton(text, look);
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    /// <summary>Roda uma ação com o cursor de espera e mostra o erro, se der.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        UseWaitCursor = true;
        Enabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            DeckBox.Show(this, Explain(ex), Text, DeckBoxIcon.Warn);
        }
        finally
        {
            Enabled = true;
            UseWaitCursor = false;
        }
    }

    private static string Explain(Exception ex) => ex switch
    {
        HttpRequestException => "Não consegui baixar da Chrome Web Store: " + ex.Message,
        COMException => "O motor do navegador recusou a extensão: " + ex.Message,
        _ => ex.Message,
    };

    private async Task RefreshListAsync()
    {
        _filling = true;
        _list.Items.Clear();
        try
        {
            if (Selected?.Profile is not CoreWebView2Profile profile)
                return;
            foreach (var extension in await profile.GetBrowserExtensionsAsync())
            {
                var item = new ListViewItem(extension.Name) { Checked = extension.IsEnabled, Tag = extension, UseItemStyleForSubItems = false };
                var info = _store.Find(extension.Id);
                item.SubItems.Add(info is null ? "" : ExtensionStore.VersionOf(info.Folder) ?? "", Ui.Muted, Color.Empty, Ui.Font);
                item.SubItems.Add(extension.Id, Ui.Muted, Color.Empty, Ui.Small);
                if (info is not null && _list.SmallImageList is { } images)
                {
                    if (!images.Images.ContainsKey(extension.Id) && ExtensionStore.IconOf(info, 18) is Image icon)
                    {
                        // No meio de um quadrado de 24, para a linha não crescer.
                        var cell = new Bitmap(24, 24);
                        using (var g = Graphics.FromImage(cell))
                            g.DrawImage(icon, 3, 3, 18, 18);
                        icon.Dispose();
                        images.Images.Add(extension.Id, cell);
                    }
                    if (images.Images.ContainsKey(extension.Id))
                        item.ImageKey = extension.Id;
                }
                _list.Items.Add(item);
            }
        }
        finally
        {
            _filling = false;
            _list.FitLastColumn();
        }
    }

    private async Task SetEnabledAsync(ListViewItem item)
    {
        if (_filling || item.Tag is not CoreWebView2BrowserExtension extension)
            return;
        await RunAsync(async () =>
        {
            await extension.EnableAsync(item.Checked);
            _changed();
        });
    }

    private async Task InstallAsync(string folder)
    {
        var targets = _allAccounts.Checked ? _panes.ToList() : Selected is { } selected ? new List<AccountPane> { selected } : new();
        ExtensionInfo? info = null;
        foreach (var pane in targets.Where(p => p.Profile is not null))
            info = await _store.InstallAsync(pane.Profile!, folder);
        await RefreshListAsync();
        _changed();
        if (info is not null)
        {
            DeckBox.Show(this,
                $"Instalada: {info.Name}\n\nEla entra na página quando a conta for recarregada (botão ⟳ no cabeçalho dela).", Text);
        }
    }

    private async Task ImportFromChromeAsync()
    {
        var available = ExtensionStore.FindChromeExtensions();
        if (available.Count == 0)
        {
            DeckBox.Show(this, "Não achei extensões no seu Google Chrome.", Text);
            return;
        }

        var list = new UiList { CheckBoxes = true, FillLast = true, HeaderStyle = ColumnHeaderStyle.None };
        list.Columns.Add("Extensão", 0);
        foreach (var extension in available)
            list.Items.Add(extension.Name);
        if (Pick(list, "Importar do Chrome", "Marque as extensões do seu Chrome que vão para a conta.", "Importar", new Size(440, 380)) != DialogResult.OK)
            return;

        foreach (int index in list.CheckedIndices.Cast<int>().ToList())
            await InstallAsync(_store.ImportFromChrome(available[index]));
    }

    private async Task FromWebStoreAsync()
    {
        var link = Prompt.Ask(this, "Da Chrome Web Store",
            "Cole o link da extensão na Chrome Web Store (ou só o ID dela):", verb: "Adicionar");
        if (string.IsNullOrWhiteSpace(link))
            return;
        await InstallAsync(await _store.DownloadFromWebStoreAsync(link));
    }

    private async Task FromFolderAsync()
    {
        using var dialog = new FolderBrowserDialog { Description = "Pasta da extensão descompactada (a que tem o manifest.json)" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        await InstallAsync(_store.CopyUnpacked(dialog.SelectedPath));
    }

    private async Task RemoveAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not CoreWebView2BrowserExtension extension)
            return;
        if (!DeckBox.Confirm(this, $"Remover \"{extension.Name}\" de {Selected?.AccountName}?\n\n" +
                "As configurações dela nesta conta vão junto.", Text, "Remover", danger: true))
            return;
        await extension.RemoveAsync();
        await _store.CleanupAsync(_panes.Select(p => p.Profile).OfType<CoreWebView2Profile>());
        await RefreshListAsync();
        _changed();
    }

    /// <summary>Copia as configurações da extensão de outro lugar (Chrome, IdleDeck, outra conta) para a conta escolhida.</summary>
    private void ImportSettings()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not CoreWebView2BrowserExtension extension || Selected is not { } target)
        {
            DeckBox.Show(this, "Escolha a conta lá em cima e clique na extensão na lista.", Text);
            return;
        }
        var others = _panes.Where(p => p != target).Select(p => (p.ProfileName, p.AccountName)).ToList();
        var sources = StorageImport.FindSources(_root, extension.Id, others);
        if (sources.Count == 0)
        {
            DeckBox.Show(this, $"Não achei configurações de \"{extension.Name}\" em outro lugar.\n\nProcurei no Chrome, no IdleDeck e nas outras contas do deck.", Text);
            return;
        }

        var list = new UiList { MultiSelect = false, FillLast = true };
        list.Columns.Add("De onde", 260);
        list.Columns.Add("Mudou em", 100);
        list.Columns.Add("Tamanho", 0, HorizontalAlignment.Right);
        foreach (var source in sources)
        {
            var item = new ListViewItem(source.Label);
            item.SubItems.Add($"{source.Changed:dd/MM HH:mm}");
            item.SubItems.Add($"{Math.Max(1, source.Size / 1024)} KB");
            list.Items.Add(item);
        }
        list.Items[0].Selected = true;
        if (Pick(list, $"Trazer configurações para {target.AccountName}",
                $"De onde vêm as configurações de \"{extension.Name}\"? As que {target.AccountName} tem hoje ficam guardadas num backup.",
                "Trazer", new Size(540, 320)) != DialogResult.OK || list.SelectedIndices.Count == 0)
            return;

        var chosen = sources[list.SelectedIndices[0]];
        StorageImport.Queue(_root, new PendingImport(extension.Id, chosen.Folder, chosen.Label, target.ProfileName));
        DeckBox.Show(this,
            $"Combinado: as configurações de \"{extension.Name}\" vindas de {chosen.Label} entram em {target.AccountName} " +
            "na próxima vez que o deck abrir.\n\n" +
            "Com ele aberto, o navegador está usando esses arquivos. Feche o deck e abra de novo.", Text);
    }

    private async Task OpenOptionsAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not CoreWebView2BrowserExtension extension || Selected is null)
            return;
        if (_store.Find(extension.Id)?.Options is not string options)
        {
            DeckBox.Show(this, "Essa extensão não tem página de opções.", Text);
            return;
        }
        var window = await PopupWindow.CreateAsync(_env, Selected.ProfileName, new Size(900, 680), closeOnDeactivate: false);
        window.Location = new Point(Left + 40, Top + 40);
        window.WebView.CoreWebView2.Navigate($"chrome-extension://{extension.Id}/{options}");
        window.Show(Owner);
    }

    /// <summary>Uma caixinha com um texto em cima, a lista e o botão de confirmar (no tema do deck).</summary>
    private DialogResult Pick(UiList list, string title, string hint, string verb, Size size)
    {
        using var pick = new Form { Text = title };
        Ui.Dialog(pick);
        var text = Ui.Label(hint);
        text.AutoSize = false;
        text.Dock = DockStyle.Top;
        text.Height = 40;
        var ok = new UiButton(verb, UiLook.Primary) { DialogResult = DialogResult.OK };
        var cancel = new UiButton("Cancelar", UiLook.Outline) { DialogResult = DialogResult.Cancel };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 12) };
        body.Controls.Add(new UiFrame(list) { Dock = DockStyle.Fill });
        body.Controls.Add(text);
        pick.Controls.Add(body);
        pick.Controls.Add(new UiFooter(null, cancel, ok));
        pick.ClientSize = size;
        pick.AcceptButton = ok;
        pick.CancelButton = cancel;
        list.DoubleClick += (_, _) => pick.DialogResult = DialogResult.OK;
        return pick.ShowDialog(this);
    }
}
