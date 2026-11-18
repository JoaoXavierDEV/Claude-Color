using ClaudeLamp.Core;
using ClaudeLamp.Rgb;

namespace ClaudeLamp.App;

/// <summary>Quais softwares de RGB do PC acompanham os sinais do Claude.</summary>
internal sealed class RgbTab : UserControl
{
    private readonly CheckBox _corsair = new() { Text = "Corsair iCUE — memória RAM, placa-mãe e outros dispositivos do iCUE", AutoSize = true };
    private readonly CheckBox _logitech = new() { Text = "Logitech G HUB — teclado, mouse e outros periféricos LIGHTSYNC", AutoSize = true };
    private readonly Label _corsairSdk = Ui.Label("");
    private readonly Label _logitechSdk = Ui.Label("");
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 760, Height = 120 };
    private readonly Label _status = Ui.Label("");
    private readonly Button _detect, _test;
    private bool _loading;

    public RgbTab()
    {
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        root.Controls.Add(Ui.Label("RGB do PC durante os sinais do Claude", bold: true));
        root.Controls.Add(Ui.Label("Durante um sinal o ClaudeLamp assume a iluminação; depois devolve, e cada software volta ao próprio perfil.\n" +
                                   "Quais eventos usam o RGB do PC é escolhido na coluna \"PC RGB\" da aba Eventos."));

        _corsair.CheckedChanged += (_, _) => Save();
        _logitech.CheckedChanged += (_, _) => Save();
        root.Controls.Add(_corsair);
        root.Controls.Add(_corsairSdk);
        root.Controls.Add(Ui.Label("   No iCUE: Configurações → Geral → \"Habilitar SDK\"."));
        root.Controls.Add(_logitech);
        root.Controls.Add(_logitechSdk);
        root.Controls.Add(Ui.Label("   No G HUB: Configurações → permitir que jogos e aplicativos controlem a iluminação."));

        _detect = Ui.Button("Detectar dispositivos", async (_, _) => await DetectAsync());
        _test = Ui.Button("Testar (amarelo por 5 s)", (_, _) => Test());
        root.Controls.Add(Ui.Row(_detect, _test, _status));
        root.Controls.Add(_output);

        root.Controls.Add(new Label { Height = 8 });
        root.Controls.Add(Ui.Label("Ainda não suportados", bold: true));
        root.Controls.Add(Ui.Label("• Placa de vídeo MSI (Mystic Light): o SDK da MSI exige rodar como administrador.\n" +
                                   "• Fans pelo Cooler Master MasterCTRL: não há SDK público (só via OpenRGB).\n" +
                                   "  Se os fans estiverem ligados na placa-mãe, eles já entram pelo iCUE."));
        Controls.Add(root);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _loading = true;
        var cfg = Storage.LoadConfig();
        _corsair.Checked = cfg.PcRgb.Corsair;
        _logitech.Checked = cfg.PcRgb.Logitech;
        _loading = false;
        SdkLabel(_corsairSdk, CorsairTarget.SdkAvailable, NativeSdk.CorsairDll);
        SdkLabel(_logitechSdk, LogitechTarget.SdkAvailable, NativeSdk.LogitechDll);
    }

    private static void SdkLabel(Label l, bool ok, string dll)
    {
        l.ForeColor = ok ? Ui.Ok : Ui.Error;
        l.Text = ok ? $"   ✓ {dll} encontrado" : $"   ✗ {dll} não encontrado em {Path.Combine(AppContext.BaseDirectory, "x64")}";
    }

    private void Save()
    {
        if (_loading) return;
        var cfg = Storage.LoadConfig();
        cfg.PcRgb.Corsair = _corsair.Checked;
        cfg.PcRgb.Logitech = _logitech.Checked;
        Storage.SaveConfig(cfg);
        _status.ForeColor = Ui.Ok;
        _status.Text = "Salvo.";
    }

    private async Task DetectAsync()
    {
        _output.Clear();
        var host = SignalHost.Current;
        await Ui.Busy(_status, "Consultando iCUE e G HUB...", [_detect, _test], async () =>
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
        var test = new SignalConfig { Color = "#FFC000", Brightness = 100, Blink = 2, RestoreAfterSeconds = 5, PcRgb = true };
        cfg.Events[ClaudeEvents.Question] = test;
        LampService.TestSignal(cfg, ClaudeEvents.Question);
        _status.ForeColor = Ui.Ok;
        _status.Text = "Testando: amarelo, depois volta ao normal.";
    }
}
