using System.Drawing.Drawing2D;
using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>
/// GIFs animados do status no Discord (a esfera do Claude Color pulsando na cor de cada sinal). Ficam em
/// docs/discord/ no repositório e o Discord os carrega pela URL (<see cref="DiscordConfig.ImageUrl"/>), sem
/// upload no Developer Portal. Nome do arquivo = chave: logo, question, plan, permission, done, working, idle.
/// </summary>
internal static class DiscordAssets
{
    private const int Size = 256, Frames = 12, DelayCs = 8;
    // Fundo do cartão do Discord: o halo é misturado a ele (GIF não tem transparência parcial).
    private static readonly Color CardBackground = Color.FromArgb(30, 31, 34);

    public static void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        Write(Path.Combine(folder, "logo.gif"), Color.FromArgb(217, 119, 87));
        foreach (var (evt, sig) in AppConfig.DefaultEvents())
            Write(Path.Combine(folder, $"{evt}.gif"), Ui.ParseOr(sig.Color, Color.FromArgb(217, 119, 87)));
    }

    private static void Write(string path, Color color)
    {
        var frames = Enumerable.Range(0, Frames)
            .Select(i => Frame(color, (1 - Math.Cos(2 * Math.PI * i / Frames)) / 2))
            .ToList();
        try { GifWriter.Save(path, frames, DelayCs); }
        finally { foreach (var f in frames) f.Dispose(); }
    }

    /// <param name="pulse">0 = halo recolhido, 1 = halo cheio.</param>
    private static Bitmap Frame(Color c, double pulse)
    {
        var bmp = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = Size;

        float halo = s * (float)(0.74 + 0.26 * pulse);
        using (var b = new SolidBrush(Mix(c, CardBackground, 0.62 - 0.22 * pulse)))
            g.FillEllipse(b, (s - halo) / 2, (s - halo) / 2, halo - 1, halo - 1);

        float core = s * 0.6f;
        using (var b = new SolidBrush(c))
            g.FillEllipse(b, (s - core) / 2, (s - core) / 2, core, core);
        using (var b = new SolidBrush(Mix(Color.White, c, 0.45)))
            g.FillEllipse(b, s * 0.33f, s * 0.3f, s * 0.16f, s * 0.16f);
        return bmp;
    }

    /// <summary>Mistura: <paramref name="amount"/> de <paramref name="b"/> sobre <paramref name="a"/>.</summary>
    private static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
}
