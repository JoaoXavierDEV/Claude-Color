using System.Diagnostics;
using System.Net.Sockets;
using ClaudeColor.Core;
using ClaudeColor.Rgb;

namespace ClaudeColor.App;

/// <summary>O que precisa estar instalado para o perfil ativo funcionar, e se está funcionando.</summary>
internal sealed class TroubleshootingPage : Page
{
    private const string OpenRgbUrl = "https://openrgb.org/releases.html";
    private const string OpenRgbWinget = "winget install OpenRGB.OpenRGB";
    private const string CorsairSdkUrl = "https://github.com/CorsairOfficial/cue-sdk/releases";
    private const string LogitechSdkUrl = "https://www.logitechg.com/sdk/LED_SDK_9.00.zip";
    private const string ClaudeCodeUrl = "https://claude.com/claude-code";

    /// <summary>Uma linha da verificação: status, explicação e ações (link ou botão).</summary>
    private sealed record Check(string Title, bool? Ok, string Detail, params (string Text, Action Act)[] Actions);

    private readonly Action<Type> _navigate;
    private readonly FlowLayoutPanel _results = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        Margin = new Padding(0, 6, 0, 0),
    };
    private readonly Label _status = Ui.Status(), _testStatus = Ui.Status();
    private readonly Button _run;
    private readonly Card _checks;

    public override string NavTitle => "Solução de problemas";

    public TroubleshootingPage(Action<Type> navigate) : base("Solução de problemas",
        "O que precisa estar instalado para o perfil ativo funcionar, e se cada parte está respondendo.")
    {
        _navigate = navigate;

        _checks = Add(new Card("Verificação",
            "Confere o Claude Code, os hooks e cada controlador do perfil ativo. Itens em amarelo são opcionais ou precisam de atenção; em vermelho, impedem o sinal."));
        _run = Ui.Button("Verificar de novo", async (_, _) => await RunAsync(), primary: true);
        _checks.Add(Ui.Row(_run, _status), _results);

        var test = Add(new Card("Teste final", "Acende tudo do perfil ativo em amarelo por 5 s e depois devolve cada dispositivo ao modo anterior."));
        test.Add(Ui.Row(Ui.Button("Testar agora", (_, _) =>
        {
            if (SignalHost.Current is null) { _testStatus.ForeColor = Ui.Error; _testStatus.Text = "O app ainda está iniciando."; return; }
            LampService.TestAllDevices();
            _testStatus.ForeColor = Ui.Ok;
            _testStatus.Text = "Testando: amarelo, depois volta ao normal. Algo não acendeu? Veja os sintomas abaixo.";
        }), _testStatus));

        var symptoms = Add(new Card("Sintomas comuns"));
        foreach (var (symptom, fix) in Symptoms)
            symptoms.Add(new Label { Text = symptom, AutoSize = true, Font = Ui.BodyBold, ForeColor = Ui.Text, Margin = new Padding(0, 6, 0, 0) },
                symptoms.Wrap(Ui.Hint(fix)));
        symptoms.Add(Ui.Row(Ui.Link("Abrir log", () => TrayContext.OpenPath(Storage.LogPath)),
            Ui.Link("Abrir config.json", () => TrayContext.OpenPath(Storage.ConfigPath))));
    }

    private static readonly (string Symptom, string Fix)[] Symptoms =
    [
        ("Nada acende", "Confira acima se os hooks estão instalados e o perfil tem um controlador ativo. Reinicie a sessão do Claude Code e veja o log."),
        ("O OpenRGB não lista memória ou placa-mãe", "Esses dispositivos só aparecem com o OpenRGB como administrador. Reinicie o Claude Color como administrador (ele inicia o OpenRGB) e feche o iCUE, que pode segurar o acesso à memória."),
        ("Cores \"brigando\" ou voltando sozinhas", "Outro software controla o mesmo dispositivo (iCUE, G HUB, MSI Center, MasterCTRL, Armoury Crate). Crie um perfil, desmarque o dispositivo no OpenRGB e use a integração nativa, ou desligue a iluminação no software do fabricante."),
        ("Fans ou fitas não voltam ao efeito original", "Confira se outro software não está reaplicando as cores e se o log mostra \"openrgb: ... restaurado para\"."),
        ("Memória Corsair não acende pelo iCUE", "Ative \"Habilitar SDK\" no iCUE (Configurações → Geral) e coloque iCUESDK.x64_2019.dll na pasta x64 do app."),
        ("Placa de vídeo MSI não muda pelo Mystic Light", "O app precisa rodar como administrador. O log mostra \"Mystic Light não inicializou\" quando não está."),
        ("Teclado / mouse Logitech não acendem", "Deixe o G HUB aberto e permita que aplicativos controlem a iluminação. Desligue a Iluminação dinâmica do Windows para eles."),
        ("Lâmpada: timeout ou nenhum protocolo responde", "Feche o app Smart Life no celular (a lâmpada aceita poucas conexões locais) e confira se a local key é do pareamento atual."),
    ];

    public override void OnShown() => _ = RunAsync();

    private async Task RunAsync()
    {
        if (!_run.Enabled) return;
        int bad = 0, warn = 0;
        await Ui.Busy(_status, "Verificando...", [_run], async () =>
        {
            var checks = await Task.Run(CollectAsync);
            if (InvokeRequired) Invoke(() => Show(checks)); else Show(checks);
            bad = checks.Count(c => c.Ok == false);
            warn = checks.Count(c => c.Ok is null);
            return bad > 0 ? $"{bad} problema(s) para resolver." : warn > 0 ? $"Funcionando, com {warn} aviso(s)." : "Tudo certo.";
        });
        if (bad > 0) _status.ForeColor = Ui.Error;
        else if (warn > 0) _status.ForeColor = Ui.Warn;
    }

    private void Show(IReadOnlyList<Check> checks)
    {
        _results.SuspendLayout();
        foreach (Control c in _results.Controls.Cast<Control>().ToArray()) c.Dispose();
        foreach (var c in checks)
        {
            var title = Ui.InlineStatus();
            title.Margin = new Padding(0, 8, 14, 0);
            Ui.SetStatus(title, c.Ok, c.Title);
            var row = Ui.Row(title);
            foreach (var (text, act) in c.Actions) row.Controls.Add(Ui.Link(text, act));
            foreach (Control l in row.Controls) if (l is LinkLabel) l.Margin = new Padding(0, 9, 10, 0);
            _results.Controls.Add(row);
            var detail = Ui.Hint(c.Detail);
            detail.MaximumSize = new Size(Math.Max(300, _checks.Width - _checks.Padding.Horizontal - 40), 0);
            detail.Margin = new Padding(22, 0, 0, 4);
            _results.Controls.Add(detail);
        }
        _results.ResumeLayout();
    }

    private async Task<IReadOnlyList<Check>> CollectAsync()
    {
        var cfg = Storage.LoadConfig();
        var list = new List<Check>();
        bool admin = Elevation.IsElevated;

        // Claude Code e hooks
        var claudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        list.Add(Directory.Exists(claudeDir)
            ? new Check("Claude Code", true, $"Encontrado ({claudeDir}).")
            : new Check("Claude Code", false, "A pasta ~/.claude não existe: instale o Claude Code e abra uma sessão uma vez.",
                ("Baixar o Claude Code", () => Ui.OpenUrl(ClaudeCodeUrl))));

        var hooks = new HooksInstaller().GetStatus(Program.ExePath);
        list.Add(hooks switch
        {
            HooksStatus.Installed => new Check("Hooks do Claude Code", true, $"Instalados e apontando para este app ({HookHealth.LastHookText()}). Sessões abertas antes da instalação precisam ser reiniciadas."),
            HooksStatus.Outdated => new Check("Hooks do Claude Code", false, "Apontam para outro executável (instalação antiga?).", ("Atualizar hooks", InstallHooks)),
            _ => new Check("Hooks do Claude Code", false, "Sem os hooks o Claude Code não avisa o Claude Color.", ("Instalar hooks", InstallHooks)),
        });

        if (HookHealth.RunAsAdminMark(Program.ExePath) is { } mark)
            list.Add(new Check("Hooks chegando ao app", false,
                "O ClaudeColor.exe está marcado \"Executar como administrador\", então o Claude Code (sem administrador) não consegue abri-lo " +
                "e nenhum sinal chega. " + HookHealth.FixRunAsAdmin(mark.AllUsers),
                ("Abrir a pasta do app", () => TrayContext.OpenPath(AppContext.BaseDirectory))));

        list.Add(cfg.SignalsEnabled
            ? new Check("Sinais", true, "Ligados.")
            : new Check("Sinais", null, "Pausados pela bandeja ou pela Visão geral.", ("Ir para a Visão geral", () => Go(typeof(OverviewPage)))));

        var (notifyOk, notifyText) = Notifier.Status();
        list.Add(notifyOk
            ? new Check("Notificações do Windows", true, notifyText + ".")
            : new Check("Notificações do Windows", null, notifyText + ". As luzes e a barra de tarefas continuam funcionando.",
                ("Abrir configurações", () => Ui.OpenUrl("ms-settings:notifications"))));

        var active = cfg.Active();
        list.Add(OverviewPage.ActiveOutputs(cfg).Count > 0
            ? new Check($"Perfil \"{active.Name}\"", true, $"Usa: {active.Summary()}.", ("Trocar perfil", () => Go(typeof(ProfilesPage))))
            : new Check($"Perfil \"{active.Name}\"", false, "Nenhum controlador ativo neste perfil.", ("Escolher dispositivos", () => Go(typeof(DevicesPage)))));

        // OpenRGB
        if (cfg.PcRgb.OpenRgb.Enabled)
        {
            var o = cfg.PcRgb.OpenRgb;
            var exe = OpenRgbTarget.ExePath;
            list.Add(exe is not null
                ? new Check("OpenRGB instalado", true, exe)
                : new Check("OpenRGB instalado", OpenRgbTarget.ServerResponds(o) ? null : false,
                    $"Não encontrado em Arquivos de Programas. Instale pelo site ou com: {OpenRgbWinget}",
                    ("Baixar o OpenRGB", () => Ui.OpenUrl(OpenRgbUrl)), ("Copiar comando winget", () => Clipboard.SetText(OpenRgbWinget))));

            bool server = OpenRgbTarget.ServerResponds(o);
            if (!server && exe is not null && o.AutoStart)
                list.Add(new Check("Servidor SDK do OpenRGB", null,
                    $"Ainda fechado em {o.Host}:{o.Port}. O Claude Color o inicia no primeiro sinal (ou agora, em \"Carregar dispositivos\").",
                    ("Iniciar agora", () => Go(typeof(DevicesPage)))));
            else if (!server)
                list.Add(new Check("Servidor SDK do OpenRGB", false,
                    $"Nada responde em {o.Host}:{o.Port}. Abra o OpenRGB e ative a aba SDK Server → Start Server, ou instale-o neste PC."));
            else
            {
                IReadOnlyList<(string Key, string Label, bool Selected)> devices = [];
                string? error = null;
                var host = SignalHost.Current;
                try { if (host is not null) devices = await host.PcRgb.OpenRgbDevicesAsync(cfg.PcRgb); }
                catch (Exception ex) { error = ex.Message; }
                int chosen = devices.Count(d => d.Selected);
                bool busOk = devices.Any(d => d.Label.StartsWith(DeviceRouting.TypeName(DeviceRouting.TypeDram)) ||
                                              d.Label.StartsWith(DeviceRouting.TypeName(DeviceRouting.TypeMotherboard)));
                if (host is null)
                    list.Add(new Check("Servidor SDK do OpenRGB", true, $"Respondendo em {o.Host}:{o.Port}."));
                else if (error is not null)
                    list.Add(new Check("Servidor SDK do OpenRGB", false, $"Responde, mas a consulta falhou: {error}"));
                else if (devices.Count == 0)
                    list.Add(new Check("Servidor SDK do OpenRGB", false, "Responde, mas não encontrou nenhum dispositivo. Rode o OpenRGB como administrador e use Rescan Devices."));
                else
                    list.Add(new Check("Servidor SDK do OpenRGB", chosen > 0 ? true : null,
                        $"{devices.Count} dispositivo(s), {chosen} acompanhando o Claude." +
                        (busOk ? "" : " Memória e placa-mãe não apareceram: elas exigem o OpenRGB como administrador."),
                        ("Ver dispositivos", () => Go(typeof(DevicesPage)))));
            }
        }

        // Integrações nativas
        if (cfg.PcRgb.Corsair)
            list.Add(Native(DeviceRouting.Corsair, CorsairTarget.SdkAvailable, NativeSdk.CorsairDll, Running("iCUE"),
                "No iCUE, \"Habilitar SDK\" precisa estar ativo (Configurações → Geral).", ("Baixar o iCUE SDK", () => Ui.OpenUrl(CorsairSdkUrl))));
        if (cfg.PcRgb.Logitech)
            list.Add(Native(DeviceRouting.Logitech, LogitechTarget.SdkAvailable, NativeSdk.LogitechDll, Running("lghub", "lghub_agent"),
                "No G HUB, aplicativos precisam ter permissão para controlar a iluminação.", ("Baixar o LED SDK", () => Ui.OpenUrl(LogitechSdkUrl))));
        if (cfg.PcRgb.Msi)
            list.Add(!MsiTarget.SdkAvailable
                ? new Check(DeviceRouting.Msi, false,
                    $"{NativeSdk.MsiDll} não encontrado. Ele vem com o MSI Center (SDK) ou com o plugin MSI do iCUE; copie a DLL x64 para a pasta x64 do app.",
                    ("Abrir a pasta x64", OpenX64))
                : !admin
                    ? new Check(DeviceRouting.Msi, false, "SDK encontrado, mas o Mystic Light só funciona com o app como administrador.",
                        ("Reiniciar como administrador", RestartAdmin))
                    : new Check(DeviceRouting.Msi, true, $"{NativeSdk.MsiDll} encontrado e app como administrador."));

        if (cfg.PcRgb.DynamicLighting)
        {
            IReadOnlyList<(string Name, int Lamps, bool Available)> lamps = [];
            string? error = null;
            try { lamps = DynamicLightingTarget.Detect(); }
            catch (Exception ex) { error = ex.Message; }
            (string, Action) open = ("Abrir Iluminação dinâmica", () => Ui.OpenUrl("ms-settings:personalization-lighting"));
            list.Add(error is not null
                ? new Check(DeviceRouting.DynamicLighting, false, $"Não foi possível consultar: {error}", open)
                : lamps.Count == 0
                    ? new Check(DeviceRouting.DynamicLighting, false,
                        "O Windows não listou nenhum dispositivo compatível. Só aparecem teclados, mouses e luminárias com suporte a " +
                        "Iluminação dinâmica (às vezes é preciso fechar o software do fabricante ou atualizar o firmware).", open)
                    : new Check(DeviceRouting.DynamicLighting, lamps.Any(l => l.Available) ? true : null,
                        string.Join("; ", lamps.Select(l => $"{l.Name} ({l.Lamps} LEDs{(l.Available ? "" : ", sem controle agora")})")) +
                        (lamps.All(l => l.Available) ? "." : ". Com o app em segundo plano o Windows pode não entregar o controle (experimental)."),
                        open));
        }

        // Lâmpada
        if (cfg.Lamp.Enabled)
        {
            if (!cfg.Lamp.IsConfigured)
                list.Add(new Check("Lâmpada Wi-Fi", false, "Falta o Device ID e a local key. Siga o passo a passo da conta Tuya em Dispositivos.",
                    ("Configurar", () => Go(typeof(DevicesPage)))));
            else if (string.IsNullOrWhiteSpace(cfg.Lamp.Ip))
                list.Add(new Check("Lâmpada Wi-Fi", null, "Sem IP salvo: use \"Procurar na rede\" em Dispositivos.", ("Configurar", () => Go(typeof(DevicesPage)))));
            else
                list.Add(await PortOpenAsync(cfg.Lamp.Ip!, 6668)
                    ? new Check("Lâmpada Wi-Fi", true, $"Responde em {cfg.Lamp.Ip}:6668 (protocolo {cfg.Lamp.ProtocolVersion}).")
                    : new Check("Lâmpada Wi-Fi", false, $"Não responde em {cfg.Lamp.Ip}:6668. Confira se ela está ligada na tomada e se o IP mudou (Procurar na rede).",
                        ("Configurar", () => Go(typeof(DevicesPage)))));
        }

        if (!admin && (cfg.PcRgb.Msi || cfg.PcRgb.OpenRgb.Enabled))
            list.Add(new Check("Administrador", null,
                "O app roda sem administrador. Só é preciso para o Mystic Light e para o OpenRGB iniciado por aqui enxergar memória e placa-mãe.",
                ("Reiniciar como administrador", RestartAdmin)));
        return list;
    }

    private static Check Native(string name, bool sdk, string dll, bool running, string howTo, params (string, Action)[] download)
    {
        var x64 = Path.Combine(AppContext.BaseDirectory, "x64");
        if (!sdk)
            return new Check(name, false, $"{dll} não encontrado. Baixe o SDK e copie a DLL x64 para {x64}.",
                [.. download, ("Abrir a pasta x64", OpenX64)]);
        if (!running)
            return new Check(name, false, $"{dll} encontrado, mas o software do fabricante não está aberto. Abra-o. {howTo}");
        return new Check(name, true, $"{dll} encontrado e software do fabricante aberto. {howTo}");
    }

    private static bool Running(params string[] names) =>
        names.Any(n => { var p = Process.GetProcessesByName(n); foreach (var x in p) x.Dispose(); return p.Length > 0; });

    private static async Task<bool> PortOpenAsync(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            await c.ConnectAsync(host, port, cts.Token);
            return c.Connected;
        }
        catch { return false; }
    }

    private static void OpenX64()
    {
        var x64 = Path.Combine(AppContext.BaseDirectory, "x64");
        Directory.CreateDirectory(x64);
        TrayContext.OpenPath(x64);
    }

    private void Go(Type page) => _navigate(page);

    private void InstallHooks()
    {
        try { new HooksInstaller().Install(Program.ExePath); }
        catch (Exception ex) { Storage.Log($"hooks: {ex}"); _status.ForeColor = Ui.Error; _status.Text = ex.Message; return; }
        _ = RunAsync();
    }

    private void RestartAdmin()
    {
        if (Elevation.RestartElevated()) TrayContext.Current?.ExitThread();
    }
}
