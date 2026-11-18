using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Lâmpada Tuya: Device ID, IP, local key, protocolo e busca da local key na nuvem.</summary>
internal sealed class LampConnectionPanel : FlowLayoutPanel
{
    private readonly TextBox _deviceId = new() { Width = 260 };
    private readonly TextBox _ip = new() { Width = 140 };
    private readonly TextBox _localKey = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly ComboBox _protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly Label _status = Ui.Status();
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 640, Height = 80, Visible = false };

    private readonly ComboBox _region = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _accessId = new() { Width = 260 };
    private readonly TextBox _secret = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly ListBox _devices = new() { Width = 520, Height = 70 };
    private readonly Label _cloudStatus = Ui.Status();
    private readonly FlowLayoutPanel _cloud;

    private readonly Button _scanBtn, _probeBtn, _saveBtn, _fetchBtn, _useBtn;

    public LampConnectionPanel()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Margin = Padding.Empty;

        _protocol.Items.AddRange(["3.5", "3.4", "3.3", "3.1"]);
        _region.Items.AddRange([.. CloudKeys.Regions]);

        var show = Ui.Check("mostrar");
        show.CheckedChanged += (_, _) => _localKey.UseSystemPasswordChar = !show.Checked;

        _scanBtn = Ui.Button("Procurar na rede", async (_, _) => await ScanAsync());
        _probeBtn = Ui.Button("Testar / detectar protocolo", async (_, _) => await ProbeAsync());
        _saveBtn = Ui.Button("Salvar", (_, _) => Save(), primary: true);

        var form = Ui.Form();
        Ui.AddField(form, "Device ID", _deviceId);
        Ui.AddField(form, "IP da lâmpada", _ip, _scanBtn);
        Ui.AddField(form, "Local key", _localKey, show);
        Ui.AddField(form, "Protocolo", _protocol, _probeBtn);
        Controls.Add(form);
        Controls.Add(Ui.Row(_saveBtn, _status));
        Controls.Add(_output);

        // Nuvem: só para obter a local key, uma vez.
        var toggleCloud = new LinkLabel { Text = "Não tem a local key? Passo a passo da conta Tuya e busca na nuvem", AutoSize = true, Margin = new Padding(0, 10, 0, 4), LinkColor = Ui.Accent, Font = Ui.Body };
        Controls.Add(toggleCloud);

        _fetchBtn = Ui.Button("Buscar dispositivos", async (_, _) => await FetchAsync());
        _useBtn = Ui.Button("Usar selecionado", (_, _) => UseSelected());
        var cloudForm = Ui.Form();
        Ui.AddField(cloudForm, "Região", _region);
        Ui.AddField(cloudForm, "Access ID", _accessId);
        Ui.AddField(cloudForm, "Access Secret", _secret);
        _cloud = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Visible = false, Margin = new Padding(16, 0, 0, 0),
        };
        AddTuyaGuide(_cloud);
        _cloud.Controls.Add(cloudForm);
        _cloud.Controls.Add(Ui.Row(_fetchBtn, _cloudStatus));
        _cloud.Controls.Add(_devices);
        _cloud.Controls.Add(_useBtn);
        Controls.Add(_cloud);
        toggleCloud.LinkClicked += (_, _) => _cloud.Visible = !_cloud.Visible;
    }

    /// <summary>Passo a passo para obter Access ID / Secret na plataforma Tuya (feito uma única vez).</summary>
    private static void AddTuyaGuide(FlowLayoutPanel panel)
    {
        const int width = 620;
        Label Step(string text)
        {
            var l = Ui.Hint(text);
            l.MaximumSize = new Size(width, 0);
            l.ForeColor = Ui.Text;
            l.Margin = new Padding(0, 4, 0, 0);
            return l;
        }
        Control Links(params LinkLabel[] links)
        {
            var row = Ui.Row(links);
            row.Margin = new Padding(16, 0, 0, 2);
            return row;
        }

        panel.Controls.Add(new Label { Text = "Como obter a local key (uma vez só)", AutoSize = true, Font = Ui.BodyBold, ForeColor = Ui.Text, Margin = new Padding(0, 4, 0, 0) });
        panel.Controls.Add(Step("1. Crie uma conta de desenvolvedor Tuya (gratuita). Qualquer e-mail serve; não precisa ser o do app Smart Life."));
        panel.Controls.Add(Links(Ui.Link("Criar conta em auth.tuya.com", "https://auth.tuya.com/")));
        panel.Controls.Add(Step("2. Entre na plataforma e crie um projeto: Cloud → Development → Create Cloud Project. " +
                                "Industry = Smart Home, Development Method = Smart Home e o data center da sua conta (Brasil: Western America)."));
        panel.Controls.Add(Links(Ui.Link("Abrir platform.tuya.com", "https://platform.tuya.com/")));
        panel.Controls.Add(Step("3. No projeto: Devices → Link App Account → Add App Account → Tuya App Account Authorization, " +
                                "e escaneie o QR code pelo app Smart Life (aba Eu, ícone de scanner)."));
        var warn = Step("Apps de marca própria (Elgin Smart e outros) mostram \"QR code has expired\": remova a lâmpada do app da marca, " +
                        "adicione-a no Smart Life e escaneie por ele.");
        warn.ForeColor = Ui.Warn;
        warn.Margin = new Padding(16, 2, 0, 0);
        panel.Controls.Add(warn);
        panel.Controls.Add(Step("4. Na aba Overview do projeto, copie o Access ID e o Access Secret (use o ícone de copiar)."));
        panel.Controls.Add(Step("5. Preencha abaixo, clique em Buscar dispositivos, escolha a lâmpada e clique em Usar selecionado. " +
                                "O Secret não é salvo. Se parear a lâmpada de novo em algum app, a local key muda e este passo precisa ser repetido."));
        var spacer = Step("");
        spacer.Margin = new Padding(0, 2, 0, 2);
        panel.Controls.Add(spacer);
    }

    public void Reload()
    {
        var cfg = Storage.LoadConfig().Lamp;
        _deviceId.Text = cfg.DeviceId ?? "";
        _ip.Text = cfg.Ip ?? "";
        _localKey.Text = cfg.LocalKey ?? "";
        _protocol.SelectedItem = _protocol.Items.Contains(cfg.ProtocolVersion) ? cfg.ProtocolVersion : "3.3";
        _region.SelectedItem ??= "WesternAmerica";
        if (!cfg.IsConfigured) _cloud.Visible = true; // ainda sem local key: o passo a passo já aparece aberto
    }

    private AppConfig Collect()
    {
        var cfg = Storage.LoadConfig();
        cfg.Lamp.DeviceId = _deviceId.Text.Trim();
        cfg.Lamp.Ip = _ip.Text.Trim();
        cfg.Lamp.LocalKey = _localKey.Text.Trim();
        cfg.Lamp.ProtocolVersion = (string?)_protocol.SelectedItem ?? "3.3";
        return cfg;
    }

    private void Save()
    {
        var cfg = Collect();
        if (cfg.Lamp.LocalKey is { Length: > 0 and not 16 })
        {
            _status.ForeColor = Ui.Error;
            _status.Text = $"A local key deve ter 16 caracteres (tem {cfg.Lamp.LocalKey.Length}).";
            return;
        }
        Storage.SaveConfig(cfg);
        _status.ForeColor = Ui.Ok;
        _status.Text = $"Salvo às {DateTime.Now:HH:mm:ss}.";
    }

    private void Log(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(line)); return; }
        _output.Visible = true;
        _output.AppendText(line + Environment.NewLine);
    }

    private async Task ScanAsync()
    {
        _output.Clear();
        var myId = _deviceId.Text.Trim();
        await Ui.Busy(_status, "Procurando (até ~15 s)...", [_scanBtn, _probeBtn, _saveBtn], async () =>
        {
            var found = await TuyaDiscovery.ScanAsync(TimeSpan.FromSeconds(8), myId.Length > 0 ? myId : null, d => Log("UDP: " + d));
            if (found.FirstOrDefault(d => d.GwId == myId) is { } me)
            {
                _ip.Text = me.Ip;
                if (_protocol.Items.Contains(me.Version)) _protocol.SelectedItem = me.Version;
                return $"Lâmpada encontrada em {me.Ip} (protocolo {me.Version}). Clique em Salvar.";
            }
            Log("Nenhum anúncio UDP da sua lâmpada (o roteador pode bloquear). Procurando porta TCP 6668...");
            var candidates = await TuyaDiscovery.FindTcpCandidatesAsync();
            foreach (var c in candidates) Log("TCP 6668 aberta: " + c);
            if (candidates.Count == 1)
            {
                _ip.Text = candidates[0];
                return $"Um dispositivo Tuya em {candidates[0]}. Use \"Testar\" para confirmar.";
            }
            return candidates.Count == 0 ? "Nada encontrado na rede." : "Vários candidatos: escolha o IP e use \"Testar\".";
        });
    }

    private async Task ProbeAsync()
    {
        _output.Clear();
        var cfg = Collect();
        await Ui.Busy(_status, "Testando protocolos 3.5 / 3.4 / 3.3 / 3.1...", [_scanBtn, _probeBtn, _saveBtn], async () =>
        {
            if (!cfg.Lamp.IsConfigured || string.IsNullOrWhiteSpace(cfg.Lamp.Ip))
                throw new InvalidOperationException("Preencha Device ID, IP e local key.");
            var result = await Task.Run(() => LampClient.ProbeAsync(cfg.Lamp, Log))
                ?? throw new InvalidOperationException("Nenhum protocolo respondeu. Feche o app Smart Life no celular e confira IP e local key.");
            cfg.Lamp.ProtocolVersion = result.Version;
            cfg.Lamp.DpsProfile = DpsProfile.Detect(result.Dps).Name;
            Storage.SaveConfig(cfg);
            _protocol.SelectedItem = result.Version;
            return $"Conectado! Protocolo {result.Version}, perfil {cfg.Lamp.DpsProfile}. Configuração salva.";
        });
    }

    private async Task FetchAsync()
    {
        _devices.Items.Clear();
        var region = (string)_region.SelectedItem!;
        var accessId = _accessId.Text;
        var secret = _secret.Text;
        var anyId = _deviceId.Text.Trim();
        await Ui.Busy(_cloudStatus, "Consultando a nuvem Tuya...", [_fetchBtn, _useBtn], async () =>
        {
            if (accessId.Trim().Length == 0 || secret.Trim().Length == 0)
                throw new InvalidOperationException("Preencha Access ID e Access Secret.");
            if (anyId.Length == 0)
                throw new InvalidOperationException("Preencha o Device ID acima (está em platform.tuya.com → Devices).");
            var devices = await CloudKeys.FetchDevicesAsync(region, accessId, secret, anyId);
            foreach (var d in devices) _devices.Items.Add(d);
            if (_devices.Items.Count > 0) _devices.SelectedIndex = Math.Max(0, devices.ToList().FindIndex(d => d.Category == "dj"));
            return $"{devices.Count} dispositivo(s). Selecione e clique em \"Usar selecionado\".";
        });
    }

    private void UseSelected()
    {
        if (_devices.SelectedItem is not CloudDevice d) return;
        _deviceId.Text = d.Id;
        _localKey.Text = d.LocalKey;
        _secret.Clear();
        _cloudStatus.ForeColor = Ui.Ok;
        _cloudStatus.Text = $"Local key de \"{d.Name}\" preenchida. Agora use \"Testar / detectar protocolo\".";
    }
}
