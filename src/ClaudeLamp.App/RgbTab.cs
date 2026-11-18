using ClaudeLamp.Core;
using ClaudeLamp.Rgb;

namespace ClaudeLamp.App;

/// <summary>Quais softwares/dispositivos de RGB do PC acompanham os sinais do Claude.</summary>
internal sealed class RgbTab : UserControl
{
    private readonly CheckBox _corsair = new() { Text = "Corsair iCUE — memória RAM, placa-mãe e outros dispositivos do iCUE", AutoSize = true };
    private readonly CheckBox _logitech = new() { Text = "Logitech G HUB — teclado, mouse e outros periféricos LIGHTSYNC", AutoSize = true };
    private readonly CheckBox _msi = new() { Text = "MSI Mystic Light — placa de vídeo / placa-mãe MSI (exige administrador)", AutoSize = true };
    private readonly CheckBox _openRgb = new() { Text = "OpenRGB — dispositivos marcados abaixo (fans Cooler Master, GPU...)", AutoSize = true };
    private readonly CheckBox _openRgbAutoStart = new() { Text = "Iniciar o servidor do OpenRGB automaticamente", AutoSize = true, Margin = new Padding(24, 3, 3, 3) };
    private readonly CheckedListBox _openRgbDevices = new() { Width = 640, Height = 130, CheckOnClick = true, Margin = new Padding(24, 3, 3, 3) };
    private readonly Label _corsairSdk = Ui.Label(""), _logitechSdk = Ui.Label(""), _msiSdk = Ui.Label(""), _openRgbSdk = Ui.Label("");
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 760, Height = 110 };
    private readonly Label _status = Ui.Label("");
    private readonly Button _detect, _test, _loadDevices;
    private bool _loading;

    public RgbTab()
    {
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        root.Controls.Add(Ui.Label("RGB do PC durante os sinais do Claude", bold: true));
        root.Controls.Add(Ui.Label("Durante um sinal o ClaudeLamp assume a iluminação; depois devolve e cada software volta ao próprio perfil.\n" +
                                   "Quais eventos usam o RGB do PC é escolhido na coluna \"PC RGB\" da aba Eventos. Evite marcar o mesmo dispositivo em dois lugares."));

        foreach (var cb in new[] { _corsair, _logitech, _msi, _openRgb, _openRgbAutoStart })
            cb.CheckedChanged += (_, _) => Save();
        _openRgbDevices.ItemCheck += (_, _) => BeginInvoke(Save); // ItemCheck dispara antes de mudar o estado

        root.Controls.Add(_corsair);
        root.Controls.Add(_corsairSdk);
        root.Controls.Add(Ui.Label("   No iCUE: Configurações → Geral → \"Habilitar SDK\"."));
        root.Controls.Add(_logitech);
        root.Controls.Add(_logitechSdk);
        root.Controls.Add(Ui.Label("   No G HUB: permitir que jogos e aplicativos controlem a iluminação."));
        root.Controls.Add(_msi);
        root.Controls.Add(_msiSdk);
        root.Controls.Add(Ui.Label("   Alternativa sem admin: a GPU MSI também aparece no OpenRGB, mas por lá ela é lenta e o MSI Center pode sobrescrever."));
        root.Controls.Add(_openRgb);
        root.Controls.Add(_openRgbSdk);
        root.Controls.Add(_openRgbAutoStart);
        _loadDevices = Ui.Button("Carregar dispositivos do OpenRGB", async (_, _) => await LoadOpenRgbDevicesAsync());
        var loadRow = Ui.Row(_loadDevices);
        loadRow.Margin = new Padding(21, 0, 0, 0);
        root.Controls.Add(loadRow);
        root.Controls.Add(_openRgbDevices);
        root.Controls.Add(Ui.Label("   Se o MasterCTRL estiver aberto ele pode sobrescrever as cores dos fans; nesse caso desative a iluminação nele."));

        _detect = Ui.Button("Detectar dispositivos", async (_, _) => await DetectAsync());
        _test = Ui.Button("Testar (amarelo por 5 s)", (_, _) => Test());
        root.Controls.Add(Ui.Row(_detect, _test, _status));
        root.Controls.Add(_output);
        Controls.Add(root);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _loading = true;
        var cfg = Storage.LoadConfig().PcRgb;
        _corsair.Checked = cfg.Corsair;
        _logitech.Checked = cfg.Logitech;
        _msi.Checked = cfg.Msi;
        _openRgb.Checked = cfg.OpenRgb.Enabled;
        _openRgbAutoStart.Checked = cfg.OpenRgb.AutoStart;
        foreach (var d in cfg.OpenRgb.Devices ?? []) _openRgbDevices.Items.Add(d, true);
        _loading = false;

        Status(_corsairSdk, CorsairTarget.SdkAvailable, $"{NativeSdk.CorsairDll} encontrado", $"{NativeSdk.CorsairDll} não encontrado em {Path.Combine(AppContext.BaseDirectory, "x64")}");
        Status(_logitechSdk, LogitechTarget.SdkAvailable, $"{NativeSdk.LogitechDll} encontrado", $"{NativeSdk.LogitechDll} não encontrado em {Path.Combine(AppContext.BaseDirectory, "x64")}");
        Status(_msiSdk, MsiTarget.SdkAvailable && Elevation.IsElevated,
            "SDK encontrado e app rodando como administrador",
            !MsiTarget.SdkAvailable ? $"{NativeSdk.MsiDll} não encontrado" : "SDK encontrado, mas o app não está como administrador (aba Sistema → Reiniciar como administrador)");
        Status(_openRgbSdk, OpenRgbTarget.ExePath is not null, $"OpenRGB instalado em {OpenRgbTarget.ExePath}", "OpenRGB não instalado (openrgb.org ou: winget install OpenRGB.OpenRGB)");
    }

    private static void Status(Label l, bool ok, string good, string bad)
    {
        l.ForeColor = ok ? Ui.Ok : Color.DarkOrange;
        l.Text = (ok ? "   ✓ " : "   ✗ ") + (ok ? good : bad);
    }

    private void Save()
    {
        if (_loading) return;
        var cfg = Storage.LoadConfig();
        cfg.PcRgb.Corsair = _corsair.Checked;
        cfg.PcRgb.Logitech = _logitech.Checked;
        cfg.PcRgb.Msi = _msi.Checked;
        cfg.PcRgb.OpenRgb.Enabled = _openRgb.Checked;
        cfg.PcRgb.OpenRgb.AutoStart = _openRgbAutoStart.Checked;
        if (_openRgbDevices.Items.Count > 0)
            cfg.PcRgb.OpenRgb.Devices = _openRgbDevices.CheckedItems.Cast<string>().ToList();
        Storage.SaveConfig(cfg);
        _status.ForeColor = Ui.Ok;
        _status.Text = "Salvo.";
    }

    private async Task LoadOpenRgbDevicesAsync()
    {
        var host = SignalHost.Current;
        await Ui.Busy(_status, "Conectando ao OpenRGB (pode iniciar o servidor)...", [_loadDevices, _detect, _test], async () =>
        {
            if (host is null) throw new InvalidOperationException("O app ainda está iniciando.");
            var cfg = Storage.LoadConfig().PcRgb.OpenRgb;
            var devices = await host.PcRgb.OpenRgbDevicesAsync(cfg);
            _loading = true;
            _openRgbDevices.Items.Clear();
            foreach (var (key, _, selected) in devices) _openRgbDevices.Items.Add(key, selected);
            _loading = false;
            Save();
            return $"{devices.Count} dispositivo(s) no OpenRGB.";
        });
    }

    private async Task DetectAsync()
    {
        _output.Clear();
        var host = SignalHost.Current;
        await Ui.Busy(_status, "Consultando os SDKs...", [_detect, _test, _loadDevices], async () =>
        {
            if (host is null) throw new InvalidOperationException("O app ainda está iniciando.");
            await host.PcRgb.ReleaseAllAsync();
            var lines = await host.PcRgb.DetectAsync(Storage.LoadConfig().PcRgb);
            _output.Text = string.Join(Environment.NewLine, lines);
            return "Detecção concluída.";
        });
    }

    private void Test()
    {
        var cfg = Storage.LoadConfig();
        cfg.Events[ClaudeEvents.Question] = new SignalConfig { Color = "#FFC000", Brightness = 100, Blink = 2, RestoreAfterSeconds = 5, PcRgb = true };
        LampService.TestSignal(cfg, ClaudeEvents.Question);
        _status.ForeColor = Ui.Ok;
        _status.Text = "Testando: amarelo, depois volta ao normal.";
    }
}
