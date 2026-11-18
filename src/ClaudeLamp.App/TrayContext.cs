using System.Diagnostics;
using ClaudeLamp.Core;

namespace ClaudeLamp.App;

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

    public TrayContext(EventWaitHandle showSignal, bool openSettings)
    {
        _invoker.CreateControl();

        var menu = new ContextMenuStrip();
        var settingsItem = new ToolStripMenuItem("Configurações...", null, (_, _) => ShowSettings())
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ligar", null, (_, _) => Apply(s => s.On = true));
        menu.Items.Add("Desligar", null, (_, _) => Apply(s => s.On = false));

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

        menu.Items.Add(new ToolStripSeparator());
        _signalsItem = new ToolStripMenuItem("Sinais do Claude Code") { CheckOnClick = true };
        _signalsItem.Click += (_, _) => ToggleSignals();
        menu.Items.Add(_signalsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => _signalsItem.Checked = Storage.LoadConfig().SignalsEnabled;

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Claude Lamp" };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Apply(s => s.On = !s.On); };
        _icon.DoubleClick += (_, _) => ShowSettings();
        UpdateIcon(Ui.ColorOf(Storage.LoadState().Manual));

        LampService.ManualStateChanged += s => _invoker.BeginInvoke(() => UpdateIcon(Ui.ColorOf(s)));

        // Atualiza o ícone quando um sinal do Claude muda a lâmpada.
        _refresh = new System.Windows.Forms.Timer { Interval = 2000 };
        _refresh.Tick += (_, _) => RefreshFromState();
        _refresh.Start();

        // Uma segunda execução do app pede para mostrar a janela.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => _invoker.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);

        var configured = Storage.LoadConfig().IsConfigured;
        if (openSettings || !configured) ShowSettings(configured ? null : SettingsForm.ConnectionTabIndex);
    }

    public void ShowSettings() => ShowSettings(null);

    private void ShowSettings(int? tab)
    {
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsForm();
            _settings.Icon = _currentIcon;
        }
        if (tab is { } t) _settings.SelectTab(t);
        _settings.Show();
        if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
        _settings.Activate();
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
            _icon.Text = $"Claude Lamp — {EventsTab.DisplayName(evt)} ({rt.LastSignalAt:HH:mm})";
        }
        else
        {
            UpdateIcon(Ui.ColorOf(rt.Manual));
            _icon.Text = "Claude Lamp";
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
            _icon.ShowBalloonTip(4000, "Claude Lamp", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ToggleSignals()
    {
        var cfg = Storage.LoadConfig();
        cfg.SignalsEnabled = _signalsItem.Checked;
        Storage.SaveConfig(cfg);
        _icon.ShowBalloonTip(2000, "Claude Lamp", cfg.SignalsEnabled ? "Sinais do Claude ativados" : "Sinais do Claude pausados", ToolTipIcon.Info);
    }

    private void UpdateIcon(Color color)
    {
        var old = _currentIcon;
        _currentIcon = Ui.BulbIcon(color);
        _icon.Icon = _currentIcon;
        if (_settings is { IsDisposed: false }) _settings.Icon = _currentIcon;
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
        _signalHost.Dispose(); // devolve o RGB do PC aos softwares dos fabricantes
        _refresh.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _settings?.Dispose();
        _currentIcon?.Dispose();
        _invoker.Dispose();
        base.ExitThreadCore();
    }
}
