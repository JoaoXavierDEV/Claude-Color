using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeColor.Core;

public sealed record DiscoveredDevice(string Ip, string GwId, string Version, string? ProductKey, int Port)
{
    public override string ToString() => $"{Ip,-15}  id={GwId}  versão={Version}  porta={Port}" +
                                         (ProductKey is null ? "" : $"  produto={ProductKey}");
}

/// <summary>
/// Escuta os broadcasts UDP que dispositivos Tuya enviam a cada ~5 s:
/// 6666 (texto puro, 3.1), 6667 (AES-ECB, 3.2–3.4) e 7000 (AES-GCM, 3.5).
/// </summary>
public static class TuyaDiscovery
{
    private static readonly byte[] UdpKey = MD5.HashData("yGAdlopoPVldABfn"u8);
    private static readonly int[] Ports = [6666, 6667, 7000];

    public static async Task<IReadOnlyList<DiscoveredDevice>> ScanAsync(
        TimeSpan duration, string? stopWhenFound = null, Action<DiscoveredDevice>? onFound = null,
        CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredDevice>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);

        var clients = new List<UdpClient>();
        foreach (var port in Ports)
        {
            try
            {
                var c = new UdpClient(AddressFamily.InterNetwork);
                c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                c.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                clients.Add(c);
            }
            catch (SocketException ex)
            {
                Storage.Log($"discovery: não foi possível escutar UDP {port}: {ex.Message}");
            }
        }

        if (clients.Count == 0)
            throw new InvalidOperationException("Nenhuma porta UDP (6666/6667/7000) pôde ser aberta. Outro programa Tuya está rodando?");

        async Task Listen(UdpClient client)
        {
            int port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await client.ReceiveAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }

                var dev = TryParse(r.Buffer, r.RemoteEndPoint.Address.ToString(), port);
                if (dev is null) continue;
                bool isNew;
                lock (found) isNew = found.TryAdd(dev.GwId, dev);
                if (!isNew) continue;
                onFound?.Invoke(dev);
                if (stopWhenFound is not null && dev.GwId == stopWhenFound) cts.Cancel();
            }
        }

        try { await Task.WhenAll(clients.Select(Listen)); }
        finally { clients.ForEach(c => c.Dispose()); }

        lock (found) return found.Values.ToList();
    }

    /// <summary>
    /// Plano B quando o roteador bloqueia broadcasts: varre as redes /24 locais procurando a porta TCP 6668.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindTcpCandidatesAsync(int timeoutMs = 1500, CancellationToken ct = default)
    {
        var prefixes = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        n.NetworkInterfaceType is not System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                        !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("WSL", StringComparison.OrdinalIgnoreCase) &&
                        !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.GetAddressBytes())
            .Where(b => b[0] != 169)
            .Select(b => $"{b[0]}.{b[1]}.{b[2]}.")
            .Distinct()
            .ToList();

        var open = new List<string>();
        var probes = prefixes.SelectMany(p => Enumerable.Range(1, 254).Select(i => p + i)).Select(async ip =>
        {
            using var c = new TcpClient();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                await c.ConnectAsync(ip, 6668, cts.Token);
                lock (open) open.Add(ip);
            }
            catch { /* fechado */ }
        });
        await Task.WhenAll(probes);
        return open;
    }

    /// <summary>Tenta todas as formas conhecidas de decodificar o pacote.</summary>
    internal static DiscoveredDevice? TryParse(byte[] packet, string fromIp, int port)
    {
        string? json = null;
        try
        {
            if (packet.Length > 24 && BinaryPrimitives.ReadUInt32BigEndian(packet) == 0x000055AA)
            {
                // prefixo(4) seq(4) cmd(4) len(4) retcode(4) payload ... crc(4) sufixo(4)
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12));
                int end = Math.Min(packet.Length, 16 + len) - 8;
                var payload = packet.AsSpan(20, Math.Max(0, end - 20)).ToArray();
                json = ExtractJson(payload) ?? ExtractJson(DecryptEcb(payload));
            }
            else if (packet.Length > 50 && BinaryPrimitives.ReadUInt32BigEndian(packet) == 0x00006699)
            {
                // prefixo(4) ?(2) seq(4) cmd(4) len(4) | iv(12) dados tag(16) | sufixo(4)
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(14));
                var aad = packet.AsSpan(4, 14);
                var iv = packet.AsSpan(18, 12);
                int dataLen = len - 12 - 16; // len cobre iv + dados + tag
                if (dataLen <= 0 || 30 + dataLen + 16 > packet.Length) return null;
                var cipher = packet.AsSpan(30, dataLen);
                var tag = packet.AsSpan(30 + dataLen, 16);
                var plain = new byte[dataLen];
                using var gcm = new AesGcm(UdpKey, 16);
                gcm.Decrypt(iv, cipher, tag, plain, aad);
                json = ExtractJson(plain);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }

        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var p) ? p.ToString() : null;
            var gwId = Get("gwId") ?? Get("devId");
            if (gwId is null) return null;
            return new DiscoveredDevice(Get("ip") ?? fromIp, gwId, Get("version") ?? "?", Get("productKey"), port);
        }
        catch (JsonException) { return null; }
    }

    private static byte[] DecryptEcb(byte[] data)
    {
        if (data.Length == 0 || data.Length % 16 != 0) return [];
        using var aes = Aes.Create();
        aes.Key = UdpKey;
        try { return aes.DecryptEcb(data, PaddingMode.PKCS7); }
        catch (CryptographicException) { return aes.DecryptEcb(data, PaddingMode.None); }
    }

    private static string? ExtractJson(byte[] data)
    {
        if (data.Length == 0) return null;
        var text = Encoding.UTF8.GetString(data);
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        return a >= 0 && b > a ? text[a..(b + 1)] : null;
    }
}
