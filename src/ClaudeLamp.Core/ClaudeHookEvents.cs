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

    /// <summary>Só a lâmpada (usado quando o app da bandeja não está aberto).</summary>
    public static async Task RunAsync(LampConfig cfg, string evt, long generation, CancellationToken ct = default)
    {
        using var lamp = new TuyaLampTarget(cfg);
        await RunAsync(cfg, evt, generation, [lamp], ct);
    }

    /// <summary>
    /// Executa o sinal em todos os alvos em paralelo. A falha de um alvo não interrompe os outros.
    /// <paramref name="isCurrent"/> diz se este sinal ainda é o mais recente (padrão: geração no state.json).
    /// </summary>
    public static async Task RunAsync(LampConfig cfg, string evt, long generation, IReadOnlyList<ILightTarget> targets,
        CancellationToken ct = default, Func<bool>? isCurrent = null)
    {
        var sig = cfg.Events[evt];
        var color = RgbColor.Parse(sig.Color);
        bool Current() => isCurrent?.Invoke() ?? Storage.LoadState().SignalGeneration == generation;

        Task All(Func<ILightTarget, Task> action) => Each(targets, action);

        Task Each(IEnumerable<ILightTarget> subset, Func<ILightTarget, Task> action) => Task.WhenAll(subset.Select(async t =>
        {
            try { await action(t); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Storage.Log($"sinal '{evt}' #{generation}: {t.Name} falhou: {ex.Message}");
            }
        }));

        // Dispositivos lentos não piscam: recebem a cor final já no início.
        var blinkers = targets.Where(t => t.Blinks).ToList();
        if (sig.Blink > 0 && blinkers.Count < targets.Count)
            await Each(targets.Where(t => !t.Blinks), t => t.ShowAsync(color, sig.Brightness, ct));

        for (int i = 0; i < sig.Blink && Current(); i++)
        {
            await Each(blinkers, t => t.ShowAsync(color, sig.Brightness, ct));
            await Task.Delay(sig.BlinkIntervalMs, ct);
            await Each(blinkers, t => t.ShowAsync(color, 1, ct));
            await Task.Delay(sig.BlinkIntervalMs, ct);
        }
        if (!Current()) { Storage.Log($"sinal '{evt}' #{generation} substituído por um mais novo"); return; }
        await All(t => t.ShowAsync(color, sig.Brightness, ct));
        Storage.Log($"sinal '{evt}' #{generation} aplicado ({sig.Color}) em {string.Join(", ", targets.Select(t => t.Name))}");

        if (sig.RestoreAfterSeconds is > 0 and var secs)
        {
            await Task.Delay(TimeSpan.FromSeconds(secs), ct);
            if (!Current()) return;
            await All(t => t.ReleaseAsync(ct));
            Storage.Log($"estado normal restaurado após '{evt}' #{generation}");
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
