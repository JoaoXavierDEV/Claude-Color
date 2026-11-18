using com.clusterrr.TuyaNet;

namespace ClaudeColor.Core;

public sealed record CloudDevice(string Id, string Name, string? Product, string? Category, string LocalKey, bool Online)
{
    public override string ToString() => $"{Name} ({Category}) — {Id}{(Online ? "" : " [offline]")}";
}

/// <summary>Busca a local key pela API cloud da Tuya (só no setup; o Secret não é guardado).</summary>
public static class CloudKeys
{
    public static IReadOnlyList<string> Regions { get; } = Enum.GetNames<TuyaApi.Region>();

    public static async Task<IReadOnlyList<CloudDevice>> FetchDevicesAsync(
        string region, string accessId, string secret, string anyDeviceId)
    {
        if (!Enum.TryParse<TuyaApi.Region>(region, true, out var r))
            throw new ArgumentException($"Região inválida: {region}");
        var api = new TuyaApi(r, accessId.Trim(), secret.Trim());
        try
        {
            var devices = await api.GetAllDevicesInfoAsync(anyDeviceId.Trim());
            return devices.Select(d => new CloudDevice(d.Id, d.Name, d.ProductName, d.Category, d.LocalKey, d.Online)).ToList();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("sign invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A Tuya recusou a assinatura: Access ID ou Access Secret não conferem. " +
                "Copie de novo em platform.tuya.com → projeto → Overview → Authorization Key.", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException($"Erro da API Tuya: {ex.Message}", ex);
        }
    }
}
