using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>
/// Imagens 512×512 para o aplicativo no Discord Developer Portal (Rich Presence → Art Assets). O nome de cada
/// arquivo é a chave que o Claude Color usa: logo, question, plan, permission, done, working, idle.
/// </summary>
internal static class DiscordAssets
{
    public static void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        Write(Path.Combine(folder, "logo.png"), Ui.Accent);
        foreach (var (evt, sig) in AppConfig.DefaultEvents())
            Write(Path.Combine(folder, $"{evt}.png"), Ui.ParseOr(sig.Color, Ui.Accent));
    }

    private static void Write(string path, Color color)
    {
        using var bmp = Ui.SignalBitmap(color, 512);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
