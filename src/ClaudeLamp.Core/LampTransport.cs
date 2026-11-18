using com.clusterrr.TuyaNet;

namespace ClaudeLamp.Core;

/// <summary>Camada de comunicação com a lâmpada (permite trocar o protocolo e simular nos testes).</summary>
public interface ILampTransport : IDisposable
{
    Task<Dictionary<int, object>> GetDpsAsync(CancellationToken ct = default);
    Task SetDpsAsync(Dictionary<int, object> dps, CancellationToken ct = default);
}

/// <summary>Protocolos 3.1 e 3.3 via biblioteca TuyaNet (ClusterM).</summary>
public sealed class TuyaNetTransport : ILampTransport
{
    private readonly TuyaDevice _device;

    public TuyaNetTransport(string ip, string localKey, string deviceId, string version)
    {
        var v = version switch
        {
            "3.1" => TuyaProtocolVersion.V31,
            "3.3" or "3.2" => TuyaProtocolVersion.V33,
            _ => throw new NotSupportedException(
                $"Protocolo Tuya {version} ainda não suportado (TuyaNet cobre 3.1/3.3). " +
                "Rode `claude-lamp discover` e me avise a versão para implementarmos 3.4/3.5."),
        };
        _device = new TuyaDevice(ip, localKey, deviceId, v, receiveTimeout: 1500)
        {
            PermanentConnection = true,
            ConnectionTimeout = 2000,
        };
    }

    public Task<Dictionary<int, object>> GetDpsAsync(CancellationToken ct = default) =>
        _device.GetDpsAsync(retries: 2, cancellationToken: ct);

    public Task SetDpsAsync(Dictionary<int, object> dps, CancellationToken ct = default) =>
        _device.SetDpsAsync(dps, retries: 2, allowEmptyResponse: true, cancellationToken: ct);

    public void Dispose() => _device.Dispose();
}
