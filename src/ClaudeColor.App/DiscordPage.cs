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
    private readonly TextBox _appId = new() { Width = 220 };
    private readonly Label _status = Ui.InlineStatus();
    private readonly Label _previewNote = Ui.Hint("");
    private readonly DiscordPreview _preview = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private bool _loading;

    public override string NavTitle => "Discord";

    public DiscordPage() : base("Discord", "Mostra no seu perfil do Discord o que o Claude Code está fazendo, como um jogo.")
    {
        var show = Add(new Card("O que mostrar",
            "Amigos e servidores veem o seu status. O título da conversa e o nome da pasta podem revelar o assunto ou o projeto: vêm desligados."));
        show.Add(_enabled, _idle, _showStatus, _model, _title, _tokens, _project, _elapsed);

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
            connection.Wrap(Ui.Hint("As imagens vêm por URL (GIFs do repositório). Para usar outras, defina \"discord.images\" no config.json " +
                                    "(chaves logo, question, plan, permission, done, working, idle).")));

        foreach (var cb in new[] { _enabled, _idle, _showStatus, _model, _title, _tokens, _project, _elapsed }) cb.CheckedChanged += (_, _) => Save();
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
        _appId.Text = d.ApplicationId;
        _loading = false;
        RefreshPreview();
        _timer.Start();
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

        var status = presence?.Status ?? "O app ainda está iniciando";
        Ui.SetStatus(_status, !cfg.Discord.Enabled || presence is null ? null : presence.Connected, status);
    }

    /// <summary>Cartão de atividade no estilo do Discord, desenhado a partir da mesma "activity" enviada.</summary>
    private sealed class DiscordPreview : Control
    {
        private static readonly Color Bg = Color.FromArgb(35, 36, 40), Fg = Color.FromArgb(242, 243, 245), Dim = Color.FromArgb(181, 186, 193);
        private static readonly Color Logo = Color.FromArgb(217, 119, 87);
        private JsonObject? _activity;
        private Color _event = Logo;

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
            Invalidate();
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

            // Imagem grande (esfera do logo) com o selo do evento no canto.
            var large = new Rectangle(16, 34, 84, 84);
            using (var path = Rounded(large, 8))
            using (var b = new SolidBrush(Color.FromArgb(30, 31, 34)))
                g.FillPath(b, path);
            using (var logo = Ui.SignalBitmap(Logo, 76)) g.DrawImage(logo, large.X + 4, large.Y + 4, 76, 76);
            var badge = new Rectangle(large.Right - 26, large.Bottom - 26, 32, 32);
            using (var ring = new SolidBrush(Bg)) g.FillEllipse(ring, badge);
            using (var dot = Ui.SignalBitmap(_event, 28)) g.DrawImage(dot, badge.X + 2, badge.Y + 2, 28, 28);

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
        }

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
