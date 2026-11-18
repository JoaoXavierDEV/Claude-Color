using System.Drawing.Drawing2D;
using ClaudeLamp.Core;

namespace ClaudeLamp.App;

/// <summary>Pequenos auxiliares para montar a interface em código.</summary>
internal static class Ui
{
    public static readonly Color Error = Color.Firebrick;
    public static readonly Color Ok = Color.SeaGreen;

    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 3),
        Font = bold ? new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) : SystemFonts.MessageBoxFont,
    };

    public static Button Button(string text, EventHandler onClick, int width = 0)
    {
        var b = new Button { Text = text, AutoSize = width == 0, Width = width == 0 ? 75 : width, Height = 28, Margin = new Padding(3) };
        b.Click += onClick;
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), FlowDirection = FlowDirection.LeftToRight };
        p.Controls.AddRange(controls);
        return p;
    }

    public static Color ToColor(RgbColor c) => Color.FromArgb(c.R, c.G, c.B);
    public static RgbColor ToRgb(Color c) => new(c.R, c.G, c.B);

    public static Color ParseOr(string hex, Color fallback) =>
        RgbColor.TryParse(hex, out var c) ? ToColor(c) : fallback;

    /// <summary>Executa trabalho assíncrono desabilitando controles e mostrando o resultado num rótulo.</summary>
    public static async Task Busy(Label status, string busyText, Control[] disable, Func<Task<string?>> work)
    {
        foreach (var c in disable) c.Enabled = false;
        status.ForeColor = SystemColors.GrayText;
        status.Text = busyText;
        try
        {
            var msg = await work();
            status.ForeColor = Ok;
            status.Text = msg ?? "OK";
        }
        catch (Exception ex)
        {
            Storage.Log($"ui: {ex}");
            status.ForeColor = Error;
            status.Text = (ex is AggregateException a ? a.InnerException ?? ex : ex).Message;
        }
        finally
        {
            foreach (var c in disable) c.Enabled = true;
        }
    }

    /// <summary>Ícone de lâmpada desenhado na cor dada.</summary>
    public static Icon BulbIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 3, 1, 26, 24);
            using var outline = new Pen(Color.FromArgb(40, 40, 40), 2);
            g.DrawEllipse(outline, 3, 1, 26, 24);
            g.FillRectangle(Brushes.DimGray, 10, 24, 12, 7);
        }
        var handle = bmp.GetHicon();
        using var tmp = Icon.FromHandle(handle);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    public static Color ColorOf(ManualState s) =>
        !s.On ? Color.FromArgb(70, 70, 70)
        : s.Mode == "colour" ? ParseOr(s.Color, Color.White)
        : Color.FromArgb(255, 236, 200);

    public static Image Swatch(Color c)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        using var b = new SolidBrush(c);
        g.FillRectangle(b, 1, 1, 14, 14);
        g.DrawRectangle(Pens.Gray, 1, 1, 13, 13);
        return bmp;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
