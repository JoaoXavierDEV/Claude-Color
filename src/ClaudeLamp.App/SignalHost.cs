using System.IO.Pipes;
using System.Text;
using ClaudeLamp.Core;
using ClaudeLamp.Rgb;

namespace ClaudeLamp.App;

/// <summary>
/// Recebe os sinais dos hooks pelo named pipe e os executa na lâmpada e no RGB do PC.
/// Fica no app da bandeja porque o RGB do PC precisa de um processo que segure o controle.
/// </summary>
internal sealed class SignalHost : IDisposable
{
    public static SignalHost? Current { get; private set; }

    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _running;

    public PcRgbController PcRgb { get; } = new();

    public SignalHost()
    {
        Current = this;
        _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(SignalPipe.Name, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token);
                var buf = new byte[128];
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                readCts.CancelAfter(1000);
                int n = await server.ReadAsync(buf, readCts.Token);
                var line = Encoding.UTF8.GetString(buf, 0, n);
                if (SignalPipe.TryParse(line, out var evt, out var gen))
                {
                    await server.WriteAsync("ok\n"u8.ToArray(), _stop.Token);
                    await server.FlushAsync(_stop.Token);
                    Dispatch(evt, gen);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Storage.Log($"signal-host: {ex.Message}");
                await Task.Delay(200);
            }
        }
    }

    /// <summary>Executa um sinal já registrado (geração obtida em <see cref="SignalRunner.Begin"/>).</summary>
    public void Dispatch(string evt, long gen, LampConfig? cfgOverride = null)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _running?.Cancel();
            _running = cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        }
        _ = Task.Run(() => RunAsync(cfgOverride ?? Storage.LoadConfig(), evt, gen, cts.Token));
    }

    private async Task RunAsync(LampConfig cfg, string evt, long gen, CancellationToken ct)
    {
        var targets = new List<ILightTarget>();
        TuyaLampTarget? lamp = null;
        try
        {
            if (cfg.IsConfigured) targets.Add(lamp = new TuyaLampTarget(cfg));

            if (cfg.Events.TryGetValue(evt, out var sig) && sig.UsesPcRgb(evt))
                targets.AddRange(PcRgb.Targets(cfg.PcRgb));
            else
                await PcRgb.ReleaseAllAsync(); // este evento não usa o RGB do PC: devolve ao perfil normal

            if (targets.Count == 0) return;
            await SignalRunner.RunAsync(cfg, evt, gen, targets, ct);
        }
        catch (OperationCanceledException) { /* substituído por um sinal mais novo */ }
        catch (Exception ex) { Storage.Log($"signal-host: '{evt}' #{gen}: {ex}"); }
        finally { lamp?.Dispose(); }
    }

    /// <summary>Ação manual na lâmpada: cancela o sinal em curso e devolve o RGB do PC.</summary>
    public async Task OnManualChangeAsync()
    {
        lock (_lock) { _running?.Cancel(); _running = null; }
        await PcRgb.ReleaseAllAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_lock) _running?.Cancel();
        try { PcRgb.ReleaseAllAsync().Wait(2000); } catch { }
        PcRgb.Dispose();
        Current = null;
    }
}
