namespace PokeIdleMulti;

/// <summary>Caixinha para pedir um texto (nome da conta, link da extensão), no tema do deck.</summary>
internal static class Prompt
{
    /// <summary><paramref name="verb"/>: o botão de confirmar ("Salvar", "Adicionar").</summary>
    public static string? Ask(IWin32Window owner, string title, string label, string value = "", string verb = "OK")
    {
        using var form = new Form { Text = title };
        Ui.Dialog(form);
        var text = Ui.Label(label);
        text.MaximumSize = new Size(440, 0);
        text.Location = new Point(18, 16);
        var field = new UiField { Text = value, Location = new Point(18, 16 + text.PreferredHeight + 10), Width = 440 };
        var ok = new UiButton(verb, UiLook.Primary) { DialogResult = DialogResult.OK };
        var cancel = new UiButton("Cancelar", UiLook.Outline) { DialogResult = DialogResult.Cancel };
        var footer = new UiFooter(null, cancel, ok);
        form.ClientSize = new Size(476, field.Bottom + 18 + footer.Height);
        form.Controls.AddRange(new Control[] { text, field, footer });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) =>
        {
            field.Box.Focus();
            field.Box.SelectAll();
        };
        return form.ShowDialog(owner) == DialogResult.OK ? field.Text.Trim() : null;
    }
}
