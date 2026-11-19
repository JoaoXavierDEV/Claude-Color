using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ClaudeColor.Core;

/// <summary>Trata uma chamada de hook do Claude Code. Nunca lança exceção.</summary>
public static class HookHandler
{
    /// <summary>Inicia o processo que executa o sinal. Substituível nos testes.</summary>
    public static Action<string, string> StartProcess { get; set; } = (exe, args) =>
        // ShellExecute não herda os pipes do Claude Code, então o hook retorna na hora.
        Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
        });

    /// <summary>Entrega o sinal ao app aberto. Substituível nos testes.</summary>
    public static Func<string, long, SessionInfo, bool> SendToHost { get; set; } = (evt, gen, s) => SignalPipe.TrySend(evt, gen, s);

    /// <summary>Janela do terminal e projeto da sessão. Substituível nos testes.</summary>
    public static Func<string, SessionInfo> FindSession { get; set; } = SessionInfo.FromHook;

    /// <summary>Pisca o terminal na barra de tarefas. Substituível nos testes.</summary>
    public static Func<long, bool> Flash { get; set; } = TaskbarFlash.Flash;

    /// <summary>Estado "ocupado" do Windows. Substituível nos testes.</summary>
    public static Func<UserBusy.State> BusyState { get; set; } = UserBusy.Current;

    /// <returns>O evento disparado, ou null se nada foi feito.</returns>
    public static string? Handle(string input, string exePath)
    {
        try
        {
            var evt = EventMapper.Map(input);
            // PostToolUse dispara a cada ferramenta: só registra no log quando algo aguardava você.
            if (evt != ClaudeEvents.Resumed) Storage.Log($"hook: {Summarize(input)} → {evt ?? "(ignorado)"}");
            if (evt is null) return null;

            var cfg = Storage.LoadConfig();
            // PostToolUse dispara a cada ferramenta: só procura a janela quando algo aguardava você.
            var session = evt == ClaudeEvents.Resumed && Storage.LoadState().WaitingFor is null ? SessionInfo.None : FindSession(input);
            if (SignalRunner.Begin(cfg, evt, session) is not { } gen) return null;

            // Barra de tarefas: o próprio hook pisca o terminal (não depende do app nem de administrador).
            if (cfg.Events.TryGetValue(evt, out var sig) && sig.UsesFlash(evt) && session.Window != 0)
            {
                var busy = BusyState();
                if (cfg.Quiet.SkipFlash && UserBusy.IsBusy(busy)) Storage.Log($"hook: barra não piscou ({UserBusy.Describe(busy)})");
                else if (Flash(session.Window)) Storage.Log($"hook: piscando a janela {session.Window:X} na barra de tarefas");
            }

            // App da bandeja aberto: ele executa o sinal em todos os dispositivos.
            if (SendToHost(evt, gen, session)) { Storage.Log($"hook: '{evt}' #{gen} entregue ao app"); return evt; }

            if (cfg.LaunchFromHook)
            {
                // App fechado: abre na bandeja; ao iniciar, ele executa o sinal recém-registrado (PendingSignal).
                StartProcess(exePath, "--tray --from-hook");
                Storage.Log($"hook: '{evt}' #{gen}: app fechado, abrindo na bandeja");
            }
            else if (cfg.Lamp.IsActive)
            {
                // Sem abrir o app: só a lâmpada, num processo separado.
                StartProcess(exePath, $"run-signal {evt} {gen}");
            }
            return evt;
        }
        catch (Exception ex)
        {
            Storage.Log($"hook falhou: {ex}");
            return null;
        }
    }

    public static async Task<string> ReadStdinAsync(TimeSpan timeout)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin);
            var read = reader.ReadToEndAsync();
            return await Task.WhenAny(read, Task.Delay(timeout)) == read ? read.Result : "";
        }
        catch { return ""; }
    }

    private static string Summarize(string json)
    {
        try
        {
            var n = JsonNode.Parse(json);
            return $"{n?["hook_event_name"]}/{n?["tool_name"] ?? n?["notification_type"]}";
        }
        catch { return json.Length > 80 ? json[..80] : json; }
    }
}
