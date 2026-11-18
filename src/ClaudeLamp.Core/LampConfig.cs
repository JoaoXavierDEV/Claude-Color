using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeLamp.Core;

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
}

/// <summary>Quais softwares de RGB do PC o ClaudeLamp pode assumir durante um sinal.</summary>
public sealed class PcRgbConfig
{
    /// <summary>Corsair iCUE (RAM, placa-mãe e o que mais o iCUE expuser).</summary>
    public bool Corsair { get; set; } = true;
    /// <summary>Logitech G HUB (teclado, mouse).</summary>
    public bool Logitech { get; set; } = true;
    /// <summary>MSI Mystic Light SDK (exige o app como administrador). A GPU também pode ir pelo OpenRGB.</summary>
    public bool Msi { get; set; }
    public OpenRgbConfig OpenRgb { get; set; } = new();
}

public sealed class OpenRgbConfig
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;
    /// <summary>Inicia o OpenRGB com o servidor SDK quando ele não está rodando.</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>"Fabricante Nome" dos dispositivos controlados. Null = padrão (o que iCUE/G HUB não cobrem).</summary>
    public List<string>? Devices { get; set; }
}

public sealed class LampConfig
{
    public string? DeviceId { get; set; }
    public string? LocalKey { get; set; }
    public string? Ip { get; set; }
    /// <summary>Versão do protocolo Tuya: "3.1", "3.3", "3.4" ou "3.5".</summary>
    public string ProtocolVersion { get; set; } = "3.3";
    /// <summary>"auto", "v1" (DPs 1–5) ou "v2" (DPs 20–24).</summary>
    public string DpsProfile { get; set; } = "auto";
    /// <summary>Liga/desliga os sinais do Claude Code (o app de bandeja alterna isso).</summary>
    public bool SignalsEnabled { get; set; } = true;
    /// <summary>RGB do PC acompanhando os sinais.</summary>
    public PcRgbConfig PcRgb { get; set; } = new();

    /// <summary>
    /// Chaves: working, question, plan, permission, idle, done.
    /// Veja <see cref="ClaudeEvents"/>.
    /// </summary>
    public Dictionary<string, SignalConfig> Events { get; set; } = DefaultEvents();

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(DeviceId) && !string.IsNullOrWhiteSpace(LocalKey);

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

public sealed class RuntimeState
{
    public ManualState Manual { get; set; } = new();
    /// <summary>Incrementado a cada sinal; um restore só ocorre se ninguém sinalizou depois.</summary>
    public long SignalGeneration { get; set; }
    public string? LastSignal { get; set; }
    public DateTimeOffset? LastSignalAt { get; set; }
    /// <summary>Quando o Claude começou a trabalhar (só com "manter enquanto trabalha"); null = não está trabalhando.</summary>
    public DateTimeOffset? WorkingSince { get; set; }
    /// <summary>Evento aguardando sua intervenção (pergunta, plano, permissão...); null = nenhum.</summary>
    public string? WaitingFor { get; set; }

    public bool IsWorking(LampConfig cfg) =>
        WorkingSince is { } since && DateTimeOffset.Now - since < SignalConfig.MaxWorkingHold &&
        cfg.Events.TryGetValue(ClaudeEvents.Working, out var w) && w.Enabled && w.HoldWhileWorking;
}

public static class Storage
{
    public static string Directory { get; } =
        Environment.GetEnvironmentVariable("CLAUDE_LAMP_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeLamp");

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

    public static LampConfig LoadConfig()
    {
        var cfg = Read<LampConfig>(ConfigPath) ?? new LampConfig();
        // Garante que eventos novos apareçam mesmo em configs antigas.
        var events = new Dictionary<string, SignalConfig>(cfg.Events, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in LampConfig.DefaultEvents())
            events.TryAdd(k, v);
        cfg.Events = events;
        return cfg;
    }

    public static void SaveConfig(LampConfig cfg) => Write(ConfigPath, cfg);

    public static RuntimeState LoadState() => Read<RuntimeState>(StatePath) ?? new RuntimeState();

    /// <summary>Lê, altera e grava o estado sob um mutex entre processos.</summary>
    public static RuntimeState UpdateState(Action<RuntimeState> change)
    {
        using var mutex = new Mutex(false, @"Local\ClaudeLamp.State");
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
