using ClaudeColor.Core;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;

namespace ClaudeColor.App;

/// <summary>
/// Iluminação dinâmica do Windows 11 (Windows.Devices.Lights.LampArray): teclados, mouses e luminárias HID
/// compatíveis, sem SDK de fabricante. Experimental: com o app em segundo plano, o Windows só entrega o
/// controle a apps com identidade de pacote escolhidos em Configurações → Personalização → Iluminação dinâmica;
/// sem isso, <see cref="LampArray.IsAvailable"/> fica falso e o dispositivo é ignorado.
/// </summary>
internal sealed class DynamicLightingTarget : ILightTarget
{
    private List<(LampArray Lamp, string Name)>? _lamps;

    public string Name => "Iluminação dinâmica";

    private static async Task<List<(LampArray, string)>> OpenAsync()
    {
        var list = new List<(LampArray, string)>();
        foreach (var info in await DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector()))
        {
            try
            {
                if (await LampArray.FromIdAsync(info.Id) is { } lamp) list.Add((lamp, info.Name));
            }
            catch (Exception ex) { Storage.Log($"iluminação dinâmica: {info.Name}: {ex.Message}"); }
        }
        return list;
    }

    /// <summary>IsAvailable só existe no Windows 11 22H2+; antes disso, considera disponível.</summary>
    private static bool Available(LampArray lamp)
    {
        try { return lamp.IsAvailable; }
        catch { return true; }
    }

    public async Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
    {
        _lamps ??= await OpenAsync();
        var c = color.Scale(brightness);
        foreach (var (lamp, name) in _lamps)
        {
            if (!Available(lamp)) continue; // outro app (ou o próprio Windows) está com o controle
            try
            {
                lamp.BrightnessLevel = 1.0;
                lamp.SetColor(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
            }
            catch (Exception ex) { Storage.Log($"iluminação dinâmica: {name}: {ex.Message}"); }
        }
    }

    /// <summary>Solta os dispositivos: sem nenhuma referência viva, o Windows volta ao efeito do sistema.</summary>
    public Task ReleaseAsync(CancellationToken ct = default)
    {
        if (_lamps is null) return Task.CompletedTask;
        _lamps = null; // LampArray não é IDisposable: o controle é liberado quando o objeto é coletado
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return Task.CompletedTask;
    }

    /// <summary>O que o Windows enxerga (para "Detectar" e para a Solução de problemas).</summary>
    public static IReadOnlyList<(string Name, int Lamps, bool Available)> Detect()
    {
        var found = Task.Run(OpenAsync).GetAwaiter().GetResult();
        return found.Select(f => (f.Item2, f.Item1.LampCount, Available(f.Item1))).ToList();
    }

    public static IEnumerable<string> DetectLines()
    {
        var all = Detect();
        if (all.Count == 0) return ["nenhum dispositivo compatível (veja Configurações → Personalização → Iluminação dinâmica)"];
        return all.Select(d => $"{d.Name} ({d.Lamps} LEDs){(d.Available ? "" : " — sem controle agora (app em segundo plano ou outro app com prioridade)")}");
    }

    public void Dispose() => ReleaseAsync().GetAwaiter().GetResult();
}
