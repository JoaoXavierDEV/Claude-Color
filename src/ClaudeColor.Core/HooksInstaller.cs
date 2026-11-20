using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeColor.Core;

public enum HooksStatus { NotInstalled, Installed, Outdated }

/// <summary>Instala/remove os hooks do Claude Color no settings.json do Claude Code.</summary>
public sealed class HooksInstaller(string? settingsPath = null)
{
    public string SettingsPath { get; } = settingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static readonly (string Event, string? Matcher)[] Ours =
    [
        ("UserPromptSubmit", null),
        ("Stop", null),
        ("Notification", null),
        ("PreToolUse", "AskUserQuestion|ExitPlanMode"),
        ("PostToolUse", null), // detecta que você respondeu/aprovou/permitiu ("aguardar intervenção")
        ("SessionStart", null), // conversa aberta: o Discord mostra antes de você digitar
        ("SessionEnd", null), // conversa fechada: sai do Discord
    ];

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string CommandFor(string exePath) => $"\"{exePath}\" hook";

    /// <summary>Nomes do executável atual e da versão 1.x (ClaudeLamp), para substituir entradas antigas.</summary>
    private static readonly string[] OurNames = ["ClaudeColor", "claude-color", "ClaudeLamp", "claude-lamp"];

    /// <summary>Uma entrada é "nossa" se o comando cita um dos nossos nomes.</summary>
    private static bool IsOurs(JsonNode? item) =>
        item?.ToJsonString() is { } json && OurNames.Any(n => json.Contains(n, StringComparison.OrdinalIgnoreCase));

    public HooksStatus GetStatus(string exePath)
    {
        var hooks = Load()["hooks"] as JsonObject;
        if (hooks is null) return HooksStatus.NotInstalled;
        var command = CommandFor(exePath);
        int ours = 0, current = 0;
        foreach (var (evt, _) in Ours)
        {
            if (hooks[evt] is not JsonArray arr) continue;
            foreach (var item in arr.Where(IsOurs))
            {
                ours++;
                if (item!["hooks"] is JsonArray inner && inner.Any(h => h?["command"]?.GetValue<string>() == command))
                    current++;
            }
        }
        if (ours == 0) return HooksStatus.NotInstalled;
        return current == Ours.Length && ours == Ours.Length ? HooksStatus.Installed : HooksStatus.Outdated;
    }

    /// <returns>Caminho do backup (ou null se não havia arquivo).</returns>
    public string? Install(string exePath) => Write(install: true, exePath);

    public string? Uninstall() => Write(install: false, null);

    private string? Write(bool install, string? exePath)
    {
        var root = Load();
        string? backup = null;
        if (File.Exists(SettingsPath))
        {
            backup = SettingsPath + $".bak-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(SettingsPath, backup, overwrite: true);
        }

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var (evt, list) in hooks.ToList())
        {
            if (list is not JsonArray arr) continue;
            foreach (var item in arr.Where(IsOurs).ToList()) arr.Remove(item);
            if (arr.Count == 0) hooks.Remove(evt);
        }

        if (install)
        {
            foreach (var (evt, matcher) in Ours)
            {
                if (hooks[evt] is not JsonArray arr) hooks[evt] = arr = new JsonArray();
                var entry = new JsonObject();
                if (matcher is not null) entry["matcher"] = matcher;
                entry["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command", ["command"] = CommandFor(exePath!), ["timeout"] = 10,
                });
                arr.Add(entry);
            }
        }
        if (hooks.Count == 0) root.Remove("hooks");

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, root.ToJsonString(Pretty));
        return backup;
    }

    private JsonObject Load()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? new JsonObject();
    }
}
