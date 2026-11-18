using ClaudeColor.Core;

namespace ClaudeColor.Rgb;

/// <summary>
/// Uma integração de RGB: o alvo, quando está ativa na config e o que ela enxerga (para "Detectar").
/// Para suportar outro tipo de dispositivo, implemente <see cref="ILightTarget"/> e acrescente uma
/// linha em <see cref="PcRgbController"/> (construtor).
/// </summary>
public sealed record RgbIntegration(ILightTarget Target, Func<PcRgbConfig, bool> IsEnabled, Func<PcRgbConfig, IEnumerable<string>> Detect);

/// <summary>Mantém os alvos de RGB do PC vivos entre sinais (o controle é devolvido no Release).</summary>
public sealed class PcRgbController : IDisposable
{
    private readonly OpenRgbTarget _openRgb = new(() => Storage.LoadConfig().PcRgb);
    private readonly IReadOnlyList<RgbIntegration> _integrations;
    /// <summary>Só para Detectar/listar (que carregam e liberam os SDKs).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ILightTarget, Lane> _lanes = [];

    public PcRgbController()
    {
        var corsair = new CorsairTarget();
        var logitech = new LogitechTarget();
        var msi = new MsiTarget();
        // Cadastro das integrações: a ordem é a do "Detectar".
        _integrations =
        [
            new(_openRgb, c => c.OpenRgb.Enabled, DetectOpenRgb),
            new(corsair, c => c.Corsair, _ => corsair.Detect()),
            new(logitech, c => c.Logitech, _ => [logitech.Detect()]),
            new(msi, c => c.Msi, _ => msi.Detect()),
        ];
    }

    private IEnumerable<string> DetectOpenRgb(PcRgbConfig cfg) => _openRgb.Devices().Select(d =>
    {
        bool on = OpenRgbTarget.IsSelected(d, cfg);
        var owner = OpenRgbTarget.NativeOwner(d, cfg) is { } native ? $" — coberto por {native}" : "";
        return $"{(on ? "[x]" : "[ ]")} {DeviceRouting.TypeName(d.Type)}: {d.Key} ({d.LedCount} LEDs, modo {d.ActiveModeData?.Name}){owner}";
    }).ToList();

    private Lane LaneFor(ILightTarget t)
    {
        lock (_lanes)
        {
            if (!_lanes.TryGetValue(t, out var lane)) _lanes[t] = lane = new Lane(t);
            return lane;
        }
    }

    /// <summary>
    /// Alvos habilitados na config. Cada integração tem sua própria fila (uma lenta não atrasa as outras) e
    /// os comandos são "o último vence": se a GPU ainda está aplicando uma cor, os passos intermediários
    /// do piscar são descartados e só a cor mais recente é aplicada.
    /// </summary>
    public IReadOnlyList<ILightTarget> Targets(PcRgbConfig cfg) =>
        _integrations.Where(i => i.IsEnabled(cfg)).Select(i => (ILightTarget)LaneFor(i.Target)).ToList();

    public async Task ReleaseAllAsync()
    {
        Lane[] lanes;
        lock (_lanes) lanes = _lanes.Values.ToArray();
        await Task.WhenAll(lanes.Select(l => l.ReleaseAndWaitAsync(TimeSpan.FromSeconds(10))));
    }

    /// <summary>Lista o que cada integração ativa enxerga (carrega e libera em seguida).</summary>
    public async Task<IReadOnlyList<string>> DetectAsync(PcRgbConfig cfg)
    {
        var lines = new List<string>();
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                foreach (var i in _integrations.Where(i => i.IsEnabled(cfg)))
                {
                    try { lines.AddRange(i.Detect(cfg).Select(d => $"{i.Target.Name} → {d}")); }
                    catch (Exception ex) { lines.Add($"{i.Target.Name}: {ex.Message}"); }
                }
            });
        }
        finally { _gate.Release(); }
        if (lines.Count == 0) lines.Add("Nenhuma integração de RGB ativa.");
        return lines;
    }

    /// <summary>Dispositivos do OpenRGB com a marcação atual (para a lista da interface).</summary>
    public async Task<IReadOnlyList<(string Key, string Label, bool Selected)>> OpenRgbDevicesAsync(PcRgbConfig cfg)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => (IReadOnlyList<(string, string, bool)>)_openRgb.Devices().Select(d =>
            {
                var owner = OpenRgbTarget.NativeOwner(d, cfg) is { } native ? $" (coberto por {native})" : "";
                return (d.Key, $"{DeviceRouting.TypeName(d.Type)}: {d.Key}, {d.LedCount} LEDs{owner}", OpenRgbTarget.IsSelected(d, cfg));
            }).ToList());
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        foreach (var i in _integrations) i.Target.Dispose();
    }

    /// <summary>Fila "último vence" de um alvo: aplica o comando mais recente, descartando os intermediários.</summary>
    private sealed class Lane(ILightTarget inner) : ILightTarget
    {
        private readonly object _lock = new();
        private (RgbColor Color, int Brightness)? _pendingShow;
        private bool _pendingRelease, _holding;
        private Task _worker = Task.CompletedTask;

        public string Name => inner.Name;

        public bool Blinks => inner.Blinks;

        public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
        {
            lock (_lock)
            {
                _pendingShow = (color, brightness);
                _pendingRelease = false;
                EnsureWorker();
            }
            return Task.CompletedTask; // não espera: um SDK lento não segura o piscar dos outros
        }

        public Task ReleaseAsync(CancellationToken ct = default) => ReleaseAndWaitAsync(TimeSpan.FromSeconds(10));

        public Task ReleaseAndWaitAsync(TimeSpan timeout)
        {
            Task worker;
            lock (_lock)
            {
                if (!_holding && _pendingShow is null) return Task.CompletedTask;
                _pendingShow = null;
                _pendingRelease = true;
                EnsureWorker();
                worker = _worker;
            }
            return Task.WhenAny(worker, Task.Delay(timeout));
        }

        private void EnsureWorker()
        {
            if (_worker.IsCompleted) _worker = Task.Run(WorkAsync);
        }

        private async Task WorkAsync()
        {
            while (true)
            {
                (RgbColor Color, int Brightness)? show;
                bool release;
                lock (_lock)
                {
                    show = _pendingShow;
                    release = _pendingRelease;
                    _pendingShow = null;
                    _pendingRelease = false;
                    if (show is null && !release) return;
                }
                try
                {
                    if (show is { } s)
                    {
                        _holding = true;
                        await inner.ShowAsync(s.Color, s.Brightness);
                    }
                    else
                    {
                        await inner.ReleaseAsync();
                        _holding = false;
                    }
                }
                catch (Exception ex)
                {
                    Storage.Log($"pc-rgb: {inner.Name}: {ex.Message}");
                }
            }
        }

        public void Dispose() { /* o controller é o dono */ }
    }
}
