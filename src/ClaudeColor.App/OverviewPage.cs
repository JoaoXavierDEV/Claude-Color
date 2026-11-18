using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Primeira página: o que falta configurar, o que está ativo e testes rápidos.</summary>
internal sealed class OverviewPage : Page
{
    private readonly Action<Type> _navigate;
    private readonly HooksInstaller _hooks = new();
    private readonly Label _hooksStatus = Ui.InlineStatus(), _devicesStatus = Ui.InlineStatus(), _signalsStatus = Ui.InlineStatus();
    private readonly Button _installHooks, _goDevices, _pause;
    private readonly PictureBox _lastDot = new() { Size = new Size(28, 28), Margin = new Padding(0, 2, 10, 2), SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _lastText = new() { AutoSize = true, Font = Ui.CardTitle, ForeColor = Ui.Text, Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _status = Ui.Status();

    public override string NavTitle => "Visão geral";

    public OverviewPage(Action<Type> navigate) : base("Claude Color", "As luzes do seu PC mostram o que o Claude Code está fazendo: perguntou algo, pediu permissão, terminou.")
    {
        _navigate = navigate;

        var setup = Add(new Card("Configuração"));
        _installHooks = Ui.Button("Instalar hooks", (_, _) => InstallHooks(), primary: true);
        _goDevices = Ui.Button("Escolher dispositivos", (_, _) => _navigate(typeof(DevicesPage)));
        _pause = Ui.Button("Pausar", (_, _) =>
        {
            var cfg = Storage.LoadConfig();
            cfg.SignalsEnabled = !cfg.SignalsEnabled;
            Storage.SaveConfig(cfg);
            OnShown();
        });
        setup.Add(Ui.Row(_hooksStatus, _installHooks), Ui.Row(_devicesStatus, _goDevices), Ui.Row(_signalsStatus, _pause));

        var last = Add(new Card("Último sinal"));
        last.Add(Ui.Row(_lastDot, _lastText));

        var test = Add(new Card("Testar", "Mostra cada sinal com as cores da página Sinais e volta ao normal em até 5 s."));
        var chips = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0), MaximumSize = new Size(760, 0) };
        foreach (var evt in SignalsPage.Order)
        {
            var b = Ui.Button(SignalsPage.DisplayName(evt), (_, _) => Test(evt));
            b.TextImageRelation = TextImageRelation.ImageBeforeText;
            b.ImageAlign = ContentAlignment.MiddleLeft;
            b.Tag = evt;
            chips.Controls.Add(b);
        }
        test.Add(chips, _status);
        _chips = chips;
    }

    private readonly FlowLayoutPanel _chips;

    public override void OnShown()
    {
        {
            var cfg = Storage.LoadConfig();
            var hooks = _hooks.GetStatus(Program.ExePath);
            Ui.SetStatus(_hooksStatus, hooks == HooksStatus.Installed ? true : hooks == HooksStatus.Outdated ? null : false,
                hooks switch
                {
                    HooksStatus.Installed => "Hooks do Claude Code instalados",
                    HooksStatus.Outdated => "Hooks apontam para outro executável",
                    _ => "Hooks do Claude Code não instalados",
                });
            _installHooks.Visible = hooks != HooksStatus.Installed;
            _installHooks.Text = hooks == HooksStatus.Outdated ? "Atualizar hooks" : "Instalar hooks";

            var active = ActiveOutputs(cfg);
            Ui.SetStatus(_devicesStatus, active.Count > 0 ? true : false,
                active.Count > 0 ? "Acende: " + string.Join(", ", active) : "Nenhum dispositivo ativo");
            _goDevices.Text = active.Count > 0 ? "Dispositivos" : "Escolher dispositivos";

            Ui.SetStatus(_signalsStatus, cfg.SignalsEnabled ? true : null, cfg.SignalsEnabled ? "Sinais ligados" : "Sinais pausados");
            _pause.Text = cfg.SignalsEnabled ? "Pausar" : "Retomar";

            foreach (Button b in _chips.Controls)
            {
                var evt = (string)b.Tag!;
                var color = cfg.Events.TryGetValue(evt, out var s) ? Ui.ParseOr(s.Color, Color.White) : Color.White;
                b.Image?.Dispose();
                b.Image = Ui.Swatch(color);
            }
            RefreshLastSignal();
        }
    }

    /// <summary>Atualiza o "último sinal" (chamado também pelo timer da janela).</summary>
    public void RefreshLastSignal()
    {
        var rt = Storage.LoadState();
        var cfg = Storage.LoadConfig();
        Color color;
        string text;
        if (rt.LastSignal is { } evt && cfg.Events.TryGetValue(evt, out var sig))
        {
            color = Ui.ParseOr(sig.Color, Ui.Accent);
            text = $"{SignalsPage.DisplayName(evt)}  ·  {rt.LastSignalAt:HH:mm}";
        }
        else
        {
            color = Ui.Accent;
            text = rt.LastSignal == ClaudeEvents.Resumed ? $"Você agiu  ·  {rt.LastSignalAt:HH:mm}" : "Nenhum sinal ainda";
        }
        if (_lastText.Text == text) return;
        _lastText.Text = text;
        var old = _lastDot.Image;
        _lastDot.Image = Ui.SignalBitmap(color, 56);
        old?.Dispose();
    }

    public static List<string> ActiveOutputs(AppConfig cfg)
    {
        var list = new List<string>();
        if (cfg.PcRgb.OpenRgb.Enabled)
            list.Add(cfg.PcRgb.OpenRgb.Devices is { } d ? $"OpenRGB ({d.Count} dispositivo{(d.Count == 1 ? "" : "s")})" : "OpenRGB");
        if (cfg.Lamp.IsActive) list.Add("lâmpada Wi-Fi");
        if (cfg.PcRgb.Corsair) list.Add(DeviceRouting.Corsair);
        if (cfg.PcRgb.Logitech) list.Add(DeviceRouting.Logitech);
        if (cfg.PcRgb.Msi) list.Add(DeviceRouting.Msi);
        return list;
    }

    private void InstallHooks()
    {
        try
        {
            _hooks.Install(Program.ExePath);
            _status.ForeColor = Ui.Ok;
            _status.Text = "Hooks instalados. Sessões já abertas do Claude Code podem precisar ser reiniciadas.";
        }
        catch (Exception ex)
        {
            Storage.Log($"hooks: {ex}");
            _status.ForeColor = Ui.Error;
            _status.Text = ex.Message;
        }
        OnShown();
    }

    private void Test(string evt)
    {
        if (SignalHost.Current is null) { _status.ForeColor = Ui.Error; _status.Text = "O app ainda está iniciando."; return; }
        LampService.TestSignal(Storage.LoadConfig(), evt);
        _status.ForeColor = Ui.Ok;
        _status.Text = $"Testando \"{SignalsPage.DisplayName(evt)}\".";
    }
}
