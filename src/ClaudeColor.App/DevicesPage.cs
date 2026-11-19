using ClaudeColor.Core;
using ClaudeColor.Rgb;

namespace ClaudeColor.App;

/// <summary>O que acende: OpenRGB, lâmpada Wi-Fi e integrações nativas (iCUE, G HUB, Mystic Light).</summary>
internal sealed class DevicesPage : Page
{
    private sealed record DeviceItem(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly Card _openRgbCard, _lampCard;
    private readonly CheckBox _openRgbAutoStart = Ui.Check("Iniciar o servidor do OpenRGB automaticamente");
    private readonly CheckedListBox _openRgbDevices = new()
    {
        Width = 640, Height = 150, CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
        Margin = new Padding(0, 4, 0, 4), Font = Ui.Body,
    };
    private readonly Label _openRgbStatus = Ui.Label(""), _lampStatus = Ui.Label("");
    private readonly Label _openRgbEmpty = Ui.Hint("Clique em \"Carregar dispositivos\" para ver o que o OpenRGB encontrou no seu PC.");
    private readonly FlowLayoutPanel _lampBody;
    private readonly LampConnectionPanel _lampConnection = new();
    private readonly LampControlPanel _lampControl = new();

    private readonly CheckBox _corsair = Ui.Check("Corsair iCUE: memória RAM, placa-mãe e o que mais o iCUE expuser", bold: true);
    private readonly CheckBox _logitech = Ui.Check("Logitech G HUB: teclado, mouse e periféricos LIGHTSYNC", bold: true);
    private readonly CheckBox _msi = Ui.Check("MSI Mystic Light: placa de vídeo e placa-mãe MSI", bold: true);
    private readonly CheckBox _dynamic = Ui.Check("Iluminação dinâmica do Windows 11 (experimental): teclados, mouses e luminárias compatíveis", bold: true);
    private readonly Label _corsairSdk = Ui.Label(""), _logitechSdk = Ui.Label(""), _msiSdk = Ui.Label("");

    private readonly TextBox _output = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 640, Height = 120, Visible = false,
        BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9F), BackColor = Ui.Background,
    };
    private readonly Label _status = Ui.Status();
    private readonly Button _detect, _test, _loadDevices, _checkAll, _uncheckAll, _newProfile;
    private readonly Label _profileStatus = Ui.InlineStatus();
    private readonly Label _profileHint = Ui.Hint("");
    private readonly Card _profileCard;
    private readonly Action<Type> _navigate;
    private bool _loading;

    public override string NavTitle => "Dispositivos";

    public DevicesPage(Action<Type> navigate) : base("Dispositivos", "O que acende quando o Claude precisa de você. Use quantos quiser ao mesmo tempo.")
    {
        _navigate = navigate;

        // Perfil
        _profileCard = Add(new Card("Perfil"));
        _newProfile = Ui.Button("Criar perfil a partir do Padrão", (_, _) => CreateProfileFromDefault(), primary: true);
        var change = Ui.Link("Trocar perfil", () => _navigate(typeof(ProfilesPage)));
        change.Margin = new Padding(0, 11, 0, 4);
        _profileCard.Add(Ui.Row(_profileStatus, change),
            _profileCard.Wrap(_profileHint), _newProfile);

        // OpenRGB
        _openRgbCard = Add(new Card("OpenRGB  ·  recomendado",
            "Controla placa-mãe, memória, placa de vídeo, fans, fitas ARGB, teclados, mouses e muito mais, de vários fabricantes, num só lugar. " +
            "O Claude Color salva o modo de cada dispositivo antes do sinal e o devolve depois.", "Usar"));
        _loadDevices = Ui.Button("Carregar dispositivos", async (_, _) => await LoadOpenRgbDevicesAsync());
        _openRgbCard.Add(_openRgbStatus, _openRgbAutoStart,
            Ui.Row(_loadDevices,
                _checkAll = Ui.Button("Marcar todos", (_, _) => CheckAll(true)),
                _uncheckAll = Ui.Button("Desmarcar todos", (_, _) => CheckAll(false))),
            _openRgbEmpty, _openRgbDevices,
            _openRgbCard.Wrap(Ui.Hint("Marque só o que nenhum outro software está controlando ao mesmo tempo (iCUE, G HUB, MSI Center, MasterCTRL...). " +
                                      "Memória e placa-mãe exigem o OpenRGB como administrador. Instale pelo openrgb.org ou: winget install OpenRGB.OpenRGB")));

        // Lâmpada
        _lampCard = Add(new Card("Lâmpada Wi-Fi (Tuya / Smart Life)",
            "Lâmpadas Tuya, Elgin Smart, Smart Life e marcas compatíveis, controladas pela rede local (sem nuvem).", "Usar"));
        _lampBody = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Margin = Padding.Empty,
        };
        _lampBody.Controls.Add(Section("Conexão"));
        _lampBody.Controls.Add(_lampConnection);
        _lampBody.Controls.Add(Section("Controle manual"));
        _lampBody.Controls.Add(_lampControl);
        _lampCard.Add(_lampStatus, _lampBody);

        // Integrações nativas
        var native = Add(new Card("Integrações nativas  ·  opcional",
            "Use os SDKs dos fabricantes quando preferir não passar pelo OpenRGB. Cada software volta ao próprio perfil depois do sinal. " +
            "As DLLs x64 dos SDKs vão em <pasta do app>\\x64 e não acompanham o Claude Color."));
        native.Add(_corsair, _corsairSdk, Indent(Ui.Hint("No iCUE: Configurações → Geral → \"Habilitar SDK\".")));
        native.Add(_logitech, _logitechSdk, Indent(Ui.Hint("No G HUB: permita que jogos e aplicativos controlem a iluminação.")));
        native.Add(_msi, _msiSdk, Indent(Ui.Hint("Exige o app como administrador (página Sistema). A GPU recebe a cor fixa: o Mystic Light é lento demais para piscar.")));
        var dynamicHint = Indent(native.Wrap(Ui.Hint(
            "Sem SDK de fabricante: usa os dispositivos listados em Configurações → Personalização → Iluminação dinâmica. " +
            "Com o app em segundo plano, o Windows só entrega o controle a apps empacotados escolhidos ali, então hoje funciona " +
            "de forma limitada. Não marque o mesmo dispositivo no OpenRGB.")));
        native.Add(_dynamic, dynamicHint, Indent(Ui.Link("Abrir Iluminação dinâmica", "ms-settings:personalization-lighting")));

        // Diagnóstico
        var diag = Add(new Card("Testar", "Detectar lista o que cada integração ativa enxerga; Testar acende tudo em amarelo por 5 s."));
        _detect = Ui.Button("Detectar dispositivos", async (_, _) => await DetectAsync());
        _test = Ui.Button("Testar", (_, _) => Test(), primary: true);
        ProfileService.Changed += () => { if (!IsDisposed && IsHandleCreated && Visible) BeginInvoke(OnShown); };
        diag.Add(Ui.Row(_test, _detect, _status), _output);

        foreach (var cb in new[] { _corsair, _logitech, _msi, _dynamic, _openRgbAutoStart, _openRgbCard.Toggle!, _lampCard.Toggle! })
            cb.CheckedChanged += (_, _) => Save();
        _openRgbDevices.ItemCheck += (_, _) => BeginInvoke(Save); // ItemCheck dispara antes de mudar o estado
    }

    private static Label Section(string text) =>
        new() { Text = text, AutoSize = true, Font = Ui.BodyBold, ForeColor = Ui.Text, Margin = new Padding(0, 12, 0, 2) };

    private static Control Indent(Control c)
    {
        c.Margin = new Padding(22, 0, 0, 10);
        return c;
    }

    public override void OnShown()
    {
        _loading = true;
        var cfg = Storage.LoadConfig();
        _corsair.Checked = cfg.PcRgb.Corsair;
        _logitech.Checked = cfg.PcRgb.Logitech;
        _msi.Checked = cfg.PcRgb.Msi;
        _dynamic.Checked = cfg.PcRgb.DynamicLighting;
        _openRgbCard.Toggle!.Checked = cfg.PcRgb.OpenRgb.Enabled;
        _openRgbAutoStart.Checked = cfg.PcRgb.OpenRgb.AutoStart;
        // A lista acompanha o perfil ativo (pode ter mudado desde a última visita).
        // Sem escolha salva (null = padrão automático), a lista só aparece depois de "Carregar dispositivos".
        var chosen = cfg.PcRgb.OpenRgb.Devices;
        if (chosen is null) _openRgbDevices.Items.Clear();
        foreach (var d in chosen ?? [])
            if (!_openRgbDevices.Items.Cast<DeviceItem>().Any(i => i.Key == d)) _openRgbDevices.Items.Add(new DeviceItem(d, d));
        for (int i = 0; i < _openRgbDevices.Items.Count; i++)
            _openRgbDevices.SetItemChecked(i, chosen!.Contains(((DeviceItem)_openRgbDevices.Items[i]).Key));
        _lampCard.Toggle!.Checked = cfg.Lamp.Enabled;
        _lampConnection.Reload();
        _lampControl.Reload();
        _loading = false;
        UpdateStatus(cfg);
    }

    private void UpdateStatus(AppConfig cfg)
    {
        var x64 = Path.Combine(AppContext.BaseDirectory, "x64");
        Sdk(_corsairSdk, CorsairTarget.SdkAvailable, $"{NativeSdk.CorsairDll} encontrado", $"{NativeSdk.CorsairDll} não encontrado em {x64}");
        Sdk(_logitechSdk, LogitechTarget.SdkAvailable, $"{NativeSdk.LogitechDll} encontrado", $"{NativeSdk.LogitechDll} não encontrado em {x64}");
        Sdk(_msiSdk, MsiTarget.SdkAvailable && Elevation.IsElevated, "SDK encontrado e app rodando como administrador",
            !MsiTarget.SdkAvailable ? $"{NativeSdk.MsiDll} não encontrado" : "SDK encontrado, mas o app não está como administrador");

        if (OpenRgbTarget.ExePath is { } exe) Ui.SetStatus(_openRgbStatus, true, $"OpenRGB instalado ({exe})");
        else Ui.SetStatus(_openRgbStatus, null, "OpenRGB não encontrado. Se ele roda em outro lugar, deixe o servidor SDK aberto na porta 6742.");

        // O Padrão é fixo: só o OpenRGB, com todos os dispositivos. A conexão da lâmpada continua editável.
        bool locked = cfg.ActiveIsDefault();
        foreach (Control c in new Control[] { _corsair, _logitech, _msi, _dynamic, _openRgbCard.Toggle!, _lampCard.Toggle!, _openRgbDevices, _checkAll, _uncheckAll })
            c.Enabled = !locked;
        _newProfile.Visible = locked;
        Ui.SetStatus(_profileStatus, true, $"Perfil ativo: {cfg.ActiveProfile}  ·  {cfg.Active().Summary()}");
        _profileHint.Text = locked
            ? "O Padrão usa só o OpenRGB, com todos os dispositivos, e não pode ser alterado. Para combinar outros controladores " +
              "(iCUE, G HUB, Mystic Light, Iluminação dinâmica, lâmpada) ou escolher dispositivos, crie um perfil."
            : "As mudanças desta página ficam salvas neste perfil. Credenciais da lâmpada valem para todos os perfis.";

        _lampBody.Visible = cfg.Lamp.Enabled || locked;
        _openRgbDevices.Visible = _openRgbDevices.Items.Count > 0;
        _openRgbEmpty.Visible = !_openRgbDevices.Visible;
        if (!cfg.Lamp.Enabled) Ui.SetStatus(_lampStatus, null, "Desativada");
        else if (cfg.Lamp.IsConfigured) Ui.SetStatus(_lampStatus, true, $"Configurada ({cfg.Lamp.Ip}, protocolo {cfg.Lamp.ProtocolVersion})");
        else Ui.SetStatus(_lampStatus, false, "Falta o Device ID e a local key");
    }

    private static void Sdk(Label l, bool ok, string good, string bad)
    {
        Ui.SetStatus(l, ok ? true : null, ok ? good : bad);
        l.Margin = new Padding(22, 0, 0, 0);
        l.Font = Ui.Small;
    }

    private void Save()
    {
        if (_loading) return;
        var cfg = Storage.LoadConfig();
        cfg.PcRgb.Corsair = _corsair.Checked;
        cfg.PcRgb.Logitech = _logitech.Checked;
        cfg.PcRgb.Msi = _msi.Checked;
        cfg.PcRgb.DynamicLighting = _dynamic.Checked;
        cfg.PcRgb.OpenRgb.Enabled = _openRgbCard.Toggle!.Checked;
        cfg.PcRgb.OpenRgb.AutoStart = _openRgbAutoStart.Checked;
        if (_openRgbDevices.Items.Count > 0)
            cfg.PcRgb.OpenRgb.Devices = _openRgbDevices.CheckedItems.Cast<DeviceItem>().Select(d => d.Key).ToList();
        cfg.Lamp.Enabled = _lampCard.Toggle!.Checked;
        cfg.SaveActiveProfile(); // com o Padrão ativo, desfaz qualquer mudança
        Storage.SaveConfig(cfg);
        UpdateStatus(cfg);
        _status.ForeColor = Ui.Ok;
        _status.Text = "Salvo.";
    }

    private void CreateProfileFromDefault()
    {
        var cfg = Storage.LoadConfig();
        var p = cfg.CreateProfile(cfg.FreeName("Meu perfil"), Profiles.DefaultName);
        cfg.ActivateProfile(p.Name);
        Storage.SaveConfig(cfg);
        ProfileService.RaiseChanged();
        OnShown();
        _status.ForeColor = Ui.Ok;
        _status.Text = $"Perfil \"{p.Name}\" criado e ativo. Renomeie na página Perfis.";
    }

    private void CheckAll(bool on)
    {
        _loading = true;
        for (int i = 0; i < _openRgbDevices.Items.Count; i++) _openRgbDevices.SetItemChecked(i, on);
        _loading = false;
        Save();
    }

    private async Task LoadOpenRgbDevicesAsync()
    {
        var host = SignalHost.Current;
        await Ui.Busy(_status, "Conectando ao OpenRGB (pode iniciar o servidor)...", [_loadDevices, _detect, _test], async () =>
        {
            if (host is null) throw new InvalidOperationException("O app ainda está iniciando.");
            var devices = await host.PcRgb.OpenRgbDevicesAsync(Storage.LoadConfig().PcRgb);
            _loading = true;
            _openRgbDevices.Items.Clear();
            foreach (var (key, label, selected) in devices) _openRgbDevices.Items.Add(new DeviceItem(key, label), selected);
            _loading = false;
            Save();
            return $"{devices.Count} dispositivo(s) no OpenRGB.";
        });
    }

    private async Task DetectAsync()
    {
        _output.Clear();
        var host = SignalHost.Current;
        await Ui.Busy(_status, "Consultando...", [_detect, _test, _loadDevices], async () =>
        {
            if (host is null) throw new InvalidOperationException("O app ainda está iniciando.");
            await host.PcRgb.ReleaseAllAsync();
            var lines = await host.PcRgb.DetectAsync(Storage.LoadConfig().PcRgb);
            _output.Visible = true;
            _output.Text = string.Join(Environment.NewLine, lines);
            return "Detecção concluída.";
        });
    }

    private void Test()
    {
        LampService.TestAllDevices();
        _status.ForeColor = Ui.Ok;
        _status.Text = "Testando: amarelo, depois volta ao normal.";
    }
}
