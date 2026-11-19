using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>
/// Status no perfil do Discord (Rich Presence): "Jogando Claude Color · Aguardando você: pergunta".
/// Fala com o Discord de desktop pelo pipe local discord-ipc-N. Sem Discord aberto, não faz nada (só log).
/// As atualizações são "o último vence": só o estado mais recente é enviado.
/// </summary>
internal sealed class DiscordPresence : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _timer;
    private NamedPipeClientStream? _pipe;
    private string? _lastSent;
    private int _pending;
    private DateTimeOffset _testUntil;
    private bool _disposed;

    /// <summary>Último resultado, para a página Sistema e a Solução de problemas.</summary>
    public string Status { get; private set; } = "Desligado";
    public bool Connected => _pipe is { IsConnected: true };

    public DiscordPresence()
    {
        // A cada 30 s: some com o "Terminou" antigo e reconecta se o Discord abriu depois.
        _timer = new System.Threading.Timer(_ => Refresh(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    /// <summary>Recalcula o status a partir do estado das sessões e envia, se mudou.</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return; // já tem uma atualização na fila
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                Interlocked.Exchange(ref _pending, 0);
                await UpdateAsync();
            }
            catch (Exception ex) { Status = ex.Message; Storage.Log($"discord: {ex.Message}"); Disconnect(); }
            finally { _gate.Release(); }
        });
    }

    /// <summary>Mostra "Aguardando você: pergunta" por 15 s, para testar.</summary>
    public void Test()
    {
        _testUntil = DateTimeOffset.Now.AddSeconds(15);
        _lastSent = null;
        Refresh();
        _ = Task.Delay(TimeSpan.FromSeconds(16)).ContinueWith(_ => { _lastSent = null; Refresh(); });
    }

    private async Task UpdateAsync()
    {
        if (_disposed) return;
        var cfg = Storage.LoadConfig();
        var d = cfg.Discord;
        if (!d.Enabled || string.IsNullOrWhiteSpace(d.ApplicationId))
        {
            if (Connected) await SendAsync(null);
            Disconnect();
            Status = !d.Enabled ? "Desligado" : "Falta o Application ID";
            return;
        }

        PresenceInfo? info = DateTimeOffset.Now < _testUntil
            ? new PresenceInfo(ClaudeEvents.Question, "teste", null)
            : PresenceInfo.From(Storage.LoadState(), cfg, TimeSpan.FromMinutes(Math.Max(1, d.ClearAfterMinutes)));
        var activity = info?.ToActivity(d.ShowProject, SignalsPage.DisplayName(info.Event));
        var key = activity?.ToJsonString() ?? "null";
        if (key == _lastSent && Connected) return;

        if (!Connected && !await ConnectAsync(d.ApplicationId))
        {
            Status = "Discord não está aberto";
            _lastSent = null;
            return;
        }
        await SendAsync(activity);
        _lastSent = key;
        Status = info is null ? "Conectado (sem status agora)" : $"Conectado: {PresenceInfo.Details(info.Event)}";
    }

    private async Task<bool> ConnectAsync(string applicationId)
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await pipe.ConnectAsync(cts.Token);
                await WriteAsync(pipe, DiscordIpc.Op.Handshake, DiscordIpc.Handshake(applicationId));
                var (op, body) = await ReadAsync(pipe);
                if (op == DiscordIpc.Op.Close)
                    throw new InvalidOperationException($"o Discord recusou a conexão ({body}). Confira o Application ID.");
                _pipe = pipe;
                Storage.Log($"discord: conectado em discord-ipc-{i}");
                return true;
            }
            catch (InvalidOperationException) { pipe.Dispose(); throw; }
            catch (Exception ex)
            {
                pipe.Dispose();
                // discord-ipc-0 é o normal; os outros só existem com mais de um Discord aberto.
                if (i == 0) LogOnce($"discord-ipc-0: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return false;
    }

    private string? _lastLogged;

    /// <summary>Registra uma falha de conexão só quando ela muda (o timer tenta a cada 30 s).</summary>
    private void LogOnce(string message)
    {
        if (message == _lastLogged) return;
        _lastLogged = message;
        Storage.Log($"discord: {message}");
    }

    private async Task SendAsync(JsonObject? activity)
    {
        var pipe = _pipe ?? throw new IOException("sem conexão com o Discord");
        await WriteAsync(pipe, DiscordIpc.Op.Frame, DiscordIpc.SetActivity(Environment.ProcessId, activity, Guid.NewGuid().ToString("N")));
        var (op, body) = await ReadAsync(pipe); // a resposta precisa ser lida para o pipe não encher
        if (op == DiscordIpc.Op.Close) throw new IOException($"o Discord fechou a conexão ({body})");
        if (body.Contains("\"evt\":\"ERROR\"")) Storage.Log($"discord: erro ao atualizar: {body}");
    }

    private static async Task WriteAsync(Stream pipe, DiscordIpc.Op op, string json)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await pipe.WriteAsync(DiscordIpc.Encode(op, json), cts.Token);
        await pipe.FlushAsync(cts.Token);
    }

    private static async Task<(DiscordIpc.Op Op, string Body)> ReadAsync(Stream pipe)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var header = new byte[DiscordIpc.HeaderSize];
        await pipe.ReadExactlyAsync(header, cts.Token);
        var (op, length) = DiscordIpc.DecodeHeader(header);
        if (length is < 0 or > 1 << 20) throw new IOException("resposta inválida do Discord");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, cts.Token);
        return (op, Encoding.UTF8.GetString(body));
    }

    private void Disconnect()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        try
        {
            if (_gate.Wait(2000))
            {
                try { if (Connected) SendAsync(null).Wait(1500); }
                finally { _gate.Release(); }
            }
        }
        catch { /* saindo */ }
        Disconnect();
    }
}
