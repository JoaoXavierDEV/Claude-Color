using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>Cor, brilho e comportamento de cada evento do Claude Code.</summary>
internal sealed class SignalsPage : Page
{
    public static string DisplayName(string evt) => evt switch
    {
        ClaudeEvents.Question => "Pergunta para você",
        ClaudeEvents.Plan => "Plano aguardando aprovação",
        ClaudeEvents.Permission => "Pedido de permissão",
        ClaudeEvents.Done => "Terminou",
        ClaudeEvents.Working => "Trabalhando",
        ClaudeEvents.Idle => "Ocioso (esperando você)",
        _ => evt,
    };

    public static readonly string[] Order =
        [ClaudeEvents.Question, ClaudeEvents.Plan, ClaudeEvents.Permission, ClaudeEvents.Done, ClaudeEvents.Working, ClaudeEvents.Idle];

    private sealed class RowControls
    {
        public required CheckBox Enabled, PcRgb, Notify, Flash;
        public CheckBox? Hold;
        public required ColorDot Color;
        public required NumericUpDown Brightness, Blink, Interval, Restore;
    }

    private readonly CheckBox _signalsEnabled = Ui.Check("Sinais do Claude Code ativados", bold: true);
    private readonly Dictionary<string, RowControls> _rows = [];
    private readonly Label _status = Ui.Status();

    public override string NavTitle => "Sinais";

    public SignalsPage() : base("Sinais", "Como cada momento do Claude Code aparece nas luzes.")
    {
        var card = Add(new Card("Eventos"));
        card.Add(_signalsEnabled);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 12, Margin = new Padding(0, 6, 0, 4), BackColor = Ui.Surface };
        (string Header, int Width)[] columns =
        [
            ("Evento", 165), ("Ativo", 40), ("RGB do PC", 48), ("Notificar", 56), ("Piscar barra", 50), ("Cor", 40), ("Brilho %", 64),
            ("Piscadas", 64), ("Intervalo (ms)", 66), ("Restaurar (s)", 66), ("Manter", 112), ("", 80),
        ];
        foreach (var (header, width) in columns)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
            grid.Controls.Add(new Label
            {
                Text = header, Dock = DockStyle.Fill, AutoSize = false, Height = 36, Font = Ui.Small, ForeColor = Ui.Muted,
                TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(0, 0, 4, 4),
            });
        }

        foreach (var evt in Order)
        {
            var row = new RowControls
            {
                Enabled = Cell(new CheckBox { AutoSize = true }),
                PcRgb = Cell(new CheckBox { AutoSize = true }),
                Notify = Cell(new CheckBox { AutoSize = true }),
                Flash = Cell(new CheckBox { AutoSize = true }),
                Color = Cell(new ColorDot()),
                Brightness = Num(1, 100),
                Blink = Num(0, 10),
                Interval = Num(100, 2000, 50),
                Restore = Num(0, 3600),
            };
            _rows[evt] = row;

            grid.Controls.Add(Cell(new Label { Text = DisplayName(evt), AutoSize = true, Font = Ui.Body, ForeColor = Ui.Text }));
            grid.Controls.AddRange([row.Enabled, row.PcRgb, row.Notify, row.Flash, row.Color, row.Brightness, row.Blink, row.Interval, row.Restore]);
            if (evt == ClaudeEvents.Working || SignalConfig.SupportsWaitForUser(evt))
            {
                var text = evt == ClaudeEvents.Working ? "até terminar" : "até você agir";
                row.Hold = Cell(new CheckBox { Text = text, AutoSize = true, Font = Ui.Body });
                row.Hold.CheckedChanged += (_, _) => row.Restore.Enabled = !row.Hold.Checked;
                grid.Controls.Add(row.Hold);
            }
            else grid.Controls.Add(new Label { AutoSize = true });
            var test = Ui.Button("Testar", (_, _) => Test(evt));
            test.MinimumSize = new Size(72, 28);
            test.Anchor = AnchorStyles.Left;
            grid.Controls.Add(test);
        }
        card.Add(grid);

        card.Add(Ui.Row(
            Ui.Button("Salvar", (_, _) => Save(), primary: true),
            Ui.Button("Restaurar padrões", (_, _) => { Show(AppConfig.DefaultEvents(), _signalsEnabled.Checked); _status.Text = "Padrões carregados (clique em Salvar)."; }),
            _status));

        var help = Add(new Card("Como funciona"));
        help.Add(
            help.Wrap(Ui.Hint("RGB do PC: o evento também acende os dispositivos da página Dispositivos (OpenRGB e integrações nativas). A lâmpada Wi-Fi, se ativa, acende sempre.")),
            help.Wrap(Ui.Hint("Notificar: mostra uma notificação do Windows com o nome do projeto; clicar nela traz o terminal da sessão para a frente. Some sozinha quando você age.")),
            help.Wrap(Ui.Hint("Piscar barra: o botão do terminal (Windows Terminal, VS Code, console) daquela sessão pisca na barra de tarefas até você abrir a janela.")),
            help.Wrap(Ui.Hint("Manter → até terminar: a cor de \"Trabalhando\" fica do envio da sua mensagem até o Claude terminar; perguntas e permissões no meio voltam para ela.")),
            help.Wrap(Ui.Hint("Manter → até você agir: a cor fica até você responder, aprovar ou permitir, em vez de voltar depois de um tempo fixo.")),
            help.Wrap(Ui.Hint("Restaurar: segundos até as luzes voltarem ao normal (0 = ficam na cor do evento). Testar sempre volta em até 5 s.")));
    }

    private static T Cell<T>(T c) where T : Control
    {
        c.Anchor = AnchorStyles.Left;
        c.Margin = new Padding(0, 4, 4, 4);
        return c;
    }

    private static NumericUpDown Num(int min, int max, int step = 1) =>
        Cell(new NumericUpDown { Minimum = min, Maximum = max, Increment = step, Width = 60, Font = Ui.Body });

    public override void OnShown()
    {
        var cfg = Storage.LoadConfig();
        Show(cfg.Events, cfg.SignalsEnabled);
    }

    private void Show(Dictionary<string, SignalConfig> events, bool signalsEnabled)
    {
        _signalsEnabled.Checked = signalsEnabled;
        foreach (var (evt, row) in _rows)
        {
            var s = events.TryGetValue(evt, out var v) ? v : new SignalConfig();
            row.Enabled.Checked = s.Enabled;
            row.PcRgb.Checked = s.UsesPcRgb(evt);
            row.Notify.Checked = s.UsesNotify(evt);
            row.Flash.Checked = s.UsesFlash(evt);
            if (row.Hold is not null) row.Hold.Checked = evt == ClaudeEvents.Working ? s.HoldWhileWorking : s.WaitForUser;
            row.Color.Value = Ui.ParseOr(s.Color, Color.White);
            row.Brightness.Value = Math.Clamp(s.Brightness, 1, 100);
            row.Blink.Value = Math.Clamp(s.Blink, 0, 10);
            row.Interval.Value = Math.Clamp(s.BlinkIntervalMs, 100, 2000);
            row.Restore.Value = Math.Clamp(s.RestoreAfterSeconds ?? 0, 0, 3600);
        }
    }

    /// <summary>Lê a tela para dentro de uma config (sem salvar).</summary>
    private AppConfig Collect()
    {
        var cfg = Storage.LoadConfig();
        cfg.SignalsEnabled = _signalsEnabled.Checked;
        foreach (var (evt, row) in _rows)
        {
            cfg.Events[evt] = new SignalConfig
            {
                Enabled = row.Enabled.Checked,
                PcRgb = row.PcRgb.Checked,
                Notify = row.Notify.Checked,
                FlashTaskbar = row.Flash.Checked,
                HoldWhileWorking = evt == ClaudeEvents.Working && (row.Hold?.Checked ?? false),
                WaitForUser = evt != ClaudeEvents.Working && (row.Hold?.Checked ?? false),
                Color = row.Color.Hex,
                Brightness = (int)row.Brightness.Value,
                Blink = (int)row.Blink.Value,
                BlinkIntervalMs = (int)row.Interval.Value,
                RestoreAfterSeconds = row.Restore.Value > 0 ? (int)row.Restore.Value : null,
            };
        }
        return cfg;
    }

    private void Save()
    {
        Storage.SaveConfig(Collect());
        _status.ForeColor = Ui.Ok;
        _status.Text = $"Salvo às {DateTime.Now:HH:mm:ss}.";
    }

    private void Test(string evt)
    {
        if (SignalHost.Current is null) { _status.ForeColor = Ui.Error; _status.Text = "O app ainda está iniciando."; return; }
        LampService.TestSignal(Collect(), evt);
        _status.ForeColor = Ui.Ok;
        _status.Text = $"Testando \"{DisplayName(evt)}\" (volta ao normal em até 5 s).";
    }
}
