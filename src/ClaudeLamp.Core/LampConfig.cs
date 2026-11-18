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
