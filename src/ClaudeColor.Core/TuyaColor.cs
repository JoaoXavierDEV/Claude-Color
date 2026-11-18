using System.Globalization;

namespace ClaudeColor.Core;

/// <summary>Cor RGB com conversões para os formatos de cor usados pelas lâmpadas Tuya.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "#FF0000", ["vermelho"] = "#FF0000",
        ["green"] = "#00FF00", ["verde"] = "#00FF00",
        ["blue"] = "#0000FF", ["azul"] = "#0000FF",
        ["yellow"] = "#FFFF00", ["amarelo"] = "#FFFF00",
        ["orange"] = "#FF8000", ["laranja"] = "#FF8000",
        ["purple"] = "#8000FF", ["roxo"] = "#8000FF",
        ["magenta"] = "#FF00FF", ["rosa"] = "#FF0080", ["pink"] = "#FF0080",
        ["cyan"] = "#00FFFF", ["ciano"] = "#00FFFF",
        ["white"] = "#FFFFFF", ["branco"] = "#FFFFFF",
    };

    public static IReadOnlyCollection<string> NamedColors => Named.Keys;

    /// <summary>Aceita "#RRGGBB", "RRGGBB", "#RGB" ou um nome (red, verde, ...).</summary>
    public static RgbColor Parse(string text)
    {
        if (!TryParse(text, out var c))
            throw new FormatException($"Cor inválida: '{text}'. Use #RRGGBB ou um nome ({string.Join(", ", Named.Keys)}).");
        return c;
    }

    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (Named.TryGetValue(s, out var hex)) s = hex;
        s = s.TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        color = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    /// <summary>HSV com H em 0–360 e S/V em 0–1.</summary>
    public (double H, double S, double V) ToHsv()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min, h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static RgbColor FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte B(double f) => (byte)Math.Round(Math.Clamp(f, 0, 1) * 255);
        return new RgbColor(B(r + m), B(g + m), B(b + m));
    }
}

/// <summary>Codificação de cor dos DPs Tuya.</summary>
public static class TuyaColor
{
    /// <summary>
    /// Formato v2 (DP 24, "colour_data_v2"): HHHHSSSSVVVV em hexa, H 0–360, S e V 0–1000.
    /// <paramref name="brightness"/> (0–100) substitui o V da cor, se informado.
    /// </summary>
    public static string ToV2(RgbColor color, int? brightness = null)
    {
        var (h, s, v) = color.ToHsv();
        int hh = (int)Math.Round(h) % 360;
        int ss = (int)Math.Round(s * 1000);
        int vv = brightness is { } b ? Math.Clamp(b, 1, 100) * 10 : (int)Math.Round(v * 1000);
        vv = Math.Clamp(vv, 10, 1000);
        return $"{hh:x4}{ss:x4}{vv:x4}";
    }

    /// <summary>
    /// Formato v1 (DP 5, "colour_data"): RRGGBB + HHHH + SS + VV (14 caracteres), S e V 0–255.
    /// </summary>
    public static string ToV1(RgbColor color, int? brightness = null)
    {
        var (h, s, v) = color.ToHsv();
        if (brightness is { } b) v = Math.Clamp(b, 1, 100) / 100.0;
        var shown = RgbColor.FromHsv(h, s, v);
        int hh = (int)Math.Round(h) % 360;
        int ss = (int)Math.Round(s * 255);
        int vv = Math.Max(1, (int)Math.Round(v * 255));
        return $"{shown.R:x2}{shown.G:x2}{shown.B:x2}{hh:x4}{ss:x2}{vv:x2}";
    }

    /// <summary>Lê de volta uma string v2 (HHHHSSSSVVVV). Retorna cor com V máximo e o brilho 0–100.</summary>
    public static bool TryParseV2(string? value, out RgbColor color, out int brightness)
    {
        color = default; brightness = 0;
        if (value is null || value.Length != 12) return false;
        if (!int.TryParse(value[..4], NumberStyles.HexNumber, null, out var h) ||
            !int.TryParse(value[4..8], NumberStyles.HexNumber, null, out var s) ||
            !int.TryParse(value[8..], NumberStyles.HexNumber, null, out var v)) return false;
        color = RgbColor.FromHsv(h, s / 1000.0, 1);
        brightness = (int)Math.Round(v / 10.0);
        return true;
    }
}
