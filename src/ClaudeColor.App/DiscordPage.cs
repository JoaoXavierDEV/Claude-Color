using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Status no perfil do Discord: o que mostrar, prévia do cartão e conexão.</summary>
internal sealed class DiscordPage : Page
{
    private readonly CheckBox _enabled = Ui.Check("Mostrar o status no Discord", bold: true);
    private readonly CheckBox _showStatus = Ui.Check("Estado (Trabalhando, Terminou, Aguardando você...)");
    private readonly CheckBox _idle = Ui.Check("Mostrar quando ocioso (sem o Claude trabalhando nem esperando você)");
    private readonly CheckBox _model = Ui.Check("Modelo (ex.: Opus 5.5)");
    private readonly CheckBox _title = Ui.Check("Título da conversa");
    private readonly CheckBox _tokens = Ui.Check("Tokens no contexto (os mesmos do /context)");
    private readonly CheckBox _project = Ui.Check("Nome da pasta");
    private readonly CheckBox _elapsed = Ui.Check("Tempo de trabalho");
    private readonly CheckBox _orb = Ui.Check("Orb de status no canto da imagem (bolinha na cor do estado)");
    private readonly CheckBox _github = Ui.Check("Botão \"View on GitHub\" com o link do Claude Color");
    private readonly TextBox _appId = new() { Width = 220 };
    private readonly Label _status = Ui.InlineStatus();
    private readonly Label _previewNote = Ui.Hint("");
    private readonly DiscordPreview _preview = new();
    private readonly FlowLayoutPanel _gallery = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, MaximumSize = new Size(760, 0),
        Margin = new Padding(0, 6, 0, 2),
    };
    private readonly Label _galleryNote = Ui.Hint("Carregando a galeria...");
    private readonly TextBox _url = new() { Width = 430 };
    private readonly Label _urlStatus = Ui.Hint("");
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private bool _loading, _galleryLoaded;
    private string? _previewUrl;

    public override string NavTitle => "Discord";

    public DiscordPage() : base("Discord", "Mostra no seu perfil do Discord o que o Claude Code está fazendo, como um jogo.")
    {
        var show = Add(new Card("O que mostrar",
            "Amigos e servidores veem o seu status. O título da conversa e o nome da pasta podem revelar o assunto ou o projeto: vêm desligados."));
        show.Add(_enabled, _idle, _showStatus, _model, _title, _tokens, _project, _elapsed, _orb, _github);

        var image = Add(new Card("Imagem",
            "A imagem grande do cartão. Escolha um GIF da galeria (os de docs/discord/gallery no GitHub) ou cole o link de uma imagem sua."));
        var urlForm = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 4, 0, 0) };
        _url.Margin = new Padding(0, 5, 8, 4);
        urlForm.Controls.Add(Ui.Label("Link (https)"));
        urlForm.Controls.Add(_url);
        urlForm.Controls.Add(Ui.Row(
            Ui.Button("Usar", (_, _) => UseUrl(_url.Text.Trim())),
            Ui.Link("Voltar ao padrão", () => UseUrl(DiscordConfig.DefaultImageBase + "logo.gif"))));
        image.Add(_gallery, _galleryNote, urlForm, image.Wrap(_urlStatus));
        _url.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; UseUrl(_url.Text.Trim()); } };

        var preview = Add(new Card("Prévia", "Como o seu perfil aparece agora, com as opções acima. É o mesmo conteúdo enviado ao Discord."));
        preview.Add(_preview, _previewNote);

        var connection = Add(new Card("Conexão",
            "Precisa do Discord de desktop aberto. O aplicativo \"Claude Color\" já vem configurado; troque o Application ID só se criar um seu."));
        var form = Ui.Form(110);
        Ui.AddField(form, "Application ID", _appId, Ui.Link("Abrir o Developer Portal", "https://discord.com/developers/applications"));
        connection.Add(form,
            Ui.Row(_status,
                Ui.Button("Testar", (_, _) =>
                {
                    Save();
                    SignalHost.Current?.Discord.Test();
                    _status.Text = "●  Testando: \"Aguardando você: pergunta\" por 15 s...";
                }),
                Ui.Link("Abrir config.json", () => TrayContext.OpenPath(Storage.ConfigPath))),
            connection.Wrap(Ui.Hint("As imagens vêm por URL. As da orb de status (question, plan, permission, done, working, idle) " +
                                    "podem ser trocadas em \"discord.images\" no config.json.")));

        foreach (var cb in new[] { _enabled, _idle, _showStatus, _model, _title, _tokens, _project, _elapsed, _orb, _github }) cb.CheckedChanged += (_, _) => Save();
        _appId.Leave += (_, _) => Save();
        _timer.Tick += (_, _) => { if (Visible) RefreshPreview(); };
    }

    public override void OnShown()
    {
        _loading = true;
        var d = Storage.LoadConfig().Discord;
        _enabled.Checked = d.Enabled;
        _idle.Checked = d.ShowWhenIdle;
        _showStatus.Checked = d.ShowStatus;
        _model.Checked = d.ShowModel;
        _title.Checked = d.ShowTitle;
        _tokens.Checked = d.ShowTokens;
        _project.Checked = d.ShowProject;
        _elapsed.Checked = d.ShowElapsed;
        _orb.Checked = d.ShowStatusOrb;
        _github.Checked = d.ShowGitHubButton;
        _appId.Text = d.ApplicationId;
        _url.Text = d.ImageUrl("logo");
        _loading = false;
        RefreshPreview();
        _timer.Start();
        if (!_galleryLoaded) { _galleryLoaded = true; _ = LoadGalleryAsync(); }
    }

    private async Task LoadGalleryAsync()
    {
        try { await FillGalleryAsync(); }
        catch (Exception ex)
        {
            Storage.Log($"discord: galeria: {ex}");
            OnUi(() => _galleryNote.Text = "Não deu para carregar a galeria: " + ex.Message);
        }
    }

    /// <summary>Executa na thread da janela (as continuações dos downloads podem voltar em outra).</summary>
    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(() => { if (!IsDisposed) action(); });
        else action();
    }

    private async Task FillGalleryAsync()
    {
        var items = await DiscordGallery.ListAsync().ConfigureAwait(false);
        var thumbs = new Dictionary<DiscordGallery.Item, Thumb>();
        OnUi(() =>
        {
            _galleryNote.Text = "Carregando as imagens...";
            foreach (var item in items)
            {
                var thumb = new Thumb(item.Url);
                _tips.SetToolTip(thumb.Picture, Path.GetFileNameWithoutExtension(item.Name));
                thumb.Picture.Click += (_, _) => UseUrl(item.Url);
                _gallery.Controls.Add(thumb);
                lock (thumbs) thumbs[item] = thumb;
            }
            HighlightSelected();
        });
        int failed = 0;
        await Task.WhenAll(items.Select(async item =>
        {
            var img = await DiscordGallery.LoadAsync(item.Url).ConfigureAwait(false);
            if (img is null) Interlocked.Increment(ref failed);
            OnUi(() =>
            {
                Thumb? thumb;
                lock (thumbs) thumbs.TryGetValue(item, out thumb);
                if (thumb is null || thumb.IsDisposed) img?.Dispose();
                else thumb.Picture.Image = img;
            });
        })).ConfigureAwait(false);
        OnUi(() => _galleryNote.Text = failed == 0
            ? "Clique numa imagem para usar. As miniaturas animam como no Discord."
            : $"{failed} imagem(ns) não carregaram (sem internet?). Clique numa imagem para usar.");
    }

    private void UseUrl(string url)
    {
        if (!DiscordConfig.IsValidImageUrl(url))
        {
            _urlStatus.ForeColor = Ui.Error;
            _urlStatus.Text = "O link precisa começar com https:// (o Discord só aceita imagens por https).";
            return;
        }
        var cfg = Storage.LoadConfig();
        cfg.Discord.EnsureImages();
        cfg.Discord.Images!["logo"] = url;
        Storage.SaveConfig(cfg);
        SignalHost.Current?.Discord.Refresh();
        _url.Text = url;
        _urlStatus.ForeColor = Ui.Muted;
        _urlStatus.Text = "Salvo em discord.images.logo. O Discord atualiza na próxima mudança de status.";
        HighlightSelected();
        RefreshPreview();
    }

    private void HighlightSelected()
    {
        var current = Storage.LoadConfig().Discord.ImageUrl("logo");
        foreach (var t in _gallery.Controls.OfType<Thumb>())
            t.SetSelected(string.Equals(t.Url, current, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Miniatura da galeria: a borda fica na cor de destaque quando é a imagem em uso.</summary>
    private sealed class Thumb : Panel
    {
        public string Url { get; }
        public PictureBox Picture { get; } = new()
        {
            Size = new Size(72, 72), SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand,
            BackColor = Color.FromArgb(30, 31, 34), Margin = Padding.Empty, Location = new Point(3, 3),
        };

        public Thumb(string url)
        {
            Url = url;
            Size = new Size(78, 78);
            Margin = new Padding(0, 0, 8, 8);
            Controls.Add(Picture);
            SetSelected(false);
        }

        public void SetSelected(bool on) => BackColor = on ? Ui.Accent : Ui.Border;
    }

    private void Save()
    {
        if (_loading) return;
        var cfg = Storage.LoadConfig();
        var d = cfg.Discord;
        d.Enabled = _enabled.Checked;
        d.ShowWhenIdle = _idle.Checked;
        d.ShowStatus = _showStatus.Checked;
        d.ShowModel = _model.Checked;
        d.ShowTitle = _title.Checked;
        d.ShowTokens = _tokens.Checked;
        d.ShowProject = _project.Checked;
        d.ShowElapsed = _elapsed.Checked;
        d.ShowStatusOrb = _orb.Checked;
        d.ShowGitHubButton = _github.Checked;
        d.ApplicationId = new string(_appId.Text.Where(char.IsAsciiDigit).ToArray());
        Storage.SaveConfig(cfg);
        SignalHost.Current?.Discord.Refresh();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var cfg = Storage.LoadConfig();
        var presence = SignalHost.Current?.Discord;
        var (info, details) = presence?.Snapshot(cfg) ?? (null, null);
        if (info is null)
        {
            // Nada no Discord agora: mostra um exemplo, com o modelo e o título da sessão mais recente se houver.
            var st = Storage.LoadState();
            var recent = DiscordPresence.MostRecentSession(st);
            details = (recent is not null ? presence?.DetailsFor(st, recent) : null)
                      ?? new PresenceDetails("claude-opus-5-5", "Exemplo de conversa", 42_000);
            info = new PresenceInfo(ClaudeEvents.Working, st.Find(recent)?.Project ?? "meu-projeto", DateTimeOffset.Now.AddMinutes(-12));
            _previewNote.Text = "Agora não há sessão ativa: o status some do Discord (ligue \"Mostrar quando ocioso\" para manter). Exemplo com \"Trabalhando\".";
        }
        else _previewNote.Text = cfg.Discord.Enabled ? "Status atual." : "Status atual (desligado: não está sendo enviado).";

        var color = cfg.Events.TryGetValue(info.Event, out var sig) ? Ui.ParseOr(sig.Color, Ui.Accent) : Ui.Accent;
        _preview.SetActivity(info.ToActivity(cfg.Discord, SignalsPage.DisplayName(info.Event), details), color);
        _ = LoadPreviewImageAsync(cfg.Discord.ImageUrl("logo"));

        var status = presence?.Status ?? "O app ainda está iniciando";
        Ui.SetStatus(_status, !cfg.Discord.Enabled || presence is null ? null : presence.Connected, status);
    }

    /// <summary>Troca a imagem grande da prévia quando o link muda (a do Discord é a mesma URL).</summary>
    private async Task LoadPreviewImageAsync(string url)
    {
        if (url == _previewUrl) return;
        _previewUrl = url;
        var img = await DiscordGallery.LoadAsync(url).ConfigureAwait(false);
        if (IsDisposed) { img?.Dispose(); return; }
        OnUi(() =>
        {
            if (url != _previewUrl) img?.Dispose();
            else _preview.SetLargeImage(img);
        });
    }

    /// <summary>Cartão de atividade no estilo do Discord, desenhado a partir da mesma "activity" enviada.</summary>
    private sealed class DiscordPreview : Control
    {
        private static readonly Color Bg = Color.FromArgb(35, 36, 40), Fg = Color.FromArgb(242, 243, 245), Dim = Color.FromArgb(181, 186, 193);
        private static readonly Color Logo = Color.FromArgb(217, 119, 87);
        private JsonObject? _activity;
        private Color _event = Logo;
        private Image? _large;

        public DiscordPreview()
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
            Line("Claude Color", bold, Fg);
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
}
