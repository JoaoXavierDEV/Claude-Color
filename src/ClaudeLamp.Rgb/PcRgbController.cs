using ClaudeLamp.Core;

namespace ClaudeLamp.Rgb;

/// <summary>Mantém os alvos de RGB do PC vivos entre sinais (o controle é devolvido no Release).</summary>
public sealed class PcRgbController : IDisposable
{
    private readonly CorsairTarget _corsair = new();
    private readonly LogitechTarget _logitech = new();
    private readonly MsiTarget _msi = new();
    private readonly OpenRgbTarget _openRgb = new(() => Storage.LoadConfig().PcRgb.OpenRgb);
    /// <summary>Só para Detectar/listar (que carregam e liberam os SDKs).</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ILightTarget, Lane> _lanes = [];

    private Lane LaneFor(ILightTarget t)
    {
        lock (_lanes)
        {
            if (!_lanes.TryGetValue(t, out var lane)) _lanes[t] = lane = new Lane(t);
            return lane;
        }
    }

    /// <summary>
    /// Alvos habilitados na config. Cada SDK tem sua própria fila (um lento não atrasa os outros) e
    /// os comandos são "o último vence": se a GPU ainda está aplicando uma cor, os passos intermediários
    /// do piscar são descartados e só a cor mais recente é aplicada.
    /// </summary>
    public IReadOnlyList<ILightTarget> Targets(PcRgbConfig cfg)
    {
        var list = new List<ILightTarget>();
        if (cfg.Corsair) list.Add(LaneFor(_corsair));
        if (cfg.Logitech) list.Add(LaneFor(_logitech));
        if (cfg.Msi) list.Add(LaneFor(_msi));
        if (cfg.OpenRgb.Enabled) list.Add(LaneFor(_openRgb));
        return list;
    }

    public async Task ReleaseAllAsync()
    {
        Lane[] lanes;
        lock (_lanes) lanes = _lanes.Values.ToArray();
        await Task.WhenAll(lanes.Select(l => l.ReleaseAndWaitAsync(TimeSpan.FromSeconds(10))));
    }

    /// <summary>Lista o que cada SDK enxerga (carrega e libera em seguida).</summary>
    public async Task<IReadOnlyList<string>> DetectAsync(PcRgbConfig cfg)
    {
        var lines = new List<string>();
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                if (cfg.Corsair)
                {
                    try { lines.AddRange(_corsair.Detect().Select(d => "Corsair iCUE → " + d)); }
                    catch (Exception ex) { lines.Add("Corsair iCUE: " + ex.Message); }
                }
                if (cfg.Logitech)
                {
                    try { lines.Add("Logitech → " + _logitech.Detect()); }
                    catch (Exception ex) { lines.Add("Logitech: " + ex.Message); }
                }
                if (cfg.Msi)
                {
                    try { lines.AddRange(_msi.Detect().Select(d => "MSI Mystic Light → " + d)); }
                    catch (Exception ex) { lines.Add("MSI Mystic Light: " + ex.Message); }
                }
                if (cfg.OpenRgb.Enabled)
                {
                    try
                    {
                        foreach (var d in _openRgb.Devices())
                        {
                            bool on = cfg.OpenRgb.Devices is null ? OpenRgbTarget.DefaultSelected(d) : cfg.OpenRgb.Devices.Contains(d.Key);
                            lines.Add($"OpenRGB → {(on ? "[x]" : "[ ]")} {OpenRgbTarget.TypeName(d.Type)}: {d.Key} ({d.LedCount} LEDs, modo {d.ActiveModeData?.Name})");
                        }
                    }
                    catch (Exception ex) { lines.Add("OpenRGB: " + ex.Message); }
                }
            });
        }
        finally { _gate.Release(); }
        if (lines.Count == 0) lines.Add("Nenhum software de RGB habilitado.");
        return lines;
    }

    /// <summary>Dispositivos do OpenRGB com a marcação atual (para a lista da interface).</summary>
    public async Task<IReadOnlyList<(string Key, string Label, bool Selected)>> OpenRgbDevicesAsync(OpenRgbConfig cfg)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => (IReadOnlyList<(string, string, bool)>)_openRgb.Devices().Select(d =>
            {
                bool on = cfg.Devices is null ? OpenRgbTarget.DefaultSelected(d) : cfg.Devices.Contains(d.Key);
                return (d.Key, $"{d.Key} — {OpenRgbTarget.TypeName(d.Type)}, {d.LedCount} LEDs", on);
            }).ToList());
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _corsair.Dispose();
        _logitech.Dispose();
        _msi.Dispose();
        _openRgb.Dispose();
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
