using System.Diagnostics;
using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Ícone na bandeja: atalhos rápidos e acesso à janela de configurações.</summary>
internal sealed class TrayContext : ApplicationContext
{
    public static readonly (string Name, string Hex)[] Presets =
    [
        ("Vermelho", "#FF0000"), ("Laranja", "#FF8000"), ("Amarelo", "#FFC000"),
        ("Verde", "#00FF40"), ("Ciano", "#00FFFF"), ("Azul", "#0040FF"),
        ("Roxo", "#A000FF"), ("Rosa", "#FF0080"),
    ];

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _signalsItem;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly Control _invoker = new();
    private readonly RegisteredWaitHandle _showWait;
    private readonly SignalHost _signalHost = new();
    private SettingsForm? _settings;
    private Icon? _currentIcon;
    private string? _lastSeenSignal;

    public static TrayContext? Current { get; private set; }

    public TrayContext(EventWaitHandle showSignal, bool openSettings)
    {
        Current = this;
        _invoker.CreateControl();

        var menu = new ContextMenuStrip();
        var settingsItem = new ToolStripMenuItem("Configurações...", null, (_, _) => ShowSettings())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        var lampHeader = new ToolStripLabel("Lâmpada") { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold), ForeColor = SystemColors.GrayText };
        menu.Items.Add(lampHeader);
        var on = new ToolStripMenuItem("Ligar", null, (_, _) => Apply(s => s.On = true));
        var off = new ToolStripMenuItem("Desligar", null, (_, _) => Apply(s => s.On = false));
        menu.Items.Add(on);
        menu.Items.Add(off);

        var colors = new ToolStripMenuItem("Cores");
        foreach (var (name, hex) in Presets)
            colors.DropDownItems.Add(name, Ui.Swatch(Ui.ParseOr(hex, Color.White)),
                (_, _) => Apply(s => { s.On = true; s.Mode = "colour"; s.Color = hex; }));
        menu.Items.Add(colors);

        var white = new ToolStripMenuItem("Branco");
        foreach (var (name, temp) in new[] { ("Quente", 0), ("Neutro", 50), ("Frio", 100) })
            white.DropDownItems.Add(name, null, (_, _) => Apply(s => { s.On = true; s.Mode = "white"; s.WhiteTemp = temp; }));
        menu.Items.Add(white);

        var bright = new ToolStripMenuItem("Brilho");
        foreach (var p in new[] { 10, 25, 50, 75, 100 })
            bright.DropDownItems.Add($"{p}%", null, (_, _) => Apply(s => { s.On = true; s.Brightness = p; }));
        menu.Items.Add(bright);
        var lampSeparator = new ToolStripSeparator();
        menu.Items.Add(lampSeparator);
        ToolStripItem[] lampItems = [lampHeader, on, off, colors, white, bright, lampSeparator];

        var profiles = new ToolStripMenuItem("Perfil");
        menu.Items.Add(profiles);

        var darkItem = new ToolStripMenuItem("Tema escuro", null, (_, _) => SetTheme(Ui.Dark ? "light" : "dark"));
        menu.Items.Add(darkItem);

        _signalsItem = new ToolStripMenuItem("Sinais do Claude Code") { CheckOnClick = true };
        _signalsItem.Click += (_, _) => ToggleSignals();
        menu.Items.Add(_signalsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());
        menu.Opening += (_, _) =>
        {
            var cfg = Storage.LoadConfig();
            _signalsItem.Checked = cfg.SignalsEnabled;
            foreach (var item in lampItems) item.Visible = cfg.Lamp.IsActive;
            profiles.DropDownItems.Clear();
            foreach (var p in cfg.Profiles)
            {
                var name = p.Name;
                profiles.DropDownItems.Add(new ToolStripMenuItem(name, null, async (_, _) => await SwitchProfileAsync(name))
                {
                    Checked = string.Equals(name, cfg.ActiveProfile, StringComparison.OrdinalIgnoreCase),
                    ToolTipText = p.Summary(),
                });
            }
            profiles.Text = $"Perfil: {cfg.ActiveProfile}";
            darkItem.Checked = Ui.Dark;
        };

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Claude Color" };
        // Clique esquerdo abre a janela (a lâmpada liga/desliga pelo menu); o clique direito abre o menu.
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        // Clique no balão de aviso (ex.: hooks sem chegar) também abre a janela.
        _icon.BalloonTipClicked += (_, _) => ShowSettings();
        UpdateIcon(IdleColor());

        _signalHost.QuitRequested += () => _invoker.BeginInvoke(ExitThread);
        LampService.ManualStateChanged += s => _invoker.BeginInvoke(() => UpdateIcon(Ui.ColorOf(s)));

        // Atualiza o ícone quando chega um sinal do Claude.
        _refresh = new System.Windows.Forms.Timer { Interval = 2000 };
        _refresh.Tick += (_, _) =>
        {
            RefreshFromState();
            if (++_ticks % 150 == 0) CheckHooks(); // a cada ~5 min
        };
        _invoker.BeginInvoke(CheckHooks);
        _refresh.Start();

        // Uma segunda execução do app pede para mostrar a janela.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => _invoker.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        // Primeira execução (nada ativo ainda): abre a janela na Visão geral.
        if (openSettings || OverviewPage.ActiveOutputs(Storage.LoadConfig()).Count == 0) ShowSettings();
    }

    /// <summary>Cor do ícone fora de um sinal: a da lâmpada, se houver; senão a cor de destaque.</summary>
    private static Color IdleColor() =>
        Storage.LoadConfig().Lamp.IsActive ? Ui.ColorOf(Storage.LoadState().Manual) : Ui.Accent;

    /// <summary>
    /// Troca o tema ("system", "light" ou "dark") na hora: grava, troca a paleta e recria a janela na mesma
    /// página e posição. Roda depois do evento atual, porque quem pede costuma estar dentro da janela.
    /// </summary>
    public void SetTheme(string theme) => _invoker.BeginInvoke(() =>
    {
        var cfg = Storage.LoadConfig();
        cfg.Theme = theme;
        Storage.SaveConfig(cfg);
        Ui.ApplyTheme(theme);
        Storage.Log($"tema: {theme} ({(Ui.Dark ? "escuro" : "claro")})");

        var old = _settings;
        if (old is null || old.IsDisposed) return;
        bool visible = old.Visible;
        var page = old.CurrentPage;
        var state = old.WindowState;
        var bounds = state == FormWindowState.Normal ? old.Bounds : old.RestoreBounds;

        _settings = new SettingsForm { StartPosition = FormStartPosition.Manual, Bounds = bounds };
        if (page is not null) _settings.Navigate(page);
        if (visible)
        {
            _settings.Show();
            _settings.WindowState = state == FormWindowState.Minimized ? FormWindowState.Normal : state;
            _settings.Activate();
        }
        old.Dispose();
    });

    public void ShowSettings()
    {
        if (_settings is null || _settings.IsDisposed) _settings = new SettingsForm();
        _settings.Show();
        if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
        _settings.Activate();
    }

    private int _ticks;
    private string? _lastHookProblems;

    /// <summary>Avisa (uma vez por problema novo) quando os hooks não conseguem chegar ao app.</summary>
    private void CheckHooks()
    {
        try
        {
            var problems = HookHealth.Check();
            var key = string.Join("|", problems.Select(p => p.Title));
            if (key == _lastHookProblems) return;
            _lastHookProblems = key;
            if (problems.Count == 0) return;
            Storage.Log($"hooks: {key}");
            _icon.ShowBalloonTip(8000, "Claude Color: os sinais não vão chegar", problems[0].Title + ". Veja Solução de problemas.", ToolTipIcon.Warning);
        }
        catch (Exception ex) { Storage.Log($"hooks: verificação falhou: {ex.Message}"); }
    }

    private void RefreshFromState()
    {
        var rt = Storage.LoadState();
        var key = $"{rt.LastSignal}@{rt.LastSignalAt:O}";
        if (key == _lastSeenSignal) return;
        _lastSeenSignal = key;
        var cfg = Storage.LoadConfig();
        if (rt.LastSignal is { } evt && cfg.Events.TryGetValue(evt, out var sig))
        {
            UpdateIcon(Ui.ParseOr(sig.Color, Color.White));
            _icon.Text = $"Claude Color: {SignalsPage.DisplayName(evt)} ({rt.LastSignalAt:HH:mm})";
        }
        else
        {
            UpdateIcon(IdleColor());
            _icon.Text = string.IsNullOrEmpty(_lastHookProblems) ? "Claude Color" : "Claude Color: hooks não estão chegando";
        }
    }

    private async void Apply(Action<ManualState> change)
    {
        var state = Storage.LoadState().Manual;
        change(state);
        try { await LampService.ApplyManualAsync(state); }
        catch (Exception ex)
        {
            Storage.Log($"tray: {ex}");
            _icon.ShowBalloonTip(4000, "Claude Color", ex.Message, ToolTipIcon.Error);
        }
    }

    private async Task SwitchProfileAsync(string name)
    {
        try
        {
            await ProfileService.ActivateAsync(name);
            _icon.ShowBalloonTip(2000, "Claude Color", $"Perfil \"{name}\": {Storage.LoadConfig().Active().Summary()}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Storage.Log($"tray: perfil: {ex}");
            _icon.ShowBalloonTip(4000, "Claude Color", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ToggleSignals()
    {
        var cfg = Storage.LoadConfig();
        cfg.SignalsEnabled = _signalsItem.Checked;
        Storage.SaveConfig(cfg);
        _icon.ShowBalloonTip(2000, "Claude Color", cfg.SignalsEnabled ? "Sinais do Claude ativados" : "Sinais do Claude pausados", ToolTipIcon.Info);
    }

    private void UpdateIcon(Color color)
    {
        var old = _currentIcon;
        _currentIcon = Ui.SignalIcon(color);
        _icon.Icon = _currentIcon;
        old?.Dispose();
    }

    public static void OpenPath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _showWait.Unregister(null);
        _signalHost.Dispose(); // devolve o RGB do PC ao OpenRGB e aos softwares dos fabricantes
        _refresh.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _settings?.Dispose();
        _currentIcon?.Dispose();
        _invoker.Dispose();
        base.ExitThreadCore();
    }
}
