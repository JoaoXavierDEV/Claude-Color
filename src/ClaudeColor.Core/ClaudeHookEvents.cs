using System.Text.Json;

namespace ClaudeColor.Core;

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
                // Uma ferramenta terminou: se havia algo aguardando você, você já agiu.
                "PostToolUse" or "PostToolUseFailure" => ClaudeEvents.Resumed,
                // Conversa aberta (antes de você digitar) ou fechada: só o Discord acompanha.
                "SessionStart" => ClaudeEvents.SessionStart,
                "SessionEnd" => ClaudeEvents.SessionEnd,
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
    /// <summary>
    /// SessionStart/SessionEnd: registra a conversa no state (sem gerar sinal nem mexer na geração, para não cancelar
    /// um sinal em curso). <paramref name="source"/> é o do SessionStart: startup, resume, clear ou compact.
    /// </summary>
    public static void TrackSession(string evt, SessionInfo session, string? source)
    {
        Storage.UpdateState(s =>
        {
            if (evt == ClaudeEvents.SessionEnd)
            {
                if (session.Id is { } gone)
                {
                    s.Sessions.Remove(gone);
                    if (s.FocusedSession == gone) s.FocusedSession = null;
                }
                return;
            }
            var me = s.Session(session.Id);
            if (session.Window != 0) me.Window = session.Window;
            me.Project = session.Project ?? me.Project;
            me.Transcript = session.TranscriptPath ?? me.Transcript;
            if (source is "startup" or "clear")
            {
                // Conversa nova: nada do que havia nesta sessão vale mais.
                me.WorkingSince = me.TaskSince = null;
                me.WaitingFor = null;
            }
            s.LastSessionId = session.Id;
            s.FocusedSession = RuntimeState.Key(session.Id); // você abriu esta conversa
            if (session.Project is not null) s.LastProject = session.Project;
            if (session.Window != 0) s.LastWindow = session.Window;
        });
    }

    /// <summary>Registra o sinal e devolve a geração dele (ou null se nada a fazer).</summary>
    public static long? Begin(AppConfig cfg, string evt, SessionInfo? session = null)
    {
        if (evt == ClaudeEvents.Resumed) return BeginResume(cfg, session?.Id);
        if (!cfg.SignalsEnabled) { Storage.Log($"sinal '{evt}' ignorado: sinais pausados"); return null; }
        if (!cfg.Events.TryGetValue(evt, out var sig) || !sig.Enabled) { Storage.Log($"sinal '{evt}' desativado"); return null; }
        bool skip = false;
        var state = Storage.UpdateState(s =>
        {
            // O app desktop também gera uma notificação de permissão para AskUserQuestion/ExitPlanMode;
            // ela não deve apagar a cor mais específica de pergunta/plano.
            if (evt == ClaudeEvents.Permission && s.LastSignal is ClaudeEvents.Question or ClaudeEvents.Plan &&
                s.LastSessionId == session?.Id &&
                s.LastSignalAt is { } at && DateTimeOffset.Now - at < TimeSpan.FromSeconds(15))
            {
                skip = true;
                return;
            }
            s.SignalGeneration++;
            s.LastSignal = evt;
            s.LastSignalAt = DateTimeOffset.Now;
            s.Prune();
            s.LastSessionId = session?.Id;
            if (session is not null)
            {
                s.LastWindow = session.Window == 0 ? null : session.Window;
                s.LastProject = session.Project;
            }
            // Cada sessão do Claude Code tem o próprio "trabalhando" e "aguardando você".
            var me = s.Session(session?.Id);
            if (session is not null)
            {
                me.Window = session.Window == 0 ? me.Window : session.Window;
                me.Project = session.Project ?? me.Project;
                me.Transcript = session.TranscriptPath ?? me.Transcript;
            }
            if (evt == ClaudeEvents.Working)
            {
                me.WorkingSince = sig.HoldWhileWorking ? DateTimeOffset.Now : null;
                me.TaskSince ??= DateTimeOffset.Now;
                me.DoneAt = null;
                s.FocusedSession = RuntimeState.Key(session?.Id); // você mandou uma mensagem nesta conversa
            }
            else if (evt is ClaudeEvents.Done or ClaudeEvents.Idle) me.WorkingSince = me.TaskSince = null;
            if (evt == ClaudeEvents.Done) me.DoneAt = DateTimeOffset.Now;
            me.WaitingFor = sig.WaitForUser && SignalConfig.SupportsWaitForUser(evt) ? evt : null;
        });
        if (skip) { Storage.Log($"sinal '{evt}' ignorado: logo após '{state.LastSignal}'"); return null; }
        return state.SignalGeneration;
    }

    /// <summary>
    /// PostToolUse: só faz algo se um sinal estava aguardando sua intervenção (o hook dispara a cada
    /// ferramenta, então sem espera ele sai em silêncio).
    /// </summary>
    private static long? BeginResume(AppConfig cfg, string? sessionId)
    {
        if (!cfg.SignalsEnabled) return null;
        string? waited = null;
        var state = Storage.UpdateState(s =>
        {
            if (s.Find(sessionId) is not { WaitingFor: { } w } me) return;
            waited = w;
            me.WaitingFor = null;
            me.LastSeen = DateTimeOffset.Now;
            s.FocusedSession = RuntimeState.Key(sessionId); // você respondeu nesta conversa
            s.SignalGeneration++;
            s.LastSignal = ClaudeEvents.Resumed;
            s.LastSignalAt = DateTimeOffset.Now;
        });
        if (waited is null) return null;
        Storage.Log($"intervenção concluída em '{waited}'");
        return state.SignalGeneration;
    }

    /// <summary>Depois de uma espera: volta para "trabalhando" (se ativo) ou para o estado normal.</summary>
    private static async Task BackToNormalOrWorkingAsync(AppConfig cfg, IReadOnlyList<ILightTarget> targets,
        Func<Func<ILightTarget, Task>, Task> all, string evt, long generation, CancellationToken ct)
    {
        // Outra sessão ainda espera você: volta para a cor dela em vez de soltar.
        if (Storage.LoadState().WaitingFor is { } other && cfg.Events.TryGetValue(other, out var otherSig) && otherSig.Enabled)
        {
            var otherColor = RgbColor.Parse(otherSig.Color);
            bool otherPc = otherSig.UsesPcRgb(other);
            await all(t => t.IsLamp || otherPc ? t.ShowAsync(otherColor, otherSig.Brightness, ct) : t.ReleaseAsync(ct));
            Storage.Log($"'{evt}' #{generation}: outra sessão ainda aguarda ('{other}')");
        }
        else if (Storage.LoadState().IsWorking(cfg))
        {
            var working = cfg.Events[ClaudeEvents.Working];
            var workingColor = RgbColor.Parse(working.Color);
            bool pc = working.UsesPcRgb(ClaudeEvents.Working);
            await all(t => t.IsLamp || pc ? t.ShowAsync(workingColor, working.Brightness, ct) : t.ReleaseAsync(ct));
            Storage.Log($"'{evt}' #{generation}: voltou para 'trabalhando'");
        }
        else
        {
            await all(t => t.ReleaseAsync(ct));
            Storage.Log($"estado normal restaurado após '{evt}' #{generation}");
        }
    }

    /// <summary>Só a lâmpada (usado quando o app da bandeja não está aberto).</summary>
    public static async Task RunAsync(AppConfig cfg, string evt, long generation, CancellationToken ct = default)
    {
        using var lamp = new TuyaLampTarget(cfg.Lamp);
        await RunAsync(cfg, evt, generation, [lamp], ct);
    }

    /// <summary>
    /// Executa o sinal em todos os alvos em paralelo. A falha de um alvo não interrompe os outros.
    /// <paramref name="isCurrent"/> diz se este sinal ainda é o mais recente (padrão: geração no state.json).
    /// </summary>
    public static async Task RunAsync(AppConfig cfg, string evt, long generation, IReadOnlyList<ILightTarget> targets,
        CancellationToken ct = default, Func<bool>? isCurrent = null)
    {
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

        if (evt == ClaudeEvents.Resumed)
        {
            if (Current()) await BackToNormalOrWorkingAsync(cfg, targets, All, evt, generation, ct);
            return;
        }

        var sig = cfg.Events[evt];
        var color = RgbColor.Parse(sig.Color);

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

        // "Trabalhando" com manter: fica até o próximo evento; só solta sozinho pelo limite de segurança.
        if (evt == ClaudeEvents.Working && sig.HoldWhileWorking)
        {
            await Task.Delay(SignalConfig.MaxWorkingHold, ct);
            if (!Current()) return;
            await All(t => t.ReleaseAsync(ct));
            Storage.Log($"'working' #{generation}: limite de {SignalConfig.MaxWorkingHold.TotalMinutes} min, estado normal restaurado");
            return;
        }

        // "Aguardar intervenção": fica até o PostToolUse/nova mensagem; só solta sozinho pelo limite de segurança.
        if (sig.WaitForUser && SignalConfig.SupportsWaitForUser(evt))
        {
            await Task.Delay(SignalConfig.MaxWorkingHold, ct);
            if (!Current()) return;
            Storage.UpdateState(s => { foreach (var x in s.Sessions.Values) if (x.WaitingFor == evt) x.WaitingFor = null; });
            await BackToNormalOrWorkingAsync(cfg, targets, All, evt, generation, ct);
            return;
        }

        // Depois do tempo de restaurar: outra sessão esperando você > alguma trabalhando > estado normal.
        if (sig.RestoreAfterSeconds is > 0 and var secs)
        {
            await Task.Delay(TimeSpan.FromSeconds(secs), ct);
            if (!Current()) return;
            await BackToNormalOrWorkingAsync(cfg, targets, All, evt, generation, ct);
        }
    }

    /// <summary>Salva o estado manual (o que restaurar depois) e invalida sinais pendentes.</summary>
    public static void RememberManual(ManualState manual) =>
        Storage.UpdateState(s =>
        {
            s.Manual = manual;
            s.WorkingSince = null;
            s.WaitingFor = null;
            s.SignalGeneration++;
            s.LastSignal = "manual";
            s.LastSignalAt = DateTimeOffset.Now;
        });
}
