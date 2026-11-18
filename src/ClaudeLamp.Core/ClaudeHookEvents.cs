using System.Text.Json;

namespace ClaudeLamp.Core;

/// <summary>Traduz o JSON que o Claude Code envia ao hook (stdin) num evento da lâmpada.</summary>
public static class EventMapper
{
    /// <returns>Uma chave de <see cref="ClaudeEvents"/>, ou null se o evento deve ser ignorado.</returns>
    public static string? Map(string hookJson)
    {
        if (string.IsNullOrWhiteSpace(hookJson)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(hookJson); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Get(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            return Get("hook_event_name") switch
            {
                "UserPromptSubmit" => ClaudeEvents.Working,
                "Stop" => ClaudeEvents.Done,
                "PreToolUse" or "PermissionRequest" => Get("tool_name") switch
                {
                    "AskUserQuestion" => ClaudeEvents.Question,
                    "ExitPlanMode" => ClaudeEvents.Plan,
                    _ => Get("hook_event_name") == "PermissionRequest" ? ClaudeEvents.Permission : null,
                },
                "Notification" => MapNotification(Get("notification_type"), Get("message")),
                _ => null,
            };
        }
    }

    private static string? MapNotification(string? type, string? message) => type switch
    {
        "permission_prompt" => ClaudeEvents.Permission,
        "elicitation_dialog" => ClaudeEvents.Question,
        "idle_prompt" => ClaudeEvents.Idle,
        "auth_success" => null,
        // Versões antigas não enviam notification_type: decide pelo texto.
        null when message?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true => ClaudeEvents.Permission,
        null when message?.Contains("waiting", StringComparison.OrdinalIgnoreCase) == true => ClaudeEvents.Idle,
        _ => null,
    };
}

/// <summary>Executa um sinal: pisca, fixa a cor e, opcionalmente, restaura o estado manual depois.</summary>
public static class SignalRunner
{
    /// <summary>Registra o sinal e devolve a geração dele (ou null se nada a fazer).</summary>
    public static long? Begin(LampConfig cfg, string evt)
    {
        if (!cfg.SignalsEnabled) { Storage.Log($"sinal '{evt}' ignorado: sinais pausados"); return null; }
        if (!cfg.Events.TryGetValue(evt, out var sig) || !sig.Enabled) { Storage.Log($"sinal '{evt}' desativado"); return null; }
        bool skip = false;
        var state = Storage.UpdateState(s =>
        {
            // O app desktop também gera uma notificação de permissão para AskUserQuestion/ExitPlanMode;
            // ela não deve apagar a cor mais específica de pergunta/plano.
            if (evt == ClaudeEvents.Permission && s.LastSignal is ClaudeEvents.Question or ClaudeEvents.Plan &&
                s.LastSignalAt is { } at && DateTimeOffset.Now - at < TimeSpan.FromSeconds(15))
            {
                skip = true;
                return;
            }
            s.SignalGeneration++;
            s.LastSignal = evt;
            s.LastSignalAt = DateTimeOffset.Now;
        });
        if (skip) { Storage.Log($"sinal '{evt}' ignorado: logo após '{state.LastSignal}'"); return null; }
        return state.SignalGeneration;
    }

    public static async Task RunAsync(LampConfig cfg, string evt, long generation, CancellationToken ct = default)
    {
        var sig = cfg.Events[evt];
        var color = RgbColor.Parse(sig.Color);
        using var lamp = new LampClient(cfg);

        bool Current() => Storage.LoadState().SignalGeneration == generation;

        if (sig.Blink > 0)
        {
            for (int i = 0; i < sig.Blink && Current(); i++)
            {
                await lamp.SetColorAsync(color, sig.Brightness, ct);
                await Task.Delay(sig.BlinkIntervalMs, ct);
                await lamp.SetColorAsync(color, 1, ct);
                await Task.Delay(sig.BlinkIntervalMs, ct);
            }
        }
        if (!Current()) { Storage.Log($"sinal '{evt}' #{generation} substituído por um mais novo"); return; }
        await lamp.SetColorAsync(color, sig.Brightness, ct);
        Storage.Log($"sinal '{evt}' #{generation} aplicado ({sig.Color})");

        if (sig.RestoreAfterSeconds is > 0 and var secs)
        {
            await Task.Delay(TimeSpan.FromSeconds(secs), ct);
            if (!Current()) return;
            var manual = Storage.LoadState().Manual;
            await lamp.ApplyAsync(manual, ct);
            Storage.Log($"estado manual restaurado após '{evt}' #{generation}");
        }
    }

    /// <summary>Salva o estado manual (o que restaurar depois) e invalida sinais pendentes.</summary>
    public static void RememberManual(ManualState manual) =>
        Storage.UpdateState(s =>
        {
            s.Manual = manual;
            s.SignalGeneration++;
            s.LastSignal = "manual";
            s.LastSignalAt = DateTimeOffset.Now;
        });
}
