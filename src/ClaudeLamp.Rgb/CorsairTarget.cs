using ClaudeLamp.Core;
using RGB.NET.Core;
using RGB.NET.Devices.Corsair;

namespace ClaudeLamp.Rgb;

/// <summary>
/// Dispositivos expostos pelo iCUE SDK (RAM, placa-mãe, ...). Assume o controle exclusivo durante o sinal
/// e o devolve no Release, quando o iCUE volta ao perfil dele.
/// </summary>
public sealed class CorsairTarget : ILightTarget
{
    private RGBSurface? _surface;
    private ListLedGroup? _group;

    public string Name => "Corsair iCUE";

    public static bool SdkAvailable => NativeSdk.Find(NativeSdk.CorsairDll) is not null;

    private void EnsureLoaded()
    {
        if (_surface is not null) return;
        var dll = NativeSdk.Find(NativeSdk.CorsairDll)
            ?? throw new FileNotFoundException($"{NativeSdk.CorsairDll} não encontrado na pasta x64 do app.");
        if (!CorsairDeviceProvider.PossibleX64NativePaths.Contains(dll))
            CorsairDeviceProvider.PossibleX64NativePaths.Insert(0, dll);
        CorsairDeviceProvider.ExclusiveAccess = true;

        // Logo depois de um Release o iCUE ainda pode estar fechando a sessão anterior ("already connected"):
        // espera um pouco e tenta de novo.
        RGBSurface? surface = null;
        for (int attempt = 1; ; attempt++)
        {
            surface = new RGBSurface();
            try
            {
                surface.Load(CorsairDeviceProvider.Instance, RGBDeviceType.All, throwExceptions: true);
                if (surface.Devices.Count == 0)
                    throw new InvalidOperationException("O iCUE não expôs nenhum dispositivo. \"Habilitar SDK\" está ligado no iCUE?");
                break;
            }
            catch (Exception ex)
            {
                surface.Dispose();
                CorsairDeviceProvider.Instance.Dispose();
                bool busy = ex.ToString().Contains("already connected", StringComparison.OrdinalIgnoreCase);
                if (!busy || attempt >= 5) throw;
                Thread.Sleep(300 * attempt);
            }
        }
        _surface = surface;
        _group = new ListLedGroup(surface, surface.Leds);
    }

    public IReadOnlyList<string> Detect()
    {
        EnsureLoaded();
        var list = _surface!.Devices.Select(d => $"{d.DeviceInfo.DeviceType}: {d.DeviceInfo.Manufacturer} {d.DeviceInfo.Model} ({d.Count()} LEDs)").ToList();
        Release();
        return list;
    }

    public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
    {
        EnsureLoaded();
        var c = color.Scale(brightness);
        _group!.Brush = new SolidColorBrush(new Color(c.R, c.G, c.B));
        _surface!.Update(flushLeds: true);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken ct = default)
    {
        Release();
        return Task.CompletedTask;
    }

    private void Release()
    {
        if (_surface is null) return;
        try
        {
            _group?.Detach();
            _surface.Detach(_surface.Devices.ToArray());
            _surface.Dispose();
        }
        finally
        {
            // Encerra a sessão do SDK: o iCUE retoma o próprio perfil.
            CorsairDeviceProvider.Instance.Dispose();
            _surface = null;
            _group = null;
        }
    }

    public void Dispose() => Release();
}
