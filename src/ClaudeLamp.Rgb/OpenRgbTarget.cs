using System.Diagnostics;
using System.Net.Sockets;
using ClaudeLamp.Core;

namespace ClaudeLamp.Rgb;

/// <summary>
/// Dispositivos escolhidos do OpenRGB (servidor SDK em 127.0.0.1:6742), por exemplo fans Cooler Master.
/// Salva o modo e as cores de cada dispositivo antes do sinal e os restaura no Release.
/// Usa <see cref="OpenRgbProtocolClient"/> (protocolo 6): no OpenRGB 1.x a troca de modo pelos
/// protocolos antigos é ignorada.
/// </summary>
public sealed class OpenRgbTarget(Func<OpenRgbConfig> config) : ILightTarget
{
    private sealed record Saved(string Key, int ModeIndex, OrgbMode? Mode, uint[] LedColors);

    private OpenRgbProtocolClient? _client;
    private Dictionary<string, Saved>? _saved;

    public string Name => "OpenRGB";

    public static string? ExePath =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "OpenRGB", "OpenRGB.exe"),
        }.FirstOrDefault(File.Exists);

    /// <summary>Por padrão, só o que os outros SDKs não cobrem (ex.: fans Cooler Master). A GPU vai melhor pelo Mystic Light.</summary>
    public static bool DefaultSelected(OrgbController d) =>
        d.Type is not (OpenRgbProtocolClient.TypeDram or OpenRgbProtocolClient.TypeKeyboard or OpenRgbProtocolClient.TypeMouse
                       or OpenRgbProtocolClient.TypeMotherboard or OpenRgbProtocolClient.TypeGpu)
        && !d.Vendor.Contains("Corsair", StringComparison.OrdinalIgnoreCase)
        && !d.Vendor.Contains("Logitech", StringComparison.OrdinalIgnoreCase);

    public static string TypeName(int type) => type switch
    {
        0 => "Placa-mãe", 1 => "Memória", 2 => "GPU", 3 => "Cooler", 4 => "Fita/ARGB", 5 => "Teclado", 6 => "Mouse",
        7 => "Mousepad", 8 => "Headset", 15 => "Gabinete", _ => $"tipo {type}",
    };

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
        _client = new OpenRgbProtocolClient(cfg.Host, cfg.Port, "ClaudeLamp");
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

    private IEnumerable<OrgbController> Selected(IReadOnlyList<OrgbController> all)
    {
        var chosen = config().Devices;
        return all.Where(d => chosen is null ? DefaultSelected(d) : chosen.Contains(d.Key));
    }

    public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
    {
        var c = color.Scale(brightness);
        Call(client =>
        {
            _saved ??= [];
            foreach (var d in Selected(client.GetControllers()))
            {
                if (!_saved.ContainsKey(d.Key))
                {
                    _saved[d.Key] = new Saved(d.Key, d.ActiveMode, d.ActiveModeData, d.Colors);
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
                var byKey = client.GetControllers().ToDictionary(d => d.Key);
                foreach (var s in _saved.Values)
                {
                    if (!byKey.TryGetValue(s.Key, out var d) || s.Mode is null) continue;
                    try
                    {
                        client.UpdateMode(d.Id, s.ModeIndex, s.Mode);
                        if (s.Mode.Name is "Direct" or "Custom" && s.LedColors.Length == d.LedCount)
                            client.UpdateLeds(d.Id, s.LedColors);
                        Storage.Log($"openrgb: {s.Key} restaurado para '{s.Mode.Name}'");
                    }
                    catch (Exception ex) { Storage.Log($"openrgb: restaurar {s.Key}: {ex.Message}"); }
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
