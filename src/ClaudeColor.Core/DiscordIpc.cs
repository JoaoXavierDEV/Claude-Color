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

/// <summary>Modelo, título e tokens da sessão mostrada (lidos do transcript); cada um só aparece se a opção estiver ligada.</summary>
public sealed record PresenceDetails(string? Model, string? Title, long? ContextTokens);

/// <summary>O que mostrar no Discord: o evento mais urgente entre as sessões do Claude Code.</summary>
public sealed record PresenceInfo(string Event, string? Project, DateTimeOffset? Since, string? SessionId = null)
{
    /// <summary>Linha principal do status.</summary>
    public static string Details(string evt) => evt switch
    {
        ClaudeEvents.Question => "Aguardando você: pergunta",
        ClaudeEvents.Plan => "Aguardando você: plano",
        ClaudeEvents.Permission => "Aguardando você: permissão",
        ClaudeEvents.Idle => "Ocioso",
        ClaudeEvents.Working => "Trabalhando",
        ClaudeEvents.Done => "Terminou",
        _ => "Claude Code",
    };

    /// <summary>
    /// Estado atual: alguém espera você (pergunta &gt; plano &gt; permissão &gt; ocioso) &gt; alguém trabalhando &gt;
    /// "terminou" recente (até <paramref name="doneFor"/>) &gt; ocioso, com <see cref="DiscordConfig.ShowWhenIdle"/>.
    /// Null = nada a mostrar.
    /// </summary>
    public static PresenceInfo? From(RuntimeState s, AppConfig cfg, TimeSpan doneFor)
    {
        if (s.WaitingFor is { } waiting)
        {
            var (id, who) = s.Sessions.Where(x => x.Value.WaitingFor == waiting).OrderByDescending(x => x.Value.LastSeen).First();
            return new PresenceInfo(waiting, who.Project, null, id);
        }
        var working = s.Sessions.Where(x => x.Value.WorkingSince is not null).OrderByDescending(x => x.Value.WorkingSince).FirstOrDefault();
        if (working.Value is { } w && s.IsWorking(cfg))
            return new PresenceInfo(ClaudeEvents.Working, w.Project, w.WorkingSince, working.Key);
        if (s.LastSignal == ClaudeEvents.Done && s.LastSignalAt is { } at && DateTimeOffset.Now - at < doneFor)
            return new PresenceInfo(ClaudeEvents.Done, s.LastProject, null, s.LastSessionId ?? RuntimeState.DefaultSession);
        if (cfg.Discord.ShowWhenIdle)
        {
            // A sessão mais recente (de preferência uma com transcript, para ter modelo e título).
            var recent = s.Sessions.OrderByDescending(x => x.Value.Transcript is not null).ThenByDescending(x => x.Value.LastSeen).FirstOrDefault();
            return new PresenceInfo(ClaudeEvents.Idle, recent.Value?.Project ?? s.LastProject, null, recent.Key);
        }
        return null;
    }

    private const int Max = 128; // limite do Discord para details, state e textos das imagens

    private static string Cut(string s) => s.Length <= Max ? s : s[..(Max - 1)] + "…";

    /// <summary>
    /// A "activity" do SET_ACTIVITY (a prévia da janela usa a mesma). details = estado (+ título); state = modelo,
    /// pasta e tokens ligados; dica da imagem grande = título.
    /// </summary>
    public JsonObject ToActivity(DiscordConfig d, string eventLabel, PresenceDetails? x = null)
    {
        var title = d.ShowTitle && !string.IsNullOrWhiteSpace(x?.Title) ? x.Title : null;
        var parts = new List<string>();
        if (d.ShowModel && !string.IsNullOrWhiteSpace(x?.Model)) parts.Add(TranscriptStats.ModelLabel(x.Model));
        if (d.ShowProject && !string.IsNullOrWhiteSpace(Project)) parts.Add(SessionInfo.Clean(Project));
        if (d.ShowTokens && x?.ContextTokens is > 0 and var tokens) parts.Add(TranscriptStats.TokenLabel(tokens));

        // Linha de cima: o estado (se ligado) e o título (se ligado). O ícone pequeno indica o estado de qualquer forma.
        var top = string.Join(" · ", new[] { d.ShowStatus ? Details(Event) : null, title }.Where(t => t is not null));
        var assets = new JsonObject
        {
            ["large_image"] = d.ImageUrl("logo"),
            ["large_text"] = Cut(title ?? "Claude Color"),
        };
        if (d.ShowStatusOrb)
        {
            assets["small_image"] = d.ImageUrl(Event);
            assets["small_text"] = Cut(eventLabel);
        }
        var activity = new JsonObject { ["assets"] = assets };
        if (top.Length >= 2) activity["details"] = Cut(top);
        var state = string.Join(" · ", parts);
        if (state.Length >= 2) activity["state"] = Cut(state); // o Discord exige 2 a 128 caracteres
        if (d.ShowElapsed && Since is { } since) activity["timestamps"] = new JsonObject { ["start"] = since.ToUnixTimeSeconds() };
        return activity;
    }
}
