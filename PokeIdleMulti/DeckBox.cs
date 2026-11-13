namespace PokeIdleMulti;

/// <summary>O ícone da <see cref="DeckBox"/>.</summary>
internal enum DeckBoxIcon { Info, Warn, Error, Question, Danger }

/// <summary>
/// A caixa de aviso do deck, no lugar da MessageBox do Windows (clara, com "OK"/"Cancel" em inglês): escura, com ícone
/// colorido, o 1º parágrafo do texto em negrito e o resto em cinza, e botões em português com o verbo da ação. As que apagam
/// ou gastam têm o botão vermelho e começam com o foco no "Cancelar". Ctrl+C copia o texto, como na do Windows.
/// </summary>
internal static class DeckBox
{
    private const int Pad = 18, IconSize = 34, TextWidth = 380, MaxBody = 480;

    /// <summary>Aviso com um botão "OK".</summary>
    public static void Show(IWin32Window? owner, string text, string title, DeckBoxIcon icon = DeckBoxIcon.Info) =>
        Run(owner, text, title, icon, "OK", null, false);

    /// <summary>
    /// Pergunta com <paramref name="verb"/> e "Cancelar": true quando escolhe o verbo. <paramref name="danger"/>: o verbo fica
    /// vermelho (apaga ou gasta) e o Enter não confirma sozinho.
    /// </summary>
    public static bool Confirm(IWin32Window? owner, string text, string title, string verb, bool danger = false) =>
        Run(owner, text, title, danger ? DeckBoxIcon.Danger : DeckBoxIcon.Question, verb, "Cancelar", danger) == DialogResult.OK;

    private static DialogResult Run(IWin32Window? owner, string text, string title, DeckBoxIcon icon, string verb, string? cancel, bool danger)
    {
        text = text.Replace("\r\n", "\n").Trim();
        int cut = text.IndexOf("\n\n", StringComparison.Ordinal);
        string head = cut > 0 ? text[..cut] : text;
        string more = cut > 0 ? text[(cut + 2)..].Trim() : "";

        using var form = new Form { Text = title, KeyPreview = true };
        Ui.Dialog(form);
        if (owner is null)
        {
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ShowInTaskbar = true;
        }

        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var headSize = TextRenderer.MeasureText(head, Ui.Bold, new Size(TextWidth, int.MaxValue), wrap);
        var moreSize = more.Length == 0 ? Size.Empty : TextRenderer.MeasureText(more, Ui.Font, new Size(TextWidth, int.MaxValue), wrap);
        int x = Pad + IconSize + 14;
        // Sem o resto, o título fica no meio do ícone; com ele, os dois começam no alto.
        int headTop = more.Length == 0 ? Pad + Math.Max(0, (IconSize - headSize.Height) / 2) : Pad + 2;
        int textHeight = headTop - Pad + headSize.Height + (more.Length == 0 ? 0 : 8 + moreSize.Height);

        var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Ui.DarkScroll(body);
        body.Controls.Add(new Badge(icon) { Location = new Point(Pad, Pad) });
        body.Controls.Add(new Label
        {
            Text = head, Font = Ui.Bold, UseMnemonic = false, AutoSize = false,
            Bounds = new Rectangle(x, headTop, TextWidth + 4, headSize.Height),
        });
        if (more.Length > 0)
            body.Controls.Add(new Label
            {
                Text = more, ForeColor = Ui.Muted, UseMnemonic = false, AutoSize = false,
                Bounds = new Rectangle(x, headTop + headSize.Height + 8, TextWidth + 4, moreSize.Height),
            });

        var main = new UiButton(verb, danger ? UiLook.DangerFill : UiLook.Primary) { DialogResult = DialogResult.OK };
        var buttons = new List<Control>();
        UiButton? no = null;
        if (cancel is not null)
        {
            no = new UiButton(cancel, UiLook.Outline) { DialogResult = DialogResult.Cancel };
            buttons.Add(no);
        }
        buttons.Add(main);
        var footer = new UiFooter(null, buttons.ToArray());

        int bodyHeight = Math.Min(MaxBody, Math.Max(IconSize, textHeight) + 2 * Pad);
        form.ClientSize = new Size(x + TextWidth + Pad + 4 + (textHeight + 2 * Pad > MaxBody ? SystemInformation.VerticalScrollBarWidth : 0),
            bodyHeight + footer.Height);
        form.Controls.Add(body);
        form.Controls.Add(footer);
        form.CancelButton = no ?? main;
        if (!danger)
            form.AcceptButton = main;
        form.Shown += (_, _) => (danger && no is not null ? no : main).Focus();
        form.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                try
                {
                    Clipboard.SetText(title + "\n\n" + text);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                }
                e.Handled = true;
            }
        };
        return owner is null ? form.ShowDialog() : form.ShowDialog(owner);
    }

    /// <summary>
    /// Uma tabela (as linhas já montadas) com os avisos embaixo, em amarelo, numa janela com "Fechar": o "Detalhes das
    /// lives". Sem linhas, mostra <paramref name="empty"/>.
    /// </summary>
    public static void ShowTable(IWin32Window owner, string title, (string Title, int Width)[] columns, IReadOnlyList<ListViewItem> rows,
        IReadOnlyList<string> notes, string empty)
    {
        using var form = new Form { Text = title };
        Ui.Dialog(form);
        const int width = 800;
        var list = new UiList { MultiSelect = false, FillLast = true };
        foreach (var (name, size) in columns)
            list.Columns.Add(name, size);
        list.Items.AddRange(rows.ToArray());

        var lines = rows.Count == 0 ? notes.Prepend(empty).ToList() : notes.ToList();
        var noteBox = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        int notesHeight = 0;
        foreach (var note in lines)
        {
            bool warn = note != empty;
            var label = Ui.Label((warn ? "⚠  " : "") + note, muted: !warn);
            if (warn)
                label.ForeColor = Color.FromArgb(243, 214, 107);
            label.MaximumSize = new Size(width - 32, 0);
            label.Margin = new Padding(0, 0, 0, 4);
            noteBox.Controls.Add(label);
            notesHeight += label.PreferredHeight + 4;
        }
        noteBox.Height = notesHeight;

        var close = new UiButton("Fechar", UiLook.Primary) { DialogResult = DialogResult.Cancel };
        var footer = new UiFooter(null, close);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 12, 14, 10) };
        body.Controls.Add(new UiFrame(list) { Dock = DockStyle.Fill });
        if (lines.Count > 0)
        {
            body.Controls.Add(Ui.Gap(10, DockStyle.Bottom));
            body.Controls.Add(noteBox);
        }
        form.Controls.Add(body);
        form.Controls.Add(footer);
        int tableHeight = 30 + 24 * Math.Clamp(rows.Count, 2, 12) + 6;
        form.ClientSize = new Size(width, 22 + tableHeight + (lines.Count > 0 ? 10 + notesHeight : 0) + footer.Height);
        form.CancelButton = close;
        form.AcceptButton = close;
        form.ShowDialog(owner);
    }

    /// <summary>A bolinha colorida com !, ?, i ou ✕.</summary>
    private sealed class Badge : Control
    {
        private static readonly Font Glyph = new("Segoe UI", 13F, FontStyle.Bold);
        private readonly DeckBoxIcon _icon;

        public Badge(DeckBoxIcon icon)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _icon = icon;
            Size = new Size(IconSize, IconSize);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var behind = Ui.Behind(this);
            var (tone, glyph) = _icon switch
            {
                DeckBoxIcon.Warn => (UiTone.Warn, "!"),
                DeckBoxIcon.Error => (UiTone.Bad, "✕"),
                DeckBoxIcon.Danger => (UiTone.Bad, "!"),
                DeckBoxIcon.Question => (UiTone.Accent, "?"),
                _ => (UiTone.Accent, "i"),
            };
            var (tint, alpha, ink) = UiPill.Colors(tone);
            var fill = Ui.Mix(tint, behind, alpha);
            Ui.Box(e.Graphics, ClientRectangle, IconSize / 2, fill, Color.Empty, behind);
            TextRenderer.DrawText(e.Graphics, glyph, Glyph, ClientRectangle, ink, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
