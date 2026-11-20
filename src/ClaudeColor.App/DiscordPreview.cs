using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>
/// Prévia do status do Discord (cartão + nota), usada na página Discord e na Visão geral. Monta a mesma "activity"
/// enviada ao Discord; a leitura do transcript (modelo, título, tokens) roda fora da thread da janela.
/// </summary>
internal sealed class DiscordPreview : FlowLayoutPanel
{
    private readonly DiscordActivityCard _card = new();
    private readonly Label _note = Ui.Hint("");
    private string? _imageUrl;
    private int _generation;

    public DiscordPreview()
    {
        AutoSize = true;
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        Margin = Padding.Empty;
        Controls.Add(_card);
        Controls.Add(_note);
    }

    /// <summary>Atualiza a prévia. Pode ser chamado de novo antes de terminar: só o mais recente é aplicado.</summary>
    public void RefreshPreview()
    {
        int gen = ++_generation;
        var presence = SignalHost.Current?.Discord;
        _ = Task.Run(() =>
        {
            var cfg = Storage.LoadConfig();
            var (info, details) = presence?.Snapshot(cfg) ?? (null, null);
            string note;
            if (info is null)
            {
                // Nada no Discord agora: mostra um exemplo, com o modelo e o título da sessão mais recente se houver.
                var st = Storage.LoadState();
                var recent = DiscordPresence.MostRecentSession(st);
                details = (recent is not null ? presence?.DetailsFor(st, recent) : null)
                          ?? new PresenceDetails("claude-opus-5-5", "Exemplo de conversa", 42_000);
                info = new PresenceInfo(ClaudeEvents.Working, st.Find(recent)?.Project ?? "meu-projeto", DateTimeOffset.Now.AddMinutes(-12));
                note = "Agora não há sessão ativa: o status some do Discord (ligue \"Mostrar quando ocioso\" para manter). Exemplo com \"Trabalhando\".";
            }
            else note = cfg.Discord.Enabled ? "Status atual." : "Status atual (desligado: não está sendo enviado).";
            var color = cfg.Events.TryGetValue(info.Event, out var sig) ? Ui.ParseOr(sig.Color, Ui.Accent) : Ui.Accent;
            var activity = info.ToActivity(cfg.Discord, SignalsPage.DisplayName(info.Event), details);
            return (activity, color, note, url: cfg.Discord.ImageUrl("logo"));
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { Storage.Log($"discord: prévia: {t.Exception?.GetBaseException().Message}"); return; }
            OnUi(() =>
            {
                if (gen != _generation) return;
                var (activity, color, note, url) = t.Result;
                _card.SetActivity(activity, color);
                if (_note.Text != note) _note.Text = note;
                _ = LoadImageAsync(url);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Troca a imagem grande quando o link muda (a do Discord é a mesma URL).</summary>
    private async Task LoadImageAsync(string url)
    {
        if (url == _imageUrl) return;
        _imageUrl = url;
        var img = await DiscordGallery.LoadAsync(url).ConfigureAwait(false);
        if (IsDisposed) { img?.Dispose(); return; }
        OnUi(() =>
        {
            if (url != _imageUrl) img?.Dispose();
            else _card.SetLargeImage(img);
        });
    }

    private Action? _pending;
    private readonly object _pendingLock = new();

    /// <summary>Executa na thread da janela; antes de a página existir na tela, espera o handle ser criado.</summary>
    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        lock (_pendingLock) if (!IsHandleCreated) { _pending += action; return; }
        if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed) action(); });
        else action();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Action? pending;
        lock (_pendingLock) { pending = _pending; _pending = null; }
        if (pending is not null) BeginInvoke(pending);
    }
}

/// <summary>Cartão de atividade no estilo do Discord, desenhado a partir da mesma "activity" enviada.</summary>
internal sealed class DiscordActivityCard : Control
{
    private static readonly Color Bg = Color.FromArgb(35, 36, 40), Fg = Color.FromArgb(242, 243, 245), Dim = Color.FromArgb(181, 186, 193);
    private static readonly Color Logo = Color.FromArgb(217, 119, 87);
    private JsonObject? _activity;
    private Color _event = Logo;
    private Image? _large;

    public DiscordActivityCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(460, 138);
        Margin = new Padding(0, 6, 0, 6);
    }

    public void SetActivity(JsonObject activity, Color eventColor)
    {
        _activity = activity;
        _event = eventColor;
        Height = Button(activity) is null ? 138 : 182;
        Invalidate();
    }

    /// <summary>A imagem grande de verdade (GIF anima); null = esfera desenhada. Passa a ser deste controle.</summary>
    public void SetLargeImage(Image? image)
    {
        if (_large is not null)
        {
            ImageAnimator.StopAnimate(_large, OnFrameChanged);
            _large.Dispose();
        }
        _large = image;
        if (_large is not null && ImageAnimator.CanAnimate(_large)) ImageAnimator.Animate(_large, OnFrameChanged);
        Invalidate();
    }

    // Vem de outra thread.
    private void OnFrameChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(Invalidate);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SetLargeImage(null);
        base.Dispose(disposing);
    }

    /// <summary>Encaixa a imagem no quadrado sem distorcer.</summary>
    private static Rectangle Fit(Size img, Rectangle box)
    {
        double k = Math.Min((double)box.Width / img.Width, (double)box.Height / img.Height);
        int w = (int)(img.Width * k), h = (int)(img.Height * k);
        return new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Ui.Surface);
        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 10))
        using (var b = new SolidBrush(Bg))
            g.FillPath(b, path);
        if (_activity is null) return;

        using var small = new Font(Ui.FontFamily, 7.5F, FontStyle.Bold);
        TextRenderer.DrawText(g, "JOGANDO", small, new Point(16, 12), Dim);

        // Imagem grande (a do link, ou a esfera enquanto carrega) com a orb de status no canto, se ligada.
        var large = new Rectangle(16, 34, 84, 84);
        using (var path = Rounded(large, 8))
        using (var b = new SolidBrush(Color.FromArgb(30, 31, 34)))
            g.FillPath(b, path);
        if (_large is not null)
        {
            ImageAnimator.UpdateFrames(_large);
            using var path = Rounded(large, 8);
            var clip = g.Clip;
            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_large, Fit(_large.Size, large));
            g.Clip = clip;
        }
        else
            using (var logo = Ui.SignalBitmap(Logo, 76)) g.DrawImage(logo, large.X + 4, large.Y + 4, 76, 76);
        if (_activity["assets"]?["small_image"] is not null)
        {
            var badge = new Rectangle(large.Right - 26, large.Bottom - 26, 32, 32);
            using (var ring = new SolidBrush(Bg)) g.FillEllipse(ring, badge);
            using (var dot = Ui.SignalBitmap(_event, 28)) g.DrawImage(dot, badge.X + 2, badge.Y + 2, 28, 28);
        }

        int x = large.Right + 18, w = Width - x - 14, y = 38;
        using var bold = new Font(Ui.FontFamily, 10F, FontStyle.Bold);
        using var body = new Font(Ui.FontFamily, 9F);
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        void Line(string? text, Font f, Color c)
        {
            if (string.IsNullOrEmpty(text)) return;
            TextRenderer.DrawText(g, text, f, new Rectangle(x, y, w, f.Height + 2), c, flags);
            y += f.Height + 3;
        }
        Line("Claude Code", bold, Fg); // o Discord mostra o nome da aplicação do Developer Portal
        Line((string?)_activity["details"], body, Dim);
        Line((string?)_activity["state"], body, Dim);
        if (_activity["timestamps"]?["start"] is { } start)
        {
            var elapsed = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeSeconds((long)start);
            Line($"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} decorridos", body, Color.FromArgb(35, 165, 90));
        }

        // Botão do cartão (o Discord desenha cinza, na largura toda, abaixo da imagem).
        if (Button(_activity) is { } label)
        {
            var button = new Rectangle(16, large.Bottom + 12, Width - 32, 32);
            using (var path = Rounded(button, 6))
            using (var b = new SolidBrush(Color.FromArgb(78, 80, 88)))
                g.FillPath(b, path);
            using var bf = new Font(Ui.FontFamily, 9F, FontStyle.Bold);
            TextRenderer.DrawText(g, label, bf, button, Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private static string? Button(JsonObject? activity) => (string?)activity?["buttons"]?[0]?["label"];

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
