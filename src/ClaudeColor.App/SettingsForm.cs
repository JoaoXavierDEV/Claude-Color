using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Janela principal: navegação lateral e páginas. Fechar só esconde; o app continua na bandeja.</summary>
internal sealed class SettingsForm : Form
{
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Ui.Background };
    private readonly FlowLayoutPanel _nav;
    private readonly List<(Page Page, NavButton Button)> _pages = [];
    private readonly PictureBox _logo = new() { Size = new Size(34, 34), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 10, 0) };
    private readonly Label _lastSignal = new() { AutoSize = true, ForeColor = Ui.Muted, Font = Ui.Small, Margin = new Padding(20, 0, 0, 16) };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 2000 };
    private string? _lastKey;

    public SettingsForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Claude Color";
        Font = Ui.Body;
        BackColor = Ui.Background;
        ForeColor = Ui.Text;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 640);
        Size = new Size(1180, 780);

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Ui.Sidebar };
        var brand = Ui.Row(_logo, new Label { Text = "Claude Color", AutoSize = true, Font = Ui.Brand, ForeColor = Ui.Text, Margin = new Padding(0, 6, 0, 0) });
        brand.Margin = new Padding(20, 22, 0, 22);
        _nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Sidebar };
        _nav.Controls.Add(brand);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, BackColor = Ui.Sidebar };
        // Interruptor de tema: aplica na hora (a bandeja recria esta janela com a outra paleta).
        var themeSwitch = Ui.Link(Ui.Dark ? "◐  Tema claro" : "◐  Tema escuro", () => TrayContext.Current?.SetTheme(Ui.Dark ? "light" : "dark"));
        themeSwitch.LinkColor = themeSwitch.VisitedLinkColor = Ui.Muted;
        themeSwitch.ActiveLinkColor = Ui.Text;
        themeSwitch.LinkBehavior = LinkBehavior.HoverUnderline;
        themeSwitch.Margin = new Padding(20, 0, 0, 8);
        footer.Controls.Add(themeSwitch);
        footer.Controls.Add(_lastSignal);
        sidebar.Controls.Add(_nav);
        sidebar.Controls.Add(footer);
        var divider = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Ui.Border };

        Controls.Add(_host);
        Controls.Add(divider);
        Controls.Add(sidebar);

        AddPage(new OverviewPage(Navigate));
        AddPage(new SignalsPage());
        AddPage(new DevicesPage(Navigate));
        AddPage(new ProfilesPage(Navigate));
        AddPage(new SystemPage());
        AddPage(new TroubleshootingPage(Navigate));
        Navigate(typeof(OverviewPage));

        _refresh.Tick += (_, _) => RefreshSignal();
        _refresh.Start();
        RefreshSignal();
    }

    private void AddPage(Page page)
    {
        var button = new NavButton(page.NavTitle);
        button.Click += (_, _) => Navigate(page.GetType());
        _nav.Controls.Add(button);
        page.Visible = false;
        _host.Controls.Add(page);
        _pages.Add((page, button));
    }

    public IReadOnlyList<Page> Pages => _pages.Select(p => p.Page).ToList();

    /// <summary>Página visível agora (para reabrir no mesmo lugar depois de trocar o tema).</summary>
    public Type? CurrentPage => _pages.FirstOrDefault(p => p.Page.Visible).Page?.GetType();

    public void Navigate(Type pageType)
    {
        foreach (var (page, button) in _pages)
        {
            bool on = page.GetType() == pageType;
            button.Selected = on;
            if (on)
            {
                page.OnShown();
                page.Visible = true;
                page.BringToFront();
            }
            else page.Visible = false;
        }
    }

    public void Navigate<T>() where T : Page => Navigate(typeof(T));

    /// <summary>Ícone e rodapé acompanham o último sinal.</summary>
    private void RefreshSignal()
    {
        if (!Visible && _lastKey is not null) return;
        var rt = Storage.LoadState();
        var key = $"{rt.LastSignal}@{rt.LastSignalAt:O}";
        if (key == _lastKey) return;
        _lastKey = key;
        var cfg = Storage.LoadConfig();
        var color = rt.LastSignal is { } evt && cfg.Events.TryGetValue(evt, out var sig) ? Ui.ParseOr(sig.Color, Ui.Accent) : Ui.Accent;
        var old = _logo.Image;
        _logo.Image = Ui.SignalBitmap(color, 68);
        old?.Dispose();
        _lastSignal.Text = rt.LastSignal is { } e && cfg.Events.ContainsKey(e)
            ? $"Último sinal: {SignalsPage.DisplayName(e)}\n{rt.LastSignalAt:dd/MM HH:mm}"
            : "";
        foreach (var (page, _) in _pages)
            if (page is OverviewPage o && o.Visible) o.RefreshLastSignal();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.StyleTitleBar(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _refresh.Stop();
        base.OnFormClosing(e);
    }

    /// <summary>Item da navegação lateral.</summary>
    private sealed class NavButton : Control
    {
        private bool _selected, _hover;

        public NavButton(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(190, 38);
            Margin = new Padding(10, 1, 10, 1);
            Cursor = Cursors.Hand;
            Font = Ui.Body;
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected
        {
            get => _selected;
            set { _selected = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var bg = _selected ? Ui.Surface : _hover ? Ui.Hover : Ui.Sidebar;
            e.Graphics.Clear(bg);
            if (_selected)
            {
                using var accent = new SolidBrush(Ui.Accent);
                e.Graphics.FillRectangle(accent, 0, 8, 3, Height - 16);
            }
            var font = _selected ? Ui.BodyBold : Ui.Body;
            TextRenderer.DrawText(e.Graphics, Text, font, new Rectangle(16, 0, Width - 16, Height), Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }
}
