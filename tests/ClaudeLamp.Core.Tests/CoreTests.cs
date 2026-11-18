using ClaudeLamp.Core;

namespace ClaudeLamp.Core.Tests;

public class TuyaColorTests
{
    [Theory]
    [InlineData("#FF0000", null, "000003e803e8")]
    [InlineData("#00FF00", null, "007803e803e8")]
    [InlineData("#0000FF", null, "00f003e803e8")]
    [InlineData("#FFFFFF", null, "0000000003e8")]
    [InlineData("#FF0000", 50, "000003e801f4")]
    [InlineData("red", 1, "000003e8000a")]
    public void V2_encoding(string color, int? brightness, string expected) =>
        Assert.Equal(expected, TuyaColor.ToV2(RgbColor.Parse(color), brightness));

    [Fact]
    public void V1_encoding_red() =>
        Assert.Equal("ff00000000ffff", TuyaColor.ToV1(RgbColor.Parse("#FF0000")));

    [Fact]
    public void V2_roundtrip()
    {
        Assert.True(TuyaColor.TryParseV2(TuyaColor.ToV2(RgbColor.Parse("#0040FF"), 40), out var c, out var b));
        Assert.Equal(40, b);
        Assert.Equal("#0040FF", c.ToHex());
    }

    [Theory]
    [InlineData("azul", "#0000FF")]
    [InlineData("F80", "#FF8800")]
    [InlineData("  #a000ff ", "#A000FF")]
    public void Parse_accepts_names_and_short_hex(string input, string hex) =>
        Assert.Equal(hex, RgbColor.Parse(input).ToHex());

    [Fact]
    public void Parse_rejects_garbage() => Assert.False(RgbColor.TryParse("roxinho", out _));
}

public class DpsProfileTests
{
    [Fact]
    public void Brightness_scales_to_v2_range()
    {
        Assert.Equal(10, DpsProfile.V2.ToBright(1));
        Assert.Equal(1000, DpsProfile.V2.ToBright(100));
        Assert.Equal(100, DpsProfile.V2.FromBright(1000));
    }

    [Fact]
    public void Detects_profile_from_dps()
    {
        Assert.Same(DpsProfile.V2, DpsProfile.Detect(new Dictionary<int, object> { [20] = true, [21] = "white" }));
        Assert.Same(DpsProfile.V1, DpsProfile.Detect(new Dictionary<int, object> { [1] = true, [2] = "colour" }));
    }
}

public class EventMapperTests
{
    [Theory]
    [InlineData("""{"hook_event_name":"Stop","stop_hook_active":false}""", ClaudeEvents.Done)]
    [InlineData("""{"hook_event_name":"UserPromptSubmit","prompt":"oi"}""", ClaudeEvents.Working)]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"AskUserQuestion","tool_input":{}}""", ClaudeEvents.Question)]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"ExitPlanMode"}""", ClaudeEvents.Plan)]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"permission_prompt","message":"Claude needs your permission to use Bash"}""", ClaudeEvents.Permission)]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"idle_prompt","message":"Claude is waiting for your input"}""", ClaudeEvents.Idle)]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"elicitation_dialog"}""", ClaudeEvents.Question)]
    [InlineData("""{"hook_event_name":"Notification","message":"Claude needs your permission to use Edit"}""", ClaudeEvents.Permission)]
    [InlineData("""{"hook_event_name":"PermissionRequest","tool_name":"Bash"}""", ClaudeEvents.Permission)]
    [InlineData("""{"hook_event_name":"PostToolUse","tool_name":"AskUserQuestion"}""", ClaudeEvents.Resumed)]
    [InlineData("""{"hook_event_name":"PostToolUse","tool_name":"Bash"}""", ClaudeEvents.Resumed)]
    public void Maps_hook_payloads(string json, string expected) => Assert.Equal(expected, EventMapper.Map(json));

    [Theory]
    [InlineData("""{"hook_event_name":"PreToolUse","tool_name":"Bash"}""")]
    [InlineData("""{"hook_event_name":"Notification","notification_type":"auth_success"}""")]
    [InlineData("""{"hook_event_name":"SessionStart"}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    public void Ignores_unrelated_payloads(string json) => Assert.Null(EventMapper.Map(json));
}

public class LampClientTests
{
    private sealed class FakeTransport : ILampTransport
    {
        public Dictionary<int, object> Dps { get; } = new() { [20] = true, [21] = "white", [22] = 1000, [23] = 500, [24] = "000003e803e8" };
        public List<Dictionary<int, object>> Sent { get; } = [];
        public Task<Dictionary<int, object>> GetDpsAsync(CancellationToken ct = default) => Task.FromResult(new Dictionary<int, object>(Dps));
        public Task SetDpsAsync(Dictionary<int, object> dps, CancellationToken ct = default)
        {
            Sent.Add(dps);
            foreach (var (k, v) in dps) Dps[k] = v;
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private static LampConfig Cfg() => new() { DeviceId = "abc", LocalKey = "0123456789abcdef", Ip = "192.168.0.50" };

    [Fact]
    public async Task SetColor_sends_v2_dps()
    {
        var t = new FakeTransport();
        using var lamp = new LampClient(Cfg(), _ => t);
        await lamp.SetColorAsync(RgbColor.Parse("#00FF00"), 100);
        var sent = Assert.Single(t.Sent);
        Assert.Equal(true, sent[20]);
        Assert.Equal("colour", sent[21]);
        Assert.Equal("007803e803e8", sent[24]);
    }

    [Fact]
    public async Task ReadState_roundtrips_manual_state()
    {
        var t = new FakeTransport();
        using var lamp = new LampClient(Cfg(), _ => t);
        await lamp.ApplyAsync(new ManualState { On = true, Mode = "colour", Color = "#FF0000", Brightness = 50 });
        var s = await lamp.ReadStateAsync();
        Assert.Equal("colour", s.Mode);
        Assert.Equal("#FF0000", s.Color);
        Assert.Equal(50, s.Brightness);

        await lamp.ApplyAsync(new ManualState { On = true, Mode = "white", Brightness = 100, WhiteTemp = 0 });
        s = await lamp.ReadStateAsync();
        Assert.Equal("white", s.Mode);
        Assert.Equal(0, s.WhiteTemp);
        Assert.Equal(100, s.Brightness);
    }

    [Fact]
    public void Unconfigured_lamp_throws() =>
        Assert.Throws<InvalidOperationException>(() => new LampClient(new LampConfig()));
}

public class DiscoveryTests
{
    [Fact]
    public void Crc32_matches_standard_check_value() =>
        Assert.Equal(0xCBF43926u, TuyaLocalTransport.Crc32("123456789"u8));

    [Fact]
    public void Parses_plain_v31_broadcast()
    {
        var json = """{"ip":"192.168.0.50","gwId":"eb123","active":2,"ability":0,"mode":0,"encrypt":false,"productKey":"key1","version":"3.1"}"""u8.ToArray();
        var packet = new List<byte>();
        void U32(uint v) => packet.AddRange(BitConverter.GetBytes(v).Reverse());
        U32(0x000055AA); U32(0); U32(0x13); U32((uint)(json.Length + 4 + 8)); U32(0);
        packet.AddRange(json); U32(0); U32(0x0000AA55);

        var dev = TuyaDiscovery.TryParse(packet.ToArray(), "192.168.0.50", 6666);
        Assert.NotNull(dev);
        Assert.Equal("eb123", dev!.GwId);
        Assert.Equal("3.1", dev.Version);
    }
}
