using System.Net.Sockets;
using System.Text.Json;

namespace ClaudeLamp.Core;

/// <summary>Mapa de DPs de uma lâmpada Tuya.</summary>
public sealed record DpsProfile(string Name, int Power, int Mode, int Brightness, int Temp, int Colour,
    int BrightMin, int BrightMax, int TempMax, bool ColourV2)
{
    /// <summary>Firmware atual (category "dj", colour_data_v2).</summary>
    public static readonly DpsProfile V2 = new("v2", 20, 21, 22, 23, 24, 10, 1000, 1000, true);
    /// <summary>Firmware antigo (colour_data de 14 caracteres).</summary>
    public static readonly DpsProfile V1 = new("v1", 1, 2, 3, 4, 5, 25, 255, 255, false);

    public static DpsProfile? FromName(string? name) => name?.ToLowerInvariant() switch
    {
        "v1" => V1,
        "v2" => V2,
        _ => null,
    };

    public static DpsProfile Detect(IReadOnlyDictionary<int, object> dps) =>
        dps.ContainsKey(20) || dps.ContainsKey(24) ? V2 : V1;

    public int ToBright(int percent) =>
        BrightMin + (int)Math.Round((BrightMax - BrightMin) * (Math.Clamp(percent, 1, 100) - 1) / 99.0);

    public int FromBright(int raw) =>
        1 + (int)Math.Round(99.0 * (Math.Clamp(raw, BrightMin, BrightMax) - BrightMin) / (BrightMax - BrightMin));
}

/// <summary>Operações de alto nível sobre a lâmpada.</summary>
public sealed class LampClient : IDisposable
{
    private readonly LampConfig _config;
    private readonly Func<LampConfig, ILampTransport> _factory;
    private ILampTransport? _transport;
    private DpsProfile? _profile;

    public LampClient(LampConfig config, Func<LampConfig, ILampTransport>? transportFactory = null)
    {
        if (!config.IsConfigured)
            throw new InvalidOperationException("Lâmpada não configurada. Rode `claude-lamp setup` primeiro.");
        _config = config;
        _factory = transportFactory ?? DefaultFactory;
        _profile = DpsProfile.FromName(config.DpsProfile);
    }

    public static ILampTransport DefaultFactory(LampConfig c) => c.ProtocolVersion == "3.1"
        ? new TuyaNetTransport(c.Ip!, c.LocalKey!, c.DeviceId!, c.ProtocolVersion)
        : new TuyaLocalTransport(c.Ip!, c.LocalKey!, c.DeviceId!, c.ProtocolVersion);

    /// <summary>Testa as versões de protocolo até uma responder. Retorna a versão e os DPs.</summary>
    public static async Task<(string Version, Dictionary<int, object> Dps)?> ProbeAsync(
        LampConfig cfg, Action<string>? log = null, CancellationToken ct = default)
    {
        foreach (var v in new[] { "3.5", "3.4", "3.3", "3.1" })
        {
            var probe = new LampConfig { DeviceId = cfg.DeviceId, LocalKey = cfg.LocalKey, Ip = cfg.Ip, ProtocolVersion = v };
            try
            {
                using var t = DefaultFactory(probe);
                var dps = await t.GetDpsAsync(ct).WaitAsync(TimeSpan.FromSeconds(6), ct);
                log?.Invoke($"  {v}: OK");
                return (v, dps);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"  {v}: {ex.GetType().Name}: {ex.Message}");
                Storage.Log($"probe {v}: {ex}");
            }
        }
        return null;
    }

    public async Task<Dictionary<int, object>> GetDpsAsync(CancellationToken ct = default) =>
        await WithRetryAsync(t => t.GetDpsAsync(ct), ct);

    public async Task<DpsProfile> GetProfileAsync(CancellationToken ct = default)
    {
        if (_profile is not null) return _profile;
        _profile = DpsProfile.Detect(await GetDpsAsync(ct));
        Storage.Log($"perfil de DPs detectado: {_profile.Name}");
        return _profile;
    }

    public Task SetPowerAsync(bool on, CancellationToken ct = default) =>
        SetAsync(p => new() { [p.Power] = on }, ct);

    public Task SetColorAsync(RgbColor color, int brightness, CancellationToken ct = default) =>
        SetAsync(p => new()
        {
            [p.Power] = true,
            [p.Mode] = "colour",
            [p.Colour] = p.ColourV2 ? TuyaColor.ToV2(color, brightness) : TuyaColor.ToV1(color, brightness),
        }, ct);

    /// <param name="temp">0 = quente, 100 = frio.</param>
    public Task SetWhiteAsync(int brightness, int temp, CancellationToken ct = default) =>
        SetAsync(p => new()
        {
            [p.Power] = true,
            [p.Mode] = "white",
            [p.Brightness] = p.ToBright(brightness),
            [p.Temp] = (int)Math.Round(p.TempMax * Math.Clamp(temp, 0, 100) / 100.0),
        }, ct);

    /// <summary>Muda só o brilho, mantendo o modo atual.</summary>
    public async Task SetBrightnessAsync(int brightness, CancellationToken ct = default)
    {
        var state = await ReadStateAsync(ct);
        state.Brightness = brightness;
        await ApplyAsync(state, ct);
    }

    public Task ApplyAsync(ManualState s, CancellationToken ct = default)
    {
        if (!s.On) return SetPowerAsync(false, ct);
        return s.Mode == "colour"
            ? SetColorAsync(RgbColor.Parse(s.Color), s.Brightness, ct)
            : SetWhiteAsync(s.Brightness, s.WhiteTemp, ct);
    }

    /// <summary>Lê o estado atual da lâmpada no formato de <see cref="ManualState"/>.</summary>
    public async Task<ManualState> ReadStateAsync(CancellationToken ct = default)
    {
        var dps = await GetDpsAsync(ct);
        var p = _profile ??= DpsProfile.Detect(dps);
        var s = new ManualState();
        if (dps.TryGetValue(p.Power, out var on)) s.On = AsBool(on);
        if (dps.TryGetValue(p.Mode, out var mode)) s.Mode = mode?.ToString() == "colour" ? "colour" : "white";
        if (dps.TryGetValue(p.Brightness, out var b) && AsInt(b) is { } bi) s.Brightness = p.FromBright(bi);
        if (dps.TryGetValue(p.Temp, out var t) && AsInt(t) is { } ti) s.WhiteTemp = (int)Math.Round(100.0 * ti / p.TempMax);
        if (s.Mode == "colour" && p.ColourV2 && dps.TryGetValue(p.Colour, out var c) &&
            TuyaColor.TryParseV2(c?.ToString(), out var rgb, out var cb))
        {
            s.Color = rgb.ToHex();
            s.Brightness = cb;
        }
        return s;
    }

    /// <summary>Pisca alternando o brilho da cor e termina na cor acesa.</summary>
    public async Task BlinkAsync(RgbColor color, int brightness, int times, int intervalMs, CancellationToken ct = default)
    {
        for (int i = 0; i < times; i++)
        {
            await SetColorAsync(color, brightness, ct);
            await Task.Delay(intervalMs, ct);
            await SetColorAsync(color, 1, ct);
            await Task.Delay(intervalMs, ct);
        }
        await SetColorAsync(color, brightness, ct);
    }

    private async Task SetAsync(Func<DpsProfile, Dictionary<int, object>> build, CancellationToken ct)
    {
        var p = await GetProfileAsync(ct);
        var dps = build(p);
        await WithRetryAsync(async t => { await t.SetDpsAsync(dps, ct); return true; }, ct);
    }

    /// <summary>Se a conexão falhar (ex.: IP mudou no DHCP), redescobre a lâmpada e tenta de novo.</summary>
    private async Task<T> WithRetryAsync<T>(Func<ILampTransport, Task<T>> op, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.Ip))
            await RediscoverAsync(ct);
        try
        {
            return await op(_transport ??= _factory(_config));
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException && !ct.IsCancellationRequested)
        {
            Storage.Log($"falha ao falar com {_config.Ip}: {ex.Message}; redescobrindo");
            _transport?.Dispose();
            _transport = null;
            if (!await RediscoverAsync(ct)) throw;
            return await op(_transport = _factory(_config));
        }
    }

    private async Task<bool> RediscoverAsync(CancellationToken ct)
    {
        var oldIp = _config.Ip;
        var devices = await TuyaDiscovery.ScanAsync(TimeSpan.FromSeconds(6), _config.DeviceId, ct: ct);
        var me = devices.FirstOrDefault(d => d.GwId == _config.DeviceId);
        if (me is not null)
        {
            _config.Ip = me.Ip;
            if (me.Version is "3.1" or "3.3" or "3.4" or "3.5") _config.ProtocolVersion = me.Version;
        }
        else if (await FindByTcpAsync(ct) is { } ip)
        {
            _config.Ip = ip;
        }
        else return false;
        bool changed = _config.Ip != oldIp;
        if (changed)
        {
            Storage.Log($"novo IP da lâmpada: {_config.Ip}");
            var saved = Storage.LoadConfig();
            saved.Ip = _config.Ip;
            saved.ProtocolVersion = _config.ProtocolVersion;
            Storage.SaveConfig(saved);
        }
        return true;
    }

    /// <summary>Testa a local key em cada host com a porta 6668 aberta; o que responder é a nossa lâmpada.</summary>
    private async Task<string?> FindByTcpAsync(CancellationToken ct)
    {
        foreach (var ip in await TuyaDiscovery.FindTcpCandidatesAsync(ct: ct))
        {
            var probe = new LampConfig
            {
                DeviceId = _config.DeviceId, LocalKey = _config.LocalKey, Ip = ip,
                ProtocolVersion = _config.ProtocolVersion,
            };
            try
            {
                using var t = _factory(probe);
                var dps = await t.GetDpsAsync(ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
                if (dps.Count > 0) return ip;
            }
            catch (Exception ex) { Storage.Log($"tcp-probe {ip}: {ex.Message}"); }
        }
        return null;
    }

    private static bool AsBool(object? v) => v switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        _ => bool.TryParse(v?.ToString(), out var r) && r,
    };

    private static int? AsInt(object? v) => v switch
    {
        int i => i,
        long l => (int)l,
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetInt32(),
        _ => int.TryParse(v?.ToString(), out var r) ? r : null,
    };

    public void Dispose() => _transport?.Dispose();
}
