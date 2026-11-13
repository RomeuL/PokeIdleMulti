using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PokeIdleMulti;

/// <summary>
/// O kit das janelas e caixas do deck: tema escuro, cantos arredondados e bordas, no jeito de um app web. Sem biblioteca e
/// sem WebView: os controles abaixo são os do WinForms desenhados em GDI+, com as mesmas propriedades de sempre
/// (<c>Checked</c>, <c>Value</c>, <c>SelectedIndex</c>, <c>Text</c>). As cores base são as do <see cref="Theme"/> (as da barra
/// e dos menus).
/// </summary>
internal static class Ui
{
    /// <summary>Fundo da janela.</summary>
    public static readonly Color Back = Theme.Back;
    /// <summary>Cartões e rodapé.</summary>
    public static readonly Color Card = Theme.Bar;
    /// <summary>Campos (texto, número, menu).</summary>
    public static readonly Color Field = Color.FromArgb(20, 20, 27);
    /// <summary>Botão comum e as pontas do − valor +.</summary>
    public static readonly Color Control = Color.FromArgb(30, 30, 40);
    /// <summary>Cabeçalho das tabelas.</summary>
    public static readonly Color Head = Color.FromArgb(22, 22, 30);
    /// <summary>Linha entre as linhas das tabelas.</summary>
    public static readonly Color Line = Color.FromArgb(36, 36, 47);
    public static readonly Color Border = Theme.Border;
    public static readonly Color Hover = Theme.Hover;
    public static readonly Color Accent = Theme.Accent;
    public static readonly Color Text = Theme.Text;
    public static readonly Color Muted = Theme.Muted;
    public static readonly Color Ok = Theme.Ok;
    public static readonly Color Warn = Theme.Warn;
    public static readonly Color Bad = Theme.Bad;
    public static readonly Color Twitch = Color.FromArgb(169, 112, 255);
    public static readonly Color Kick = Color.FromArgb(83, 252, 24);

    public static readonly Font Font = new("Segoe UI", 9F);
    public static readonly Font Bold = new("Segoe UI Semibold", 9F);
    public static readonly Font Small = new("Segoe UI", 8.25F);
    public static readonly Font SmallBold = new("Segoe UI Semibold", 8.25F);
    public static readonly Font Big = new("Segoe UI Semibold", 10.5F);

    /// <summary>Canto dos controles e dos cartões.</summary>
    public const int Radius = 6, CardRadius = 8;

    /// <summary>Uma cor por cima de outra, com transparência (o rgba das folhas de estilo).</summary>
    public static Color Mix(Color top, Color under, double alpha) => Color.FromArgb(
        (int)Math.Round(top.R * alpha + under.R * (1 - alpha)),
        (int)Math.Round(top.G * alpha + under.G * (1 - alpha)),
        (int)Math.Round(top.B * alpha + under.B * (1 - alpha)));

    /// <summary>A cor de trás de um controle (a do pai; a da janela, se o pai for transparente).</summary>
    public static Color Behind(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent.BackColor.A == 255)
                return parent.BackColor;
        return Back;
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Caixa arredondada com borda de 1 px dentro de <paramref name="bounds"/>; o canto de fora fica com
    /// <paramref name="outside"/> (a cor de quem está atrás). <paramref name="border"/> vazio: sem borda.
    /// </summary>
    public static void Box(Graphics g, Rectangle bounds, int radius, Color fill, Color border, Color outside)
    {
        using (var back = new SolidBrush(outside))
            g.FillRectangle(back, bounds);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Round(new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1f, bounds.Height - 1f), radius);
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        if (!border.IsEmpty)
            using (var pen = new Pen(border))
                g.DrawPath(pen, path);
        g.SmoothingMode = old;
    }

    /// <summary>
    /// O botão do kit (o <see cref="UiButton"/> e o de uma célula de lista): fundo, borda e texto pelo jeito e pelo estado.
    /// </summary>
    public static void DrawButton(Graphics g, Rectangle bounds, string text, Font font, UiLook look, bool enabled, bool hover, bool down,
        bool focus, Color behind)
    {
        (Color fill, Color border, Color ink) = look switch
        {
            UiLook.Primary => (down ? Color.FromArgb(104, 74, 230) : hover ? Color.FromArgb(143, 116, 255) : Accent, Color.Empty, Color.White),
            UiLook.DangerFill => (down ? Color.FromArgb(200, 60, 46) : hover ? Color.FromArgb(240, 98, 83) : Bad, Color.Empty, Color.White),
            UiLook.Danger => (down ? Mix(Bad, behind, .3) : hover ? Mix(Bad, behind, .16) : behind,
                Mix(Bad, behind, .7), Color.FromArgb(255, 143, 130)),
            UiLook.Outline => (down ? Mix(Hover, behind, .9) : hover ? Mix(Hover, behind, .6) : behind, Border, Text),
            _ => (down ? Color.FromArgb(52, 52, 70) : hover ? Hover : Control, Border, Text),
        };
        if (border.IsEmpty)
            border = fill;
        if (!enabled)
        {
            fill = Mix(fill, behind, .45);
            border = Mix(border, behind, .45);
            ink = Mix(ink, behind, .45);
        }
        if (focus && enabled)
            border = look is UiLook.Primary or UiLook.DangerFill ? Color.White : Accent;
        Box(g, bounds, Radius, fill, border, behind);
        TextRenderer.DrawText(g, text, font, bounds, ink, fill, OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Texto numa linha, sem "&amp;" virar atalho.</summary>
    public const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

    /// <summary>Para medir uma linha (com as reticências, o MeasureText devolve o texto já cortado).</summary>
    public const TextFormatFlags Measure = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    // ---------------------------------------------------------------- janelas

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Barras de rolagem escuras do próprio Windows (10 1809+) no controle.</summary>
    public static void DarkScroll(System.Windows.Forms.Control control)
    {
        if (control.IsHandleCreated)
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        control.HandleCreated += (_, _) => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
    }

    /// <summary>Janela no tema: barra de título escura, fundo, letra e ícone do deck. Abre no centro da janela dona.</summary>
    public static void Window(Form form)
    {
        form.Icon = Theme.AppIcon;
        form.BackColor = Back;
        form.ForeColor = Text;
        form.Font = Font;
        if (form.IsHandleCreated)
            Theme.DarkTitleBar(form);
        form.HandleCreated += (_, _) => Theme.DarkTitleBar(form);
        form.Load += (_, _) => CenterOnOwner(form);
    }

    /// <summary>Caixa de diálogo no tema (tamanho fixo, sem maximizar, fora da barra de tarefas, no centro do deck).</summary>
    public static void Dialog(Form form)
    {
        Window(form);
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = form.MinimizeBox = false;
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.CenterParent;
    }

    // O centro da janela dona também quando ela abre com Show (o CenterParent do WinForms só vale no ShowDialog), e sem
    // passar da tela onde a dona está.
    private static void CenterOnOwner(Form form)
    {
        if (form.StartPosition != FormStartPosition.CenterParent || form.Owner is not Form owner
            || !owner.Visible || owner.WindowState == FormWindowState.Minimized)
            return;
        var area = Screen.FromControl(owner).WorkingArea;
        int x = owner.Left + (owner.Width - form.Width) / 2;
        int y = owner.Top + (owner.Height - form.Height) / 2;
        form.Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - form.Width)),
            Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - form.Height)));
    }

    /// <summary>Texto solto. <paramref name="muted"/>: cinza; <paramref name="small"/>: letra menor.</summary>
    public static Label Label(string text, bool muted = false, bool small = false, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        UseMnemonic = false,
        ForeColor = muted ? Muted : Text,
        Font = bold ? (small ? SmallBold : Bold) : small ? Small : Font,
    };

    /// <summary>Espaço vazio encaixado (as bordas encaixadas ignoram a margem).</summary>
    public static Panel Gap(int size, DockStyle dock) => new()
    {
        Dock = dock,
        Height = dock is DockStyle.Top or DockStyle.Bottom ? size : 0,
        Width = dock is DockStyle.Left or DockStyle.Right ? size : 0,
    };

    /// <summary>Linha com um texto à esquerda e um controle à direita (o − valor +, um menu), centrados na altura.</summary>
    public static Panel Row(Control left, Control? right = null, int height = 30)
    {
        var row = new Panel { Dock = DockStyle.Bottom, Height = height };
        if (left is Label label)
        {
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
        }
        else
        {
            left.Anchor = AnchorStyles.Left;
            left.Location = new Point(0, (height - left.Height) / 2);
        }
        row.Controls.Add(left);
        // O da direita fica num suporte encaixado à direita, sem ficar por cima do texto (o DrawToBitmap desenha os
        // filhos na ordem errada e esconderia o de cima).
        if (right is not null)
        {
            var holder = new Panel { Dock = DockStyle.Right, Width = right.Width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            right.Margin = Padding.Empty;
            right.Location = new Point(0, (height - right.Height) / 2);
            holder.Controls.Add(right);
            row.Controls.Add(holder);
        }
        return row;
    }

    // ---------------------------------------------------------------- dicas

    private const int TipMax = 360, TipPadX = 10, TipPadY = 7;
    private static readonly Color TipBack = Color.FromArgb(31, 31, 42);

    /// <summary>
    /// Dicas escuras, com borda e no máximo 360 px de largura (as do Windows são claras e atravessam a tela). Usar como
    /// um ToolTip comum (<c>SetToolTip</c>).
    /// </summary>
    public static ToolTip Tips(int autoPop = 20000)
    {
        var tips = new ToolTip { OwnerDraw = true, AutoPopDelay = autoPop, InitialDelay = 350, ReshowDelay = 100 };
        tips.Popup += (_, e) =>
        {
            string text = (e.AssociatedControl is { } control ? tips.GetToolTip(control) : null) ?? "";
            e.ToolTipSize = TipSize(text);
        };
        tips.Draw += (_, e) => DrawTip(e.Graphics, e.Bounds, e.ToolTipText ?? "");
        return tips;
    }

    private static Size TipSize(string text)
    {
        var size = TextRenderer.MeasureText(text, Font, new Size(TipMax - 2 * TipPadX, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        return new Size(size.Width + 2 * TipPadX, size.Height + 2 * TipPadY);
    }

    private static void DrawTip(Graphics g, Rectangle bounds, string text)
    {
        using (var back = new SolidBrush(TipBack))
            g.FillRectangle(back, bounds);
        using (var pen = new Pen(Border))
            g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var area = new Rectangle(bounds.X + TipPadX, bounds.Y + TipPadY, bounds.Width - 2 * TipPadX, bounds.Height - 2 * TipPadY);
        TextRenderer.DrawText(g, text, Font, area, Text, TipBack, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Jeito do <see cref="UiButton"/>: comum, principal (roxo), contorno, perigo (contorno vermelho) e perigo cheio.</summary>
internal enum UiLook { Normal, Primary, Outline, Danger, DangerFill }

/// <summary>Botão arredondado (continua um Button: Click, DialogResult, AcceptButton).</summary>
internal sealed class UiButton : Button
{
    private UiLook _look;
    private bool _hover, _down;

    /// <summary><paramref name="compact"/>: só a largura do texto (botões de lista e de ícone, como ↑ ↓ ✕).</summary>
    public UiButton(string text = "", UiLook look = UiLook.Normal, bool compact = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        Font = Ui.Font;
        Text = text;
        _look = look;
        Cursor = Cursors.Hand;
        UseMnemonic = false;
        int width = TextRenderer.MeasureText(text, Ui.Font, Size.Empty, Ui.Measure).Width;
        Size = compact ? new Size(Math.Max(28, width + 14), 28) : new Size(Math.Max(84, width + 28), 28);
        Margin = compact ? new Padding(0, 0, 6, 0) : new Padding(8, 0, 0, 0);
    }

    public UiLook Look
    {
        get => _look;
        set
        {
            _look = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = _down = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
            _down = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _down = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent) =>
        Ui.DrawButton(pevent.Graphics, ClientRectangle, Text, Font, _look, Enabled, _hover, _down, Focused && ShowFocusCues, Ui.Behind(this));
}

/// <summary>Liga/desliga (continua um CheckBox: Checked, CheckedChanged), com o texto opcional à direita.</summary>
internal sealed class UiSwitch : CheckBox
{
    private const int TrackW = 30, TrackH = 16, Knob = 10;
    private bool _hover;

    public UiSwitch(string text = "")
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        Text = text;
        AutoSize = true;
        Cursor = Cursors.Hand;
        UseMnemonic = false;
        Margin = new Padding(0);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = TrackW + 2 + (Text.Length > 0 ? 9 + TextRenderer.MeasureText(Text, Font, Size.Empty, Ui.Measure).Width : 0);
        return new Size(width, Math.Max(TrackH + 6, Font.Height + 6));
    }

    protected override void OnMouseEnter(EventArgs eventargs)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(eventargs);
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(eventargs);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        var behind = Ui.Behind(this);
        g.Clear(behind);
        var track = new Rectangle(1, (Height - TrackH) / 2, TrackW, TrackH);
        Color fill = Checked ? (_hover ? Color.FromArgb(143, 116, 255) : Ui.Accent) : _hover ? Color.FromArgb(54, 54, 70) : Ui.Hover;
        Color border = Checked ? fill : Ui.Border;
        Color knob = Checked ? Color.White : Ui.Muted;
        Color text = ForeColor;
        if (!Enabled)
        {
            fill = Ui.Mix(fill, behind, .45);
            border = Ui.Mix(border, behind, .45);
            knob = Ui.Mix(knob, behind, .45);
            text = Ui.Muted;
        }
        if (Focused && ShowFocusCues)
            border = Checked ? Color.White : Ui.Accent;
        Ui.Box(g, track, TrackH / 2, fill, border, behind);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int x = Checked ? track.Right - 3 - Knob : track.X + 3;
        using (var brush = new SolidBrush(knob))
            g.FillEllipse(brush, x, track.Y + (TrackH - Knob) / 2f, Knob, Knob);
        g.SmoothingMode = SmoothingMode.None;
        if (Text.Length > 0)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(TrackW + 10, 0, Width - TrackW - 10, Height), text, behind,
                Ui.OneLine | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Campo de texto com moldura arredondada (roxa com o foco). <see cref="Box"/> é o TextBox de dentro.</summary>
internal sealed class UiField : Control
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None };

    public UiField(bool multiline = false)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Field;
        Box.BackColor = Ui.Field;
        Box.ForeColor = Ui.Text;
        if (multiline)
        {
            Box.Multiline = true;
            Box.ScrollBars = ScrollBars.Vertical;
            Box.AcceptsReturn = true;
            Ui.DarkScroll(Box);
            Padding = new Padding(9, 6, 2, 6);
        }
        else
        {
            Padding = new Padding(9, 0, 9, 0);
        }
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Size = new Size(200, 28);
        Cursor = Cursors.IBeam;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => Box.Text;
        set => Box.Text = value ?? "";
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var inner = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
        if (Box.Multiline)
            Box.Bounds = inner;
        else
            Box.Bounds = new Rectangle(inner.X, (Height - Box.Height) / 2, inner.Width, Box.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Box.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e) =>
        Ui.Box(e.Graphics, ClientRectangle, Ui.Radius, Ui.Field, Box.Focused ? Ui.Accent : Ui.Border, Ui.Behind(this));
}

/// <summary>
/// Número com − e + nas pontas (no lugar do NumericUpDown, cujas setinhas não escurecem): Value, Minimum, Maximum,
/// Increment e ValueChanged como os dele. Dá para digitar (com ponto, vírgula ou nada), usar as setas do teclado e a
/// rodinha. <see cref="Thousands"/>: escreve 30.000.
/// </summary>
internal sealed class UiStepper : Control
{
    private const int Zone = 26;
    private readonly TextBox _box = new() { BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center };
    private readonly System.Windows.Forms.Timer _repeat = new();
    private decimal _value, _minimum, _maximum = 100;
    private int _hover, _held;
    private bool _thousands, _typed, _writing;

    public event EventHandler? ValueChanged;

    public UiStepper()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Field;
        _box.BackColor = Ui.Field;
        _box.ForeColor = Ui.Text;
        Controls.Add(_box);
        Size = new Size(104, 28);
        Write();
        // Texto digitado (ou posto em Text) só vale ao sair do campo, no Enter ou ao ler o Value, como no NumericUpDown.
        _box.TextChanged += (_, _) => _typed |= !_writing;
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) =>
        {
            Commit();
            Invalidate();
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Up or Keys.Down)
            {
                Commit();
                Step(e.KeyCode == Keys.Up ? 1 : -1);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                Commit();
            }
        };
        _box.MouseWheel += (_, e) => Wheel(e.Delta);
        _repeat.Tick += (_, _) =>
        {
            _repeat.Interval = 60;
            Step(_held);
        };
    }

    public decimal Increment { get; set; } = 1;

    /// <summary>Escreve com o ponto do milhar (30.000).</summary>
    public bool Thousands
    {
        get => _thousands;
        set
        {
            _thousands = value;
            Write();
        }
    }

    /// <summary>O texto do campo; posto aqui, vale como digitado.</summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value ?? "";
    }

    public decimal Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            _maximum = Math.Max(_maximum, value);
            Value = _value;
        }
    }

    public decimal Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            _minimum = Math.Min(_minimum, value);
            Value = _value;
        }
    }

    public decimal Value
    {
        get
        {
            if (_typed)
                Commit();
            return _value;
        }
        set
        {
            value = Math.Clamp(value, _minimum, _maximum);
            bool changed = value != _value;
            _value = value;
            Write();
            Invalidate();
            if (changed)
                ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Write()
    {
        _writing = true;
        _box.Text = _thousands ? Restock.N((double)_value) : _value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _writing = false;
        _typed = false;
    }

    private void Commit()
    {
        _typed = false;
        var digits = new string(_box.Text.Where(char.IsDigit).ToArray());
        if (decimal.TryParse(digits, out var typed))
            Value = _box.Text.TrimStart().StartsWith('-') ? -typed : typed;
        else
            Write();
    }

    private void Step(int direction)
    {
        if (direction != 0)
            Value = _value + direction * Increment;
    }

    private void Wheel(int delta)
    {
        Commit();
        Step(Math.Sign(delta));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Wheel(e.Delta);
        base.OnMouseWheel(e);
    }

    private int ZoneAt(int x) => x < Zone ? -1 : x >= Width - Zone ? 1 : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int zone = ZoneAt(e.X);
        if (zone != _hover)
        {
            _hover = zone;
            Cursor = zone == 0 ? Cursors.IBeam : Cursors.Hand;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = 0;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        int zone = ZoneAt(e.X);
        if (zone == 0)
        {
            _box.Focus();
            return;
        }
        Commit();
        _held = zone;
        Step(zone);
        _repeat.Interval = 400;
        _repeat.Start();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _repeat.Stop();
        _held = 0;
        base.OnMouseUp(e);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        _box.Bounds = new Rectangle(Zone + 4, (Height - _box.Height) / 2, Math.Max(0, Width - 2 * Zone - 8), _box.Height);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _box.Enabled = Enabled;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var behind = Ui.Behind(this);
        bool focus = _box.Focused;
        Ui.Box(g, ClientRectangle, Ui.Radius, Ui.Field, focus ? Ui.Accent : Ui.Border, behind);
        using var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.Radius);
        g.SetClip(path);
        using (var zone = new SolidBrush(Ui.Control))
        {
            g.FillRectangle(zone, 0, 0, Zone, Height);
            g.FillRectangle(zone, Width - Zone, 0, Zone, Height);
        }
        g.ResetClip();
        using (var pen = new Pen(Ui.Border))
        {
            g.DrawLine(pen, Zone, 1, Zone, Height - 2);
            g.DrawLine(pen, Width - Zone - 1, 1, Width - Zone - 1, Height - 2);
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(focus ? Ui.Accent : Ui.Border))
            g.DrawPath(pen, path);
        g.SmoothingMode = SmoothingMode.None;
        Color Glyph(int side, bool atEnd) => !Enabled || atEnd ? Ui.Mix(Ui.Muted, Ui.Control, .4) : _hover == side ? Ui.Text : Ui.Muted;
        DrawGlyph(g, false, new Rectangle(0, 0, Zone, Height), Glyph(-1, _value <= _minimum));
        DrawGlyph(g, true, new Rectangle(Width - Zone, 0, Zone, Height), Glyph(1, _value >= _maximum));
    }

    private static void DrawGlyph(Graphics g, bool plus, Rectangle zone, Color color)
    {
        int cx = zone.X + zone.Width / 2, cy = zone.Y + zone.Height / 2;
        using var pen = new Pen(color, 1.4f);
        g.DrawLine(pen, cx - 4, cy, cx + 4, cy);
        if (plus)
            g.DrawLine(pen, cx, cy - 4, cx, cy + 4);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _repeat.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Menu de escolha (ComboBox só de escolher) desenhado escuro: o fechado e a lista.</summary>
internal sealed class UiSelect : ComboBox
{
    private bool _hover;

    public UiSelect()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        Font = Ui.Font;
        ItemHeight = 22;
        BackColor = Ui.Field;
        ForeColor = Ui.Text;
        Cursor = Cursors.Hand;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var back = selected ? Ui.Hover : Ui.Field;
        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height), selected ? Color.White : Ui.Text, back,
            Ui.OneLine | TextFormatFlags.VerticalCenter);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        bool open = Focused || DroppedDown;
        var border = open ? Ui.Accent : _hover ? Ui.Mix(Ui.Text, Ui.Border, .18) : Ui.Border;
        Ui.Box(g, ClientRectangle, Ui.Radius, Ui.Field, Enabled ? border : Ui.Mix(Ui.Border, Ui.Field, .5), Ui.Behind(this));
        TextRenderer.DrawText(g, Text, Font, new Rectangle(9, 0, Width - 34, Height), Enabled ? ForeColor : Ui.Muted, Ui.Field,
            Ui.OneLine | TextFormatFlags.VerticalCenter);
        int cx = Width - 15, cy = Height / 2;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Ui.Muted, 1.5f))
            g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
        g.SmoothingMode = SmoothingMode.None;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        Invalidate();
        base.OnSelectedIndexChanged(e);
    }

    protected override void OnDropDownClosed(EventArgs e)
    {
        Invalidate();
        base.OnDropDownClosed(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }
}

/// <summary>Cartão: fundo da cor dos cartões, borda e canto de 8 px. Os controles de dentro herdam a cor.</summary>
internal class UiCard : Panel
{
    public UiCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Card;
        Padding = new Padding(12);
    }

    protected override void OnPaintBackground(PaintEventArgs e) =>
        Ui.Box(e.Graphics, ClientRectangle, Ui.CardRadius, BackColor, Ui.Border, Ui.Behind(this));
}

/// <summary>Rodapé das janelas: faixa da cor dos cartões com a linha em cima; botões à direita e um texto cinza à esquerda.</summary>
internal sealed class UiFooter : Panel
{
    public UiFooter(string? note, params Control[] buttons)
    {
        Dock = DockStyle.Bottom;
        Height = 52;
        BackColor = Ui.Card;
        Padding = new Padding(14, 12, 14, 12);
        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
        };
        right.Controls.AddRange(buttons);
        Controls.Add(right);
        if (note is not null)
        {
            var label = Ui.Label(note, muted: true, small: true);
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            Controls.Add(label);
            label.BringToFront();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Ui.Border);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

/// <summary>
/// Tabela (TableLayoutPanel) com moldura arredondada, a 1ª linha como cabeçalho (fundo mais escuro) e uma linha fina entre
/// as linhas. Os controles do cabeçalho devem usar <see cref="Ui.Head"/> de fundo.
/// </summary>
internal sealed class UiTable : TableLayoutPanel
{
    public UiTable()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Card;
        Padding = new Padding(1);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Behind(this));
        using var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.CardRadius);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(BackColor))
            g.FillPath(brush, path);
        g.SmoothingMode = SmoothingMode.None;
        var rows = GetRowHeights();
        if (rows.Length > 0)
        {
            int y = Padding.Top + rows[0];
            g.SetClip(path);
            using (var head = new SolidBrush(Ui.Head))
                g.FillRectangle(head, 0, 0, Width, y);
            g.ResetClip();
            using (var pen = new Pen(Ui.Border))
                g.DrawLine(pen, 1, y, Width - 2, y);
            using var line = new Pen(Ui.Line);
            for (int i = 1; i < rows.Length - 1; i++)
            {
                y += rows[i];
                g.DrawLine(line, 1, y, Width - 2, y);
            }
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Ui.Border))
            g.DrawPath(pen, path);
        g.SmoothingMode = SmoothingMode.None;
    }
}

/// <summary>Os tons das etiquetas (<see cref="UiPill"/>).</summary>
internal enum UiTone { Neutral, Accent, Ok, Warn, Bad, Twitch, Kick }

/// <summary>Etiqueta arredondada ("pill"): Twitch, Kick, anunciado, recusado…</summary>
internal sealed class UiPill : Control
{
    private UiTone _tone;

    public UiPill(string text, UiTone tone)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        Font = Ui.Small;
        Text = text;
        _tone = tone;
        AutoSize = true;
        Margin = new Padding(0, 0, 6, 0);
        Size = GetPreferredSize(Size.Empty);
    }

    public UiTone Tone
    {
        get => _tone;
        set
        {
            _tone = value;
            Invalidate();
        }
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(TextRenderer.MeasureText(Text, Font, Size.Empty, Ui.Measure).Width + 16, 20);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    public static (Color Tint, double Alpha, Color Ink) Colors(UiTone tone) => tone switch
    {
        UiTone.Accent => (Ui.Accent, .2, Color.FromArgb(196, 181, 255)),
        UiTone.Ok => (Ui.Ok, .14, Color.FromArgb(126, 226, 168)),
        UiTone.Warn => (Ui.Warn, .13, Color.FromArgb(243, 214, 107)),
        UiTone.Bad => (Ui.Bad, .16, Color.FromArgb(255, 154, 142)),
        UiTone.Twitch => (Ui.Twitch, .18, Color.FromArgb(205, 176, 255)),
        UiTone.Kick => (Ui.Kick, .1, Color.FromArgb(142, 245, 110)),
        _ => (Ui.Hover, 1, Ui.Text),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        var behind = Ui.Behind(this);
        var (tint, alpha, ink) = Colors(_tone);
        var fill = Ui.Mix(tint, behind, alpha);
        Ui.Box(e.Graphics, ClientRectangle, Height / 2, fill, Color.Empty, behind);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, fill,
            Ui.OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>O ⓘ: bolinha com "i" que mostra a explicação na dica (passando o mouse ou clicando).</summary>
internal sealed class UiInfo : Control
{
    private static readonly Font Letter = new("Segoe UI", 6.75F, FontStyle.Bold);

    public UiInfo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        Size = new Size(14, 14);
        Cursor = Cursors.Help;
        Margin = new Padding(5, 0, 0, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ui.Behind(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(Color.FromArgb(85, 85, 108)))
            g.DrawEllipse(pen, 0.5f, 0.5f, Width - 2f, Height - 2f);
        g.SmoothingMode = SmoothingMode.None;
        TextRenderer.DrawText(g, "i", Letter, new Rectangle(0, 0, Width - 1, Height - 1), Ui.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Moldura arredondada em volta de um controle do Windows (a lista, o registro): borda de 1 px e 2 px de folga.</summary>
internal sealed class UiFrame : Panel
{
    public UiFrame(Control inner)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        BackColor = inner.BackColor;
        Padding = new Padding(2);
        inner.Dock = DockStyle.Fill;
        Controls.Add(inner);
    }

    protected override void OnPaintBackground(PaintEventArgs e) =>
        Ui.Box(e.Graphics, ClientRectangle, Ui.Radius, BackColor, Ui.Border, Ui.Behind(this));
}

/// <summary>
/// Seletor de opções lado a lado (no lugar de RadioButtons ou de caixinhas que se excluem): a escolhida fica roxa e em
/// negrito. SelectedIndex e SelectedIndexChanged; setas do teclado trocam com o foco nele.
/// </summary>
internal sealed class UiSegment : Control
{
    private readonly string[] _options;
    private readonly int[] _widths;
    private int _index, _hover = -1;

    public event EventHandler? SelectedIndexChanged;

    public UiSegment(params string[] options)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        _options = options;
        Font = Ui.Font;
        _widths = options.Select(o => TextRenderer.MeasureText(o, Ui.Bold, Size.Empty, Ui.Measure).Width + 24).ToArray();
        Size = new Size(_widths.Sum() + 2, 28);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public int SelectedIndex
    {
        get => _index;
        set
        {
            value = Math.Clamp(value, 0, _options.Length - 1);
            if (value == _index)
                return;
            _index = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int At(int x)
    {
        int left = 1;
        for (int i = 0; i < _widths.Length; i++)
        {
            if (x < left + _widths[i])
                return i;
            left += _widths[i];
        }
        return _widths.Length - 1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int at = At(e.X);
        if (at != _hover)
        {
            _hover = at;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left)
            SelectedIndex = At(e.X);
        base.OnMouseDown(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left)
            SelectedIndex--;
        else if (e.KeyCode == Keys.Right)
            SelectedIndex++;
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var behind = Ui.Behind(this);
        var border = Focused && ShowFocusCues ? Ui.Accent : Ui.Border;
        Ui.Box(g, ClientRectangle, Ui.Radius, Ui.Field, border, behind);
        using var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Ui.Radius);
        int left = 1;
        for (int i = 0; i < _options.Length; i++)
        {
            var cell = new Rectangle(left, 1, i == _options.Length - 1 ? Width - 1 - left : _widths[i], Height - 2);
            bool on = i == _index;
            if (on || i == _hover)
            {
                g.SetClip(path);
                using (var brush = new SolidBrush(on ? Ui.Accent : Ui.Hover))
                    g.FillRectangle(brush, cell);
                g.ResetClip();
            }
            else if (i > 0 && i - 1 != _index)
            {
                using var pen = new Pen(Ui.Border);
                g.DrawLine(pen, left, 5, left, Height - 6);
            }
            TextRenderer.DrawText(g, _options[i], on ? Ui.Bold : Font, cell, on ? Color.White : Enabled ? Ui.Text : Ui.Muted,
                Ui.OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            left += _widths[i];
        }
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(border))
            g.DrawPath(pen, path);
        g.SmoothingMode = SmoothingMode.None;
    }
}

/// <summary>
/// Lista (ListView em detalhes) desenhada escura: cabeçalho, linhas mais altas, seleção roxa, rolagem escura e a
/// caixinha desenhada (com CheckBoxes). Continua um ListView (Items, SelectedItems, Checked…). Uma célula com
/// <see cref="UiTone"/> no Tag sai como etiqueta.
/// </summary>
internal sealed class UiList : ListView
{
    private const int LVM_GETHEADER = 0x101F;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    /// <summary>
    /// Desenho próprio de uma célula (o botão "Ir" do Farm): recebe a célula e a cor de fundo já pintada; true = desenhou.
    /// </summary>
    public Func<DrawListViewSubItemEventArgs, Rectangle, Color, bool>? DrawCustom { get; set; }

    public UiList(int rowHeight = 24)
    {
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        BackColor = Ui.Field;
        ForeColor = Ui.Text;
        Font = Ui.Font;
        DoubleBuffered = true;
        // A altura da linha vem da lista de ícones (sem ícone nenhum).
        SmallImageList = new ImageList { ImageSize = new Size(1, rowHeight) };
        Ui.DarkScroll(this);
        HandleCreated += (_, _) =>
        {
            var header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero)
                Ui.SetWindowTheme(header, "DarkMode_ItemsView", null);
        };
    }

    /// <summary>
    /// A última coluna fica com o que sobra da largura (sem rolagem para o lado), descontando a barra vertical quando as
    /// linhas passam da altura. Chamar também depois de trocar as linhas.
    /// </summary>
    public bool FillLast { get; set; }

    /// <summary>A coluna pela qual a janela ordenou a lista (-1: nenhuma); o cabeçalho dela ganha ▲ ou ▼.</summary>
    public int SortColumn { get; set; } = -1;

    public bool SortDescending { get; set; }

    public void FitLastColumn()
    {
        if (!FillLast || Columns.Count == 0)
            return;
        int count = VirtualMode ? VirtualListSize : Items.Count;
        int row = SmallImageList?.ImageSize.Height ?? 24;
        bool scroll = count * row + row + 4 > Height;
        int others = Columns.Cast<ColumnHeader>().Take(Columns.Count - 1).Sum(c => c.Width);
        int width = Math.Max(30, Width - others - (scroll ? SystemInformation.VerticalScrollBarWidth : 0) - 4);
        if (Columns[Columns.Count - 1].Width != width)
            Columns[Columns.Count - 1].Width = width;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitLastColumn();
    }

    private static TextFormatFlags Align(ColumnHeader? header) =>
        header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right
        : header?.TextAlign == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        using (var brush = new SolidBrush(Ui.Head))
            g.FillRectangle(brush, e.Bounds);
        using (var pen = new Pen(Ui.Border))
            g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var area = Rectangle.Inflate(e.Bounds, -8, 0);
        bool sorted = e.ColumnIndex == SortColumn;
        string text = (e.Header?.Text ?? "") + (sorted ? (SortDescending ? " ▼" : " ▲") : "");
        TextRenderer.DrawText(g, text, Ui.SmallBold, area, sorted ? Ui.Text : Ui.Muted, Ui.Head,
            Ui.OneLine | TextFormatFlags.VerticalCenter | Align(e.Header));
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        // Tudo no DrawSubItem.
        if (View != View.Details)
            e.DrawDefault = true;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        var g = e.Graphics;
        var item = e.Item!;
        var bounds = e.ColumnIndex == 0 ? new Rectangle(e.Bounds.X, e.Bounds.Y, Columns[0].Width, e.Bounds.Height) : e.Bounds;
        var back = item.Selected ? Ui.Mix(Ui.Accent, BackColor, .32) : BackColor;
        using (var brush = new SolidBrush(back))
            g.FillRectangle(brush, bounds);
        using (var pen = new Pen(Ui.Line))
            g.DrawLine(pen, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        if (DrawCustom?.Invoke(e, bounds, back) == true)
            return;
        var area = Rectangle.Inflate(bounds, -8, 0);
        if (e.ColumnIndex == 0 && CheckBoxes)
        {
            var box = new Rectangle(bounds.X + 7, bounds.Y + (bounds.Height - 14) / 2, 14, 14);
            Ui.Box(g, box, 4, item.Checked ? Ui.Accent : Ui.Field, item.Checked ? Ui.Accent : Color.FromArgb(68, 68, 90), back);
            if (item.Checked)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(Color.White, 1.8f);
                g.DrawLines(pen, new[] { new PointF(box.X + 3.5f, box.Y + 7.5f), new PointF(box.X + 6f, box.Y + 10f), new PointF(box.X + 10.5f, box.Y + 4.5f) });
                g.SmoothingMode = SmoothingMode.None;
            }
            area = new Rectangle(box.Right + 8, bounds.Y, Math.Max(0, bounds.Right - box.Right - 14), bounds.Height);
        }
        // O ícone da linha (a lista de imagens com a chave no ImageKey), antes do texto da 1ª coluna.
        if (e.ColumnIndex == 0 && SmallImageList is { } images && item.ImageKey is { Length: > 0 } key && images.Images.IndexOfKey(key) is int at and >= 0)
        {
            int size = images.ImageSize.Width;
            images.Draw(g, area.X, bounds.Y + (bounds.Height - images.ImageSize.Height) / 2, at);
            area = new Rectangle(area.X + size + 6, area.Y, Math.Max(0, area.Width - size - 6), area.Height);
        }
        var sub = e.SubItem;
        string text = sub?.Text ?? "";
        var color = item.UseItemStyleForSubItems || sub is null ? item.ForeColor : sub.ForeColor;
        if (color == SystemColors.WindowText || color.IsEmpty)
            color = ForeColor;
        var align = Align(e.Header);
        if (sub?.Tag is UiTone tone && text.Length > 0)
        {
            var (tint, alpha, ink) = UiPill.Colors(tone);
            int width = Math.Min(area.Width, TextRenderer.MeasureText(text, Ui.Small, Size.Empty, Ui.Measure).Width + 14);
            int x = align == TextFormatFlags.Right ? area.Right - width : area.X;
            var pill = new Rectangle(x, bounds.Y + (bounds.Height - 18) / 2, width, 18);
            var fill = Ui.Mix(tint, back, alpha);
            Ui.Box(g, pill, 9, fill, Color.Empty, back);
            TextRenderer.DrawText(g, text, Ui.Small, pill, ink, fill, Ui.OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var font = item.UseItemStyleForSubItems || sub is null ? item.Font : sub.Font;
        TextRenderer.DrawText(g, text, font ?? Font, area, color, back, Ui.OneLine | TextFormatFlags.VerticalCenter | align);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            SmallImageList?.Dispose();
        base.Dispose(disposing);
    }
}
