using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ClaudeColor.Core;

namespace ClaudeColor.Core.Tests;

internal static class TestSetup
{
    /// <summary>Isola config/estado/log dos testes num diretório temporário.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("CLAUDE_COLOR_HOME",
            Path.Combine(Path.GetTempPath(), "claude-color-tests-" + Environment.ProcessId));
}

public class HooksInstallerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hooks-").FullName;
    private string Settings => Path.Combine(_dir, "settings.json");
    private const string Exe = @"C:\Users\x\.claude-color\bin\ClaudeColor.exe";

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Install_preserves_other_settings_and_hooks()
    {
        File.WriteAllText(Settings, """
            {
              // comentário
              "model": "opus",
              "hooks": {
                "Stop": [ { "hooks": [ { "type": "command", "command": "other-tool notify" } ] } ]
              }
            }
            """);
        var inst = new HooksInstaller(Settings);
        Assert.Equal(HooksStatus.NotInstalled, inst.GetStatus(Exe));

        var backup = inst.Install(Exe);
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));

        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal(2, root["hooks"]!["Stop"]!.AsArray().Count); // o de terceiros + o nosso
        Assert.Equal("AskUserQuestion|ExitPlanMode", root["hooks"]!["PreToolUse"]![0]!["matcher"]!.GetValue<string>());
        Assert.Equal(HooksStatus.Installed, inst.GetStatus(Exe));
    }

    [Fact]
    public void Install_is_idempotent_and_replaces_old_exe()
    {
        var inst = new HooksInstaller(Settings);
        inst.Install(@"C:\Users\x\.claude-lamp\bin\claude-lamp.exe");
        Assert.Equal(HooksStatus.Outdated, inst.GetStatus(Exe));

        inst.Install(Exe);
        inst.Install(Exe);
        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        foreach (var evt in new[] { "UserPromptSubmit", "Stop", "Notification", "PreToolUse", "PostToolUse" })
            Assert.Single(root["hooks"]![evt]!.AsArray());
        Assert.Equal(HooksStatus.Installed, inst.GetStatus(Exe));
        Assert.Equal($"\"{Exe}\" hook", root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Install_replaces_1x_ClaudeLamp_entries()
    {
        var inst = new HooksInstaller(Settings);
        inst.Install(@"C:\Users\x\.claude-lamp\bin\ClaudeLamp.exe");
        Assert.Equal(HooksStatus.Outdated, inst.GetStatus(Exe));
        inst.Install(Exe);
        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
        Assert.DoesNotContain("ClaudeLamp", File.ReadAllText(Settings));
        Assert.Equal(HooksStatus.Installed, inst.GetStatus(Exe));
    }

    [Fact]
    public void Uninstall_removes_only_ours()
    {
        File.WriteAllText(Settings, """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"other-tool"}]}]}}""");
        var inst = new HooksInstaller(Settings);
        inst.Install(Exe);
        inst.Uninstall();
        var root = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
        Assert.Null(root["hooks"]!["PreToolUse"]);
        Assert.Equal(HooksStatus.NotInstalled, inst.GetStatus(Exe));
    }
}

[Collection("Storage")]
public class HookHandlerTests
{
    public HookHandlerTests()
    {
        // Os testes não podem piscar o terminal de quem roda nem depender da janela real.
        HookHandler.FindSession = json => new SessionInfo(0x1234, SessionInfo.ProjectFromHook(json));
        HookHandler.Flash = _ => true;
        HookHandler.BusyState = () => UserBusy.State.AcceptsNotifications;
    }

    [Fact]
    public void Ignored_payload_starts_nothing()
    {
        var started = new List<string>();
        HookHandler.StartProcess = (_, args) => started.Add(args);
        Assert.Null(HookHandler.Handle("""{"hook_event_name":"PreToolUse","tool_name":"Bash"}""", "x.exe"));
        Assert.Null(HookHandler.Handle("lixo", "x.exe"));
        Assert.Empty(started);
    }

    private static AppConfig WithLamp(bool launchFromHook) => new()
    {
        LaunchFromHook = launchFromHook,
        Lamp = new LampConfig { Enabled = true, DeviceId = "abc", LocalKey = "0123456789abcdef", Ip = "127.0.0.1" },
    };

    [Fact]
    public void Closed_app_is_opened_in_tray()
    {
        Storage.SaveConfig(new AppConfig()); // sem lâmpada: só o RGB do PC
        var started = new List<string>();
        HookHandler.StartProcess = (_, args) => started.Add(args);
        HookHandler.SendToHost = (_, _, _) => false; // app fechado
        Assert.Equal(ClaudeEvents.Done, HookHandler.Handle("""{"hook_event_name":"Stop"}""", "x.exe"));
        Assert.Equal("--tray --from-hook", Assert.Single(started));
        Assert.Equal((ClaudeEvents.Done, Storage.LoadState().SignalGeneration), Storage.LoadState().PendingSignal(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Without_launch_and_without_lamp_nothing_starts()
    {
        Storage.SaveConfig(new AppConfig { LaunchFromHook = false });
        var started = new List<string>();
        HookHandler.StartProcess = (_, args) => started.Add(args);
        HookHandler.SendToHost = (_, _, _) => false;
        Assert.Equal(ClaudeEvents.Done, HookHandler.Handle("""{"hook_event_name":"Stop"}""", "x.exe"));
        Assert.Empty(started);
    }

    [Fact]
    public void Configured_lamp_starts_run_signal()
    {
        Storage.SaveConfig(WithLamp(launchFromHook: false));
        var started = new List<string>();
        HookHandler.StartProcess = (_, args) => started.Add(args);
        HookHandler.SendToHost = (_, _, _) => false; // app fechado
        Assert.Equal(ClaudeEvents.Done, HookHandler.Handle("""{"hook_event_name":"Stop"}""", "x.exe"));
        var args = Assert.Single(started);
        Assert.StartsWith("run-signal done ", args);
    }

    [Fact]
    public void Open_app_receives_signal_instead_of_new_process()
    {
        Storage.SaveConfig(WithLamp(launchFromHook: true));
        var started = new List<string>();
        var sent = new List<(string, long)>();
        HookHandler.StartProcess = (_, args) => started.Add(args);
        HookHandler.SendToHost = (e, g, _) => { sent.Add((e, g)); return true; };
        Assert.Equal(ClaudeEvents.Question, HookHandler.Handle("""{"hook_event_name":"PreToolUse","tool_name":"AskUserQuestion"}""", "x.exe"));
        Assert.Empty(started);
        Assert.Equal(ClaudeEvents.Question, Assert.Single(sent).Item1);
    }
}

public class SignalPipeTests
{
    [Fact]
    public void Roundtrip_and_rejects_garbage()
    {
        Assert.True(SignalPipe.TryParse(SignalPipe.Format("done", 42) + "\n", out var e, out var g));
        Assert.Equal(("done", 42L), (e, g));
        Assert.False(SignalPipe.TryParse("rm -rf 1", out _, out _));
        Assert.False(SignalPipe.TryParse("done", out _, out _));
        Assert.False(SignalPipe.TryParse(null, out _, out _));
    }

    [Fact]
    public void Security_descriptor_limits_to_user_and_medium_integrity()
    {
        var sddl = SignalPipe.SecurityDescriptor("S-1-5-21-1-2-3-1001");
        Assert.Equal("D:(A;;GA;;;S-1-5-21-1-2-3-1001)(A;;GA;;;SY)S:(ML;;NW;;;ME)", sddl);
    }

    [Fact]
    public async Task Server_with_acl_receives_signal_end_to_end()
    {
        var name = "ClaudeColor.Test." + Guid.NewGuid().ToString("N");
        await using var server = SignalPipe.CreateServer(name);
        var serve = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var buf = new byte[64];
            int n = await server.ReadAsync(buf);
            await server.WriteAsync("ok\n"u8.ToArray());
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        });
        Assert.True(SignalPipe.TrySend("plan", 7, timeoutMs: 1000, name: name));
        Assert.Equal("plan 7\n", await serve);
    }

    [Fact]
    public void Send_without_app_returns_false_quickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Nome de pipe padrão: nos testes nenhum app está ouvindo... a não ser que o Claude Color real esteja aberto.
        if (System.IO.Directory.GetFiles(@"\\.\pipe\").Any(p => p.EndsWith(SignalPipe.Name))) return;
        Assert.False(SignalPipe.TrySend("done", 1, timeoutMs: 100));
        Assert.True(sw.ElapsedMilliseconds < 2000);
    }
}

[Collection("Storage")] // usa o state.json compartilhado: não roda em paralelo com HookHandlerTests
public class MultiTargetSignalTests
{
    private sealed class FakeTarget(string name, bool fail = false) : ILightTarget
    {
        public List<string> Calls { get; } = [];
        public string Name => name;
        public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default)
        {
            Calls.Add($"show {color.ToHex()} {brightness}");
            return fail ? throw new InvalidOperationException("sdk off") : Task.CompletedTask;
        }
        public Task ReleaseAsync(CancellationToken ct = default) { Calls.Add("release"); return Task.CompletedTask; }
        public void Dispose() { }
    }

    private static AppConfig Cfg(SignalConfig sig)
    {
        var cfg = new AppConfig();
        cfg.Events[ClaudeEvents.Done] = sig;
        return cfg;
    }

    [Fact]
    public async Task All_targets_blink_show_and_release_even_if_one_fails()
    {
        var a = new FakeTarget("a");
        var broken = new FakeTarget("b", fail: true);
        var cfg = Cfg(new SignalConfig { Color = "#00FF00", Brightness = 80, Blink = 1, BlinkIntervalMs = 1, RestoreAfterSeconds = 1 });

        await SignalRunner.RunAsync(cfg, ClaudeEvents.Done, 1, [a, broken], isCurrent: () => true);

        Assert.Equal(["show #00FF00 80", "show #00FF00 1", "show #00FF00 80", "release"], a.Calls);
        Assert.Equal("release", broken.Calls[^1]);
    }

    private sealed class SlowTarget : ILightTarget
    {
        public List<string> Calls { get; } = [];
        public string Name => "slow";
        public bool Blinks => false;
        public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default) { Calls.Add($"show {brightness}"); return Task.CompletedTask; }
        public Task ReleaseAsync(CancellationToken ct = default) { Calls.Add("release"); return Task.CompletedTask; }
        public void Dispose() { }
    }

    [Fact]
    public async Task Slow_targets_skip_blink_and_get_final_color()
    {
        var fast = new FakeTarget("fast");
        var slow = new SlowTarget();
        var cfg = Cfg(new SignalConfig { Color = "#00FF00", Brightness = 90, Blink = 3, BlinkIntervalMs = 1, RestoreAfterSeconds = 1 });
        await SignalRunner.RunAsync(cfg, ClaudeEvents.Done, 1, [fast, slow], isCurrent: () => true);
        Assert.Equal(7, fast.Calls.Count(c => c.StartsWith("show"))); // 3 piscadas × 2 + final
        Assert.Equal(["show 90", "show 90", "release"], slow.Calls);   // início + final, sem o brilho 1
    }

    private sealed class KindTarget(bool lamp) : ILightTarget
    {
        public List<string> Calls { get; } = [];
        public string Name => lamp ? "lamp" : "pc";
        public bool IsLamp => lamp;
        public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default) { Calls.Add($"show {color.ToHex()}"); return Task.CompletedTask; }
        public Task ReleaseAsync(CancellationToken ct = default) { Calls.Add("release"); return Task.CompletedTask; }
        public void Dispose() { }
    }

    [Fact]
    public async Task Working_with_hold_does_not_restore()
    {
        var lamp = new KindTarget(lamp: true);
        var cfg = new AppConfig();
        cfg.Events[ClaudeEvents.Working] = new SignalConfig { Color = "#0040FF", RestoreAfterSeconds = 1, HoldWhileWorking = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SignalRunner.RunAsync(cfg, ClaudeEvents.Working, 1, [lamp], cts.Token, isCurrent: () => true));
        Assert.Equal(["show #0040FF"], lamp.Calls); // segurou: sem release em 2 s, apesar de "restaurar após 1 s"
    }

    [Fact]
    public async Task Signal_during_work_returns_to_working_color()
    {
        var cfg = new AppConfig();
        cfg.Events[ClaudeEvents.Working] = new SignalConfig { Color = "#0040FF", HoldWhileWorking = true, PcRgb = false };
        cfg.Events[ClaudeEvents.Question] = new SignalConfig { Color = "#FFC000", RestoreAfterSeconds = 1 };
        Storage.UpdateState(s => s.WorkingSince = DateTimeOffset.Now);
        try
        {
            var lamp = new KindTarget(lamp: true);
            var pc = new KindTarget(lamp: false);
            await SignalRunner.RunAsync(cfg, ClaudeEvents.Question, 1, [lamp, pc], isCurrent: () => true);
            Assert.Equal(["show #FFC000", "show #0040FF"], lamp.Calls); // volta para a cor de trabalhando
            Assert.Equal(["show #FFC000", "release"], pc.Calls);       // "trabalhando" sem PC RGB: devolve o PC
        }
        finally { Storage.UpdateState(s => s.WorkingSince = null); }
    }

    [Fact]
    public void Done_and_manual_end_working()
    {
        var cfg = new AppConfig();
        cfg.Events[ClaudeEvents.Working].HoldWhileWorking = true;
        Assert.NotNull(SignalRunner.Begin(cfg, ClaudeEvents.Working));
        Assert.True(Storage.LoadState().IsWorking(cfg));
        Assert.NotNull(SignalRunner.Begin(cfg, ClaudeEvents.Done));
        Assert.False(Storage.LoadState().IsWorking(cfg));

        SignalRunner.Begin(cfg, ClaudeEvents.Working);
        SignalRunner.RememberManual(new ManualState());
        Assert.False(Storage.LoadState().IsWorking(cfg));
    }

    [Fact]
    public async Task Wait_for_user_holds_until_resumed()
    {
        var cfg = new AppConfig();
        cfg.Events[ClaudeEvents.Question] = new SignalConfig { Color = "#FFC000", RestoreAfterSeconds = 1, WaitForUser = true };
        Storage.UpdateState(s => { s.WaitingFor = null; s.WorkingSince = null; });

        Assert.Null(SignalRunner.Begin(cfg, ClaudeEvents.Resumed)); // nada aguardando: PostToolUse é ignorado

        var gen = SignalRunner.Begin(cfg, ClaudeEvents.Question)!.Value;
        Assert.Equal(ClaudeEvents.Question, Storage.LoadState().WaitingFor);

        var lamp = new KindTarget(lamp: true);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                SignalRunner.RunAsync(cfg, ClaudeEvents.Question, gen, [lamp], cts.Token, isCurrent: () => true));
        Assert.Equal(["show #FFC000"], lamp.Calls); // segurou, apesar de "restaurar após 1 s"

        var resumed = SignalRunner.Begin(cfg, ClaudeEvents.Resumed);
        Assert.NotNull(resumed);
        Assert.Null(Storage.LoadState().WaitingFor);
        await SignalRunner.RunAsync(cfg, ClaudeEvents.Resumed, resumed.Value, [lamp], isCurrent: () => true);
        Assert.Equal("release", lamp.Calls[^1]); // não está trabalhando: volta ao normal
    }

    [Fact]
    public async Task Resume_goes_back_to_working_when_holding()
    {
        var cfg = new AppConfig();
        cfg.Events[ClaudeEvents.Working] = new SignalConfig { Color = "#0040FF", HoldWhileWorking = true, PcRgb = true };
        cfg.Events[ClaudeEvents.Permission] = new SignalConfig { Color = "#FF4000", WaitForUser = true };
        SignalRunner.Begin(cfg, ClaudeEvents.Working);
        SignalRunner.Begin(cfg, ClaudeEvents.Permission);
        var gen = SignalRunner.Begin(cfg, ClaudeEvents.Resumed)!.Value;
        var lamp = new KindTarget(lamp: true);
        var pc = new KindTarget(lamp: false);
        await SignalRunner.RunAsync(cfg, ClaudeEvents.Resumed, gen, [lamp, pc], isCurrent: () => true);
        Assert.Equal(["show #0040FF"], lamp.Calls);
        Assert.Equal(["show #0040FF"], pc.Calls); // "trabalhando" com PC RGB: o PC também volta para ela
        SignalRunner.Begin(cfg, ClaudeEvents.Done);
    }

    [Fact]
    public async Task Superseded_signal_does_not_show_or_release()
    {
        var a = new FakeTarget("a");
        var cfg = Cfg(new SignalConfig { Color = "#00FF00", RestoreAfterSeconds = 1 });
        await SignalRunner.RunAsync(cfg, ClaudeEvents.Done, 1, [a], isCurrent: () => false);
        Assert.Empty(a.Calls);
    }

    [Fact]
    public void Pc_rgb_defaults_per_event()
    {
        var s = new SignalConfig();
        Assert.True(s.UsesPcRgb(ClaudeEvents.Question));
        Assert.True(s.UsesPcRgb(ClaudeEvents.Done));
        Assert.False(s.UsesPcRgb(ClaudeEvents.Working));
        Assert.False(s.UsesPcRgb(ClaudeEvents.Idle));
        Assert.True(new SignalConfig { PcRgb = true }.UsesPcRgb(ClaudeEvents.Working));
    }

    [Fact]
    public void Pc_rgb_config_defaults()
    {
        var cfg = new AppConfig().PcRgb;
        Assert.False(cfg.Corsair);  // integrações nativas: opcionais
        Assert.False(cfg.Logitech);
        Assert.False(cfg.Msi);
        Assert.True(cfg.OpenRgb.Enabled);
        Assert.Null(cfg.OpenRgb.Devices); // null = seleção padrão
        Assert.Equal(6742, cfg.OpenRgb.Port);
    }

    [Fact]
    public void Scale_applies_brightness()
    {
        Assert.Equal("#804000", RgbColor.Parse("#FF8000").Scale(50).ToHex());
    }
}

public class SignalPipeSessionTests
{
    [Fact]
    public void Session_roundtrips_with_spaces_and_old_format_still_parses()
    {
        var line = SignalPipe.Format("question", 9, new SessionInfo(0x1A2B, "meu projeto\nlegal", "abc-123"));
        Assert.True(SignalPipe.TryParse(line, out var e, out var g, out var s));
        Assert.Equal(("question", 9L, 0x1A2BL, "meu projetolegal", "abc-123"), (e, g, s.Window, s.Project, s.Id));

        Assert.True(SignalPipe.TryParse(SignalPipe.Format("done", 2, new SessionInfo(5, "p")), out _, out _, out var noId));
        Assert.Equal((5L, "p", (string?)null), (noId.Window, noId.Project, noId.Id));

        Assert.True(SignalPipe.TryParse("done 3", out _, out _, out var old));
        Assert.Equal(SessionInfo.None, old);
        Assert.True(SignalPipe.TryParse("done 3 0", out _, out _, out var noProject));
        Assert.Null(noProject.Project);
        Assert.False(SignalPipe.TryParse("done 3 janela", out _, out _, out _));
        Assert.False(SignalPipe.TryParse("done 3 1 " + new string('x', 2000), out _, out _, out _));
    }

    [Fact]
    public void Project_comes_from_cwd()
    {
        Assert.Equal("claude-lamp", SessionInfo.ProjectFromHook("""{"cwd":"C:\\Users\\x\\source\\claude-lamp\\"}"""));
        Assert.Equal("C:\\", SessionInfo.ProjectFromHook("""{"cwd":"C:\\"}"""));
        Assert.Null(SessionInfo.ProjectFromHook("""{"hook_event_name":"Stop"}"""));
        Assert.Null(SessionInfo.ProjectFromHook("lixo"));
    }

    [Fact]
    public void Defaults_notify_and_flash_only_when_you_are_needed()
    {
        var s = new SignalConfig();
        Assert.True(s.UsesNotify(ClaudeEvents.Question) && s.UsesFlash(ClaudeEvents.Permission));
        Assert.True(s.UsesNotify(ClaudeEvents.Done));
        Assert.False(s.UsesFlash(ClaudeEvents.Done) || s.UsesNotify(ClaudeEvents.Working) || s.UsesFlash(ClaudeEvents.Idle));
    }
}


[Collection("Storage")]
public class SessionStateTests
{
    private static AppConfig WaitCfg()
    {
        var cfg = new AppConfig();
        cfg.Events[ClaudeEvents.Question] = new SignalConfig { Color = "#FFC000", WaitForUser = true };
        cfg.Events[ClaudeEvents.Permission] = new SignalConfig { Color = "#FF4000", WaitForUser = true };
        cfg.Events[ClaudeEvents.Working] = new SignalConfig { Color = "#0040FF", HoldWhileWorking = true };
        return cfg;
    }

    private static void Reset() => Storage.UpdateState(s => s.Sessions.Clear());

    [Fact]
    public async Task Resuming_one_session_keeps_the_other_waiting()
    {
        Reset();
        var cfg = WaitCfg();
        var a = new SessionInfo(0, "proj-a", "sessao-a");
        var b = new SessionInfo(0, "proj-b", "sessao-b");
        SignalRunner.Begin(cfg, ClaudeEvents.Question, a);
        SignalRunner.Begin(cfg, ClaudeEvents.Permission, b);
        Assert.Equal(ClaudeEvents.Question, Storage.LoadState().WaitingFor); // pergunta é mais urgente

        var gen = SignalRunner.Begin(cfg, ClaudeEvents.Resumed, a)!.Value;
        var st = Storage.LoadState();
        Assert.Null(st.Find("sessao-a")!.WaitingFor);
        Assert.Equal(ClaudeEvents.Permission, st.Find("sessao-b")!.WaitingFor);

        // A luz volta para a cor da sessão que ainda espera, em vez de soltar.
        var lamp = new SessionLamp();
        await SignalRunner.RunAsync(cfg, ClaudeEvents.Resumed, gen, [lamp], isCurrent: () => true);
        Assert.Equal(["show #FF4000"], lamp.Calls);

        // PostToolUse de uma sessão que não esperava nada: ignorado.
        Assert.Null(SignalRunner.Begin(cfg, ClaudeEvents.Resumed, a));
        Reset();
    }

    [Fact]
    public void Done_in_one_session_keeps_the_other_working()
    {
        Reset();
        var cfg = WaitCfg();
        SignalRunner.Begin(cfg, ClaudeEvents.Working, new SessionInfo(0, null, "x"));
        SignalRunner.Begin(cfg, ClaudeEvents.Working, new SessionInfo(0, null, "y"));
        SignalRunner.Begin(cfg, ClaudeEvents.Done, new SessionInfo(0, null, "x"));
        Assert.True(Storage.LoadState().IsWorking(cfg));
        SignalRunner.Begin(cfg, ClaudeEvents.Done, new SessionInfo(0, null, "y"));
        Assert.False(Storage.LoadState().IsWorking(cfg));
        Reset();
    }

    [Fact]
    public void Hooks_without_session_id_share_the_default_session_and_old_sessions_are_pruned()
    {
        Reset();
        var cfg = WaitCfg();
        SignalRunner.Begin(cfg, ClaudeEvents.Question);
        Assert.Equal(ClaudeEvents.Question, Storage.LoadState().Find(null)!.WaitingFor);
        Assert.NotNull(SignalRunner.Begin(cfg, ClaudeEvents.Resumed));

        Storage.UpdateState(s => s.Session("velha").LastSeen = DateTimeOffset.Now.AddHours(-2));
        SignalRunner.Begin(cfg, ClaudeEvents.Working, new SessionInfo(0, null, "nova"));
        Assert.Null(Storage.LoadState().Find("velha"));
        Assert.Equal("abc", SessionInfo.IdFromHook("""{"session_id":"a b\nc"}"""));
        Assert.Null(SessionInfo.IdFromHook("""{"cwd":"x"}"""));
        Reset();
    }

    private sealed class SessionLamp : ILightTarget
    {
        public List<string> Calls { get; } = [];
        public string Name => "lamp";
        public bool IsLamp => true;
        public Task ShowAsync(RgbColor color, int brightness, CancellationToken ct = default) { Calls.Add($"show {color.ToHex()}"); return Task.CompletedTask; }
        public Task ReleaseAsync(CancellationToken ct = default) { Calls.Add("release"); return Task.CompletedTask; }
        public void Dispose() { }
    }
}


[Collection("Storage")]
public class DiscordTests
{
    [Fact]
    public void Frames_are_little_endian_with_utf8_body()
    {
        var frame = DiscordIpc.Encode(DiscordIpc.Op.Frame, """{"a":"é"}""");
        Assert.Equal([1, 0, 0, 0, 10, 0, 0, 0], frame[..8]);
        Assert.Equal((DiscordIpc.Op.Frame, 10), DiscordIpc.DecodeHeader(frame));
        Assert.Equal("""{"a":"é"}""", System.Text.Encoding.UTF8.GetString(frame, 8, 10));
        Assert.Equal("""{"v":1,"client_id":"123"}""", DiscordIpc.Handshake("123"));
    }

    [Fact]
    public void Activity_shows_only_the_enabled_details_and_null_clears()
    {
        var since = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var info = new PresenceInfo(ClaudeEvents.Working, "meu \"projeto\"", since);
        var x = new PresenceDetails("claude-opus-5-5", "Página de perfis", 128_400);

        // Padrão: modelo e tempo; título, pasta e tokens desligados.
        var d = new DiscordConfig();
        var a = info.ToActivity(d, "Trabalhando", x);
        Assert.Equal("Trabalhando", (string?)a["details"]);
        Assert.Equal("Opus 5.5", (string?)a["state"]);
        Assert.Equal("Claude Color", (string?)a["assets"]!["large_text"]);
        Assert.Equal(1_700_000_000L, (long?)a["timestamps"]!["start"]);
        Assert.Equal(d.ImageUrl("working"), (string?)a["assets"]!["small_image"]);

        // Tudo ligado.
        d.ShowTitle = d.ShowProject = d.ShowTokens = true;
        a = info.ToActivity(d, "Trabalhando", x);
        Assert.Equal("Trabalhando · Página de perfis", (string?)a["details"]);
        Assert.Equal("Opus 5.5 · meu \"projeto\" · 128 mil tokens", (string?)a["state"]);
        Assert.Equal("Página de perfis", (string?)a["assets"]!["large_text"]);
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(DiscordIpc.SetActivity(42, a, "n1"))!; // aspas escapadas
        Assert.Equal("Opus 5.5 · meu \"projeto\" · 128 mil tokens", (string?)parsed["args"]!["activity"]!["state"]);

        // Sem a linha de estado: o título sobe sozinho; sem estado nem título, "details" some.
        d.ShowStatus = false;
        a = info.ToActivity(d, "Trabalhando", x);
        Assert.Equal("Página de perfis", (string?)a["details"]);
        d.ShowTitle = false;
        Assert.Null(info.ToActivity(d, "Trabalhando", x)["details"]);
        d.ShowStatus = true;

        // Tudo desligado: só o estado, sem "state" nem tempo.
        d.ShowModel = d.ShowTitle = d.ShowProject = d.ShowTokens = d.ShowElapsed = false;
        a = info.ToActivity(d, "Trabalhando", x);
        Assert.Null(a["state"]);
        Assert.Null(a["timestamps"]);

        // Título enorme: cortado em 128 (limite do Discord).
        d.ShowTitle = true;
        a = info.ToActivity(d, "Trabalhando", x with { Title = new string('t', 300) });
        Assert.Equal(128, ((string?)a["details"])!.Length);
        Assert.Equal(128, ((string?)a["assets"]!["large_text"])!.Length);
        Assert.Contains("\"activity\":null", DiscordIpc.SetActivity(42, null, "n2"));
    }

    [Fact]
    public void Transcript_is_read_incrementally()
    {
        var path = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path,
                """{"type":"user","message":{"content":"oi"}}""" + "\n" +
                """{"type":"assistant","message":{"id":"m1","model":"claude-sonnet-4-5-20250929","usage":{"input_tokens":10,"cache_read_input_tokens":1000,"cache_creation_input_tokens":90,"output_tokens":5}}}""" + "\n" +
                """{"type":"assistant","message":{"id":"m1","model":"claude-sonnet-4-5-20250929","usage":{"input_tokens":10,"cache_read_input_tokens":1000,"cache_creation_input_tokens":90,"output_tokens":7}}}""" + "\n" +
                "lixo que não é json\n" +
                """{"type":"custom-title","customTitle":"Primeiro título","sessionId":"s"}""" + "\n" +
                """{"type":"assistant","message":{"model":"claude-opus-5-5","usage":{"input_to""");  // linha ainda sendo escrita
            var t = new TranscriptStats(path);
            t.Update();
            Assert.Equal("claude-sonnet-4-5-20250929", t.Model);
            Assert.Equal(1100L, t.ContextTokens);
            Assert.Equal("Primeiro título", t.Title);

            // A linha cortada termina de ser escrita e chegam outras: só o que é novo é lido.
            File.AppendAllText(path,
                """kens":20,"cache_read_input_tokens":200000}}}""" + "\n" +
                """{"type":"summary","summary":"Título antigo"}""" + "\n");
            t.Update();
            Assert.Equal("claude-opus-5-5", t.Model);
            Assert.Equal(200_020L, t.ContextTokens);
            Assert.Equal("Título antigo", t.Title);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Model_and_token_labels()
    {
        Assert.Equal("Opus 5.5", TranscriptStats.ModelLabel("claude-opus-5-5"));
        Assert.Equal("Sonnet 4.5", TranscriptStats.ModelLabel("claude-sonnet-4-5-20250929"));
        Assert.Equal("Fable 5.2", TranscriptStats.ModelLabel("claude-fable-5-2"));
        Assert.Equal("Opus 4", TranscriptStats.ModelLabel("claude-opus-4-20250514"));
        Assert.Equal("Sonnet 3.5", TranscriptStats.ModelLabel("claude-3-5-sonnet-20241022"));
        Assert.Equal("gpt-x", TranscriptStats.ModelLabel("gpt-x"));
        Assert.Equal("850 tokens", TranscriptStats.TokenLabel(850));
        Assert.Equal("128 mil tokens", TranscriptStats.TokenLabel(128_400));
        Assert.Equal("1,2 mi tokens", TranscriptStats.TokenLabel(1_234_567));
    }

    [Fact]
    public void Hook_session_keeps_the_transcript_path()
    {
        var json = """{"session_id":"abc","cwd":"C:\\p\\proj","transcript_path":"C:\\Users\\x\\.claude\\projects\\p\\abc.jsonl"}""";
        Assert.Equal(@"C:\Users\x\.claude\projects\p\abc.jsonl", SessionInfo.TranscriptFromHook(json));
        Assert.Null(SessionInfo.TranscriptFromHook("""{"transcript_path":"C:\\x\\nada.txt"}"""));

        Storage.UpdateState(s => s.Sessions.Clear());
        var cfg = new AppConfig();
        SignalRunner.Begin(cfg, ClaudeEvents.Working, new SessionInfo(0, "proj", "abc", SessionInfo.TranscriptFromHook(json)));
        Assert.Equal(@"C:\Users\x\.claude\projects\p\abc.jsonl", Storage.LoadState().Find("abc")!.Transcript);
        Storage.UpdateState(s => s.Sessions.Clear());
    }

    [Fact]
    public void Images_come_from_the_repository_unless_overridden_with_https()
    {
        var d = new DiscordConfig();
        Assert.Equal(DiscordConfig.DefaultImageBase + "question.gif", d.ImageUrl("question"));
        d.Images = new() { ["question"] = "https://exemplo.com/q.gif", ["plan"] = "http://inseguro/p.gif", ["done"] = "lixo" };
        Assert.Equal("https://exemplo.com/q.gif", d.ImageUrl("question"));
        Assert.Equal(DiscordConfig.DefaultImageBase + "plan.gif", d.ImageUrl("plan")); // só https
        Assert.Equal(DiscordConfig.DefaultImageBase + "done.gif", d.ImageUrl("done"));
        Assert.Equal(DiscordConfig.DefaultImageBase + "logo.gif", d.ImageUrl("logo"));

        var a = new PresenceInfo(ClaudeEvents.Question, null, null).ToActivity(d, "Pergunta");
        Assert.Equal("https://exemplo.com/q.gif", (string?)a["assets"]!["small_image"]);
        Assert.Equal(DiscordConfig.DefaultImageBase + "logo.gif", (string?)a["assets"]!["large_image"]);
    }

    [Fact]
    public void Image_urls_are_written_to_config_and_missing_keys_are_filled()
    {
        var fresh = new DiscordConfig();
        Assert.True(fresh.EnsureImages()); // config sem a seção: grava
        var json = System.Text.Json.JsonSerializer.Serialize(fresh);
        Assert.Contains("logo.gif", json);
        Assert.Contains("working.gif", json);

        var d = new DiscordConfig { Images = new() { ["question"] = "https://exemplo.com/q.gif" } };
        Assert.True(d.EnsureImages());
        Assert.Equal("https://exemplo.com/q.gif", d.Images!["question"]); // a sua fica
        Assert.Equal(DiscordConfig.ImageKeys.Length, d.Images.Count);
        Assert.False(d.EnsureImages());

        d.Images = null;
        Assert.True(d.EnsureImages());
        Assert.Equal(DiscordConfig.DefaultImageBase + "idle.gif", d.Images!["idle"]);
    }

    [Fact]
    public void Presence_prefers_waiting_over_working_over_recent_done()
    {
        var cfg = new AppConfig();
        cfg.Events[ClaudeEvents.Working].HoldWhileWorking = true;
        var st = new RuntimeState();
        st.Session("a").WorkingSince = DateTimeOffset.Now.AddMinutes(-3);
        st.Session("a").Project = "proj-a";
        st.Session("b").WaitingFor = ClaudeEvents.Question;
        st.Session("b").Project = "proj-b";
        Assert.Equal(new PresenceInfo(ClaudeEvents.Question, "proj-b", null, "b"), PresenceInfo.From(st, cfg, TimeSpan.FromMinutes(5)));

        st.Session("b").WaitingFor = null;
        var working = PresenceInfo.From(st, cfg, TimeSpan.FromMinutes(5))!;
        Assert.Equal((ClaudeEvents.Working, "proj-a"), (working.Event, working.Project));

        st.Session("a").WorkingSince = null;
        st.LastSignal = ClaudeEvents.Done;
        st.LastSignalAt = DateTimeOffset.Now.AddMinutes(-1);
        Assert.Equal(ClaudeEvents.Done, PresenceInfo.From(st, cfg, TimeSpan.FromMinutes(5))!.Event);
        st.LastSignalAt = DateTimeOffset.Now.AddMinutes(-10);
        Assert.Null(PresenceInfo.From(st, cfg, TimeSpan.FromMinutes(5)));

        // Ocioso: com a opção ligada, mostra a sessão mais recente em vez de sumir.
        st.Session("a").Transcript = @"C:\t\a.jsonl";
        cfg.Discord.ShowWhenIdle = true;
        var idle = PresenceInfo.From(st, cfg, TimeSpan.FromMinutes(5))!;
        Assert.Equal((ClaudeEvents.Idle, "proj-a", "a"), (idle.Event, idle.Project, idle.SessionId));
        Assert.Equal("Ocioso", PresenceInfo.Details(ClaudeEvents.Idle));
    }
}
