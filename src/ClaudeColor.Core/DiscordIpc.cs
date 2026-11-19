using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace ClaudeColor.Core;

/// <summary>
/// Protocolo local do Discord (Rich Presence): quadros "opcode (int32 LE) + tamanho (int32 LE) + JSON UTF-8"
/// no pipe \\.\pipe\discord-ipc-N do Discord de desktop.
/// </summary>
public static class DiscordIpc
{
    public enum Op { Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4 }

    public const int HeaderSize = 8;

    public static byte[] Encode(Op op, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, HeaderSize);
        return frame;
    }

    public static (Op Op, int Length) DecodeHeader(ReadOnlySpan<byte> header) =>
        ((Op)BinaryPrimitives.ReadInt32LittleEndian(header), BinaryPrimitives.ReadInt32LittleEndian(header[4..]));

    public static string Handshake(string clientId) =>
        new JsonObject { ["v"] = 1, ["client_id"] = clientId }.ToJsonString();

    /// <summary>SET_ACTIVITY; <paramref name="activity"/> null limpa o status.</summary>
    public static string SetActivity(int pid, JsonObject? activity, string nonce) =>
        new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = pid, ["activity"] = activity },
            ["nonce"] = nonce,
        }.ToJsonString();
}

/// <summary>O que mostrar no Discord: o evento mais urgente entre as sessões do Claude Code.</summary>
public sealed record PresenceInfo(string Event, string? Project, DateTimeOffset? Since)
{
    /// <summary>Linha principal do status.</summary>
    public static string Details(string evt) => evt switch
    {
        ClaudeEvents.Question => "Aguardando você: pergunta",
        ClaudeEvents.Plan => "Aguardando você: plano",
        ClaudeEvents.Permission => "Aguardando você: permissão",
        ClaudeEvents.Idle => "Esperando você",
        ClaudeEvents.Working => "Trabalhando",
        ClaudeEvents.Done => "Terminou",
        _ => "Claude Code",
    };

    /// <summary>
    /// Estado atual: alguém espera você (pergunta &gt; plano &gt; permissão &gt; ocioso) &gt; alguém trabalhando &gt;
    /// "terminou" recente (até <paramref name="doneFor"/>). Null = nada a mostrar.
    /// </summary>
    public static PresenceInfo? From(RuntimeState s, AppConfig cfg, TimeSpan doneFor)
    {
        if (s.WaitingFor is { } waiting)
        {
            var who = s.Sessions.Values.Where(x => x.WaitingFor == waiting).OrderByDescending(x => x.LastSeen).First();
            return new PresenceInfo(waiting, who.Project, null);
        }
        var working = s.Sessions.Values.Where(x => x.WorkingSince is not null).OrderByDescending(x => x.WorkingSince).FirstOrDefault();
        if (working is not null && s.IsWorking(cfg))
            return new PresenceInfo(ClaudeEvents.Working, working.Project, working.WorkingSince);
        if (s.LastSignal == ClaudeEvents.Done && s.LastSignalAt is { } at && DateTimeOffset.Now - at < doneFor)
            return new PresenceInfo(ClaudeEvents.Done, s.LastProject, null);
        return null;
    }

    /// <summary>A "activity" do SET_ACTIVITY. O projeto só entra com <paramref name="showProject"/>.</summary>
    /// <param name="image">Chave (logo, question...) → URL ou chave de Art Asset.</param>
    public JsonObject ToActivity(bool showProject, string eventLabel, Func<string, string>? image = null)
    {
        image ??= key => key;
        var activity = new JsonObject
        {
            ["details"] = Details(Event),
            ["assets"] = new JsonObject
            {
                ["large_image"] = image("logo"),
                ["large_text"] = "Claude Color",
                ["small_image"] = image(Event),
                ["small_text"] = eventLabel,
            },
        };
        if (showProject && !string.IsNullOrWhiteSpace(Project)) activity["state"] = $"em {SessionInfo.Clean(Project)}";
        if (Since is { } since) activity["timestamps"] = new JsonObject { ["start"] = since.ToUnixTimeSeconds() };
        return activity;
    }
}
