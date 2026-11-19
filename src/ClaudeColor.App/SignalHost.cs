using System.IO.Pipes;
using System.Text;
using ClaudeColor.Core;
using ClaudeColor.Rgb;

namespace ClaudeColor.App;

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

    /// <summary>Alguém pediu pelo pipe para o app sair ("ClaudeColor.exe --exit").</summary>
    public event Action? QuitRequested;

    public PcRgbController PcRgb { get; } = new(
    [
        new RgbIntegration(new DynamicLightingTarget(), c => c.DynamicLighting, _ => DynamicLightingTarget.DetectLines()),
    ]);

    public SignalHost()
    {
        Current = this;
        _ = Task.Run(ListenAsync);
        // O Mystic Light demora para inicializar: faz isso já, antes do primeiro sinal.
        _ = Task.Run(() => { if (Storage.LoadConfig().PcRgb.Msi && Elevation.IsElevated) MsiTarget.Warmup(); });
        // Aberto por um hook (o app estava fechado): executa o sinal que o hook acabou de registrar.
        var rt = Storage.LoadState();
        if (rt.PendingSignal(TimeSpan.FromSeconds(20)) is { } pending)
        {
            Storage.Log($"signal-host: executando '{pending.Event}' #{pending.Generation} pendente");
            Dispatch(pending.Event, pending.Generation, session: new SessionInfo(rt.LastWindow ?? 0, rt.LastProject));
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = SignalPipe.CreateServer();
                await server.WaitForConnectionAsync(_stop.Token);
                var buf = new byte[SignalPipe.MaxLine];
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                readCts.CancelAfter(1000);
                int n = await server.ReadAsync(buf, readCts.Token);
                var line = Encoding.UTF8.GetString(buf, 0, n);
                if (line.Trim() == SignalPipe.QuitCommand)
                {
                    await server.WriteAsync("ok\n"u8.ToArray(), _stop.Token);
                    await server.FlushAsync(_stop.Token);
                    Storage.Log("signal-host: saída pedida pelo pipe");
                    QuitRequested?.Invoke();
                    break;
                }
                if (SignalPipe.TryParse(line, out var evt, out var gen, out var session))
                {
                    await server.WriteAsync("ok\n"u8.ToArray(), _stop.Token);
                    await server.FlushAsync(_stop.Token);
                    Dispatch(evt, gen, session: session);
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
    public void Dispatch(string evt, long gen, AppConfig? cfgOverride = null, SessionInfo? session = null)
    {
        var cfg = cfgOverride ?? Storage.LoadConfig();
        // Notificação: independe das luzes (funciona mesmo sem nenhum dispositivo RGB).
        if (cfgOverride is null || session is not null) Notifier.Signal(cfg, evt, session ?? SessionInfo.None);
        CancellationTokenSource cts;
        lock (_lock)
        {
            _running?.Cancel();
            _running = cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        }
        _ = Task.Run(() => RunAsync(cfg, evt, gen, cts.Token));
    }

    private async Task RunAsync(AppConfig cfg, string evt, long gen, CancellationToken ct)
    {
        var targets = new List<ILightTarget>();
        TuyaLampTarget? lamp = null;
        try
        {
            if (cfg.Quiet.SkipLights && evt != ClaudeEvents.Resumed && UserBusy.Current() is var busy && UserBusy.IsBusy(busy))
            {
                Storage.Log($"signal-host: luzes seguradas para '{evt}' #{gen} ({UserBusy.Describe(busy)})");
                return;
            }
            if (cfg.Lamp.IsActive) targets.Add(lamp = new TuyaLampTarget(cfg.Lamp));

            if (evt == ClaudeEvents.Resumed)
                targets.AddRange(PcRgb.Targets(cfg.PcRgb)); // volta para "trabalhando" ou devolve o PC
            else if (cfg.Events.TryGetValue(evt, out var sig) && sig.UsesPcRgb(evt))
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
