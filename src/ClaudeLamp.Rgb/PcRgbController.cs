using ClaudeLamp.Core;

namespace ClaudeLamp.Rgb;

/// <summary>Mantém os alvos de RGB do PC vivos entre sinais (o controle é devolvido no Release).</summary>
public sealed class PcRgbController : IDisposable
{
    private readonly CorsairTarget _corsair = new();
    private readonly LogitechTarget _logitech = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _holding;

    /// <summary>Alvos habilitados na config, já serializados (os SDKs não gostam de chamadas concorrentes).</summary>
    public IReadOnlyList<ILightTarget> Targets(PcRgbConfig cfg)
    {
        var list = new List<ILightTarget>();
        if (cfg.Corsair) list.Add(new Serialized(this, _corsair));
        if (cfg.Logitech) list.Add(new Serialized(this, _logitech));
        return list;
    }

    public async Task ReleaseAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_holding) return;
            foreach (var t in new ILightTarget[] { _corsair, _logitech })
            {
                try { await t.ReleaseAsync(); }
                catch (Exception ex) { Storage.Log($"pc-rgb: release {t.Name}: {ex.Message}"); }
            }
            _holding = false;
        }
        finally { _gate.Release(); }
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
            });
        }
        finally { _gate.Release(); }
        if (lines.Count == 0) lines.Add("Nenhum software de RGB habilitado.");
        return lines;
    }

    public void Dispose()
    {
        _corsair.Dispose();
        _logitech.Dispose();
    }

    private sealed class Serialized(PcRgbController owner, ILightTarget inner) : ILightTarget
    {
        public string Name => inner.Name;

        public async Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
        {
            await owner._gate.WaitAsync(ct);
            try
            {
                owner._holding = true;
                await Task.Run(() => inner.ShowAsync(color, brightness, ct), ct);
            }
            finally { owner._gate.Release(); }
        }

        public async Task ReleaseAsync(CancellationToken ct = default)
        {
            await owner._gate.WaitAsync(ct);
            try { await Task.Run(() => inner.ReleaseAsync(ct), ct); }
            finally { owner._gate.Release(); }
        }

        public void Dispose() { /* o controller é o dono */ }
    }
}
