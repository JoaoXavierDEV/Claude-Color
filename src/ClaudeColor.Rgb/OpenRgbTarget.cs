using System.Diagnostics;
using System.Net.Sockets;
using ClaudeColor.Core;

namespace ClaudeColor.Rgb;

/// <summary>
/// Dispositivos escolhidos do OpenRGB (servidor SDK em 127.0.0.1:6742): placa-mãe, RAM, GPU, fans, periféricos...
/// Salva o modo e as cores de cada dispositivo antes do sinal e os restaura no Release.
/// Usa <see cref="OpenRgbProtocolClient"/> (protocolo 6): no OpenRGB 1.x a troca de modo pelos
/// protocolos antigos é ignorada.
/// </summary>
public sealed class OpenRgbTarget(Func<PcRgbConfig> pcConfig) : ILightTarget
{
    private sealed record Saved(string Key, int ModeIndex, OrgbMode? Mode, uint[] LedColors);

    private OpenRgbProtocolClient? _client;

    private OpenRgbConfig config() => pcConfig().OpenRgb;
    private Dictionary<string, Saved>? _saved;

    public string Name => "OpenRGB";

    public static string? ExePath =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe"),
        }.FirstOrDefault(File.Exists);

    /// <summary>A integração nativa ativa que já cobre o dispositivo, ou null se ele fica com o OpenRGB.</summary>
    public static string? NativeOwner(OrgbController d, PcRgbConfig cfg) => DeviceRouting.NativeOwner(d.Type, d.Vendor, d.Name, cfg);

    /// <summary>Por padrão, tudo que nenhuma integração nativa ativa cobre.</summary>
    public static bool IsSelected(OrgbController d, PcRgbConfig cfg) =>
        cfg.OpenRgb.Devices is { } chosen ? chosen.Contains(d.Key) : DeviceRouting.DefaultSelected(d.Type, d.Vendor, d.Name, cfg);

    private OpenRgbProtocolClient Client()
    {
        if (_client is not null) return _client;
        var cfg = config();
        if (!PortOpen(cfg.Host, cfg.Port))
        {
            if (!cfg.AutoStart || ExePath is not { } exe)
                throw new InvalidOperationException($"Servidor do OpenRGB não responde em {cfg.Host}:{cfg.Port}.");
            Storage.Log("openrgb: iniciando servidor");
            Process.Start(new ProcessStartInfo(exe, "--server --startminimized") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!PortOpen(cfg.Host, cfg.Port))
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("O OpenRGB não abriu o servidor SDK em 30 s.");
                Thread.Sleep(500);
            }
            Thread.Sleep(3000); // dá tempo para a detecção de dispositivos
        }
        _client = new OpenRgbProtocolClient(cfg.Host, cfg.Port, "Claude Color");
        return _client;
    }

    /// <summary>Executa com reconexão única, caso o OpenRGB tenha sido reiniciado.</summary>
    private T Call<T>(Func<OpenRgbProtocolClient, T> action)
    {
        try { return action(Client()); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            _client?.Dispose();
            _client = null;
            return action(Client());
        }
    }

    /// <summary>O servidor SDK do OpenRGB responde?</summary>
    public static bool ServerResponds(OpenRgbConfig cfg) => PortOpen(cfg.Host, cfg.Port);

    private static bool PortOpen(string host, int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(host, port).Wait(300) && c.Connected;
        }
        catch { return false; }
    }

    public IReadOnlyList<OrgbController> Devices() => Call(c => c.GetControllers());

    /// <summary>Cada dispositivo com seu identificador único (nomes repetidos, como dois pentes iguais, não colidem).</summary>
    private static IEnumerable<(string Slot, OrgbController Device)> WithSlots(IReadOnlyList<OrgbController> all) =>
        DeviceRouting.Slots(all.Select(d => (d.Key, d.Location))).Zip(all);

    public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
    {
        var c = color.Scale(brightness);
        Call(client =>
        {
            _saved ??= [];
            var cfg = pcConfig();
            foreach (var (slot, d) in WithSlots(client.GetControllers()))
            {
                if (!IsSelected(d, cfg)) continue;
                if (!_saved.ContainsKey(slot))
                {
                    _saved[slot] = new Saved(d.Key, d.ActiveMode, d.ActiveModeData, d.Colors);
                    client.SetCustomMode(d.Id); // "Direct": permite pintar LED a LED
                }
                client.UpdateLeds(d.Id, Enumerable.Repeat((c.R, c.G, c.B), d.LedCount).ToList());
            }
            return true;
        });
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken ct = default)
    {
        Restore();
        return Task.CompletedTask;
    }

    private void Restore()
    {
        if (_saved is null || _saved.Count == 0) { _saved = null; return; }
        try
        {
            Call(client =>
            {
                var bySlot = WithSlots(client.GetControllers()).ToDictionary(x => x.Slot, x => x.Device);
                foreach (var (slot, s) in _saved)
                {
                    if (!bySlot.TryGetValue(slot, out var d) || s.Mode is null) continue;
                    try
                    {
                        client.UpdateMode(d.Id, s.ModeIndex, s.Mode);
                        if (s.Mode.Name is "Direct" or "Custom" && s.LedColors.Length == d.LedCount)
                            client.UpdateLeds(d.Id, s.LedColors);
                        Storage.Log($"openrgb: {slot} restaurado para '{s.Mode.Name}'");
                    }
                    catch (Exception ex) { Storage.Log($"openrgb: restaurar {slot}: {ex.Message}"); }
                }
                return true;
            });
        }
        catch (Exception ex) { Storage.Log($"openrgb: restaurar: {ex.Message}"); }
        _saved = null;
    }

    public void Dispose()
    {
        Restore();
        _client?.Dispose();
        _client = null;
    }
}
