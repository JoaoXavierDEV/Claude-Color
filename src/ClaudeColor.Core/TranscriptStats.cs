using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeColor.Core;

/// <summary>
/// Lê o transcript (JSONL) de uma sessão do Claude Code de forma incremental: guarda até onde leu e, a cada
/// <see cref="Update"/>, processa só as linhas novas (os arquivos passam de vários MB). Extrai o modelo, o título
/// da conversa e os tokens no contexto (os mesmos do /context).
/// </summary>
public sealed partial class TranscriptStats(string path)
{
    private long _offset;

    public string Path { get; } = path;
    /// <summary>Último modelo usado (ex.: "claude-opus-5-5").</summary>
    public string? Model { get; private set; }
    /// <summary>Título da conversa ("custom-title" ou, em versões antigas, "summary").</summary>
    public string? Title { get; private set; }
    /// <summary>Tokens no contexto na última chamada: entrada + cache lido + cache criado.</summary>
    public long? ContextTokens { get; private set; }

    public void Update()
    {
        if (!File.Exists(Path)) return;
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _offset) _offset = 0; // arquivo recriado
        if (fs.Length == _offset) return;
        fs.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[fs.Length - _offset];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        // Só linhas completas: a última pode estar sendo escrita agora; ela é relida na próxima vez.
        int end = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
        if (end < 0) return;
        foreach (var line in Encoding.UTF8.GetString(buffer, 0, end + 1).Split('\n'))
            if (line.Length > 1) Parse(line);
        _offset += end + 1;
    }

    private void Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return;
            switch (type.GetString())
            {
                case "assistant" when root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object:
                    if (msg.TryGetProperty("model", out var model) && model.GetString() is { Length: > 0 } m && !m.StartsWith('<'))
                        Model = m;
                    if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        long total = Long(usage, "input_tokens") + Long(usage, "cache_read_input_tokens") + Long(usage, "cache_creation_input_tokens");
                        if (total > 0) ContextTokens = total;
                    }
                    break;
                case "custom-title" when Str(root, "customTitle") is { } t: Title = t; break;
                case "ai-title" when Str(root, "aiTitle") is { } t: Title = t; break;
                case "summary" when Str(root, "summary") is { } t: Title = t; break;
            }
        }
        catch (JsonException) { /* linha inválida: ignora */ }
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? SessionInfo.Clean(s.Length > 200 ? s[..200] : s) : null;

    /// <summary>"claude-opus-5-5" → "Opus 5.5"; "claude-3-5-sonnet-20241022" → "Sonnet 3.5"; outro fica como está.</summary>
    public static string ModelLabel(string model)
    {
        if (NewModel().Match(model) is { Success: true } a)
            return $"{Cap(a.Groups[1].Value)} {a.Groups[2].Value}{(a.Groups[3].Success ? "." + a.Groups[3].Value : "")}";
        if (OldModel().Match(model) is { Success: true } b)
            return $"{Cap(b.Groups[3].Value)} {b.Groups[1].Value}{(b.Groups[2].Success ? "." + b.Groups[2].Value : "")}";
        return model;
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"850 tokens", "128 mil tokens", "1,2 mi tokens".</summary>
    public static string TokenLabel(long n)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        return n < 1_000 ? $"{n} tokens"
            : n < 1_000_000 ? $"{Math.Round(n / 1_000.0).ToString("0", pt)} mil tokens"
            : $"{(n / 1_000_000.0).ToString("0.#", pt)} mi tokens";
    }

    [GeneratedRegex(@"^claude-(opus|sonnet|haiku|fable)-(\d+)(?:-(\d{1,2}))?(?:-\d{8})?(?:\[.*\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex NewModel();

    [GeneratedRegex(@"^claude-(\d+)(?:-(\d{1,2}))?-(opus|sonnet|haiku)(?:-\d{8})?$", RegexOptions.IgnoreCase)]
    private static partial Regex OldModel();
}
