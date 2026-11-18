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
    public static Func<string, long, bool> SendToHost { get; set; } = (evt, gen) => SignalPipe.TrySend(evt, gen);

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
            if (SignalRunner.Begin(cfg, evt) is not { } gen) return null;

            // App da bandeja aberto: ele executa o sinal em todos os dispositivos.
            if (SendToHost(evt, gen)) { Storage.Log($"hook: '{evt}' #{gen} entregue ao app"); return evt; }

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
