namespace ClaudeLamp.Core;

/// <summary>Algo que acende durante um sinal do Claude e depois devolve o controle.</summary>
public interface ILightTarget : IDisposable
{
    string Name { get; }

    /// <param name="brightness">1–100.</param>
    Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default);

    /// <summary>Volta ao estado normal (estado manual da lâmpada / perfil do software do fabricante).</summary>
    Task ReleaseAsync(CancellationToken ct = default);
}

/// <summary>A lâmpada Tuya como alvo de sinal.</summary>
public sealed class TuyaLampTarget(LampConfig cfg) : ILightTarget
{
    private readonly LampClient _lamp = new(cfg);

    public string Name => "Lâmpada";

    public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default) =>
        _lamp.SetColorAsync(color, brightness, ct);

    public Task ReleaseAsync(CancellationToken ct = default) =>
        _lamp.ApplyAsync(Storage.LoadState().Manual, ct);

    public void Dispose() => _lamp.Dispose();
}

public static class RgbColorExtensions
{
    /// <summary>Escala a cor pelo brilho (1–100), para dispositivos que só aceitam RGB.</summary>
    public static RgbColor Scale(this RgbColor c, int brightness)
    {
        double f = Math.Clamp(brightness, 0, 100) / 100.0;
        return new RgbColor((byte)Math.Round(c.R * f), (byte)Math.Round(c.G * f), (byte)Math.Round(c.B * f));
    }
}
