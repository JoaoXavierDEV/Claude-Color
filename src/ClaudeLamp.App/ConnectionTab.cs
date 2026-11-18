using ClaudeLamp.Core;

namespace ClaudeLamp.App;

/// <summary>Device ID, IP, local key, protocolo e busca da local key na nuvem Tuya.</summary>
internal sealed class ConnectionTab : UserControl
{
    private readonly TextBox _deviceId = new() { Width = 260 };
    private readonly TextBox _ip = new() { Width = 140 };
    private readonly TextBox _localKey = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly ComboBox _protocol = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    private readonly Label _status = Ui.Label("");
    private readonly TextBox _output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 760, Height = 90 };

    private readonly ComboBox _region = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _accessId = new() { Width = 260 };
    private readonly TextBox _secret = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly ListBox _devices = new() { Width = 520, Height = 70 };
    private readonly Label _cloudStatus = Ui.Label("");

    private readonly Button _scanBtn, _probeBtn, _saveBtn, _fetchBtn, _useBtn;

    public ConnectionTab()
    {
        _protocol.Items.AddRange(["3.5", "3.4", "3.3", "3.1"]);
        _region.Items.AddRange([.. CloudKeys.Regions]);

        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

        var show = new CheckBox { Text = "mostrar", AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        show.CheckedChanged += (_, _) => _localKey.UseSystemPasswordChar = !show.Checked;

        _scanBtn = Ui.Button("Procurar na rede", async (_, _) => await ScanAsync());
        _probeBtn = Ui.Button("Testar / detectar protocolo", async (_, _) => await ProbeAsync());
        _saveBtn = Ui.Button("Salvar", (_, _) => Save(), 90);

        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };
        form.Controls.Add(Ui.Label("Device ID")); form.Controls.Add(_deviceId); form.Controls.Add(new Label());
        form.Controls.Add(Ui.Label("IP da lâmpada")); form.Controls.Add(_ip); form.Controls.Add(_scanBtn);
        form.Controls.Add(Ui.Label("Local key")); form.Controls.Add(_localKey); form.Controls.Add(show);
        form.Controls.Add(Ui.Label("Protocolo")); form.Controls.Add(_protocol); form.Controls.Add(_probeBtn);

        root.Controls.Add(Ui.Label("Lâmpada na rede local", bold: true));
        root.Controls.Add(form);
        root.Controls.Add(Ui.Row(_saveBtn, _status));
        root.Controls.Add(_output);

        // Nuvem
        _fetchBtn = Ui.Button("Buscar dispositivos", async (_, _) => await FetchAsync());
        _useBtn = Ui.Button("Usar selecionado", (_, _) => UseSelected());
        var cloud = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        cloud.Controls.Add(Ui.Label("Região")); cloud.Controls.Add(_region);
        cloud.Controls.Add(Ui.Label("Access ID")); cloud.Controls.Add(_accessId);
        cloud.Controls.Add(Ui.Label("Access Secret")); cloud.Controls.Add(_secret);
        var group = new GroupBox { Text = "Buscar local key na nuvem Tuya (platform.tuya.com → projeto → Overview)", AutoSize = true, Padding = new Padding(10), Margin = new Padding(3, 12, 3, 3) };
        var gflow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        gflow.Controls.Add(cloud);
        gflow.Controls.Add(Ui.Row(_fetchBtn, _cloudStatus));
        gflow.Controls.Add(_devices);
        gflow.Controls.Add(_useBtn);
        gflow.Controls.Add(Ui.Label("O Access Secret só é usado nesta busca e não é salvo."));
        group.Controls.Add(gflow);
        root.Controls.Add(group);

        Controls.Add(root);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var cfg = Storage.LoadConfig();
        _deviceId.Text = cfg.DeviceId ?? "";
        _ip.Text = cfg.Ip ?? "";
        _localKey.Text = cfg.LocalKey ?? "";
        _protocol.SelectedItem = _protocol.Items.Contains(cfg.ProtocolVersion) ? cfg.ProtocolVersion : "3.3";
        _region.SelectedItem = "WesternAmerica";
    }

    private LampConfig Collect()
    {
        var cfg = Storage.LoadConfig();
        cfg.DeviceId = _deviceId.Text.Trim();
        cfg.Ip = _ip.Text.Trim();
        cfg.LocalKey = _localKey.Text.Trim();
        cfg.ProtocolVersion = (string?)_protocol.SelectedItem ?? "3.3";
        return cfg;
    }

    private void Save()
    {
        var cfg = Collect();
        if (cfg.LocalKey is { Length: > 0 and not 16 })
        {
            _status.ForeColor = Ui.Error;
            _status.Text = $"A local key deve ter 16 caracteres (tem {cfg.LocalKey.Length}).";
            return;
        }
        Storage.SaveConfig(cfg);
        _status.ForeColor = Ui.Ok;
        _status.Text = $"Salvo às {DateTime.Now:HH:mm:ss}.";
    }

    private void Log(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(line)); return; }
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
                return $"Lâmpada encontrada em {me.Ip} (protocolo {me.Version}). Clique Salvar.";
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
            if (!cfg.IsConfigured || string.IsNullOrWhiteSpace(cfg.Ip))
                throw new InvalidOperationException("Preencha Device ID, IP e local key.");
            var result = await Task.Run(() => LampClient.ProbeAsync(cfg, Log))
                ?? throw new InvalidOperationException("Nenhum protocolo respondeu. Feche o app Smart Life no celular e confira IP e local key.");
            cfg.ProtocolVersion = result.Version;
            cfg.DpsProfile = DpsProfile.Detect(result.Dps).Name;
            Storage.SaveConfig(cfg);
            _protocol.SelectedItem = result.Version;
            return $"Conectado! Protocolo {result.Version}, perfil {cfg.DpsProfile}. Configuração salva.";
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
            return $"{devices.Count} dispositivo(s). Selecione e clique \"Usar selecionado\".";
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
