namespace ClaudeColor.App;

/// <summary>Salva um PNG de cada página da janela: <c>ClaudeColor.exe --screenshots &lt;pasta&gt;</c>.</summary>
internal static class Screenshots
{
    /// <param name="bothThemes">Depois das páginas no tema atual, troca o tema na hora e salva de novo (sufixo "-claro"/"-escuro").</param>
    public static void Save(string folder, bool bothThemes = false)
    {
        Directory.CreateDirectory(folder);
        // Sem Application.Run: as continuações assíncronas das páginas precisam voltar para esta thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        SavePages(folder, "");
        if (!bothThemes) return;
        Ui.ApplyTheme(Ui.Dark ? "light" : "dark");
        SavePages(folder, Ui.Dark ? "-escuro" : "-claro");
    }

    private static void SavePages(string folder, string suffix)
    {
        using var form = new SettingsForm { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false };
        form.Show();
        int i = 1;
        foreach (var page in form.Pages)
        {
            form.Navigate(page.GetType());
            // Dá tempo para páginas que carregam em segundo plano (Solução de problemas).
            for (int t = 0; t < 30; t++) { Application.DoEvents(); Thread.Sleep(50); }
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            var name = Path.Combine(folder, $"{i++}-{page.GetType().Name.Replace("Page", "").ToLowerInvariant()}{suffix}.png");
            bmp.Save(name, System.Drawing.Imaging.ImageFormat.Png);
        }
        form.Hide();
    }
}
