using System.IO.Pipes;
using System.Text;

namespace ClaudeLamp.Core;

/// <summary>
/// Canal entre o hook (processo curto) e o app da bandeja (processo longo, que segura o RGB do PC).
/// Protocolo: uma linha "evento geração\n"; o app responde "ok\n".
/// </summary>
public static class SignalPipe
{
    public const string Name = "ClaudeLamp.Signals";

    public static string Format(string evt, long gen) => $"{evt} {gen}";

    public static bool TryParse(string? line, out string evt, out long gen)
    {
        evt = ""; gen = 0;
        var parts = line?.Trim().Split(' ');
        if (parts is not { Length: 2 } || !ClaudeEvents.All.Contains(parts[0]) || !long.TryParse(parts[1], out gen)) return false;
        evt = parts[0];
        return true;
    }

    /// <summary>Entrega o sinal ao app aberto. Retorna false se ninguém atendeu (app fechado).</summary>
    public static bool TrySend(string evt, long gen, int timeoutMs = 300)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            var data = Encoding.UTF8.GetBytes(Format(evt, gen) + "\n");
            pipe.Write(data);
            pipe.Flush();
            var buf = new byte[8];
            var read = pipe.ReadAsync(buf).AsTask();
            return read.Wait(timeoutMs * 2) && Encoding.UTF8.GetString(buf, 0, read.Result).StartsWith("ok");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
