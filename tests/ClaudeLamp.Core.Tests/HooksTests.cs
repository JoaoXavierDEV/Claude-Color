using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ClaudeLamp.Core;

namespace ClaudeLamp.Core.Tests;

internal static class TestSetup
{
    /// <summary>Isola config/estado/log dos testes num diretório temporário.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("CLAUDE_LAMP_HOME",
            Path.Combine(Path.GetTempPath(), "claude-lamp-tests-" + Environment.ProcessId));
}

public class HooksInstallerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hooks-").FullName;
    private string Settings => Path.Combine(_dir, "settings.json");
    private const string Exe = @"C:\Users\x\.claude-lamp\bin\ClaudeLamp.exe";

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
        foreach (var evt in new[] { "UserPromptSubmit", "Stop", "Notification", "PreToolUse" })
            Assert.Single(root["hooks"]![evt]!.AsArray());
        Assert.Equal(HooksStatus.Installed, inst.GetStatus(Exe));
        Assert.Equal($"\"{Exe}\" hook", root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
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
    [Fact]
    public void Ignored_payload_starts_nothing()
    {
        var started = new List<string>();
        HookHandler.StartProcess = (_, args) => started.Add(args);
        Assert.Null(HookHandler.Handle("""{"hook_event_name":"PreToolUse","tool_name":"Bash"}""", "x.exe"));
        Assert.Null(HookHandler.Handle("lixo", "x.exe"));
        Assert.Empty(started);
    }

    [Fact]
    public void Configured_lamp_starts_run_signal()
    {
        Storage.SaveConfig(new LampConfig { DeviceId = "abc", LocalKey = "0123456789abcdef", Ip = "127.0.0.1" });
        var started = new List<string>();
        HookHandler.StartProcess = (_, args) => started.Add(args);
        Assert.Equal(ClaudeEvents.Done, HookHandler.Handle("""{"hook_event_name":"Stop"}""", "x.exe"));
        var args = Assert.Single(started);
        Assert.StartsWith("run-signal done ", args);
    }
}
