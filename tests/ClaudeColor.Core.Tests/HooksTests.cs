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
        var line = SignalPipe.Format("question", 9, new SessionInfo(0x1A2B, "meu projeto\nlegal"));
        Assert.True(SignalPipe.TryParse(line, out var e, out var g, out var s));
        Assert.Equal(("question", 9L, 0x1A2BL, "meu projetolegal"), (e, g, s.Window, s.Project));

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
