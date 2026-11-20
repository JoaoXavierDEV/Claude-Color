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
        preview.Add(_preview);

        var connection = Add(new Card("Conexão",
            "Precisa do Discord de desktop aberto. Um aplicativo já vem configurado; o nome no topo do cartão é o dessa aplicação no Developer Portal. Troque o Application ID só se criar uma sua."));
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
        _preview.RefreshPreview();
        var cfg = Storage.LoadConfig();
        var presence = SignalHost.Current?.Discord;
        var status = presence?.Status ?? "O app ainda está iniciando";
        Ui.SetStatus(_status, !cfg.Discord.Enabled || presence is null ? null : presence.Connected, status);
    }
}
