using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeColor.Core;

/// <summary>Como reagir a um evento do Claude Code.</summary>
public sealed class SignalConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>"#RRGGBB" ou nome de cor.</summary>
    public string Color { get; set; } = "#FFFFFF";
    /// <summary>Brilho 1–100.</summary>
    public int Brightness { get; set; } = 100;
    /// <summary>Quantas vezes piscar antes de fixar a cor (0 = não pisca).</summary>
    public int Blink { get; set; }
    public int BlinkIntervalMs { get; set; } = 350;
    /// <summary>Segundos até voltar ao estado manual anterior (null/0 = fica na cor do sinal).</summary>
    public int? RestoreAfterSeconds { get; set; }
    /// <summary>Também acende o RGB do PC (RAM, placa-mãe, teclado, mouse). Null = padrão do evento.</summary>
    public bool? PcRgb { get; set; }
    /// <summary>
    /// Só para "trabalhando": mantém a cor do momento em que você envia a mensagem até o Claude terminar.
    /// Perguntas/permissões/planos no meio voltam para a cor de trabalhando em vez do estado normal.
    /// </summary>
    public bool HoldWhileWorking { get; set; }
    /// <summary>
    /// Para pergunta, plano, permissão e ocioso: mantém a cor até você agir (responder, aprovar, permitir
    /// ou mandar outra mensagem), em vez de restaurar após um tempo fixo.
    /// </summary>
    public bool WaitForUser { get; set; }

    public static bool SupportsWaitForUser(string evt) =>
        evt is ClaudeEvents.Question or ClaudeEvents.Plan or ClaudeEvents.Permission or ClaudeEvents.Idle;

    /// <summary>Limite de segurança caso o Claude feche sem enviar "Stop".</summary>
    public static readonly TimeSpan MaxWorkingHold = TimeSpan.FromMinutes(30);

    public static bool DefaultPcRgb(string evt) => evt is not (ClaudeEvents.Working or ClaudeEvents.Idle);
    public bool UsesPcRgb(string evt) => PcRgb ?? DefaultPcRgb(evt);

    /// <summary>Notificação do Windows. Null = padrão do evento.</summary>
    public bool? Notify { get; set; }
    /// <summary>Pisca o botão do terminal na barra de tarefas. Null = padrão do evento.</summary>
    public bool? FlashTaskbar { get; set; }

    public static bool DefaultNotify(string evt) => evt is ClaudeEvents.Question or ClaudeEvents.Plan or ClaudeEvents.Permission or ClaudeEvents.Done;
    public static bool DefaultFlash(string evt) => evt is ClaudeEvents.Question or ClaudeEvents.Plan or ClaudeEvents.Permission;
    public bool UsesNotify(string evt) => Notify ?? DefaultNotify(evt);
    public bool UsesFlash(string evt) => FlashTaskbar ?? DefaultFlash(evt);
}

/// <summary>Quais softwares de RGB do PC o Claude Color pode assumir durante um sinal.</summary>
public sealed class PcRgbConfig
{
    /// <summary>Corsair iCUE (RAM, placa-mãe e o que mais o iCUE expuser). Integração nativa opcional.</summary>
    public bool Corsair { get; set; }
    /// <summary>Logitech G HUB (teclado, mouse). Integração nativa opcional.</summary>
    public bool Logitech { get; set; }
    /// <summary>MSI Mystic Light SDK (exige o app como administrador). A GPU também pode ir pelo OpenRGB.</summary>
    public bool Msi { get; set; }
    /// <summary>Iluminação dinâmica do Windows 11 (LampArray). Experimental.</summary>
    public bool DynamicLighting { get; set; }
    public OpenRgbConfig OpenRgb { get; set; } = new();
}

public sealed class OpenRgbConfig
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;
    /// <summary>Inicia o OpenRGB com o servidor SDK quando ele não está rodando.</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>"Fabricante Nome" dos dispositivos controlados. Null = padrão (tudo que uma integração nativa ativa não cobre).</summary>
    public List<string>? Devices { get; set; }
}

/// <summary>O que segurar quando o Windows diz que você está ocupado (tela cheia, apresentação, horário silencioso).</summary>
public sealed class QuietConfig
{
    public bool SkipNotify { get; set; } = true;
    public bool SkipFlash { get; set; } = true;
    public bool SkipLights { get; set; }
}

/// <summary>Status no perfil do Discord (Rich Presence). Desligado por padrão.</summary>
public sealed class DiscordConfig
{
    public bool Enabled { get; set; }
    /// <summary>Mostra "em &lt;projeto&gt;": amigos e servidores veem o nome da pasta.</summary>
    public bool ShowProject { get; set; }
    /// <summary>Application ID do aplicativo "Claude Color" no Discord Developer Portal (público).</summary>
    public string ApplicationId { get; set; } = DefaultApplicationId;
    /// <summary>Minutos que "Terminou" fica no perfil antes de o status sumir.</summary>
    public int ClearAfterMinutes { get; set; } = 5;

    public const string DefaultApplicationId = "1572975278500876358";

    /// <summary>
    /// Imagens por chave (logo, question, plan, permission, done, working, idle) → URL https. O Discord carrega e
    /// anima GIFs direto da URL, sem upload no Developer Portal. Chave ausente = GIF do repositório.
    /// </summary>
    public Dictionary<string, string>? Images { get; set; }

    public const string DefaultImageBase = "https://raw.githubusercontent.com/JoaoXavierDEV/claude-lamp/main/docs/discord/";

    public string ImageUrl(string key) =>
        Images is not null && Images.TryGetValue(key, out var url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            ? url
            : $"{DefaultImageBase}{key}.gif";
}

/// <summary>Lâmpada Wi-Fi Tuya (opcional).</summary>
public sealed class LampConfig
{
    /// <summary>Usar a lâmpada nos sinais. Desligada por padrão numa instalação nova.</summary>
    public bool Enabled { get; set; }
    public string? DeviceId { get; set; }
    public string? LocalKey { get; set; }
    public string? Ip { get; set; }
    /// <summary>Versão do protocolo Tuya: "3.1", "3.3", "3.4" ou "3.5".</summary>
    public string ProtocolVersion { get; set; } = "3.3";
    /// <summary>"auto", "v1" (DPs 1–5) ou "v2" (DPs 20–24).</summary>
    public string DpsProfile { get; set; } = "auto";

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(LocalKey);

    /// <summary>Ativa e com Device ID e local key.</summary>
    [JsonIgnore]
    public bool IsActive => Enabled && IsConfigured;
}

public sealed class AppConfig
{
    /// <summary>Liga/desliga os sinais do Claude Code (o app de bandeja alterna isso).</summary>
    public bool SignalsEnabled { get; set; } = true;
    /// <summary>Com o app fechado, o hook abre o app na bandeja para executar o sinal.</summary>
    public bool LaunchFromHook { get; set; } = true;
    /// <summary>Tema da janela: "system" (segue o Windows), "light" ou "dark". Vale ao abrir o app.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>RGB do PC acompanhando os sinais.</summary>
    public PcRgbConfig PcRgb { get; set; } = new();
    public LampConfig Lamp { get; set; } = new();
    public QuietConfig Quiet { get; set; } = new();
    public DiscordConfig Discord { get; set; } = new();

    /// <summary>Combinações de controladores. O "Padrão" (só OpenRGB) sempre existe. Veja <see cref="Profiles"/>.</summary>
    public List<DeviceProfile> Profiles { get; set; } = [];
    /// <summary>Nome do perfil em uso; as escolhas dele estão copiadas em <see cref="PcRgb"/> e <see cref="LampConfig.Enabled"/>.</summary>
    public string ActiveProfile { get; set; } = ClaudeColor.Core.Profiles.DefaultName;

    /// <summary>
    /// Chaves: working, question, plan, permission, idle, done.
    /// Veja <see cref="ClaudeEvents"/>.
    /// </summary>
    public Dictionary<string, SignalConfig> Events { get; set; } = DefaultEvents();

    // Formato 1.x (ClaudeLamp): campos da lâmpada na raiz. Lidos uma vez e movidos para Lamp.
    [JsonPropertyName("deviceId")] public string? LegacyDeviceId { get; set; }
    [JsonPropertyName("localKey")] public string? LegacyLocalKey { get; set; }
    [JsonPropertyName("ip")] public string? LegacyIp { get; set; }
    [JsonPropertyName("protocolVersion")] public string? LegacyProtocolVersion { get; set; }
    [JsonPropertyName("dpsProfile")] public string? LegacyDpsProfile { get; set; }

    /// <summary>Move os campos do formato 1.x para <see cref="Lamp"/>. Retorna true se mudou algo.</summary>
    public bool MigrateLegacy()
    {
        if (LegacyDeviceId is null && LegacyLocalKey is null && LegacyIp is null) return false;
        if (!Lamp.IsConfigured)
        {
            Lamp = new LampConfig
            {
                Enabled = !string.IsNullOrWhiteSpace(LegacyDeviceId) && !string.IsNullOrWhiteSpace(LegacyLocalKey),
                DeviceId = LegacyDeviceId,
                LocalKey = LegacyLocalKey,
                Ip = LegacyIp,
                ProtocolVersion = LegacyProtocolVersion ?? "3.3",
                DpsProfile = LegacyDpsProfile ?? "auto",
            };
        }
        LegacyDeviceId = LegacyLocalKey = LegacyIp = LegacyProtocolVersion = LegacyDpsProfile = null;
        return true;
    }

    public static Dictionary<string, SignalConfig> DefaultEvents() => new(StringComparer.OrdinalIgnoreCase)
    {
        [ClaudeEvents.Working] = new() { Color = "#0040FF", Brightness = 40 },
        [ClaudeEvents.Question] = new() { Color = "#FFC000", Brightness = 100, Blink = 3 },
        [ClaudeEvents.Plan] = new() { Color = "#A000FF", Brightness = 100, Blink = 3 },
        [ClaudeEvents.Permission] = new() { Color = "#FF4000", Brightness = 100, Blink = 3 },
        [ClaudeEvents.Idle] = new() { Enabled = false, Color = "#FFC000", Brightness = 60 },
        [ClaudeEvents.Done] = new() { Color = "#00FF40", Brightness = 100, Blink = 2, RestoreAfterSeconds = 60 },
    };
}

public static class ClaudeEvents
{
    public const string Working = "working";
    public const string Question = "question";
    public const string Plan = "plan";
    public const string Permission = "permission";
    public const string Idle = "idle";
    public const string Done = "done";
    /// <summary>Interno: o Claude retomou depois de uma intervenção sua (hook PostToolUse).</summary>
    public const string Resumed = "resumed";

    public static readonly string[] All = [Working, Question, Plan, Permission, Idle, Done];
}

/// <summary>Último estado definido manualmente (CLI/bandeja), usado para restaurar após um sinal.</summary>
public sealed class ManualState
{
    public bool On { get; set; } = true;
    /// <summary>"colour" ou "white".</summary>
    public string Mode { get; set; } = "white";
    public string Color { get; set; } = "#FFFFFF";
    public int Brightness { get; set; } = 100;
    /// <summary>Temperatura de branco 0 (quente) – 100 (frio).</summary>
    public int WhiteTemp { get; set; } = 50;
}

/// <summary>O que uma sessão do Claude Code está fazendo.</summary>
public sealed class SessionState
{
    /// <summary>Começou a trabalhar (só com "manter enquanto trabalha"); null = não está trabalhando.</summary>
    public DateTimeOffset? WorkingSince { get; set; }
    /// <summary>Evento aguardando sua intervenção (pergunta, plano, permissão...); null = nenhum.</summary>
    public string? WaitingFor { get; set; }
    public long? Window { get; set; }
    public string? Project { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.Now;
}

public sealed class RuntimeState
{
    public ManualState Manual { get; set; } = new();
    /// <summary>Incrementado a cada sinal; um restore só ocorre se ninguém sinalizou depois.</summary>
    public long SignalGeneration { get; set; }
    public string? LastSignal { get; set; }
    public DateTimeOffset? LastSignalAt { get; set; }
    /// <summary>Janela do terminal, projeto e sessão do último sinal (para o sinal pendente e a notificação).</summary>
    public long? LastWindow { get; set; }
    public string? LastProject { get; set; }
    public string? LastSessionId { get; set; }
    /// <summary>Último hook recebido (qualquer um, inclusive ignorado): mostra se os hooks estão chegando.</summary>
    public DateTimeOffset? LastHookAt { get; set; }

    /// <summary>Estado de cada sessão do Claude Code (chave: session_id do hook).</summary>
    public Dictionary<string, SessionState> Sessions { get; set; } = new(StringComparer.Ordinal);

    public const string DefaultSession = "default";

    /// <summary>Ordem de urgência quando mais de uma sessão espera você.</summary>
    private static readonly string[] Urgency = [ClaudeEvents.Question, ClaudeEvents.Plan, ClaudeEvents.Permission, ClaudeEvents.Idle];

    /// <summary>A sessão (criada se não existe), marcada como vista agora.</summary>
    public SessionState Session(string? id)
    {
        id = string.IsNullOrWhiteSpace(id) ? DefaultSession : id;
        if (!Sessions.TryGetValue(id, out var s)) Sessions[id] = s = new SessionState();
        s.LastSeen = DateTimeOffset.Now;
        return s;
    }

    public SessionState? Find(string? id) =>
        Sessions.TryGetValue(string.IsNullOrWhiteSpace(id) ? DefaultSession : id, out var s) ? s : null;

    /// <summary>Remove sessões sem atividade há mais que o limite de segurança.</summary>
    public void Prune()
    {
        foreach (var (id, s) in Sessions.ToList())
            if (DateTimeOffset.Now - s.LastSeen > SignalConfig.MaxWorkingHold) Sessions.Remove(id);
    }

    /// <summary>
    /// Quando alguma sessão começou a trabalhar (null = nenhuma). Atribuir null encerra o "trabalhando" de todas;
    /// atribuir um valor vale para a sessão padrão (hooks sem session_id).
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? WorkingSince
    {
        get => Sessions.Values.Select(s => s.WorkingSince).Where(w => w is not null).Max();
        set
        {
            if (value is null) foreach (var s in Sessions.Values) s.WorkingSince = null;
            else Session(DefaultSession).WorkingSince = value;
        }
    }

    /// <summary>
    /// O evento mais urgente que alguma sessão espera de você (null = nenhum). Atribuir null limpa todas;
    /// atribuir um valor vale para a sessão padrão.
    /// </summary>
    [JsonIgnore]
    public string? WaitingFor
    {
        get => Urgency.FirstOrDefault(e => Sessions.Values.Any(s => s.WaitingFor == e));
        set
        {
            if (value is null) foreach (var s in Sessions.Values) s.WaitingFor = null;
            else Session(DefaultSession).WaitingFor = value;
        }
    }

    /// <summary>
    /// Sinal registrado por um hook pouco antes do app abrir (o hook abriu o app porque ele estava fechado).
    /// </summary>
    public (string Event, long Generation)? PendingSignal(TimeSpan maxAge) =>
        LastSignal is { } e && (ClaudeEvents.All.Contains(e) || e == ClaudeEvents.Resumed) &&
        LastSignalAt is { } at && DateTimeOffset.Now - at < maxAge
            ? (e, SignalGeneration)
            : null;

    public bool IsWorking(AppConfig cfg) =>
        WorkingSince is { } since && DateTimeOffset.Now - since < SignalConfig.MaxWorkingHold &&
        cfg.Events.TryGetValue(ClaudeEvents.Working, out var w) && w.Enabled && w.HoldWhileWorking;
}

public static class Storage
{
    public static string Directory { get; } =
        Environment.GetEnvironmentVariable("CLAUDE_COLOR_HOME") is { Length: > 0 } custom ? custom
        : Environment.GetEnvironmentVariable("CLAUDE_LAMP_HOME") is { Length: > 0 } legacy ? legacy
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeColor");

    /// <summary>Pasta da versão 1.x (ClaudeLamp).</summary>
    public static string LegacyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeLamp");

    /// <summary>
    /// Copia config.json e state.json da pasta 1.x se a pasta atual ainda não tem config.
    /// Retorna true se copiou.
    /// </summary>
    public static bool MigrateFrom(string legacyDirectory)
    {
        try
        {
            if (File.Exists(ConfigPath) || !File.Exists(Path.Combine(legacyDirectory, "config.json"))) return false;
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var name in new[] { "config.json", "state.json" })
            {
                var from = Path.Combine(legacyDirectory, name);
                if (File.Exists(from)) File.Copy(from, Path.Combine(Directory, name), overwrite: false);
            }
            Log($"configuração migrada de {legacyDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"migração de {legacyDirectory} falhou: {ex.Message}");
            return false;
        }
    }

    public static string ConfigPath => Path.Combine(Directory, "config.json");
    public static string StatePath => Path.Combine(Directory, "state.json");
    public static string LogPath => Path.Combine(Directory, "log.txt");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppConfig LoadConfig()
    {
        var cfg = Read<AppConfig>(ConfigPath) ?? new AppConfig();
        bool migrated = cfg.MigrateLegacy();
        // Sem config.json ainda (instalação nova), os perfis ficam só em memória até o primeiro Salvar.
        if ((cfg.EnsureProfiles() | migrated) && File.Exists(ConfigPath)) SaveConfig(cfg);
        // Garante que eventos novos apareçam mesmo em configs antigas.
        var events = new Dictionary<string, SignalConfig>(cfg.Events, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in AppConfig.DefaultEvents())
            events.TryAdd(k, v);
        cfg.Events = events;
        return cfg;
    }

    public static void SaveConfig(AppConfig cfg) => Write(ConfigPath, cfg);

    public static RuntimeState LoadState() => Read<RuntimeState>(StatePath) ?? new RuntimeState();

    /// <summary>Lê, altera e grava o estado sob um mutex entre processos.</summary>
    public static RuntimeState UpdateState(Action<RuntimeState> change)
    {
        using var mutex = new Mutex(false, @"Local\ClaudeColor.State");
        bool owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { owned = true; }
            var state = LoadState();
            change(state);
            Write(StatePath, state);
            return state;
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    public static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 512 * 1024) info.Delete();
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch { /* log nunca pode derrubar o hook */ }
    }

    private static T? Read<T>(string path) where T : class
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
            }
            catch (IOException) { Thread.Sleep(50); }
        }
        return null;
    }

    private static void Write<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
