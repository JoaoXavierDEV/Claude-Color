using System.Drawing.Drawing2D;
using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Cores, fontes e peças da interface montada em código.</summary>
internal static class Ui
{
    // Paleta: tons quentes neutros com destaque coral. Definida uma vez, antes da primeira janela (ApplyTheme).
    public static bool Dark { get; private set; }
    public static Color Background { get; private set; }
    public static Color Sidebar { get; private set; }
    public static Color Surface { get; private set; }
    public static Color Hover { get; private set; }
    public static Color Border { get; private set; }
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Accent { get; private set; }
    public static Color Error { get; private set; }
    public static Color Warn { get; private set; }
    public static Color Ok { get; private set; }

    static Ui() => SetPalette(dark: false);

    private static void SetPalette(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            Background = Color.FromArgb(31, 30, 28);
            Sidebar = Color.FromArgb(38, 37, 34);
            Surface = Color.FromArgb(45, 44, 41);
            Hover = Color.FromArgb(52, 50, 46);
            Border = Color.FromArgb(66, 64, 59);
            Text = Color.FromArgb(236, 234, 226);
            Muted = Color.FromArgb(163, 159, 149);
            Accent = Color.FromArgb(224, 128, 96);
            Error = Color.FromArgb(240, 113, 102);
            Warn = Color.FromArgb(232, 172, 72);
            Ok = Color.FromArgb(110, 200, 140);
        }
        else
        {
            Background = Color.FromArgb(247, 246, 242);
            Sidebar = Color.FromArgb(238, 236, 228);
            Surface = Color.White;
            Hover = Color.FromArgb(230, 227, 217);
            Border = Color.FromArgb(222, 219, 208);
            Text = Color.FromArgb(38, 37, 33);
            Muted = Color.FromArgb(110, 107, 98);
            Accent = Color.FromArgb(217, 119, 87);
            Error = Color.FromArgb(185, 50, 40);
            Warn = Color.FromArgb(196, 118, 10);
            Ok = Color.FromArgb(46, 125, 80);
        }
    }

    /// <summary>
    /// Aplica o tema ("system", "light" ou "dark") antes de criar qualquer janela: a paleta do app e o modo de cor
    /// do Windows Forms (.NET 10), que cuida dos controles padrão (caixas de texto, listas, barras de rolagem).
    /// </summary>
    public static void ApplyTheme(string? theme)
    {
#pragma warning disable WFO5001 // modo escuro do WinForms: ainda marcado como experimental no .NET 10
        var mode = theme?.ToLowerInvariant() switch
        {
            "dark" => SystemColorMode.Dark,
            "light" => SystemColorMode.Classic,
            _ => SystemColorMode.System,
        };
        try { Application.SetColorMode(mode); }
        catch (Exception ex) { Storage.Log($"ui: tema: {ex.Message}"); }
        SetPalette(Application.IsDarkModeEnabled);
#pragma warning restore WFO5001
    }

    /// <summary>Barra de título escura (DWM) para combinar com o tema.</summary>
    public static void StyleTitleBar(Form form)
    {
        int on = Dark ? 1 : 0;
        try { DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int)); }
        catch { /* Windows antigo: fica a barra padrão */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public const string FontFamily = "Segoe UI";
    public static readonly Font Body = new(FontFamily, 9F);
    public static readonly Font BodyBold = new(FontFamily, 9F, FontStyle.Bold);
    public static readonly Font Small = new(FontFamily, 8.25F);
    public static readonly Font CardTitle = new(FontFamily, 10.5F, FontStyle.Bold);
    public static readonly Font PageTitle = new(FontFamily, 15F, FontStyle.Bold);
    public static readonly Font Brand = new(FontFamily, 12F, FontStyle.Bold);

    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 6, 8, 4), ForeColor = Text,
        Font = bold ? BodyBold : Body,
    };

    /// <summary>Texto secundário que quebra linha na largura do cartão.</summary>
    public static Label Hint(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 6), ForeColor = Muted, Font = Small,
    };

    public static Label Status() => new() { AutoSize = true, Margin = new Padding(8, 8, 0, 4), ForeColor = Muted, Font = Body };

    public static Button Button(string text, EventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(84, 30),
            Padding = new Padding(8, 0, 8, 0), Margin = new Padding(0, 4, 8, 4), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface, ForeColor = primary ? Color.White : Text, Cursor = Cursors.Hand,
            Font = primary ? BodyBold : Body, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Dark(Accent, 0.05f) : Background;
        b.Click += onClick;
        return b;
    }

    /// <summary>Link que abre um site no navegador.</summary>
    public static LinkLabel Link(string text, string url) => Link(text, () => OpenUrl(url));

    /// <summary>Link que executa uma ação.</summary>
    public static LinkLabel Link(string text, Action onClick)
    {
        var l = new LinkLabel
        {
            Text = text, AutoSize = true, Margin = new Padding(0, 4, 8, 4), Font = Body, LinkColor = Accent,
            ActiveLinkColor = ControlPaint.Dark(Accent, 0.1f), VisitedLinkColor = Accent,
        };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Storage.Log($"ui: abrir {url}: {ex.Message}"); }
    }

    public static CheckBox Check(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 6, 8, 4), ForeColor = Text, Font = bold ? BodyBold : Body,
    };

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            Margin = new Padding(0, 2, 0, 2), FlowDirection = FlowDirection.LeftToRight,
        };
        p.Controls.AddRange(controls);
        return p;
    }

    /// <summary>Grade de formulário "rótulo: campo [extra]".</summary>
    public static TableLayoutPanel Form(int labelWidth = 120)
    {
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = new Padding(0, 4, 0, 4) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    public static void AddField(TableLayoutPanel form, string label, Control field, Control? extra = null)
    {
        var l = Label(label);
        l.Anchor = AnchorStyles.Left;
        field.Anchor = AnchorStyles.Left;
        field.Margin = new Padding(0, 4, 8, 4);
        form.Controls.Add(l);
        form.Controls.Add(field);
        if (extra is not null) extra.Anchor = AnchorStyles.Left;
        form.Controls.Add(extra ?? new Label { AutoSize = true });
    }

    /// <summary>Rótulo de status alinhado ao centro de um botão na mesma linha.</summary>
    public static Label InlineStatus() => new() { AutoSize = true, Font = BodyBold, Margin = new Padding(0, 11, 14, 4) };

    /// <summary>Linha de status "● texto" colorida.</summary>
    public static void SetStatus(Label l, bool? ok, string text)
    {
        l.ForeColor = ok switch { true => Ok, false => Error, null => Warn };
        l.Text = "●  " + text;
    }

    public static Color ToColor(RgbColor c) => Color.FromArgb(c.R, c.G, c.B);
    public static RgbColor ToRgb(Color c) => new(c.R, c.G, c.B);

    public static Color ParseOr(string hex, Color fallback) =>
        RgbColor.TryParse(hex, out var c) ? ToColor(c) : fallback;

    /// <summary>Executa trabalho assíncrono desabilitando controles e mostrando o resultado num rótulo.</summary>
    public static async Task Busy(Label status, string busyText, Control[] disable, Func<Task<string?>> work)
    {
        foreach (var c in disable) c.Enabled = false;
        status.ForeColor = Muted;
        status.Text = busyText;
        try
        {
            var msg = await work();
            status.ForeColor = Ok;
            status.Text = msg ?? "OK";
        }
        catch (Exception ex)
        {
            Storage.Log($"ui: {ex}");
            status.ForeColor = Error;
            status.Text = (ex is AggregateException a ? a.InnerException ?? ex : ex).Message;
        }
        finally
        {
            foreach (var c in disable) c.Enabled = true;
        }
    }

    /// <summary>Círculo com halo na cor dada (ícone da bandeja e da janela).</summary>
    public static Bitmap SignalBitmap(Color color, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size;
        using (var halo = new SolidBrush(Color.FromArgb(70, color)))
            g.FillEllipse(halo, 0, 0, s - 1, s - 1);
        float inset = s * 0.2f;
        using (var core = new SolidBrush(color))
            g.FillEllipse(core, inset, inset, s - 2 * inset - 1, s - 2 * inset - 1);
        using (var shine = new SolidBrush(Color.FromArgb(110, Color.White)))
            g.FillEllipse(shine, s * 0.33f, s * 0.3f, s * 0.16f, s * 0.16f);
        return bmp;
    }

    /// <summary>Logo do app (o mascote), 256 px. Quem recebe descarta.</summary>
    public static Bitmap LogoBitmap()
    {
        using var s = typeof(Ui).Assembly.GetManifestResourceStream("logo.png")!;
        return new Bitmap(s);
    }

    /// <summary>Ícone da janela (o mesmo do .exe). Compartilhado: não descarte.</summary>
    public static Icon LogoIcon => _logoIcon ??= LoadLogoIcon();
    private static Icon? _logoIcon;

    private static Icon LoadLogoIcon()
    {
        using var s = typeof(Ui).Assembly.GetManifestResourceStream("logo.ico")!;
        return new Icon(s);
    }

    public static Icon SignalIcon(Color color)
    {
        using var bmp = SignalBitmap(color, 32);
        var handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    public static Color ColorOf(ManualState s) =>
        !s.On ? Color.FromArgb(70, 70, 70)
        : s.Mode == "colour" ? ParseOr(s.Color, Color.White)
        : Color.FromArgb(255, 236, 200);

    public static Image Swatch(Color c)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(c);
        g.FillEllipse(b, 1, 1, 13, 13);
        using var pen = new Pen(Border);
        g.DrawEllipse(pen, 1, 1, 13, 13);
        return bmp;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    /// <summary>
    /// Faz a troca dentro de <paramref name="change"/> sem mostrar etapas: congela o desenho de <paramref name="control"/>
    /// (WM_SETREDRAW) e pinta tudo de uma vez no fim. Sem isso, cada controle filho se pinta sozinho e dá para ver a
    /// página se montando.
    /// </summary>
    public static void WithoutRedraw(Control control, Action change)
    {
        const int WM_SETREDRAW = 0x000B;
        const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;
        if (!control.IsHandleCreated) { change(); return; }
        SendMessage(control.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try { change(); }
        finally
        {
            SendMessage(control.Handle, WM_SETREDRAW, 1, IntPtr.Zero);
            RedrawWindow(control.Handle, IntPtr.Zero, IntPtr.Zero, RDW_ERASE | RDW_FRAME | RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }
    }
}

/// <summary>Botão redondo que mostra e escolhe uma cor.</summary>
internal sealed class ColorDot : Control
{
    private Color _color = Color.White;

    public ColorDot()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(30, 30);
        Margin = new Padding(0, 3, 8, 3);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        Click += (_, _) => Pick();
    }

    public event EventHandler? ColorChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Value
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    public string Hex => Ui.ToRgb(_color).ToHex();

    private void Pick()
    {
        using var dlg = new ColorDialog { FullOpen = true, Color = _color };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        Value = dlg.Color;
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(2, 2, Width - 5, Height - 5);
        using var b = new SolidBrush(_color);
        e.Graphics.FillEllipse(b, r);
        using var pen = new Pen(Ui.Border, 1.5f);
        e.Graphics.DrawEllipse(pen, r);
    }
}

/// <summary>Cartão branco com borda, título, descrição opcional e uma chave liga/desliga ao lado do título.</summary>
internal sealed class Card : FlowLayoutPanel
{
    private readonly List<Label> _wrapping = [];

    public CheckBox? Toggle { get; }

    public Card(string title, string? description = null, string? toggleText = null)
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        BackColor = Ui.Surface;
        Padding = new Padding(18, 14, 18, 12);
        Margin = new Padding(0, 0, 0, 14);
        DoubleBuffered = true;

        var head = Ui.Row(new Label { Text = title, AutoSize = true, Font = Ui.CardTitle, ForeColor = Ui.Text, Margin = new Padding(0, 2, 18, 2) });
        head.Margin = new Padding(0, 0, 0, 2);
        if (toggleText is not null)
        {
            Toggle = Ui.Check(toggleText);
            Toggle.Margin = new Padding(0, 5, 0, 0);
            head.Controls.Add(Toggle);
        }
        Controls.Add(head);
        if (description is not null) Controls.Add(Wrap(Ui.Hint(description)));
    }

    /// <summary>Faz o rótulo quebrar linha na largura útil do cartão.</summary>
    public Label Wrap(Label l)
    {
        _wrapping.Add(l);
        l.MaximumSize = new Size(Math.Max(200, Width - Padding.Horizontal - 8), 0);
        return l;
    }

    public void Add(params Control[] controls) => Controls.AddRange(controls);

    public void SetWidth(int width)
    {
        MinimumSize = new Size(width, 0);
        MaximumSize = new Size(width, 0);
        foreach (var l in _wrapping) l.MaximumSize = new Size(Math.Max(200, width - Padding.Horizontal - 8), 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Ui.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Invalidate();
    }
}

/// <summary>Página da janela: título, subtítulo e cartões empilhados com rolagem.</summary>
internal abstract class Page : UserControl
{
    private readonly FlowLayoutPanel _body;
    private readonly Label _subtitle;
    // Espaço no fim da rolagem: o FlowLayoutPanel com AutoScroll ignora o Padding de baixo.
    private readonly Panel _bottomSpace = new() { Size = new Size(1, 32), Margin = Padding.Empty, BackColor = Ui.Background };

    protected Page(string title, string subtitle)
    {
        Dock = DockStyle.Fill;
        BackColor = Ui.Background;
        _body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(28, 22, 28, 12), BackColor = Ui.Background,
        };
        _body.Controls.Add(new Label { Text = title, AutoSize = true, Font = Ui.PageTitle, ForeColor = Ui.Text, Margin = new Padding(0, 0, 0, 2) });
        _subtitle = new Label { Text = subtitle, AutoSize = true, Font = Ui.Body, ForeColor = Ui.Muted, Margin = new Padding(0, 0, 0, 16) };
        _body.Controls.Add(_subtitle);
        _body.Controls.Add(_bottomSpace);
        _body.Resize += (_, _) => Relayout();
        Controls.Add(_body);
    }

    public abstract string NavTitle { get; }

    /// <summary>Chamado sempre que a página fica visível.</summary>
    public virtual void OnShown() { }

    protected T Add<T>(T control) where T : Control
    {
        _body.Controls.Add(control);
        _body.Controls.SetChildIndex(_bottomSpace, _body.Controls.Count - 1);
        Relayout();
        return control;
    }

    private void Relayout()
    {
        int w = _body.ClientSize.Width - _body.Padding.Horizontal - (_body.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth);
        w = Math.Max(480, w);
        _subtitle.MaximumSize = new Size(w, 0);
        foreach (Control c in _body.Controls)
            if (c is Card card) card.SetWidth(w);
    }
}
