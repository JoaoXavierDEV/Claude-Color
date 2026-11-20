using ClaudeColor.Core;
using RGB.NET.Core;
using RGB.NET.Devices.Corsair;

namespace ClaudeColor.Rgb;

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

    /// <summary>A fila dos sinais e o "Detectar" da interface usam o mesmo SDK: um de cada vez.</summary>
    private readonly object _sync = new();

    private void EnsureLoaded()
    {
        if (_surface is not null) return;
        var dll = NativeSdk.Find(NativeSdk.CorsairDll)
            ?? throw new FileNotFoundException($"{NativeSdk.CorsairDll} não encontrado na pasta x64 do app.");
        if (!CorsairDeviceProvider.PossibleX64NativePaths.Contains(dll))
            CorsairDeviceProvider.PossibleX64NativePaths.Insert(0, dll);
        CorsairDeviceProvider.ExclusiveAccess = true;
        // O padrão (500 ms) é curto quando o iCUE está ocupado: a conexão completava depois do timeout.
        CorsairDeviceProvider.ConnectionTimeout = TimeSpan.FromSeconds(3);

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
                bool stale = ex.ToString().Contains("already connected", StringComparison.OrdinalIgnoreCase);
                if (!stale || attempt >= 3) throw;
                // Aqui não há sessão nossa aberta (a DLL acabou de ser descarregada), então o "already connected"
                // é a flag do RGB.NET que ficou presa: o callback de desconexão não chegou antes do Free.
                ResetStaleSessionFlag();
                Thread.Sleep(300 * attempt);
            }
        }
        _surface = surface;
        _group = new ListLedGroup(surface, surface.Leds);
    }

    /// <summary>
    /// O RGB.NET 3.x guarda o estado da sessão numa propriedade estática (<c>_CUESDK.SesionState</c>) que só muda
    /// pelo callback do iCUE. Se a DLL é descarregada antes do callback de desconexão, ela fica "Connected" para
    /// sempre e toda conexão nova falha com "already connected" até reiniciar o app. Zera essa flag.
    /// </summary>
    private static void ResetStaleSessionFlag()
    {
        try
        {
            var sdk = typeof(CorsairDeviceProvider).Assembly.GetType("RGB.NET.Devices.Corsair.Native._CUESDK");
            var prop = sdk?.GetProperty("SesionState", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (prop is null) { Storage.Log("corsair: flag de sessão do RGB.NET não encontrada (versão nova?)"); return; }
            prop.SetValue(null, CorsairSessionState.Closed);
            Storage.Log("corsair: sessão presa como 'conectada' no RGB.NET: flag zerada, tentando de novo");
        }
        catch (Exception ex) { Storage.Log($"corsair: não deu para zerar a flag de sessão: {ex.Message}"); }
    }

    public IReadOnlyList<string> Detect()
    {
        lock (_sync)
        {
            EnsureLoaded();
            var list = _surface!.Devices.Select(d => $"{d.DeviceInfo.DeviceType}: {d.DeviceInfo.Manufacturer} {d.DeviceInfo.Model} ({d.Count()} LEDs)").ToList();
            Release();
            return list;
        }
    }

    public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
    {
        lock (_sync)
        {
            EnsureLoaded();
            var c = color.Scale(brightness);
            _group!.Brush = new SolidColorBrush(new Color(c.R, c.G, c.B));
            _surface!.Update(flushLeds: true);
        }
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken ct = default)
    {
        lock (_sync) Release();
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

    public void Dispose() { lock (_sync) Release(); }
}
