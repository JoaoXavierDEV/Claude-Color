using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Serializa os comandos à lâmpada vindos da bandeja e da janela.</summary>
internal static class LampService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static event Action<ManualState>? ManualStateChanged;

    public static async Task<T> RunAsync<T>(Func<LampClient, Task<T>> action)
    {
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(async () =>
            {
                using var lamp = new LampClient(Storage.LoadConfig().Lamp);
                return await action(lamp);
            });
        }
        finally { Gate.Release(); }
    }

    public static Task RunAsync(Func<LampClient, Task> action) =>
        RunAsync(async lamp => { await action(lamp); return true; });

    /// <summary>Aplica um estado manual e o guarda para restaurar depois dos sinais.</summary>
    public static async Task ApplyManualAsync(ManualState state)
    {
        if (SignalHost.Current is { } host) await host.OnManualChangeAsync();
        await RunAsync(lamp => lamp.ApplyAsync(state));
        SignalRunner.RememberManual(state);
        ManualStateChanged?.Invoke(state);
    }

    /// <summary>Testa um evento com os valores ainda não salvos da tela (lâmpada + RGB do PC).</summary>
    public static void TestSignal(AppConfig cfg, string evt)
    {
        cfg.SignalsEnabled = true;
        cfg.Events[evt].Enabled = true;
        // O teste nunca segura: sem "manter enquanto trabalha" nem "aguardar intervenção".
        cfg.Events[evt].HoldWhileWorking = false;
        cfg.Events[evt].WaitForUser = false;
        // No teste, restaura em até 5 s para não deixar tudo na cor do evento.
        if (cfg.Events[evt].RestoreAfterSeconds is null or 0 or > 5) cfg.Events[evt].RestoreAfterSeconds = 5;
        if (SignalRunner.Begin(cfg, evt) is not { } gen) return;
        SignalHost.Current?.Dispatch(evt, gen, cfg, new SessionInfo(0, "teste"));
    }

    /// <summary>Acende tudo do perfil ativo em amarelo por 5 s.</summary>
    public static void TestAllDevices()
    {
        var cfg = Storage.LoadConfig();
        cfg.Events[ClaudeEvents.Question] = new SignalConfig { Color = "#FFC000", Brightness = 100, Blink = 2, RestoreAfterSeconds = 5, PcRgb = true, Notify = false };
        TestSignal(cfg, ClaudeEvents.Question);
    }
}
