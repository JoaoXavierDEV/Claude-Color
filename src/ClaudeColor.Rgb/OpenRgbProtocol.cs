using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace ClaudeColor.Rgb;

/// <summary>Modo de um controlador OpenRGB (campos do "Mode Data" do SDK).</summary>
public sealed record OrgbMode(
    string Name, int Value, uint Flags, uint SpeedMin, uint SpeedMax, uint BrightnessMin, uint BrightnessMax,
    uint ColorsMin, uint ColorsMax, uint Speed, uint Brightness, uint Direction, uint ColorMode, uint[] Colors);

/// <summary>Dados de um controlador OpenRGB (só o que o Claude Color usa).</summary>
public sealed record OrgbController(uint Id, int Type, string Name, string Vendor, string Location,
    int ActiveMode, IReadOnlyList<OrgbMode> Modes, int LedCount, uint[] Colors)
{
    public string Key => $"{Vendor} {Name}".Trim();
    public OrgbMode? ActiveModeData => ActiveMode >= 0 && ActiveMode < Modes.Count ? Modes[ActiveMode] : null;
}

/// <summary>
/// Cliente mínimo do SDK do OpenRGB que negocia até o protocolo 6. Necessário porque no OpenRGB 1.x
/// clientes com protocolo menor que 6 têm UPDATEMODE/LOADPROFILE ignorados (issue #5924), e o
/// OpenRGB.NET só chega ao 4.
/// Formato: https://gitlab.com/CalcProgrammer1/OpenRGB/-/blob/master/Documentation/OpenRGBSDK.md
/// </summary>
public sealed class OpenRgbProtocolClient : IDisposable
{
    public const uint MaxProtocol = 6;

    private const uint PktRequestControllerCount = 0, PktRequestControllerData = 1;
    private const uint PktRequestProtocolVersion = 40, PktSetClientName = 50;
    private const uint PktUpdateLeds = 1050, PktSetCustomMode = 1100, PktUpdateMode = 1101;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly int _timeoutMs;

    public uint Protocol { get; }

    public OpenRgbProtocolClient(string host, int port, string clientName, int timeoutMs = 3000)
    {
        _timeoutMs = timeoutMs;
        _tcp = new TcpClient { NoDelay = true, ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
        if (!_tcp.ConnectAsync(host, port).Wait(timeoutMs) || !_tcp.Connected)
            throw new TimeoutException($"OpenRGB não respondeu em {host}:{port}.");
        _stream = _tcp.GetStream();

        Send(0, PktRequestProtocolVersion, U32(MaxProtocol));
        uint server = 0;
        try { server = BinaryPrimitives.ReadUInt32LittleEndian(Receive(PktRequestProtocolVersion)); }
        catch (IOException) { /* servidor protocolo 0 não responde */ }
        Protocol = Math.Min(MaxProtocol, server);
        Send(0, PktSetClientName, [.. Encoding.ASCII.GetBytes(clientName), 0]);
    }

    public IReadOnlyList<OrgbController> GetControllers()
    {
        Send(0, PktRequestControllerCount, []);
        var data = Receive(PktRequestControllerCount);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var ids = new List<uint>();
        for (uint i = 0; i < count; i++)
            ids.Add(Protocol >= 6 && data.Length >= 4 + 4 * (i + 1) ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + 4 * (int)i)) : i);

        var list = new List<OrgbController>();
        foreach (var id in ids)
        {
            Send(id, PktRequestControllerData, Protocol >= 1 ? U32(Protocol) : []);
            list.Add(ParseController(id, Receive(PktRequestControllerData)));
        }
        return list;
    }

    public void SetCustomMode(uint id) => Send(id, PktSetCustomMode, []);

    public void UpdateLeds(uint id, IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        var buf = new List<byte>();
        buf.AddRange(U32((uint)(4 + 2 + 4 * colors.Count)));
        buf.AddRange(U16((ushort)colors.Count));
        foreach (var (r, g, b) in colors) buf.AddRange(new byte[] { r, g, b, 0 });
        Send(id, PktUpdateLeds, [.. buf]);
    }

    public void UpdateLeds(uint id, uint[] packedColors) =>
        UpdateLeds(id, packedColors.Select(c => ((byte)c, (byte)(c >> 8), (byte)(c >> 16))).ToList());

    public void UpdateMode(uint id, int modeIndex, OrgbMode mode)
    {
        var body = new List<byte>();
        body.AddRange(U32((uint)modeIndex));
        WriteMode(body, mode);
        var data = new List<byte>(U32((uint)(4 + body.Count)));
        data.AddRange(body);
        Send(id, PktUpdateMode, [.. data]);
    }

    // ───────────── serialização ─────────────

    private void WriteMode(List<byte> b, OrgbMode m)
    {
        b.AddRange(Str(m.Name));
        if (Protocol < 6) b.AddRange(U32((uint)m.Value));
        b.AddRange(U32(m.Flags));
        b.AddRange(U32(m.SpeedMin)); b.AddRange(U32(m.SpeedMax));
        if (Protocol >= 3) { b.AddRange(U32(m.BrightnessMin)); b.AddRange(U32(m.BrightnessMax)); }
        b.AddRange(U32(m.ColorsMin)); b.AddRange(U32(m.ColorsMax));
        b.AddRange(U32(m.Speed));
        if (Protocol >= 3) b.AddRange(U32(m.Brightness));
        b.AddRange(U32(m.Direction)); b.AddRange(U32(m.ColorMode));
        b.AddRange(U16((ushort)m.Colors.Length));
        foreach (var c in m.Colors) b.AddRange(U32(c));
    }

    private OrgbController ParseController(uint id, byte[] data)
    {
        var r = new Reader(data);
        r.U32(); // data_size
        int type = r.I32();
        string name = r.Str();
        string vendor = Protocol >= 1 ? r.Str() : "";
        r.Str(); r.Str(); r.Str(); // description, version, serial
        string location = r.Str();
        int numModes = r.U16();
        int active = r.I32();
        var modes = new List<OrgbMode>();
        for (int i = 0; i < numModes; i++) modes.Add(ReadMode(r));
        int numZones = r.U16();
        for (int i = 0; i < numZones; i++) SkipZone(r);
        int numLeds = r.U16();
        for (int i = 0; i < numLeds; i++) { r.Str(); if (Protocol < 6) r.U32(); }
        int numColors = r.U16();
        var colors = new uint[numColors];
        for (int i = 0; i < numColors; i++) colors[i] = r.U32();
        // O resto (nomes de LED, flags, display name, configuração) não é usado.
        return new OrgbController(id, type, name, vendor, location, active, modes, numLeds, colors);
    }

    private OrgbMode ReadMode(Reader r)
    {
        string name = r.Str();
        int value = Protocol < 6 ? r.I32() : 0;
        uint flags = r.U32(), speedMin = r.U32(), speedMax = r.U32();
        uint brMin = 0, brMax = 0;
        if (Protocol >= 3) { brMin = r.U32(); brMax = r.U32(); }
        uint colMin = r.U32(), colMax = r.U32(), speed = r.U32();
        uint brightness = Protocol >= 3 ? r.U32() : 0;
        uint direction = r.U32(), colorMode = r.U32();
        int n = r.U16();
        var colors = new uint[n];
        for (int i = 0; i < n; i++) colors[i] = r.U32();
        return new OrgbMode(name, value, flags, speedMin, speedMax, brMin, brMax, colMin, colMax, speed, brightness, direction, colorMode, colors);
    }

    private void SkipZone(Reader r)
    {
        r.Str(); r.I32(); r.U32(); r.U32(); r.U32();       // nome, tipo, min, max, count
        int matrixLen = r.U16(); r.Skip(matrixLen);
        if (Protocol >= 4)
        {
            int segs = r.U16();
            for (int s = 0; s < segs; s++)
            {
                r.Str(); r.I32(); r.U32(); r.U32();
                if (Protocol >= 6) { int ml = r.U16(); r.Skip(ml); r.U32(); }
            }
        }
        if (Protocol >= 5) r.U32();                          // zone_flags
        if (Protocol >= 6)
        {
            r.I32();                                         // zone_active_mode
            int zm = r.U16();
            for (int i = 0; i < zm; i++) ReadMode(r);
            r.Str();                                         // zone_display_name
        }
    }

    // ───────────── transporte ─────────────

    private void Send(uint devId, uint pktId, byte[] data)
    {
        var header = new byte[16];
        "ORGB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), devId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), pktId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)data.Length);
        _stream.Write([.. header, .. data]);
        _stream.Flush();
    }

    /// <summary>Lê pacotes até chegar o esperado (o servidor pode mandar avisos no meio).</summary>
    private byte[] Receive(uint expectedPktId)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var header = ReadExact(16);
            if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8)) throw new InvalidDataException("Pacote OpenRGB inválido.");
            uint pktId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            var data = ReadExact(size);
            if (pktId == expectedPktId) return data;
        }
        throw new TimeoutException("O OpenRGB não respondeu a tempo.");
    }

    private byte[] ReadExact(int n)
    {
        var buf = new byte[n];
        _stream.ReadExactly(buf);
        return buf;
    }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }
    private static byte[] Str(string s) { var bytes = Encoding.UTF8.GetBytes(s + "\0"); return [.. U16((ushort)bytes.Length), .. bytes]; }

    private sealed class Reader(byte[] data)
    {
        private int _pos;
        public uint U32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_pos)); _pos += 4; return v; }
        public int I32() => (int)U32();
        public ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_pos)); _pos += 2; return v; }
        public void Skip(int n) => _pos += n;
        public string Str()
        {
            int len = U16();
            var s = Encoding.UTF8.GetString(data, _pos, Math.Max(0, len - 1));
            _pos += len;
            return s;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcp.Dispose();
    }
}
