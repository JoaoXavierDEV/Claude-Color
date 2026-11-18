using ClaudeLamp.Core;

namespace ClaudeLamp.App;

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
                using var lamp = new LampClient(Storage.LoadConfig());
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
        await RunAsync(lamp => lamp.ApplyAsync(state));
        SignalRunner.RememberManual(state);
        ManualStateChanged?.Invoke(state);
    }

    public static async Task TestSignalAsync(LampConfig cfg, string evt)
    {
        cfg.SignalsEnabled = true;
        cfg.Events[evt].Enabled = true;
        // No teste, restaura em até 5 s para não deixar a janela esperando.
        if (cfg.Events[evt].RestoreAfterSeconds is > 5) cfg.Events[evt].RestoreAfterSeconds = 5;
        if (SignalRunner.Begin(cfg, evt) is not { } gen) return;
        await Gate.WaitAsync();
        try { await Task.Run(() => SignalRunner.RunAsync(cfg, evt, gen)); }
        finally { Gate.Release(); }
    }
}
