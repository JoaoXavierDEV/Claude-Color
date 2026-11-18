namespace ClaudeColor.App;

/// <summary>Salva um PNG de cada página da janela: <c>ClaudeColor.exe --screenshots &lt;pasta&gt;</c>.</summary>
internal static class Screenshots
{
    public static void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        using var form = new SettingsForm { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false };
        form.Show();
        int i = 1;
        foreach (var page in form.Pages)
        {
            form.Navigate(page.GetType());
            Application.DoEvents();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            var name = Path.Combine(folder, $"{i++}-{page.GetType().Name.Replace("Page", "").ToLowerInvariant()}.png");
            bmp.Save(name, System.Drawing.Imaging.ImageFormat.Png);
        }
        form.Hide();
    }
}
