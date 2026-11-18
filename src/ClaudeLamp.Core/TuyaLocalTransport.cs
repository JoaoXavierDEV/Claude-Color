using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeLamp.Core;

/// <summary>
/// Implementação própria do protocolo local Tuya (TCP 6668) para as versões 3.3, 3.4 e 3.5,
/// baseada no TinyTuya. 3.4 e 3.5 negociam uma chave de sessão a cada conexão.
/// </summary>
public sealed class TuyaLocalTransport : ILampTransport
{
    private const uint Prefix55AA = 0x000055AA, Suffix55AA = 0x0000AA55;
    private const uint Prefix6699 = 0x00006699, Suffix6699 = 0x00009966;

    private const uint CmdControl = 7, CmdStatus = 8, CmdDpQuery = 10, CmdControlNew = 0x0d, CmdDpQueryNew = 0x10;
    private const uint CmdSessNegStart = 3, CmdSessNegResp = 4, CmdSessNegFinish = 5;

    private readonly string _ip, _devId;
    private readonly byte[] _localKey;
    private readonly int _port, _timeoutMs;
    private readonly double _version;
    private readonly string _versionText;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private byte[] _key;
    private uint _seq;

    public TuyaLocalTransport(string ip, string localKey, string deviceId, string version, int port = 6668, int timeoutMs = 3000)
    {
        _ip = ip;
        _devId = deviceId;
        _localKey = Encoding.UTF8.GetBytes(localKey);
        if (_localKey.Length != 16) throw new ArgumentException($"A local key deve ter 16 caracteres (tem {_localKey.Length}).");
        _key = _localKey;
        _versionText = version;
        _version = version switch
        {
            "3.3" or "3.2" => 3.3,
            "3.4" => 3.4,
            "3.5" => 3.5,
            _ => throw new NotSupportedException($"Protocolo {version} não suportado por este transporte."),
        };
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public async Task<Dictionary<int, object>> GetDpsAsync(CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        if (_version >= 3.4)
            await SendAsync(CmdDpQueryNew, "{}"u8.ToArray(), header: false, ct);
        else
            await SendAsync(CmdDpQuery, Json(new JsonObject
            {
                ["gwId"] = _devId, ["devId"] = _devId, ["uid"] = _devId, ["t"] = Now().ToString(),
            }), header: false, ct);

        var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var msg = await ReceiveAsync(ct);
            if (ParseDps(msg.Payload) is { } dps) return dps;
        }
        throw new TimeoutException("A lâmpada não enviou os DPs.");
    }

    public async Task SetDpsAsync(Dictionary<int, object> dps, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        var dpsObj = new JsonObject();
        foreach (var (k, v) in dps)
            dpsObj[k.ToString()] = v switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                _ => JsonValue.Create(v.ToString()),
            };

        if (_version >= 3.4)
            await SendAsync(CmdControlNew, Json(new JsonObject
            {
                ["protocol"] = 5, ["t"] = Now(), ["data"] = new JsonObject { ["dps"] = dpsObj },
            }), header: true, ct);
        else
            await SendAsync(CmdControl, Json(new JsonObject
            {
                ["devId"] = _devId, ["uid"] = _devId, ["t"] = Now().ToString(), ["dps"] = dpsObj,
            }), header: true, ct);

        // A lâmpada responde com um ACK (às vezes vazio) e/ou um STATUS; não é obrigatório esperar os dois.
        try { await ReceiveAsync(ct, Math.Min(_timeoutMs, 1500)); }
        catch (TimeoutException) { /* alguns firmwares não respondem ao controle */ }
    }

    // ───────────────────────────── conexão e sessão ─────────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_tcp is { Connected: true } && _stream is not null) return;
        Close();
        _tcp = new TcpClient { NoDelay = true };
        using (var cts = Timeout(ct, _timeoutMs))
        {
            try { await _tcp.ConnectAsync(_ip, _port, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Sem resposta de {_ip}:{_port}.");
            }
        }
        _stream = _tcp.GetStream();
        _key = _localKey;
        if (_version >= 3.4) await NegotiateSessionKeyAsync(ct);
    }

    private async Task NegotiateSessionKeyAsync(CancellationToken ct)
    {
        var localNonce = RandomNumberGenerator.GetBytes(16);
        await SendAsync(CmdSessNegStart, localNonce, header: false, ct);

        Message resp;
        do resp = await ReceiveAsync(ct);
        while (resp.Cmd != CmdSessNegResp);

        var p = resp.Payload;
        if (p.Length < 48) throw new InvalidDataException($"Resposta de negociação curta ({p.Length} bytes). A local key está certa?");
        if (p.Length > 48) p = p[^48..];
        var remoteNonce = p[..16];
        var hmacCheck = p[16..48];
        if (!CryptographicOperations.FixedTimeEquals(hmacCheck, HMACSHA256.HashData(_localKey, localNonce)))
            throw new InvalidDataException("A lâmpada rejeitou a negociação: local key incorreta (ou de outro pareamento).");

        await SendAsync(CmdSessNegFinish, HMACSHA256.HashData(_localKey, remoteNonce), header: false, ct);

        var xor = new byte[16];
        for (int i = 0; i < 16; i++) xor[i] = (byte)(localNonce[i] ^ remoteNonce[i]);

        if (_version >= 3.5)
        {
            using var gcm = new AesGcm(_localKey, 16);
            var cipher = new byte[16];
            gcm.Encrypt(localNonce[..12], xor, cipher, new byte[16]);
            _key = cipher;
        }
        else
        {
            using var aes = Aes.Create();
            aes.Key = _localKey;
            _key = aes.EncryptEcb(xor, PaddingMode.None);
        }
    }

    // ───────────────────────────── enquadramento ─────────────────────────────

    private async Task SendAsync(uint cmd, byte[] plain, bool header, CancellationToken ct)
    {
        if (header)
            plain = [.. Encoding.ASCII.GetBytes(_versionText.Length == 3 ? _versionText : "3.3"), .. new byte[12], .. plain];

        var frame = _version >= 3.5 ? Pack6699(cmd, plain) : Pack55AA(cmd, plain, header);
        using var cts = Timeout(ct, _timeoutMs);
        await _stream!.WriteAsync(frame, cts.Token);
    }

    private byte[] Pack55AA(uint cmd, byte[] plain, bool header)
    {
        byte[] payload;
        if (_version >= 3.4)
        {
            payload = EcbEncrypt(_key, plain, pad: true);
        }
        else if (header)
        {
            // 3.3: o cabeçalho de versão fica fora da criptografia.
            var json = plain[15..];
            payload = [.. plain[..15], .. EcbEncrypt(_key, json, pad: true)];
        }
        else payload = EcbEncrypt(_key, plain, pad: true);

        int hashLen = _version >= 3.4 ? 32 : 4;
        var buf = new byte[16 + payload.Length + hashLen + 4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, Prefix55AA);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), ++_seq);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), cmd);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), (uint)(payload.Length + hashLen + 4));
        payload.CopyTo(buf, 16);
        var signed = buf.AsSpan(0, 16 + payload.Length);
        if (_version >= 3.4) HMACSHA256.HashData(_key, signed).CopyTo(buf, 16 + payload.Length);
        else BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(16 + payload.Length), Crc32(signed));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(buf.Length - 4), Suffix55AA);
        return buf;
    }

    private byte[] Pack6699(uint cmd, byte[] plain)
    {
        var buf = new byte[18 + 12 + plain.Length + 16 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, Prefix6699);
        // buf[4..6] = 0
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(6), ++_seq);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(10), cmd);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(14), (uint)(12 + plain.Length + 16));
        var iv = buf.AsSpan(18, 12);
        RandomNumberGenerator.Fill(iv);
        using var gcm = new AesGcm(_key, 16);
        gcm.Encrypt(iv, plain, buf.AsSpan(30, plain.Length), buf.AsSpan(30 + plain.Length, 16), buf.AsSpan(4, 14));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(buf.Length - 4), Suffix6699);
        return buf;
    }

    private readonly record struct Message(uint Cmd, byte[] Payload);

    private async Task<Message> ReceiveAsync(CancellationToken ct, int? timeoutMs = null)
    {
        using var cts = Timeout(ct, timeoutMs ?? _timeoutMs);
        try
        {
            var prefix = await ReadExactAsync(4, cts.Token);
            uint p = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (p == Prefix55AA)
            {
                var head = await ReadExactAsync(12, cts.Token);
                uint cmd = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
                if (len is < 8 or > 65536) throw new InvalidDataException($"Tamanho inválido: {len}");
                var body = await ReadExactAsync(len, cts.Token);
                int hashLen = _version >= 3.4 ? 32 : 4;
                var payload = body[..Math.Max(0, len - hashLen - 4)];
                payload = StripRetcode(payload);
                return new Message(cmd, Decrypt55AA(payload));
            }
            if (p == Prefix6699)
            {
                var head = await ReadExactAsync(14, cts.Token);
                uint cmd = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(6));
                int len = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(10));
                if (len is < 28 or > 65536) throw new InvalidDataException($"Tamanho inválido: {len}");
                var body = await ReadExactAsync(len + 4, cts.Token);
                var iv = body.AsSpan(0, 12);
                var cipher = body.AsSpan(12, len - 28);
                var tag = body.AsSpan(len - 16, 16);
                var plain = new byte[cipher.Length];
                using var gcm = new AesGcm(_key, 16);
                gcm.Decrypt(iv, cipher, tag, plain, head);
                // Mensagens do dispositivo trazem retcode (4 bytes) dentro da parte cifrada.
                if (cmd == CmdSessNegResp) return new Message(cmd, plain.Length > 48 ? plain[^48..] : plain);
                return new Message(cmd, StripVersionHeader(StripRetcode(plain)));
            }
            throw new InvalidDataException($"Prefixo desconhecido 0x{p:X8} — versão de protocolo errada?");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"A lâmpada não respondeu (protocolo {_versionText}).");
        }
    }

    private byte[] Decrypt55AA(byte[] payload)
    {
        if (payload.Length == 0) return payload;
        if (_version >= 3.4)
            return StripVersionHeader(EcbDecrypt(_key, payload));
        // 3.3: pode vir com cabeçalho "3.3" + 12 bytes fora da criptografia, ou JSON puro.
        if (payload.Length > 15 && payload[0] == '3' && payload[1] == '.') payload = payload[15..];
        if (payload[0] == '{') return payload;
        return payload.Length % 16 == 0 ? StripVersionHeader(EcbDecrypt(_key, payload)) : payload;
    }

    private static byte[] StripRetcode(byte[] p) =>
        p.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(p) < 256 ? p[4..] : p;

    private static byte[] StripVersionHeader(byte[] p) =>
        p.Length >= 15 && p[0] == '3' && p[1] == '.' && char.IsDigit((char)p[2]) ? p[15..] : p;

    private static Dictionary<int, object>? ParseDps(byte[] payload)
    {
        if (payload.Length == 0) return null;
        try
        {
            var node = JsonNode.Parse(payload);
            var dps = node?["dps"] ?? node?["data"]?["dps"];
            if (dps is not JsonObject obj) return null;
            var result = new Dictionary<int, object>();
            foreach (var (k, v) in obj)
            {
                if (!int.TryParse(k, out var id) || v is null) continue;
                result[id] = v.GetValueKind() switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => v.GetValue<int>(),
                    _ => v.ToString(),
                };
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    // ───────────────────────────── utilitários ─────────────────────────────

    private async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
    {
        var buf = new byte[n];
        await _stream!.ReadExactlyAsync(buf, ct);
        return buf;
    }

    private static CancellationTokenSource Timeout(CancellationToken ct, int ms)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ms);
        return cts;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static byte[] Json(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static byte[] EcbEncrypt(byte[] key, byte[] data, bool pad)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(data, pad ? PaddingMode.PKCS7 : PaddingMode.None);
    }

    private static byte[] EcbDecrypt(byte[] key, byte[] data)
    {
        if (data.Length % 16 != 0) return data;
        using var aes = Aes.Create();
        aes.Key = key;
        try { return aes.DecryptEcb(data, PaddingMode.PKCS7); }
        catch (CryptographicException) { return aes.DecryptEcb(data, PaddingMode.None); }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}
